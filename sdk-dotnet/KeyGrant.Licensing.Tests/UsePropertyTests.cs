using System.Text.Json.Nodes;
using KeyGrant.Licensing.Tests.Support;

namespace KeyGrant.Licensing.Tests;

/// <summary>
/// A property test of <c>UseAsync</c> over the real <see cref="License"/>: seeded random sequences of spends, check-ins and
/// releases, from two processes of the app over one state file, against a server whose answers are lost, late, refused or lost
/// after it has recorded the spend, with a disk that refuses its writes and a device clock that jumps forward and back. The
/// properties (<see cref="PropertyWorld"/>):
/// <list type="bullet">
/// <item><c>UseAsync</c> and <c>StatusAsync</c> never throw;</item>
/// <item>a spend the offline allowance covers is refused only for the journal's size, and asks the server unless a wait runs;</item>
/// <item>a spend it does not cover is never refused without asking, unless a void under a minute old stands (by the device clock too), and never asks while one does;</item>
/// <item>the stored journal holds no more than 64 entries (but for the spends the customer was told were made while only memory could hold them) and 16 voids, and a process holds no more than 16 voids in memory, none of them marked told;</item>
/// <item>offline, the unsynced spends of a meter never pass its allowance, those same told spends apart;</item>
/// <item>the server never holds more than the customer was told and the refusals it recorded, at any step; once everything is reported it holds every spend the customer was told was made, once (but for what the contract lets a release or the end of a process drop unreported), a spend they were refused after it recorded it is there only when nothing could have lost its void, and every status shows what the server has left;</item>
/// <item>what <c>UseAsync</c> answers as <c>Left</c> is the figure less the unsynced less the spend when made offline, and never more than the server has when made online.</item>
/// </list>
/// The oracle takes its constants, figures and waits from the contract, not from the code it judges. A case replays the same way
/// every time (a test below holds it to that), which is what lets a failing one be shrunk and kept. The environment variables
/// <c>PROPERTY_RUNS</c> and <c>PROPERTY_SEED</c> choose how many sequences run and from which seed: the default is 1500 sequences,
/// which the suite affords in seconds, a hunt is <c>PROPERTY_RUNS=20000</c>. A failing case is shrunk and printed as data, with what happened: add it to the
/// kept cases (<see cref="PropertyCases"/>).
/// </summary>
public class UsePropertyTests
{
    /// <summary>A failing case with steps taken out while it fails the same way: the shortest sequence that shows the bug.</summary>
    private static async Task<Case> Shrink(Case c, PropertyFailure failure)
    {
        var best = c;
        for (var size = (int)Math.Ceiling(best.Steps.Count / 2.0); size >= 1; size /= 2)
        {
            for (var at = 0; at < best.Steps.Count;)
            {
                var trial = best.WithSteps(best.Steps.Take(at).Concat(best.Steps.Skip(at + size)).ToList());
                var again = await PropertyWorld.RunCaseAsync(trial);
                if (again is not null && again.Invariant == failure.Invariant) best = trial;
                else at += size;
            }
            if (size == 1) break;
        }
        var lean = best with { Preload = [] };
        var bare = await PropertyWorld.RunCaseAsync(lean);
        return bare is not null && bare.Invariant == failure.Invariant ? lean : best;
    }

    private static long Setting(string name, long fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        return text is null ? fallback : long.TryParse(text, out var value) ? value : throw new InvalidOperationException($"{name} must be a whole number: {text}");
    }

    [Fact]
    public async Task Keeps_every_property_of_the_contract_over_seeded_sequences()
    {
        var runs = Setting("PROPERTY_RUNS", 1500);
        var first = Setting("PROPERTY_SEED", 1);
        // A seed or a count that would run nothing, and pass.
        Assert.True(runs >= 1, "PROPERTY_RUNS must be at least 1");
        for (var seed = first; seed < first + runs; seed++)
        {
            var c = CaseGenerator.Generate(seed);
            var failure = await PropertyWorld.RunCaseAsync(c);
            if (failure is null) continue;
            var small = await Shrink(c, failure);
            var trace = new List<string>();
            var again = await PropertyWorld.RunCaseAsync(small, trace);
            throw new Xunit.Sdk.XunitException(
                $"seed {seed}: {failure.Message}\nshrunk to {small.Steps.Count} steps ({again?.Message ?? "no longer fails"}):\n{small.ToJson().ToJsonString()}\n{string.Join("\n", trace)}");
        }
    }

