using System.Collections.ObjectModel;

namespace KeyGrant.Licensing.Internal;

/// <summary>What <c>UseAsync(meter, n)</c> asks for, or why it asks nothing (<see cref="Meters.AskedFor"/>).</summary>
/// <param name="Meter">The meter's code; empty when refused.</param>
/// <param name="N">How many uses; 0 when refused.</param>
/// <param name="Refused">Why nothing is asked, when it is not.</param>
internal readonly record struct UseAsked(string Meter, long N, UseError? Refused);

/// <summary>
/// Metered uses, decided without I/O: what <c>UseAsync</c> may ask for, the figures a device holds, what it may
/// spend while it gets no answer, and the figures a status shows. When it asks the server and what an answer
/// does are <c>Meters.Asks.cs</c> and <c>Meters.Answers.cs</c>; how the journal lands on the stored state is
/// <see cref="Journal"/>.
/// <para>A meter never takes a licence away: nothing here is read to decide whether the app is licensed.</para>
/// </summary>
internal static partial class Meters
{
    /// <summary>The most uses one spend may ask for, and the most a figure of a meter is.</summary>
    public const long MaxMeterUses = 1_000_000_000;

    /// <summary>The most entries the journal holds, and a report carries (the server reads no more).</summary>
    public const int MaxPendingSpends = 64;

    /// <summary>
    /// The most <c>void</c> entries the journal holds: a void is the take-back of a spend that was refused after it got no
    /// answer, and a device out of reach that keeps trying a spend its allowance cannot cover would otherwise fill the
    /// journal, leaving no room for the spends it can make. A new void past them takes the place of the oldest.
    /// </summary>
    public const int MaxPendingVoids = 16;

    /// <summary>How long an entry is kept unreported (ms): twice the longest lease. The tenant loses a report, never the customer a use.</summary>
    public const long PendingMaxMs = 180L * 86_400_000;

    /// <summary>How long a <c>use</c> request may take (ms): a person waits on it, so a status check's bound (the HTTP timeout when that is shorter).</summary>
    public const long UseTimeoutMs = Wire.StatusCheckTimeoutMs;

    /// <summary>The longest meter code a call may name, in UTF-16 units: an entitlement's name.</summary>
    public const int MaxMeterCode = 32;

    // --- what was asked ---------------------------------------------------------

    /// <summary>
    /// How many uses a call spends: a whole number from 1 to <see cref="MaxMeterUses"/> is itself; anything else
    /// (0, a negative, past the bound) is none (<see cref="UseError.InvalidAmount"/>).
    /// </summary>
    public static long? UseAmount(long n) => n is >= 1 and <= MaxMeterUses ? n : null;

    /// <summary>A meter's code as a call names it: 1 to 32 UTF-16 units, matched exactly; anything else is none (<see cref="UseError.NoMeter"/>).</summary>
    public static string? MeterCode(string? code) => code is { Length: >= 1 and <= MaxMeterCode } ? code : null;

    /// <summary>What <c>UseAsync(meter, n)</c> asks for, or why it asks nothing: the amount is judged before the code.</summary>
    public static UseAsked AskedFor(string? meter, long n)
    {
        if (UseAmount(n) is not { } amount) return new UseAsked("", 0, UseError.InvalidAmount);
        return MeterCode(meter) is { } code ? new UseAsked(code, amount, null) : new UseAsked("", 0, UseError.NoMeter);
    }

    /// <summary>A spend's id: 16 bytes from the platform's secure random source, base64url with no padding (22 characters), never reused.</summary>
    public static string NewSpendId() => Base64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    // --- the figures --------------------------------------------------------------

    /// <summary>Uses this device spent that KeyGrant has not heard of: the <c>N</c> of a meter's <c>Offline</c> and <c>Unanswered</c> entries, never a <c>Void</c>.</summary>
    public static long UnsyncedOf(IReadOnlyList<SpendEntry>? spends, string meter)
    {
        long sum = 0;
        if (spends is null) return sum;
        foreach (var e in spends)
        {
            if (e.Kind != SpendKind.Void && string.Equals(e.Meter, meter, StringComparison.Ordinal)) sum += e.N;
        }
        return sum;
    }

