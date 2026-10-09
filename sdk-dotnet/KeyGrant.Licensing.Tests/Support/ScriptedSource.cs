using System.ComponentModel;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>
/// A machine-id source that answers from a vector's <c>machineIds[].source</c>: a file's content or the
/// error reading it gives (unlisted: ENOENT) and when it was written; a command's output or the error
/// running it gives (unlisted: ENOENT). Each error is the exception .NET surfaces for it, so this SDK's own
/// classification is what is tested.
/// </summary>
internal sealed class ScriptedSource(JsonObject source) : IMachineIdSource
{
    public string Platform => Vectors.Str(source["platform"])!;

    public Task<string?> ReadMachineGuidAsync() =>
        throw new NotSupportedException("the vectors script reg.exe; this SDK reads the registry API (its Windows cases are not replayed)");

    public Task<string> RunAsync(string command, IReadOnlyList<string> args, TimeSpan timeout)
    {
        if (source["commands"]?[command] is not JsonObject answer) throw new Win32Exception(2, $"{command}: not there (ENOENT)");
        if (answer["error"] is JsonObject error) throw Failure(error);
        return Task.FromResult(Vectors.Str(answer["output"]) ?? "");
    }

    public Task<string> ReadFileAsync(string path)
    {
        if (source["files"]?[path] is not JsonObject file) throw new FileNotFoundException("not there (ENOENT)", path);
        if (file["error"] is JsonObject error) throw Failure(error);
        return Task.FromResult(Vectors.Str(file["content"]) ?? "");
    }

    public Task<double?> MtimeMsAsync(string path)
    {
        // The reference's scripted stat: no time given is a file it cannot stat (ENOENT).
        if (source["files"]?[path]?["mtimeMs"] is not { } mtime) throw new FileNotFoundException("no time (ENOENT)", path);
        return Task.FromResult<double?>(Vectors.Long(mtime));
    }

    /// <summary>
    /// The exception .NET gives for what <c>execFile</c> and <c>fs</c> reject with: a process killed (or
    /// ended by a signal) at its time box; one that exited non-zero; a file or command not there, not
    /// ours, or not a file; and anything else (EIO, EAGAIN, ...) as a plain I/O error.
    /// </summary>
    private static Exception Failure(JsonObject error)
    {
        if (Vectors.Bool(error["killed"]) == true || error["signal"] is not null) return new CommandKilledException();
        if (error["code"] is JsonValue code && Json.TryNumber(code, out var exit)) return new CommandExitException((int)exit);
        return Vectors.Str(error["code"]) switch
        {
            "ENOENT" => new FileNotFoundException("ENOENT"),
            "EACCES" or "EPERM" or "EISDIR" => new UnauthorizedAccessException(Vectors.Str(error["code"])),
            "ENOTDIR" => new DirectoryNotFoundException("ENOTDIR"),
            var other => new IOException(other ?? "a failure with no code"),
        };
    }
}
