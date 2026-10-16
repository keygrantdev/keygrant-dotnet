using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>What the server did with one request.</summary>
/// <param name="Status">The status it answered.</param>
/// <param name="Json">The body it answered.</param>
/// <param name="Spent">The id of a spend of its own that this request recorded: a <c>use</c> that landed.</param>
internal sealed record Served(int Status, JsonObject Json, string? Spent = null);

/// <summary>
/// The server a property test of <c>UseAsync</c> runs the real <see cref="License"/> against: what the KeyGrant API does with a
/// request that carries a spend or a report of unsynced ones, as the contract says: every report lands once by its id, an
/// entry that grew adds what it grew by, a void takes an online spend or a report back and keeps its id from counting later,
/// and an answer names the entries it holds. Test support only.
/// </summary>
internal sealed class PropServer
{
    /// <summary>What it holds by spend id: an online spend or a report, at the count it has.</summary>
    public sealed class Held(string meter, long n)
    {
        public string Meter { get; } = meter;
        public long N { get; set; } = n;
    }

    private readonly TestSigner signer;
    private readonly Dictionary<string, string> leases = new(StringComparer.Ordinal);
    private int activations;

    public PropServer(IReadOnlyDictionary<string, MeterSpec> meters, TestSigner signer, long now)
    {
        Meters = meters;
        this.signer = signer;
        Now = now;
    }

    public IReadOnlyDictionary<string, MeterSpec> Meters { get; }

    public Dictionary<string, Held> Recorded { get; } = new(StringComparer.Ordinal);

    /// <summary>The ids a void took back, or was the first to name.</summary>
    public HashSet<string> Voided { get; } = new(StringComparer.Ordinal);

    /// <summary>The server's time (ms): it moves on by a millisecond with each request, so no two answers carry one time.</summary>
    public long Now { get; set; }

    /// <summary>What a meter has used: its recorded spends, whatever the allowance.</summary>
    public long Used(string code) => Recorded.Values.Where(h => h.Meter == code).Sum(h => h.N);

    public long Left(string code) => Meters.TryGetValue(code, out var spec) ? Math.Max(0, spec.Total - Used(code)) : 0;

    private JsonObject Figures()
    {
        var figures = new JsonObject();
        foreach (var (code, m) in Meters)
        {
            figures[code] = new JsonObject { ["left"] = Left(code), ["total"] = m.Total, ["offline"] = m.Offline };
            if (m.Resets) figures[code]!["resets"] = true;
        }
        return figures;
    }

    /// <summary>The lease the server signs now for an activation: the key's meters as they stand, valid for 60 days.</summary>
    public string Lease(string aid)
    {
        var met = Figures();
        var key = $"{aid}|{Now / 1000}|{met.ToJsonString()}";
        if (leases.TryGetValue(key, out var known)) return known;
        var input = new LeaseInput { Sub = Metered.Key, Aid = aid, Model = "subscription", Lim = 3, Major = 3, TtlSeconds = 60 * 86_400, Met = met };
        return leases[key] = signer.SignLease(input, Now);
    }

    /// <summary>Take a request's reports, and name the entries held after (always accepted).</summary>
    private JsonArray? Report(JsonArray? spends)
    {
        var confirmed = new JsonArray();
        foreach (var e in spends ?? [])
        {
            var id = Vectors.Str(e!["id"])!;
            var isVoid = e["void"] is not null;
            var n = Vectors.Long(e["n"]) ?? 0;
            if (Voided.Contains(id))
            {
                confirmed.Add(isVoid ? new JsonObject { ["id"] = id, ["void"] = true } : new JsonObject { ["id"] = id, ["n"] = n });
                continue;
            }
            if (isVoid)
            {
                Recorded.Remove(id);
                Voided.Add(id);
                confirmed.Add(new JsonObject { ["id"] = id, ["void"] = true });
                continue;
            }
            if (!Recorded.TryGetValue(id, out var held)) Recorded[id] = new Held(Vectors.Str(e["meter"])!, n);
            else if (n > held.N) held.N = n;
            confirmed.Add(new JsonObject { ["id"] = id, ["n"] = n });
        }
        return confirmed.Count > 0 ? confirmed : null;
    }

    /// <summary>What the server answers a request of <paramref name="action"/>, and what it recorded.</summary>
    public Served Handle(string action, JsonObject body)
    {
        Now += 1;
        var named = Report(body["spends"]?.AsArray());
        JsonObject WithNamed(JsonObject json)
        {
            if (named is not null) json["spends"] = named.DeepClone();
            return json;
        }
        switch (action)
        {
            case "use":
                return Use(body, WithNamed);
            case "validate":
                return new Served(200, WithNamed(new JsonObject { ["lease"] = Lease(Vectors.Str(body["activationId"]) ?? "") }));
            case "deactivate":
                return new Served(200, WithNamed(new JsonObject { ["released"] = true }));
            case "activate":
                activations += 1;
                var aid = $"act_{activations + 1}";
                return new Served(200, new JsonObject { ["lease"] = Lease(aid), ["activationId"] = aid });
            default:
                return new Served(404, new JsonObject { ["error"] = "unknown" });
        }
    }

    private Served Use(JsonObject body, Func<JsonObject, JsonObject> withNamed)
    {
        var meter = Vectors.Str(body["meter"]) ?? "";
        var n = Vectors.Long(body["n"]) ?? 0;
        var id = Vectors.Str(body["id"]) ?? "";
        if (!Meters.ContainsKey(meter)) return new Served(404, withNamed(new JsonObject { ["error"] = "unknown-meter" }));
        if (Recorded.ContainsKey(id)) return new Served(200, withNamed(new JsonObject { ["spent"] = true, ["meters"] = Figures(), ["at"] = Now }));
        if (n > Left(meter)) return new Served(409, withNamed(new JsonObject { ["error"] = "used-up", ["left"] = Left(meter), ["meters"] = Figures(), ["at"] = Now }));
        Recorded[id] = new Held(meter, n);
        return new Served(200, withNamed(new JsonObject { ["spent"] = true, ["meters"] = Figures(), ["at"] = Now }), id);
    }
}