    /// <summary>
    /// The base figures of each meter: its stored figure (a <c>use</c> answer's) when that figure is at or after the
    /// lease's own second (<paramref name="iat"/>, seconds), else the lease's <c>met</c> member, else none, and the meter
    /// is unknown on this device. A meter the newest lease no longer names, and no newer answer names, is gone.
    /// </summary>
    public static IReadOnlyDictionary<string, BaseFigure> FiguresOf(
        IReadOnlyDictionary<string, BaseFigure>? met,
        long iat,
        IReadOnlyDictionary<string, MeterFigure>? stored)
    {
        var figures = new Dictionary<string, BaseFigure>(StringComparer.Ordinal);
        if (met is not null)
        {
            foreach (var (code, leased) in met)
            {
                figures[code] = stored is not null && stored.TryGetValue(code, out var answered) && Newer(answered, iat) ? BaseFigure.Of(answered) : leased;
            }
        }
        if (stored is not null)
        {
            foreach (var (code, answered) in stored)
            {
                if (!figures.ContainsKey(code) && Newer(answered, iat)) figures[code] = BaseFigure.Of(answered);
            }
        }
        return figures;
    }

    /// <summary>Whether a stored figure is at or after the lease's own second: one from that second counts as newer (it is at worst a second's spends behind).</summary>
    private static bool Newer(MeterFigure figure, long iat) => figure.At >= (double)iat * 1000;

    /// <summary>The base figures of the lease that licenses this device and the answers stored beside it.</summary>
    public static IReadOnlyDictionary<string, BaseFigure> FiguresFor(LeaseClaims claims, StoredState state) =>
        FiguresOf(claims.Met, claims.Iat, state.MeterFigures);

    /// <summary><c>status().Meters</c>: each meter with base figures, its <c>Left</c> net of this device's unsynced spends and never below 0.</summary>
    public static IReadOnlyDictionary<string, MeterStatus> MeterStatusOf(IReadOnlyDictionary<string, BaseFigure> figures, IReadOnlyList<SpendEntry>? spends)
    {
        var shown = new Dictionary<string, MeterStatus>(StringComparer.Ordinal);
        foreach (var (code, f) in figures)
        {
            var unsynced = UnsyncedOf(spends, code);
            shown[code] = new MeterStatus(Math.Max(0, f.Left - unsynced), f.Total, f.Offline, unsynced, f.Resets);
        }
        return new ReadOnlyDictionary<string, MeterStatus>(shown);
    }

    /// <summary>
    /// A <c>Licensed</c> status from a key's lease with its meters (<see cref="MeterStatusOf"/>); any other status, and a
    /// lease with no meter, unchanged.
    /// </summary>
    public static LicenseStatus Metered(LicenseStatus status, LeaseClaims claims, StoredState state)
    {
        if (status.State != LicenseState.Licensed) return status;
        var meters = MeterStatusOf(FiguresFor(claims, state), state.Spends);
        return meters.Count == 0 ? status : status with { Meters = meters };
    }

    // --- a spend with no answer -----------------------------------------------------

    /// <summary>What a spend with no answer comes to (<see cref="OfflineSpend"/>).</summary>
    /// <param name="Allowed">Whether the allowance covers it.</param>
    /// <param name="Error"><see cref="UseError.NoMeter"/> for a meter with no figure, <see cref="UseError.OfflineLimit"/> for a spend past the allowance; none when allowed.</param>
    /// <param name="Left">When allowed, what is left after the spend; when refused for the limit, what is left now (never below 0).</param>
    /// <param name="Limit">When allowed, the most the meter's unsynced spends may come to with it (the smaller of the allowance and what is left), which the landing judges again.</param>
    internal readonly record struct OfflineVerdict(bool Allowed, UseError? Error, long Left, long Limit);

    /// <summary>
    /// Whether a spend with no answer is allowed, from the meter's base figures and the journal: its unsynced spends with
    /// this one at most the figure's <c>Offline</c> allowance and at most its <c>Left</c>. A meter with no base figure is
    /// <see cref="UseError.NoMeter"/>.
    /// </summary>
    public static OfflineVerdict OfflineSpend(IReadOnlyDictionary<string, BaseFigure> figures, IReadOnlyList<SpendEntry>? spends, string meter, long n)
    {
        if (!figures.TryGetValue(meter, out var f)) return new OfflineVerdict(false, UseError.NoMeter, 0, 0);
        var unsynced = UnsyncedOf(spends, meter);
        var left = Math.Max(0, f.Left - unsynced);
        if (unsynced + n <= f.Offline && n <= left) return new OfflineVerdict(true, null, left - n, Math.Min(f.Offline, f.Left));
        return new OfflineVerdict(false, UseError.OfflineLimit, left, 0);
    }
}
