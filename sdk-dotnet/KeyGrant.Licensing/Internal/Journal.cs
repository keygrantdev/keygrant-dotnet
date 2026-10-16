namespace KeyGrant.Licensing.Internal;

/// <summary>A report the server names as recorded: how many it holds of a spend, or that it took a void.</summary>
/// <param name="Id">The spend's id.</param>
/// <param name="N">How many of it the server holds; not read when <paramref name="Void"/>.</param>
/// <param name="Void">Whether the server took the spend back (a void).</param>
internal readonly record struct Confirmed(string Id, long N, bool Void);

/// <summary>One change to the journal or its figures, about the activation it names.</summary>
internal abstract record JournalOp;

/// <summary>
/// A spend made with no request sent (<see cref="SpendKind.Offline"/>), or whose own request got no answer
/// (<see cref="SpendKind.Unanswered"/>), or one refused after that (<see cref="SpendKind.Void"/>). A spend carries
/// the most its meter's unsynced spends may come to with it (<paramref name="Limit"/>, the smaller of the device's
/// allowance and what is left), judged again where it lands. <paramref name="Told"/>: the customer was told the
/// spend was made while a failed save kept it in memory, so it lands whatever the limit and the cap say: a spend is
/// never taken back. Not read on a void, which is the take-back of a spend the customer was refused, never one
/// told: a void is always held to the void cap and to the journal's.
/// </summary>
/// <param name="Kind">Which kind of entry it opens or grows.</param>
/// <param name="About">The activation it is about.</param>
/// <param name="Id">The spend's id.</param>
/// <param name="Meter">The meter's code.</param>
/// <param name="N">How many uses.</param>
/// <param name="At">When (the guarded clock) the spend was made.</param>
/// <param name="Limit">The most the meter's unsynced spends may come to with it.</param>
/// <param name="Told">Whether the customer was told it was made while only memory could hold it.</param>
internal sealed record SpendOp(SpendKind Kind, string About, string Id, string Meter, long N, long At, long? Limit = null, bool Told = false) : JournalOp;

/// <summary>An answer's <c>spends</c>: the entries the server recorded.</summary>
/// <param name="About">The activation it is about.</param>
/// <param name="Entries">What the server named.</param>
internal sealed record ConfirmedOp(string About, IReadOnlyList<Confirmed> Entries) : JournalOp;

/// <summary>The figures a <c>use</c> answer gave, by code.</summary>
/// <param name="About">The activation it is about.</param>
/// <param name="Figures">The figures.</param>
internal sealed record FiguresOp(string About, IReadOnlyDictionary<string, MeterFigure> Figures) : JournalOp;

/// <summary>An activation, saved: another seat's journal is dropped, the same seat's is kept.</summary>
/// <param name="About">The activation saved.</param>
internal sealed record ActivatedOp(string About) : JournalOp;

/// <summary>A deactivation: the journal goes with the rest of the licence.</summary>
internal sealed record DeactivatedOp : JournalOp;

/// <summary>The fields after an op; <paramref name="Refused"/> is true when it was a spend the full journal could not take, or its limit forbids.</summary>
/// <param name="Spends">The journal.</param>
/// <param name="MeterFigures">The figures.</param>
/// <param name="Refused">Whether an append was refused.</param>
internal sealed record Journaled(IReadOnlyList<SpendEntry> Spends, IReadOnlyDictionary<string, MeterFigure> MeterFigures, bool Refused);

/// <summary>
/// How the journal of unsynced spends (<see cref="StoredState.Spends"/>) and the figures beside it
/// (<see cref="StoredState.MeterFigures"/>) land on the stored state: on the state as stored at that moment, as
/// <see cref="Offline.Merged"/> lands the rest, so that two processes of one app never lose each other's spends. Both
/// fields are about the stored activation: a save made for another activation, or on a state with none, writes neither.
/// </summary>
internal static class Journal
{
    private static readonly IReadOnlyDictionary<string, MeterFigure> NoFigures = new Dictionary<string, MeterFigure>();

    /// <summary>Whether an answer's confirmation names a stored entry as recorded. A void is named by <c>void</c>, any other by how many the server holds.</summary>
    private static bool Confirms(SpendEntry entry, IReadOnlyList<Confirmed> confirmed) =>
        confirmed.Any(c => string.Equals(c.Id, entry.Id, StringComparison.Ordinal)
            && (c.Void ? entry.Kind == SpendKind.Void : entry.Kind != SpendKind.Void && entry.N <= c.N));

