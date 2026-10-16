using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

// Metered uses under License: UseAsync asks the server to spend (exact, atomic, idempotent by the spend's id), and
// when it gets no answer spends from the device's offline allowance, keeping the spend in the journal
// (License.Journal.cs) to report with the next request.
//
// A meter never takes a licence away, and licensing fails in favour of the customer: no answer is never a refusal
// of the licence, only of a spend past the allowance, and the journal never fails a renewal or a release.
public sealed partial class License
{
    /// <summary>
    /// Spend <paramref name="n"/> (default 1) uses of the key's meter <paramref name="meter"/>, before the work they count;
    /// do the work only when <see cref="UseResult.Ok"/>. Online it is exact: the server takes <paramref name="n"/> only
    /// while the meter has room for all of it (<see cref="UseError.UsedUp"/>, with <see cref="UseResult.Left"/>, otherwise).
    /// With no answer (no network, a timeout, a 5xx or 429, an answer it cannot use) it spends from this device's offline
    /// allowance for the meter, never past what the device last heard is left (<see cref="UseError.OfflineLimit"/>), answers
    /// <see cref="UseResult.Offline"/>, and reports the spend with its next request. Needs a key's lease that licenses this
    /// build here (<see cref="UseError.NotLicensed"/> otherwise, asking nothing); a device trial and an offline licence file have no
    /// meters. Two calls are two spends: the spend's id covers the SDK's own retries and reports, never an app that calls twice.
    /// <para>
    /// It never throws for an outcome: every one is in the result, behind a call that has not finished too, and what no adapter
    /// can tell it (one threw where there is no word for it) is <see cref="UseError.Storage"/>. Calls run one at a time with
    /// the rest. The one exception is <paramref name="cancellationToken"/>: cancelled while the call waits its turn, or before
    /// its request is sent, it throws <see cref="OperationCanceledException"/> and nothing is spent. Cancelled while the request
    /// is out, it stops waiting for the answer and the spend is made as for no answer (in doubt, so reported), never an exception:
    /// the call RETURNS its result, so read it even after cancelling (a spend that was made is the customer's). No wait is recorded
    /// for the ask it cut short (<see cref="StoredState.BackoffSince"/> stays as it was), but the journal entry is, and the journal's
    /// own wait (for a spend the allowance refuses, its void's minute) runs from that entry.
    /// </para>
    /// </summary>
    /// <param name="meter">The meter's code, 1 to 32 UTF-16 units, matched exactly (case included); anything else is <see cref="UseError.NoMeter"/>.</param>
    /// <param name="n">How many uses: a whole number from 1 to 1,000,000,000; anything else is <see cref="UseError.InvalidAmount"/>.</param>
    /// <param name="cancellationToken">As described above.</param>
    /// <returns>Spent, or why not.</returns>
    public async Task<UseResult> UseAsync(string meter, long n = 1, CancellationToken cancellationToken = default)
    {
        var asked = Meters.AskedFor(meter, n);
        if (asked.Refused is { } refusal) return UseResult.Refused(refusal);
        try
        {
            return await Serial(readOnly => SpendAsync(asked.Meter, asked.N, readOnly, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Nothing an adapter does may reject a spend: the customer is told to try again shortly.
            return UseResult.Refused(UseError.Storage);
        }
    }

    /// <summary>One call to <see cref="UseAsync"/>, as the steps below read it.</summary>
    /// <param name="State">The state the call read, with what this process could not save landed on it.</param>
    /// <param name="Claims">The claims of the lease that licenses this build.</param>
    /// <param name="Meter">The meter's code.</param>
    /// <param name="N">How many uses.</param>
    /// <param name="Id">The spend's id.</param>
    /// <param name="At">When (the guarded clock) the spend is made.</param>
    /// <param name="ReadOnly">Behind a call that has not finished: nothing is saved.</param>
    /// <param name="Verdict">What the offline allowance says of the spend as the call read the state.</param>
    private sealed record Spending(StoredState State, LeaseClaims Claims, string Meter, long N, string Id, long At, bool ReadOnly, Meters.OfflineVerdict Verdict);

    /// <summary>The steps of a spend: the state and the licence, then the server when it should be asked, then the allowance.</summary>
    private async Task<UseResult> SpendAsync(string meter, long n, bool readOnly, CancellationToken cancellationToken)
    {
        if (!readOnly) await SaveUnsavedJournalAsync().ConfigureAwait(false);
        var state = await LoadForStatusAsync(readOnly).ConfigureAwait(false);
        if (state is null) return UseResult.Refused(UseError.Storage);
        var claims = await KeyClaimsAsync(state, cancellationToken).ConfigureAwait(false);
        if (claims is null || state.ActivationId is null) return UseResult.Refused(UseError.NotLicensed);
        var at = EffectiveNow(state);
        var verdict = Meters.OfflineSpend(Meters.FiguresFor(claims, state), state.Spends, meter, n);
        var spending = new Spending(state, claims, meter, n, Meters.NewSpendId(), at, readOnly, verdict);
        var sent = false;
        if (Meters.UseAsksNow(state, clock.Now(), at, verdict.Allowed))
        {
            if (await AskToSpendAsync(spending, cancellationToken).ConfigureAwait(false) is { } answered) return answered;
            sent = true;
        }
        return await SpendOfflineAsync(spending, sent).ConfigureAwait(false);
    }

    /// <summary>
    /// The claims of the key's lease when the state licenses this build here by a key's lease: what <see cref="StatusAsync"/>
    /// would give as licensed from what is stored (no ask, no write), and not by a licence file. Null otherwise.
    /// </summary>
    private async Task<LeaseClaims?> KeyClaimsAsync(StoredState state, CancellationToken cancellationToken)
    {
        var status = await StatusNowAsync(state, false, true, cancellationToken).ConfigureAwait(false);
        if (status.State != LicenseState.Licensed || status.Source == LicenseSource.File || string.IsNullOrEmpty(state.Lease)) return null;
        var check = await EvaluateAsync(state.Lease, EffectiveNow(state), state.Key, state.ActivationId, state.DeviceKind, cancellationToken).ConfigureAwait(false);
        return check.Ok ? check.Claims : null;
    }

    /// <summary>
    /// Ask the server to spend (<c>use</c>, bounded by <see cref="Meters.UseTimeoutMs"/> or the HTTP timeout when shorter), with
    /// the journal's report. Its answer is saved as a validate's is (<see cref="Meters.OutcomeOf"/>) unless this call is behind
    /// one that has not finished. The result it gives, or null when there was no usable answer: the spend is then in doubt, the
    /// server may hold it.
    /// </summary>
    private async Task<UseResult?> AskToSpendAsync(Spending sp, CancellationToken cancellationToken)
    {
        var state = sp.State;
        var askedAt = clock.Now();
        var asked = new Asked(sp.At, state.ActivationId, state.VerdictAt);
        var device = await DeviceAsync(TimeSpan.FromMilliseconds(MachineId.TimeoutMs), state.DeviceKind, cancellationToken).ConfigureAwait(false);
        var body = DeviceBody(state, device);
        body["meter"] = sp.Meter;
        body["n"] = sp.N;
        body["id"] = sp.Id;
        if (Meters.SpendsReported(await ReportableAsync(state).ConfigureAwait(false)) is { } reports) body["spends"] = reports;
        body["kids"] = KidsJson();
        // Cancelled before the request is sent: nothing was asked, so nothing is spent.
        cancellationToken.ThrowIfCancellationRequested();
        HttpResult? response;
        var cutShort = false;
        try
        {
            response = await PostAsync("use", body, TimeSpan.FromMilliseconds(Math.Min(Meters.UseTimeoutMs, (long)httpTimeout.TotalMilliseconds)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            response = null;
            cutShort = cancellationToken.IsCancellationRequested;
        }
        var outcome = Meters.OutcomeOf(response, askedAt, major, device);
        // The caller stopped waiting: the spend is in doubt, and nothing is recorded of the ask it cut short (no wait).
        if (cutShort) outcome = outcome with { Patch = new StatePatch() };
        var ops = JournalOps(state, outcome);
        // A verdict or a wait is the answer to an ask, stamped as a validate's is; figures and confirmations are not a
        // verdict, so a spend never makes a validate another process asked for earlier look out of date.
        var answer = outcome.Patch.Entries.Count > 0 ? new Basis { Answer = asked } : null;
        if (!sp.ReadOnly && (ops.Count > 0 || answer is not null)) await SaveOpsAsync(state, outcome.Patch, answer, ops).ConfigureAwait(false);
        return outcome.Kind == UseOutcomeKind.Unanswered ? null : Answered(outcome, sp, Journal.Land(state, ops, sp.At).State);
    }

    /// <summary>The journal changes an answer makes: the reports it confirms and the figures it gives, about the activation asked for.</summary>
    private static List<JournalOp> JournalOps(StoredState state, UseOutcome outcome)
    {
        var ops = new List<JournalOp>();
        if (state.ActivationId is not { } about) return ops;
        if (outcome.Confirmed.Count > 0) ops.Add(new ConfirmedOp(about, outcome.Confirmed));
        if (outcome.Figures.Count > 0) ops.Add(new FiguresOp(about, outcome.Figures));
        return ops;
    }

    /// <summary>
    /// What <see cref="UseAsync"/> answers for an answer the server gave. A spend: what is left of the meter by the figures
    /// as the answer left them (<paramref name="after"/>: the base figure, its own from the answer when it gave one, less
    /// what this device has not reported). <c>used-up</c>: the answer's <c>left</c>. A verdict: the refusal, as
    /// <see cref="StatusAsync"/> will say it.
    /// </summary>
    private static UseResult Answered(UseOutcome outcome, Spending sp, StoredState after)
    {
        switch (outcome.Kind)
        {
            case UseOutcomeKind.Spent:
                return UseResult.Spent(LeftOf(sp, after));
            case UseOutcomeKind.UsedUp:
                return UseResult.Refused(UseError.UsedUp, outcome.Left);
            case UseOutcomeKind.NoMeter:
                return UseResult.Refused(UseError.NoMeter);
            default:
                return UseResult.Refused(outcome.Refusal is { } refusal ? Names.UseErrorOf(refusal) : UseError.NotLicensed);
        }
    }

    /// <summary>What is left of the spend's meter by <paramref name="state"/>: the base figure less the unsynced spends, never below 0; 0 for a meter with no figure.</summary>
    private static long LeftOf(Spending sp, StoredState state) =>
        Meters.FiguresFor(sp.Claims, state).TryGetValue(sp.Meter, out var figure) ? Math.Max(0, figure.Left - Meters.UnsyncedOf(state.Spends, sp.Meter)) : 0;

    /// <summary>
    /// With no answer (or none asked): spend from the allowance (<see cref="Meters.OfflineSpend"/>). Allowed, the spend is
    /// journaled, <c>Offline</c> when it was never sent and <c>Unanswered</c> when it was in doubt, with the most the meter's
    /// unsynced spends may come to (<c>Limit</c>), which the landing judges again on the state as stored then, so two processes
    /// never pass the allowance together. Refused, a spend in doubt is journaled as a <c>Void</c>, for the server to take it
    /// back off if it recorded it, and so is one the landing refuses (the journal full, or the allowance taken by another
    /// process meanwhile, answered with what is left as the landing found it): the customer is told
    /// <see cref="UseError.OfflineLimit"/>, the server is told the spend is taken back. Voids are kept to
    /// <see cref="Meters.MaxPendingVoids"/>, a new one taking the place of the oldest, and a void a full journal cannot take is lost.
    /// </summary>
    private async Task<UseResult> SpendOfflineAsync(Spending sp, bool doubt)
    {
        var about = sp.State.ActivationId!;
        var verdict = sp.Verdict;
        var voided = new SpendOp(SpendKind.Void, about, sp.Id, sp.Meter, sp.N, sp.At);
        if (!verdict.Allowed)
        {
            if (doubt) await JournalAsync(sp, [voided]).ConfigureAwait(false);
            return verdict.Error == UseError.NoMeter ? UseResult.Refused(UseError.NoMeter) : UseResult.Refused(UseError.OfflineLimit, verdict.Left);
        }
        var spend = new SpendOp(doubt ? SpendKind.Unanswered : SpendKind.Offline, about, sp.Id, sp.Meter, sp.N, sp.At, verdict.Limit);
        if ((await JournalAsync(sp, [spend]).ConfigureAwait(false))[0])
        {
            if (doubt) await JournalAsync(sp, [voided]).ConfigureAwait(false);
            return UseResult.Refused(UseError.OfflineLimit, await LandedLeftAsync(sp).ConfigureAwait(false));
        }
        return UseResult.Spent(verdict.Left, offline: true);
    }

    /// <summary>
    /// What is left of the meter by the state as a refused landing found it (another process had spent meanwhile): the figure
    /// less the unsynced spends there then, never below 0, and 0 when the state cannot be read just now.
    /// </summary>
    private async Task<long> LandedLeftAsync(Spending sp)
    {
        var fresh = await LoadForStatusAsync(sp.ReadOnly).ConfigureAwait(false);
        return fresh is null ? 0 : LeftOf(sp, fresh);
    }

    /// <summary><paramref name="ops"/> saved, or kept in memory when this call may save nothing or the disk refuses. Whether each was refused.</summary>
    private async Task<bool[]> JournalAsync(Spending sp, IReadOnlyList<JournalOp> ops) =>
        sp.ReadOnly ? KeepUnsaved(sp.State, ops) : await SaveOpsAsync(sp.State, new StatePatch(), null, ops).ConfigureAwait(false);
}
