using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using static KeyGrant.Licensing.Tests.Support.Vectors;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>Metered-uses vectors' JSON to the SDK's types and back, read explicitly (not through the state file's own reader).</summary>
internal static class MeterSupport
{
    /// <summary>Base figures as the vectors write them: a code to <c>{left, total, offline, resets}</c>.</summary>
    public static Dictionary<string, BaseFigure> Figures(JsonNode? node)
    {
        var figures = new Dictionary<string, BaseFigure>(StringComparer.Ordinal);
        if (node is null) return figures;
        foreach (var (code, f) in node.AsObject()) figures[code] = new BaseFigure(Long(f!["left"])!.Value, Long(f["total"])!.Value, Long(f["offline"])!.Value, Bool(f["resets"]) == true);
        return figures;
    }

    /// <summary>Stored figures as the vectors write them: base figures with the server's time (<c>at</c>).</summary>
    public static Dictionary<string, MeterFigure> StoredFigures(JsonNode? node)
    {
        var figures = new Dictionary<string, MeterFigure>(StringComparer.Ordinal);
        if (node is null) return figures;
        foreach (var (code, f) in node.AsObject())
        {
            figures[code] = new MeterFigure(Long(f!["left"])!.Value, Long(f["total"])!.Value, Long(f["offline"])!.Value, Bool(f["resets"]) == true, Long(f["at"])!.Value);
        }
        return figures;
    }

    /// <summary>A journal as the vectors write it.</summary>
    public static List<SpendEntry> Spends(JsonNode? node) =>
        node is null ? [] : node.AsArray().Select(e => new SpendEntry(Str(e!["id"])!, Str(e["meter"])!, Long(e["n"])!.Value, Long(e["at"])!.Value, Names.SpendKindOf(Str(e["kind"]))!.Value)).ToList();

    /// <summary>A journal as the vectors write it, from the SDK's.</summary>
    public static JsonArray SpendsJson(IEnumerable<SpendEntry> spends) => new(spends.Select(e => (JsonNode?)new JsonObject
    {
        ["id"] = e.Id,
        ["meter"] = e.Meter,
        ["n"] = e.N,
        ["at"] = e.At,
        ["kind"] = Names.Of(e.Kind),
    }).ToArray());

    /// <summary>An answer as the vectors write it: null when there was none at all.</summary>
    public static HttpResult? Response(JsonNode? node) =>
        node is null ? null : new HttpResult((int)Long(node["status"])!.Value, node["json"]?.DeepClone());

    /// <summary>The reports an answer confirmed, as the vectors write them.</summary>
    public static JsonArray ConfirmedJson(IEnumerable<Confirmed> confirmed) => new(confirmed
        .Select(c => (JsonNode?)(c.Void ? new JsonObject { ["id"] = c.Id, ["void"] = true } : new JsonObject { ["id"] = c.Id, ["n"] = c.N }))
        .ToArray());

    public static string OutcomeName(UseOutcomeKind kind) => kind switch
    {
        UseOutcomeKind.Spent => "spent",
        UseOutcomeKind.UsedUp => "used-up",
        UseOutcomeKind.NoMeter => "no-meter",
        UseOutcomeKind.Refused => "refused",
        UseOutcomeKind.Unanswered => "unanswered",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>One change to the journal as the vectors write it.</summary>
    public static JournalOp Op(JsonNode op)
    {
        var about = Str(op["about"]);
        switch (Str(op["kind"]))
        {
            case "confirmed":
                return new ConfirmedOp(about!, op["entries"]!.AsArray().Select(e => Bool(e!["void"]) == true ? new Confirmed(Str(e["id"])!, 0, true) : new Confirmed(Str(e["id"])!, Long(e["n"])!.Value, false)).ToList());
            case "figures":
                return new FiguresOp(about!, StoredFigures(op["figures"]));
            case "activated":
                return new ActivatedOp(about!);
            case "deactivated":
                return new DeactivatedOp();
            default:
                return new SpendOp(Names.SpendKindOf(Str(op["kind"]))!.Value, about!, Str(op["id"])!, Str(op["meter"])!, Long(op["n"])!.Value, Long(op["at"])!.Value, Long(op["limit"]), Bool(op["told"]) == true);
        }
    }
}