    /// <summary><paramref name="stored"/> with the incoming figures, for each meter the newer by <c>At</c> kept (the stored one on a tie).</summary>
    private static Dictionary<string, MeterFigure> Newest(IReadOnlyDictionary<string, MeterFigure> stored, IReadOnlyDictionary<string, MeterFigure> incoming)
    {
        var merged = new Dictionary<string, MeterFigure>(stored, StringComparer.Ordinal);
        foreach (var (code, figure) in incoming)
        {
            if (!merged.TryGetValue(code, out var held) || figure.At > held.At) merged[code] = figure;
        }
        return merged;
    }

    /// <summary>The index of the void a new void takes the place of: the oldest, when the journal already holds <see cref="Meters.MaxPendingVoids"/>; -1 for none.</summary>
    private static int VoidToReplace(List<SpendEntry> journal)
    {
        var voids = 0;
        var oldest = -1;
        for (var i = 0; i < journal.Count; i++)
        {
            if (journal[i].Kind != SpendKind.Void) continue;
            if (oldest < 0) oldest = i;
            voids++;
        }
        return voids >= Meters.MaxPendingVoids ? oldest : -1;
    }

    /// <summary>Whether a spend was told to the customer: so it lands whatever the limit and the cap say. A void never is.</summary>
    private static bool ToldOf(SpendOp op) => op.Kind != SpendKind.Void && op.Told;

    /// <summary>
    /// The journal an entry is opened in: for a void, without the oldest void when <see cref="Meters.MaxPendingVoids"/> are held
    /// (the new one takes its place), so the attempts of a device out of reach never fill the journal the spends it can make
    /// need. Whether it has room: a new entry past the cap is refused, unless it was told.
    /// </summary>
    private static (List<SpendEntry> Spends, bool Refused) Opened(List<SpendEntry> journal, SpendOp op)
    {
        var replaced = op.Kind == SpendKind.Void ? VoidToReplace(journal) : -1;
        var room = replaced < 0 ? journal : journal.Where((_, i) => i != replaced).ToList();
        if (room.Count >= Meters.MaxPendingSpends && !ToldOf(op)) return (journal, true);
        return ([.. room, new SpendEntry(op.Id, op.Meter, op.N, op.At, op.Kind)], false);
    }

    /// <summary>
    /// The journal with an append applied: an <c>Offline</c> entry grows or is opened, the others are opened once by id;
    /// none that would take the meter's unsynced spends past the op's limit (a void is no spend, and a told one lands
    /// whatever it says), nor a new entry past the cap (<see cref="Opened"/>). An offline spend that joins an entry loses
    /// its id, which is why no request reports a spend still held in memory.
    /// </summary>
    private static (List<SpendEntry> Spends, bool Refused) Appended(List<SpendEntry> journal, SpendOp op)
    {
        // A spend whose id the journal holds is the one it is: left as it is, whatever the limit now says.
        if (op.Kind != SpendKind.Offline && journal.Any(e => string.Equals(e.Id, op.Id, StringComparison.Ordinal))) return (journal, false);
        var limit = op.Kind != SpendKind.Void && !ToldOf(op) ? op.Limit : null;
        if (limit is { } most && Meters.UnsyncedOf(journal, op.Meter) + op.N > most) return (journal, true);
        var open = op.Kind == SpendKind.Offline
            ? journal.FindIndex(e => e.Kind == SpendKind.Offline && string.Equals(e.Meter, op.Meter, StringComparison.Ordinal))
            : -1;
        if (open >= 0)
        {
            var grown = journal.ToList();
            grown[open] = grown[open] with { N = grown[open].N + op.N };
            return (grown, false);
        }
        return Opened(journal, op);
    }

