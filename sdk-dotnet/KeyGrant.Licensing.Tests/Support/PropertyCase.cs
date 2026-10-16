using System.Text.Json.Nodes;

namespace KeyGrant.Licensing.Tests.Support;

// A case of the property test of UseAsync (UsePropertyTests): plain data, so a failing one can be shrunk and kept as a named
// test. The generator is the reference's, line for line (mulberry32 and the draws in the same order), so a seed means the same case.

/// <summary>A meter the key holds.</summary>
internal sealed record MeterSpec(int Total, int Offline, bool Resets);

/// <summary>One <c>UseAsync</c> of a process.</summary>
internal sealed record Call(int P, string Meter, int N);

/// <summary>An entry the journal already holds when the case begins. <c>Recorded</c>: the server holds the spend (or the report) already.</summary>
internal sealed record Preloaded(string Meter, int N, SpendKind Kind, long AgoMs, bool Recorded);

/// <summary>What a step does; <c>R</c> are the random numbers its requests draw their fates from, so a case replays whole.</summary>
internal sealed record Step
{
    public required string T { get; init; }
    public IReadOnlyList<Call> Calls { get; init; } = [];
    public (int P, bool Force)? Status { get; init; }
    public Call? Call { get; init; }
    public int Times { get; init; }
    public long GapMs { get; init; }
    public int P { get; init; }
    public bool Force { get; init; }
    public long Ms { get; init; }
    public string Mode { get; init; } = "";
    public bool Failing { get; init; }
    public IReadOnlyList<double> R { get; init; } = [];

    public JsonNode ToJson()
    {
        var json = new JsonObject { ["t"] = T };
        switch (T)
        {
            case "use":
                json["calls"] = new JsonArray(Calls.Select(c => (JsonNode?)CallJson(c)).ToArray());
                if (Status is { } s) json["status"] = new JsonObject { ["p"] = s.P, ["force"] = s.Force };
                break;
            case "status":
                json["p"] = P;
                json["force"] = Force;
                break;
            case "repeat":
                json["call"] = CallJson(Call!);
                json["times"] = Times;
                json["gapMs"] = GapMs;
                break;
            case "deactivate":
            case "restart":
                json["p"] = P;
                break;
            case "wait":
            case "jump":
                json["ms"] = Ms;
                break;
            case "net":
                json["mode"] = Mode;
                break;
            case "disk":
                json["failing"] = Failing;
                break;
        }
        if (R.Count > 0) json["r"] = new JsonArray(R.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());
        return json;
    }

    private static JsonObject CallJson(Call c) => new() { ["p"] = c.P, ["meter"] = c.Meter, ["n"] = c.N };

    public static Step FromJson(JsonNode node)
    {
        Call? CallOf(JsonNode? c) => c is null ? null : new Call((int)(long)c["p"]!, (string)c["meter"]!, (int)(long)c["n"]!);
        var t = (string)node["t"]!;
        return new Step
        {
            T = t,
            Calls = node["calls"]?.AsArray().Select(c => CallOf(c)!).ToList() ?? [],
            Status = node["status"] is { } s ? ((int)(long)s["p"]!, (bool)s["force"]!) : null,
            Call = CallOf(node["call"]),
            Times = (int?)(long?)node["times"] ?? 0,
            GapMs = (long?)node["gapMs"] ?? 0,
            P = (int?)(long?)node["p"] ?? 0,
            Force = (bool?)node["force"] ?? false,
            Ms = (long?)node["ms"] ?? 0,
            Mode = (string?)node["mode"] ?? "",
            Failing = (bool?)node["failing"] ?? false,
            R = node["r"]?.AsArray().Select(r => (double)r!).ToList() ?? [],
        };
    }
}

