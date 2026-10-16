using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeyGrant.Licensing.Internal;

/// <summary>What a <c>use</c> answer comes to (<see cref="Meters.OutcomeOf"/>).</summary>
internal enum UseOutcomeKind
{
    /// <summary>A 200 with <c>spent: true</c>.</summary>
    Spent,

    /// <summary>Fewer left than asked.</summary>
    UsedUp,

    /// <summary>The server knows no such meter.</summary>
    NoMeter,

    /// <summary>The server's verdict on the key, this build or this device.</summary>
    Refused,

    /// <summary>No usable answer: the spend comes from the offline allowance, in doubt when it was sent.</summary>
    Unanswered,
}

/// <summary>What a <c>use</c> answer does.</summary>
/// <param name="Kind">What the answer comes to.</param>
/// <param name="Refusal">The refusal, when the server's verdict was one.</param>
/// <param name="Left">What the answer says is left, for <see cref="UseOutcomeKind.UsedUp"/>.</param>
/// <param name="Patch">What is saved as the answer to the ask.</param>
/// <param name="Figures">The figures saved (the newer by <c>At</c> is kept).</param>
/// <param name="Confirmed">The reports the answer confirms.</param>
internal sealed record UseOutcome(
    UseOutcomeKind Kind,
    Refusal? Refusal,
    long? Left,
    StatePatch Patch,
    IReadOnlyDictionary<string, MeterFigure> Figures,
    IReadOnlyList<Confirmed> Confirmed);

// What a request reports of the journal, the reports an answer confirms, and what a `use` answer saves and says.
internal static partial class Meters
{
    private static readonly IReadOnlyDictionary<string, MeterFigure> NoFigures = new Dictionary<string, MeterFigure>();

    // --- reports ----------------------------------------------------------------

    /// <summary>
    /// The report a body carries: the journal's entries, oldest first, at most <see cref="MaxPendingSpends"/>, a void marked;
    /// null for an empty journal (the member is absent, never an empty list).
    /// </summary>
    public static JsonArray? SpendsReported(IEnumerable<SpendEntry> spends)
    {
        var report = new JsonArray();
        foreach (var e in spends.Take(MaxPendingSpends))
        {
            var entry = new JsonObject { ["id"] = e.Id, ["meter"] = e.Meter, ["n"] = e.N, ["at"] = e.At };
            if (e.Kind == SpendKind.Void) entry["void"] = true;
            report.Add(entry);
        }
        return report.Count > 0 ? report : null;
    }