    /// <summary>
    /// The journal and the figures after <paramref name="op"/>, landed on <paramref name="latest"/> at <paramref name="now"/>
    /// (the guarded clock). An op about another activation than the one stored, or about none, changes nothing; a
    /// deactivation clears both, and an activation of another seat does (the same seat activated again keeps them). Every
    /// op that lands drops the entries opened more than <see cref="Meters.PendingMaxMs"/> before <paramref name="now"/>,
    /// unreported. An append the journal has no room for is refused.
    /// </summary>
    public static Journaled Landed(StoredState latest, JournalOp op, long now)
    {
        var spends = (IReadOnlyList<SpendEntry>)(latest.Spends ?? []);
        var figures = latest.MeterFigures ?? NoFigures;
        if (op is DeactivatedOp) return new Journaled([], NoFigures, false);
        var about = op switch
        {
            SpendOp s => s.About,
            ConfirmedOp c => c.About,
            FiguresOp f => f.About,
            ActivatedOp a => a.About,
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        if (latest.ActivationId is null || !string.Equals(latest.ActivationId, about, StringComparison.Ordinal))
        {
            return op is ActivatedOp ? new Journaled([], NoFigures, false) : new Journaled(spends, figures, false);
        }
        var live = spends.Where(e => now - e.At <= Meters.PendingMaxMs).ToList();
        switch (op)
        {
            case ActivatedOp:
                return new Journaled(live, figures, false);
            case ConfirmedOp confirmed:
                return new Journaled(live.Where(e => !Confirms(e, confirmed.Entries)).ToList(), figures, false);
            case FiguresOp answered:
                return new Journaled(live, Newest(figures, answered.Figures), false);
            default:
                var landed = Appended(live, (SpendOp)op);
                return new Journaled(landed.Spends, figures, landed.Refused);
        }
    }

    /// <summary>Confirmations with each id once as a count and once as a void, the larger count kept: the entries any of them would have dropped.</summary>
    private static List<Confirmed> FoldedConfirmations(IEnumerable<Confirmed> entries)
    {
        var order = new List<string>();
        var folded = new Dictionary<string, Confirmed>(StringComparer.Ordinal);
        foreach (var c in entries)
        {
            var key = (c.Void ? "void " : "count ") + c.Id;
            if (!folded.TryGetValue(key, out var held))
            {
                order.Add(key);
                folded[key] = c;
            }
            else if (!c.Void && c.N > held.N)
            {
                folded[key] = c;
            }
        }
        return order.Select(key => folded[key]).ToList();
    }

    /// <summary>
    /// <paramref name="ops"/> with neighbours folded together where landing them one after the other lands what landing the
    /// fold does: offline spends of one meter (their counts added, the first's id and time kept), figures (the newer of each
    /// meter) and confirmations (each id once, the larger count). A disk that keeps refusing the journal then costs the
    /// process a short list, however many spends it holds.
    /// </summary>
    public static List<JournalOp> Compacted(IEnumerable<JournalOp> ops)
    {
        var folded = new List<JournalOp>();
        foreach (var op in ops)
        {
            var last = folded.Count > 0 ? folded[^1] : null;
            if (last is SpendOp { Kind: SpendKind.Offline } before && op is SpendOp { Kind: SpendKind.Offline } next
                && before.About == next.About && before.Meter == next.Meter)
            {
                folded[^1] = before with { N = before.N + next.N };
            }
            else if (last is FiguresOp earlier && op is FiguresOp later && earlier.About == later.About)
            {
                folded[^1] = earlier with { Figures = Newest(earlier.Figures, later.Figures) };
            }
            else if (last is ConfirmedOp first && op is ConfirmedOp second && first.About == second.About)
            {
                folded[^1] = first with { Entries = FoldedConfirmations(first.Entries.Concat(second.Entries)) };
            }
            else
            {
                folded.Add(op);
            }
        }
        return folded;
    }

    /// <summary><paramref name="ops"/> with all but the newest <see cref="Meters.MaxPendingVoids"/> void ops dropped: a process that cannot save keeps no more voids than the journal would.</summary>
    public static List<JournalOp> BoundedVoids(IEnumerable<JournalOp> ops)
    {
        var all = ops.ToList();
        var voids = all.Where(op => op is SpendOp { Kind: SpendKind.Void }).ToList();
        var dropped = new HashSet<JournalOp>(voids.Take(Math.Max(0, voids.Count - Meters.MaxPendingVoids)), ReferenceEqualityComparer.Instance);
        return all.Where(op => !dropped.Contains(op)).ToList();
    }

    /// <summary>A state with the journal fields <paramref name="journaled"/> gives: an empty journal or no figures is the field absent.</summary>
    public static StoredState WithJournal(StoredState state, Journaled journaled) => StateFields.WithJournal(state, journaled.Spends, journaled.MeterFigures);

    /// <summary><paramref name="ops"/> landed in order on <paramref name="state"/> at <paramref name="now"/>, and whether each was refused; the state itself when there are none.</summary>
    public static (StoredState State, bool[] Refused) Land(StoredState state, IReadOnlyList<JournalOp> ops, long now)
    {
        var current = state;
        var refused = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            var landed = Landed(current, ops[i], now);
            refused[i] = landed.Refused;
            current = WithJournal(current, landed);
        }
        return (current, refused);
    }
}