/// <summary>A case: the meters, the journal it begins with, and the steps.</summary>
internal sealed record Case(long Seed, IReadOnlyDictionary<string, MeterSpec> Meters, IReadOnlyList<Preloaded> Preload, IReadOnlyList<Step> Steps)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["seed"] = Seed,
        ["meters"] = new JsonObject(Meters.Select(m => KeyValuePair.Create(m.Key, (JsonNode?)new JsonObject { ["total"] = m.Value.Total, ["offline"] = m.Value.Offline, ["resets"] = m.Value.Resets }))),
        ["preload"] = new JsonArray(Preload.Select(p => (JsonNode?)new JsonObject
        {
            ["meter"] = p.Meter,
            ["n"] = p.N,
            ["kind"] = Internal.Names.Of(p.Kind),
            ["agoMs"] = p.AgoMs,
            ["recorded"] = p.Recorded,
        }).ToArray()),
        ["steps"] = new JsonArray(Steps.Select(s => (JsonNode?)s.ToJson()).ToArray()),
    };

    public static Case FromJson(string json)
    {
        var node = JsonNode.Parse(json)!;
        return new Case(
            (long)node["seed"]!,
            node["meters"]!.AsObject().ToDictionary(m => m.Key, m => new MeterSpec((int)(long)m.Value!["total"]!, (int)(long)m.Value["offline"]!, (bool)m.Value["resets"]!)),
            node["preload"]!.AsArray().Select(p => new Preloaded((string)p!["meter"]!, (int)(long)p["n"]!, Internal.Names.SpendKindOf((string)p["kind"]!)!.Value, (long)p["agoMs"]!, (bool)p["recorded"]!)).ToList(),
            node["steps"]!.AsArray().Select(s => Step.FromJson(s!)).ToList());
    }

    public Case WithSteps(IReadOnlyList<Step> steps) => this with { Steps = steps };
}

/// <summary>The reference's seeded generator (mulberry32): the same seed, the same sequence.</summary>
internal sealed class Rng(uint seed)
{
    private uint a = seed;

    public double Next()
    {
        unchecked
        {
            a += 0x6d2b79f5u;
            var t = (a ^ (a >> 15)) * (1u | a);
            t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }
}

/// <summary>Cases from seeds: the reference's generator, line for line.</summary>
internal static class CaseGenerator
{
    private const long Minute = 60_000;
    private const long Hour = 3_600_000;
    private static readonly string[] Meters3 = ["exports", "renders", "credits"];