    [Fact]
    public async Task A_case_replayed_runs_the_same_way_every_time_the_same_requests_answers_and_journals()
    {
        for (long seed = 1; seed <= 60; seed++)
        {
            var c = CaseGenerator.Generate(seed);
            var first = await PropertyWorld.TraceOfAsync(c);
            for (var again = 0; again < 2; again++)
            {
                var next = await PropertyWorld.TraceOfAsync(c);
                Assert.True(first == next, $"seed {seed} ran differently the {again + 2}nd time:{Environment.NewLine}{Difference(first, next)}");
            }
        }
    }

    /// <summary>The first line two traces differ at, with the lines before it.</summary>
    private static string Difference(string a, string b)
    {
        var (x, y) = (a.Split('\n'), b.Split('\n'));
        var at = Enumerable.Range(0, Math.Min(x.Length, y.Length)).FirstOrDefault(i => x[i] != y[i], Math.Min(x.Length, y.Length));
        var before = string.Join(Environment.NewLine, x.Skip(Math.Max(0, at - 6)).Take(Math.Min(6, at)));
        return $"{before}{Environment.NewLine}--- first run, line {at}:{Environment.NewLine}{(at < x.Length ? x[at] : "(ends)")}{Environment.NewLine}--- other run:{Environment.NewLine}{(at < y.Length ? y[at] : "(ends)")}";
    }

    [Fact]
    public void Generates_the_first_draws_the_reference_generates()
    {
        // The reference's generator, line for line: the first draws of mulberry32(1).
        var rng = new Rng(1);
        Assert.Equal([0.6270739405881613, 0.002735721180215478, 0.5274470399599522], new[] { rng.Next(), rng.Next(), rng.Next() });
    }

    public static TheoryData<long, string> GoldenCases()
    {
        var data = new TheoryData<long, string>();
        foreach (var (seed, json) in PropertyGolden.Cases) data.Add(seed, json);
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Generates_the_case_the_reference_generates_from_a_seed_so_a_seed_found_there_means_the_same_here(long seed, string reference)
    {
        var made = CaseGenerator.Generate(seed);
        // Both through the same serializer, so a number the reference wrote is compared as the number it is.
        Assert.Equal(Case.FromJson(reference).ToJson().ToJsonString(), made.ToJson().ToJsonString());
        // And it is data: the case survives its own JSON.
        Assert.Equal(made.ToJson().ToJsonString(), Case.FromJson(made.ToJson().ToJsonString()).ToJson().ToJsonString());
    }

    public static TheoryData<long> GoldenShapes()
    {
        var data = new TheoryData<long>();
        foreach (var shape in PropertyGolden.Shapes) data.Add(shape.Seed);
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenShapes))]
    public void Generates_the_long_cases_the_reference_generates_by_what_they_hold(long seed)
    {
        // A storm of voids (6) and a journal past its cap (115): their size, their steps in order and their first draw.
        var shape = PropertyGolden.Shapes.Single(s => s.Seed == seed);
        var made = CaseGenerator.Generate(seed);
        Assert.Equal(shape.Preload, made.Preload.Count);
        Assert.Equal(shape.Voids, made.Preload.Count(p => p.Kind == SpendKind.Void));
        Assert.Equal(shape.Steps, string.Join(" ", made.Steps.Select(s => s.T)));
        Assert.Equal(shape.FirstDraw, made.Steps.First(s => s.R.Count > 0).R[0]);
    }

    public static TheoryData<int, string> FoundCases()
    {
        var data = new TheoryData<int, string>();
        for (var i = 0; i < PropertyCases.Found.Length; i++) data.Add(i, PropertyCases.Found[i].Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(FoundCases))]
    public async Task Holds_the_case_the_property_test_found(int index, string name)
    {
        var failure = await PropertyWorld.RunCaseAsync(Case.FromJson(PropertyCases.Found[index].Json));
        Assert.True(failure is null, $"{name}: {failure?.Message}");
    }
}
