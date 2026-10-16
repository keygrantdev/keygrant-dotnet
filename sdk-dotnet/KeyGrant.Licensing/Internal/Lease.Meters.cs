using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace KeyGrant.Licensing.Internal;

/// <summary>A meter's figures without a time: as a lease signs them or an answer gives them.</summary>
/// <param name="Left">What the key had left.</param>
/// <param name="Total">The uses it holds.</param>
/// <param name="Offline">The allowance of each device.</param>
/// <param name="Resets">Whether the meter comes back each billing period.</param>
internal readonly record struct BaseFigure(long Left, long Total, long Offline, bool Resets)
{
    public static BaseFigure Of(MeterFigure figure) => new(figure.Left, figure.Total, figure.Offline, figure.Resets);
}

// The lease claims' meters (`met`), read leniently.
internal static partial class ClaimsParser
{
    /// <summary>The largest whole number a double holds exactly (2^53 - 1): every SDK draws the line there.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>
    /// <c>met</c> read leniently: an object's members that are each an object whose <c>left</c>, <c>total</c> and
    /// <c>offline</c> are each a non-negative whole number of at most 2^53 - 1 (a JSON number with no fractional part
    /// however it is written: <c>4.0</c> is 4, <c>-0</c> is 0; one out of a double's range reads as infinity and drops
    /// its member), with <c>resets</c> true only for JSON <c>true</c>. Any other member is dropped, never the lease
    /// and never the meters beside it; anything but an object is no meters. Names are kept as they are
    /// (<c>__proto__</c> included) and a meter's other members are ignored. The server's caps are not enforced: the
    /// signature is the server's word, and a later server may raise them without locking out a build made before.
    /// </summary>
    /// <param name="met">The claim as the payload's reader holds it; null when it is absent.</param>
    /// <returns>The meters by code; empty for none.</returns>
    public static IReadOnlyDictionary<string, BaseFigure> MetersOf(RawJson? met) => met is { } raw ? MetersOf(raw.Text) : None;

    /// <summary><see cref="MetersOf(RawJson?)"/> of a JSON text (a lease's claim, or a <c>use</c> answer's <c>meters</c>); no meters when it is not JSON.</summary>
    /// <param name="text">The JSON text.</param>
    /// <returns>The meters by code; empty for none.</returns>
    public static IReadOnlyDictionary<string, BaseFigure> MetersOf(string text)
    {
        TopLevelJson top;
        try
        {
            top = TopLevelJson.Read(text);
        }
        catch (JsonException)
        {
            return None;
        }
        if (top.Kind != JsonValueKind.Object) return None;
        var read = new Dictionary<string, BaseFigure>(StringComparer.Ordinal);
        foreach (var (name, value) in top.Members)
        {
            if (MeterOf(value) is { } figure) read[name] = figure;
        }
        return new ReadOnlyDictionary<string, BaseFigure>(read);
    }

    private static readonly IReadOnlyDictionary<string, BaseFigure> None = ReadOnlyDictionary<string, BaseFigure>.Empty;

    /// <summary>A meter: an object with the three figures, or null for anything else.</summary>
    private static BaseFigure? MeterOf(RawJson value)
    {
        // A scalar is no object; a list or an object reads on.
        if (value.Depth == 0) return null;
        var meter = TopLevelJson.Read(value.Text);
        if (meter.Kind != JsonValueKind.Object) return null;
        long? left = null;
        long? total = null;
        long? offline = null;
        var resets = false;
        foreach (var (name, member) in meter.Members)
        {
            if (member.Depth != 0) continue;
            switch (name)
            {
                case "left": left = FigureOf(member); break;
                case "total": total = FigureOf(member); break;
                case "offline": offline = FigureOf(member); break;
                case "resets": resets = member.Element().ValueKind == JsonValueKind.True; break;
            }
        }
        return left is { } l && total is { } t && offline is { } o ? new BaseFigure(l, t, o, resets) : null;
    }

    /// <summary>A figure: a JSON number that is a non-negative whole number a double holds exactly; null otherwise.</summary>
    private static long? FigureOf(RawJson value)
    {
        var element = value.Element();
        if (element.ValueKind != JsonValueKind.Number) return null;
        // Read as a double, as JSON.parse reads it: 4.0 is 4, 1e1 is 10, -0 is 0, 1e400 is infinity (no figure).
        var d = double.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(d) && d >= 0 && Math.Floor(d) == d && d <= MaxSafeInteger ? (long)d : null;
    }
}
