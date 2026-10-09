using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>
/// A stand-in system for the machine id: what the registry holds (or how reading it fails), what each
/// command prints, what each file holds, and when each was written. Anything not named is not there.
/// </summary>
internal sealed class FakeMachineIdSource : IMachineIdSource
{
    public const string TestGuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private readonly object gate = new();
    private readonly List<(string Command, IReadOnlyList<string> Args, TimeSpan Timeout)> runs = new();
    private int guidReads;

    public string Platform { get; init; } = "win32";

    /// <summary>The registry's MachineGuid: a value, null for none, or how reading it fails.</summary>
    public Func<Task<string?>> MachineGuid { get; init; } = () => Task.FromResult<string?>(null);

    public Dictionary<string, Func<Task<string>>> Commands { get; init; } = new();

    public Dictionary<string, Func<Task<string>>> Files { get; init; } = new();

    public Func<string, Task<double?>> Mtime { get; init; } = _ => Task.FromResult<double?>(null);

    public int GuidReads
    {
        get
        {
            lock (gate) return guidReads;
        }
    }

    public IReadOnlyList<(string Command, IReadOnlyList<string> Args, TimeSpan Timeout)> Runs
    {
        get
        {
            lock (gate) return runs.ToList();
        }
    }

    public Task<string?> ReadMachineGuidAsync()
    {
        lock (gate) guidReads++;
        return MachineGuid();
    }

    public Task<string> RunAsync(string command, IReadOnlyList<string> args, TimeSpan timeout)
    {
        lock (gate) runs.Add((command, args, timeout));
        return Commands.TryGetValue(command, out var output) ? output() : throw new System.ComponentModel.Win32Exception(2, "not there");
    }

    public Task<string> ReadFileAsync(string path) =>
        Files.TryGetValue(path, out var text) ? text() : throw new FileNotFoundException("not there", path);

    public Task<double?> MtimeMsAsync(string path) => Mtime(path);

    // --- the systems the tests describe ------------------------------------------------

    /// <summary>A Windows machine whose registry read never finishes the first <paramref name="hangs"/> times, then answers.</summary>
    public static FakeMachineIdSource SlowRegistry(int hangs)
    {
        var left = hangs;
        return new FakeMachineIdSource
        {
            MachineGuid = () => Interlocked.Decrement(ref left) >= 0
                ? new TaskCompletionSource<string?>().Task
                : Task.FromResult<string?>(TestGuid),
        };
    }

    /// <summary>A Windows machine whose registry refuses this process: no id to read (an endpoint tool blocking it, say).</summary>
    public static FakeMachineIdSource BlockedRegistry() => new()
    {
        MachineGuid = () => Task.FromException<string?>(new UnauthorizedAccessException("access denied")),
    };

    public static Func<Task<string>> Text(string text) => () => Task.FromResult(text);

    public static Func<Task<string>> Failing(Exception error) => () => Task.FromException<string>(error);

    public static Func<Task<string>> Never() => () => new TaskCompletionSource<string>().Task;
}