    public static Case Generate(long seed)
    {
        var rng = new Rng((uint)seed);
        double Rand() => rng.Next();
        int Int(int lo, int hi) => lo + (int)Math.Floor(Rand() * (hi - lo + 1));
        T Pick<T>(IReadOnlyList<T> xs) => xs[(int)Math.Floor(Rand() * xs.Count)];

        var meters = new Dictionary<string, MeterSpec>
        {
            ["exports"] = new(Int(8, 150), Int(1, 12), false),
            ["renders"] = new(Int(3, 40), 0, true),
            ["credits"] = new(Int(4, 60), Int(0, 6), false),
        };

        string Code() => Rand() < 0.1 ? "ghost" : Pick<string>(["exports", "exports", "renders", "credits"]);
        int Amount() => Rand() < 0.7 ? 1 : Rand() < 0.8 ? Int(2, 5) : Rand() < 0.7 ? Int(6, 14) : 500;
        double[] Draws() => Enumerable.Range(0, 6).Select(_ => Rand()).ToArray();
        Call MakeCall(int? p = null, string? not = null)
        {
            var process = p ?? Int(0, 1);
            var meter = Code();
            while (meter == not) meter = Code();
            return new Call(process, meter, Amount());
        }

        // A storm: sixteen voids or one short of it, an outage, and spends the allowance cannot cover a minute apart: the cap is reached.
        var storm = Rand() < 0.12;
        // A journal past the cap, which a release reports 64 of.
        var overfull = !storm && Rand() < 0.06;
        var preload = new List<Preloaded>();
        if (storm || overfull || Rand() < 0.4)
        {
            var offline = Meters3.Where(_ => Rand() < 0.3).ToList();
            // How many of the entries are voids is drawn once: sixteen or one short of it in a storm, else none to sixteen.
            var voids = storm ? Int(14, 16) : overfull ? Int(0, 13) : Int(0, 16);
            var count = overfull ? Int(65, 70) : Math.Min(storm ? voids + Int(0, 6) : Pick<int>([1, 2, 3, 5, 8, 20, 40, 55, 62, 64]), 64 - offline.Count);
            for (var i = 0; i < count; i++)
            {
                var kind = i < voids ? SpendKind.Void : SpendKind.Unanswered;
                var meter = Pick<string>(Meters3);
                var n = Int(1, 2);
                var ago = Pick<long>(storm ? [5 * Minute, 2 * Hour, 20 * Hour] : [1_000, 30_000, 5 * Minute, 2 * Hour, 20 * Hour]);
                preload.Add(new Preloaded(meter, n, kind, ago, Rand() < 0.5));
            }
            foreach (var m in offline) preload.Add(new Preloaded(m, Int(1, 3), SpendKind.Offline, Pick<long>([5 * Minute, 2 * Hour]), Rand() < 0.5));
            // Stable, as Array.prototype.sort is: the oldest first.
            preload = preload.Select((p, i) => (p, i)).OrderByDescending(x => x.p.AgoMs).ThenBy(x => x.i).Select(x => x.p).ToList();
            // Past the cap, the newest entries are voids: the release carries 64 and leaves these behind.
            if (overfull)
            {
                for (var k = Int(1, 3); k > 0; k--) preload.Add(new Preloaded(Pick<string>(Meters3), 1, SpendKind.Void, 500, Rand() < 0.7));
            }
        }

        var steps = new List<Step>();
        if (storm)
        {
            steps.Add(new Step { T = "net", Mode = "down" });
            var p = Int(0, 1);
            var times = Int(3, 8);
            steps.Add(new Step { T = "repeat", Call = new Call(p, "renders", 1), Times = times, GapMs = 61_000, R = Draws() });
        }
        else if (overfull)
        {
            steps.Add(new Step { T = "net", Mode = "good" });
            var p = Int(0, 1);
            steps.Add(new Step { T = "deactivate", P = p, R = Draws() });
        }
        for (var i = Int(8, 26); i > 0; i--)
        {
            var x = Rand();
            if (x < 0.3)
            {
                var c = MakeCall();
                steps.Add(new Step { T = "use", Calls = [c], R = Draws() });
            }
            else if (x < 0.34)
            {
                var c = MakeCall();
                var times = Int(2, 14);
                var gap = Pick<long>([10_000, 61_000, 5 * Minute, 70 * Minute]);
                steps.Add(new Step { T = "repeat", Call = c, Times = times, GapMs = gap, R = Draws() });
            }
            else if (x < 0.42)
            {
                var a = MakeCall(0);
                var b = MakeCall(1);
                steps.Add(new Step { T = "use", Calls = [a, b], R = Draws() });
            }
            else if (x < 0.48)
            {
                var first = MakeCall();
                var second = MakeCall(first.P, first.Meter);
                steps.Add(new Step { T = "use", Calls = [first, second], R = Draws() });
            }
            else if (x < 0.53)
            {
                var c = MakeCall();
                var p = Int(0, 1);
                var force = Rand() < 0.7;
                steps.Add(new Step { T = "use", Calls = [c], Status = (p, force), R = Draws() });
            }
            else if (x < 0.64)
            {
                var p = Int(0, 1);
                var force = Rand() < 0.5;
                steps.Add(new Step { T = "status", P = p, Force = force, R = Draws() });
            }
            else if (x < 0.78) steps.Add(new Step { T = "wait", Ms = Pick<long>([1_000, 20_000, 59_000, 61_000, 5 * Minute, 70 * Minute, 3 * Hour, 26 * Hour]) });
            else if (x < 0.84) steps.Add(new Step { T = "jump", Ms = Pick<long>([30_000, 10 * Minute, 2 * Hour, 30 * Hour, -30_000, -10 * Minute, -2 * Hour, -30 * Hour]) });
            else if (x < 0.92) steps.Add(new Step { T = "net", Mode = Pick<string>(["good", "down", "down", "flaky", "flaky"]) });
            else if (x < 0.97) steps.Add(new Step { T = "disk", Failing = Rand() < 0.6 });
            else if (x < 0.985) steps.Add(new Step { T = "restart", P = Int(0, 1) });
            else steps.Add(new Step { T = "deactivate", P = Int(0, 1), R = Draws() });
        }
        // A release after an outage: spends made while the disk refused, the disk back, and a deactivate, which must carry them.
        if (Rand() < 0.1)
        {
            var p = Int(0, 1);
            steps.Add(new Step { T = "net", Mode = "down" });
            steps.Add(new Step { T = "disk", Failing = true });
            var first = new Call(p, Pick<string>(["exports", "credits"]), 1);
            steps.Add(new Step { T = "use", Calls = [first], R = Draws() });
            steps.Add(new Step { T = "use", Calls = [new Call(p, "exports", 1)], R = Draws() });
            steps.Add(new Step { T = "disk", Failing = false });
            steps.Add(new Step { T = "net", Mode = "good" });
            steps.Add(new Step { T = "deactivate", P = p, R = Draws() });
        }
        return new Case(seed, meters, preload, steps);
    }
}
