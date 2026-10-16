using System.Text.Json.Nodes;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>
/// The bodies the reference captured of the requests that carry a report (<c>conformance/wire-bodies.json</c>). The file sits
/// beside the reference's generator, so only the repository that holds the reference has it; the public export of this SDK
/// carries <c>conformance/vectors.json</c> alone. A test that needs it is skipped, with the reason, outside that repository,
/// and FAILS inside it: a test that cannot find its file must never pass, or be skipped, in the place that makes it.
/// </summary>
internal static class WireBodies
{
    private static readonly Lazy<bool> Repository = new(() => Above(Path.Combine("scripts", "export-sdk.mjs")) is not null);

    /// <summary>Whether this is the repository that holds the reference (it has the script that exports the SDKs).</summary>
    public static bool InRepository => Repository.Value;

    /// <summary>Where the captured bodies are, or null.</summary>
    public static string? PathOnDisk { get; } = Environment.GetEnvironmentVariable("KEYGRANT_WIRE_BODIES") is { Length: > 0 } given
        ? given
        : Above(Path.Combine("conformance", "wire-bodies.json"));

    /// <summary>Whether a test needing the file is skipped: it is missing, and this is not the repository that holds the reference.</summary>
    public static bool Skips(bool present, bool inRepository) => !present && !inRepository;

    public static bool Skipped => Skips(PathOnDisk is not null, InRepository);

    /// <summary>The captured bodies: a missing file fails in the repository that holds the reference.</summary>
    public static JsonObject Read() =>
        JsonNode.Parse(File.ReadAllText(PathOnDisk ?? throw new FileNotFoundException("conformance/wire-bodies.json was not found above the test's folder")))!.AsObject();

    /// <summary>The file <paramref name="relative"/> in a folder above the test's folder (or under its <c>sdk</c> folder), or null.</summary>
    private static string? Above(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, relative), Path.Combine(dir.FullName, "sdk", relative) })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

/// <summary>A test over the reference's captured bodies: skipped, with the reason, only outside the repository that holds the reference.</summary>
public sealed class WireBodiesFactAttribute : FactAttribute
{
    public WireBodiesFactAttribute()
    {
        if (WireBodies.Skipped) Skip = "the reference's captured request bodies are not in this checkout (it is not the repository that holds the reference)";
    }
}