    /// <summary>
    /// An answer's body as the members of a JSON object, the last of a repeated name kept as <c>JSON.parse</c> keeps it, and
    /// held to the depth <see cref="TopLevelJson.ElementDepth"/>; null when it is not an object, or cannot be read.
    /// </summary>
    internal static Dictionary<string, JsonElement>? ObjectMembers(JsonNode? json)
    {
        if (json is not JsonObject) return null;
        try
        {
            using var document = JsonDocument.Parse(json.ToJsonString(Json.Options), new JsonDocumentOptions { MaxDepth = TopLevelJson.ElementDepth });
            return Json.Properties(document.RootElement).ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsTrue(Dictionary<string, JsonElement>? members, string name) =>
        members is not null && members.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// The reports an answer names as recorded (<c>spends</c>), read leniently: a member of another shape confirms nothing. A
    /// void names itself (its <c>void</c> is JSON <c>true</c>; its <c>n</c> is then not read); any other names how many the
    /// server holds (a whole number from 1 to 2^53 - 1). No answer at all, a 5xx and a 429 confirm nothing, whatever their body says.
    /// </summary>
    public static IReadOnlyList<Confirmed> SpendsConfirmed(HttpResult? response)
    {
        if (response is null || Wire.NoAnswer(response.Status)) return [];
        var body = ObjectMembers(response.Json);
        if (body is null || !body.TryGetValue("spends", out var listed) || listed.ValueKind != JsonValueKind.Array) return [];
        var confirmed = new List<Confirmed>();
        foreach (var item in listed.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var member = Json.Properties(item).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (!member.TryGetValue("id", out var idElement) || Json.StringOf(idElement) is not { } id) continue;
            if (IsTrue(member, "void")) confirmed.Add(new Confirmed(id, 0, true));
            else if (member.TryGetValue("n", out var n) && Json.SafeInteger(n) is { } count and >= 1) confirmed.Add(new Confirmed(id, count, false));
        }
        return confirmed;
    }

    // --- what an answer does -------------------------------------------------------

    /// <summary>The figures a <c>use</c> answer gives, each stamped with the server's time (<c>at</c>); none for an answer without a whole <c>at</c>.</summary>
    private static IReadOnlyDictionary<string, MeterFigure> AnsweredFigures(Dictionary<string, JsonElement>? body)
    {
        if (body is null || !body.TryGetValue("at", out var atElement) || Json.SafeInteger(atElement) is not { } at || at < 0) return NoFigures;
        if (!body.TryGetValue("meters", out var meters)) return NoFigures;
        return ClaimsParser.MetersOf(meters.GetRawText()).ToDictionary(p => p.Key, p => new MeterFigure(p.Value.Left, p.Value.Total, p.Value.Offline, p.Value.Resets, at), StringComparer.Ordinal);
    }

    /// <summary>An answer that saves and confirms nothing.</summary>
    private static UseOutcome Nothing(UseOutcomeKind kind, StatePatch? patch = null) => new(kind, null, null, patch ?? new StatePatch(), NoFigures, []);

    /// <summary>
    /// What a <c>use</c> answer saves and says. No answer at all: the wait (a minute) and the device's kind. A 5xx or a 429, and
    /// any answer that is neither a spend nor a recognised word: nothing, the offline path. A 200 is a spend only with
    /// <c>spent: true</c>. On any other status, <c>used-up</c>, <c>unknown-meter</c> and a validate's verdicts
    /// (<see cref="Wire.RefusalOf"/>, stored as a validate's are) are read; each saves the reports it confirms, and a verdict its refusal.
    /// </summary>
    /// <param name="response">The answer; null when there was none at all (the request threw or timed out).</param>
    /// <param name="askedAt">When the request was made, by the device clock (ms): where the wait before asking again starts.</param>
    /// <param name="major">This build's major, sent with the request.</param>
    /// <param name="device">Who this device is, as the request read it.</param>
    public static UseOutcome OutcomeOf(HttpResult? response, long askedAt, int major, Device device)
    {
        var keyed = Offline.KeyedBy(device);
        if (response is null)
        {
            var wait = new StatePatch { [StateField.BackoffSince] = askedAt, [StateField.BackoffMs] = Offline.RetryUnansweredMs }.With(keyed);
            return Nothing(UseOutcomeKind.Unanswered, wait);
        }
        if (Wire.NoAnswer(response.Status)) return Nothing(UseOutcomeKind.Unanswered);
        var body = ObjectMembers(response.Json);
        if (response.Status == 200)
        {
            if (!IsTrue(body, "spent")) return Nothing(UseOutcomeKind.Unanswered);
            return new UseOutcome(UseOutcomeKind.Spent, null, null, new StatePatch(), AnsweredFigures(body), SpendsConfirmed(response));
        }
        var error = Wire.ErrorOf(response.Json);
        var confirmed = SpendsConfirmed(response);
        if (error == "used-up")
        {
            long? left = body is not null && body.TryGetValue("left", out var leftElement) && Json.SafeInteger(leftElement) is { } l and >= 0 ? l : null;
            return new UseOutcome(UseOutcomeKind.UsedUp, null, left, new StatePatch(), AnsweredFigures(body), confirmed);
        }
        if (error == "unknown-meter") return new UseOutcome(UseOutcomeKind.NoMeter, null, null, new StatePatch(), NoFigures, confirmed);
        if (Wire.RefusalOf(error) is not { } refused) return Nothing(UseOutcomeKind.Unanswered);
        var verdict = new StatePatch { [StateField.CheckedMajor] = major, [StateField.BackoffSince] = askedAt, [StateField.BackoffMs] = Offline.AskAgainMs }
            .With(keyed)
            .With(Offline.RefusalPatch(refused, major, device.OwnClaim));
        return new UseOutcome(UseOutcomeKind.Refused, refused, null, verdict, NoFigures, confirmed);
    }
}
