using System.Runtime.CompilerServices;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

// The journal of unsynced spends under License: what this process could not save is kept in memory and counts
// everywhere the stored state is read, a save lands the journal changes on the state as stored at that moment,
// and a request reports the journal as it is stored. Metered uses (License.Meters.cs) spend; the request that
// reports and confirms the journal is the online validate (License.Engine.cs).
public sealed partial class License
{
    private readonly object journalGate = new();

    /// <summary>
    /// Journal changes this process could not save: the disk refused them, or the call was behind one that has not
    /// finished. Kept for the process, in the state every read sees (<see cref="LoadAsync"/>), and saved with the next
    /// save that can (<see cref="SaveWithAsync"/>), as a trial the disk refused is kept. A process that ends first loses
    /// the report, never the customer's use. Replaced, never changed: a reader holds a snapshot.
    /// </summary>
    private IReadOnlyList<JournalOp> unsaved = [];

    /// <summary>The journals of the views <see cref="LoadAsync"/> made with <see cref="unsaved"/> landed on them: a view that read held spends (<see cref="ReportableAsync"/>).</summary>
    private readonly ConditionalWeakTable<IReadOnlyList<SpendEntry>, object> carrying = new();

    private static readonly object Carried = new();

    /// <summary>Saves in flight (<see cref="SaveWithAsync"/>): while one is, <see cref="unsaved"/> is only added to, never folded (<see cref="KeepUnsaved"/>).</summary>
    private int saving;

    private IReadOnlyList<JournalOp> Held()
    {
        lock (journalGate) return unsaved;
    }

    /// <summary>The journal changes this process holds in memory because a save failed. For the tests; not public API.</summary>
    internal IReadOnlyList<JournalOp> HeldChanges => Held();

    /// <summary>The state as this process sees it: the stored one with what it could not save landed on it. For the tests; not public API.</summary>
    internal Task<StoredState> ViewAsync() => LoadAsync(readOnly: true);

    /// <summary>The stored state, as the storage has it.</summary>
    private async Task<StoredState> StoredAsync(bool readOnly = false) =>
        await storage.ReadAsync(readOnly).ConfigureAwait(false) ?? new StoredState();

    /// <summary>
    /// The stored state with the journal changes this process could not save (<see cref="unsaved"/>) landed on it, as they
    /// will be; <paramref name="readOnly"/> behind a call that has not finished, so the read writes nothing either.
    /// </summary>
    private async Task<StoredState> LoadAsync(bool readOnly = false)
    {
        var state = await StoredAsync(readOnly).ConfigureAwait(false);
        var held = Held();
        if (held.Count == 0) return state;
        var landed = Journal.Land(state, held, Offline.GuardedNow(clock.Now(), state.LastSeen)).State;
        if (landed.Spends is { } spends) carrying.AddOrUpdate(spends, Carried);
        return landed;
    }

    /// <summary>
    /// The journal a request reports for <paramref name="view"/> (the state a call read): the journal as stored, with what
    /// this process could not save (<see cref="unsaved"/>) that opens an entry of its own, once, by its id: an
    /// <c>Unanswered</c> spend and a <c>Void</c>. A spend held in memory that was made offline is withheld: it joins the entry
    /// of another when it lands (<see cref="Journal.Landed"/>), so a report of it under its own id would be counted a second
    /// time when the entry it joined is reported. It is reported once it has landed, which the next call that can save does.
    /// The entries the server has since confirmed are left out: there is no need to say again what it holds. A view that
    /// carried held spends (<see cref="LoadAsync"/>) is judged the same, though they may have landed since.
    /// </summary>
    private async Task<IReadOnlyList<SpendEntry>> ReportableAsync(StoredState view)
    {
        var carried = view.Spends is { } spends && carrying.TryGetValue(spends, out _);
        var held = Held();
        if (!carried && held.Count == 0) return view.Spends ?? [];
        StoredState stored;
        try
        {
            stored = await StoredAsync(readOnly: true).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }
        if (!string.Equals(stored.ActivationId, view.ActivationId, StringComparison.Ordinal)) return [];
        var reportable = held.Where(op => op is SpendOp { Kind: SpendKind.Unanswered or SpendKind.Void } or ConfirmedOp).ToList();
        return Journal.Land(stored, reportable, Offline.GuardedNow(clock.Now(), stored.LastSeen)).State.Spends ?? [];
    }

    /// <summary>
    /// <see cref="SaveAsync"/> with changes to the journal (<paramref name="ops"/>), landed on the state as stored now
    /// (<see cref="Journal.Landed"/>), after the ones this process could not save before. Whether each of
    /// <paramref name="ops"/> was refused, the journal being full. Throws when the save fails; the changes stay in
    /// <see cref="unsaved"/> until one lands.
    /// </summary>
    private async Task<bool[]> SaveWithAsync(StatePatch patch, long? guard, Basis? basis, IReadOnlyList<JournalOp> ops)
    {
        IReadOnlyList<JournalOp> all;
        int before;
        lock (journalGate)
        {
            before = unsaved.Count;
            all = [.. unsaved, .. ops];
            saving++;
        }
        var refused = Array.Empty<bool>();

        StoredState Merge(StoredState latest)
        {
            var clockNow = clock.Now();
            var landed = Journal.Land(latest, all, Offline.GuardedNow(clockNow, latest.LastSeen));
            refused = landed.Refused;
            return StateFields.Set(Offline.Merged(landed.State, patch, basis), StateField.LastSeen, Offline.SavedLastSeen(latest.LastSeen, clockNow, guard));
        }

        try
        {
            if (!await storage.UpdateAsync(Merge).ConfigureAwait(false)) await storage.WriteAsync(Merge(await StoredAsync().ConfigureAwait(false))).ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (journalGate) saving--;
            throw;
        }
        lock (journalGate)
        {
            saving--;
            unsaved = unsaved.Skip(before).ToList();
        }
        return refused.Skip(before).ToArray();
    }

    /// <summary>
    /// <see cref="SaveWithAsync"/>, whose failure fails nothing: the changes it could not save are kept for this process
    /// (<see cref="unsaved"/>), those the journal had room for (judged on <paramref name="view"/>, the state the call read,
    /// when the save failed). Whether each of <paramref name="ops"/> was refused.
    /// </summary>
    private async Task<bool[]> SaveOpsAsync(StoredState view, StatePatch patch, Basis? basis, IReadOnlyList<JournalOp> ops)
    {
        try
        {
            return await SaveWithAsync(patch, null, basis, ops).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return KeepUnsaved(view, ops);
        }
    }

    /// <summary>Keep <paramref name="ops"/> for this process (<see cref="unsaved"/>), those the journal has room for on <paramref name="view"/>. Whether each was refused.</summary>
    private bool[] KeepUnsaved(StoredState view, IReadOnlyList<JournalOp> ops)
    {
        var refused = Journal.Land(view, ops, Offline.GuardedNow(clock.Now(), view.LastSeen)).Refused;
        lock (journalGate)
        {
            // A spend is told to the customer from here on: it lands whatever the journal holds by then (`Told`). A void is no
            // spend told, so it is held to the journal's rules, and to the newest MaxPendingVoids here.
            var kept = ops
                .Where((_, i) => !refused[i])
                .Select(op => op is SpendOp { Kind: SpendKind.Offline or SpendKind.Unanswered } spend ? spend with { Told = true } : op);
            var all = unsaved.Concat(kept).ToList();
            unsaved = saving == 0 ? Journal.BoundedVoids(Journal.Compacted(all)) : all;
        }
        return refused;
    }

    /// <summary>Save what this process could not (<see cref="unsaved"/>), best-effort, at the start of a call that may write.</summary>
    private async Task SaveUnsavedJournalAsync()
    {
        if (Held().Count > 0) await SaveBestEffortAsync(new StatePatch()).ConfigureAwait(false);
    }
}
