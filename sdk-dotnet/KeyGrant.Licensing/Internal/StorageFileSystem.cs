using System.Runtime.InteropServices;
using System.Text;

namespace KeyGrant.Licensing.Internal;

/// <summary>A new file being written: what is written is flushed to the disk by <see cref="SyncAsync"/>.</summary>
internal interface IStorageFile
{
    Task WriteAsync(string data);

    Task SyncAsync();

    Task CloseAsync();
}

/// <summary>The file operations under <see cref="FileStorage"/>: the real ones, or stand-ins in tests.</summary>
internal interface IStorageFileSystem
{
    Task<string> ReadFileAsync(string path);

    /// <summary>A new file to write, failing when there is one by that name.</summary>
    Task<IStorageFile> CreateAsync(string path);

    /// <summary>Rename <paramref name="from"/> over <paramref name="to"/>, replacing it atomically.</summary>
    Task RenameAsync(string from, string to);

    /// <summary>Remove a file; nothing when it is not there.</summary>
    Task RemoveAsync(string path);

    /// <summary>The names of a folder's entries.</summary>
    Task<IReadOnlyList<string>> ReadDirAsync(string dir);

    Task<double> MtimeMsAsync(string path);

    Task MkdirAsync(string dir);

    /// <summary>Flush a folder's entries (a rename in it) to the disk.</summary>
    Task SyncDirAsync(string dir);
}

/// <summary>An error as the reference names it (<c>ENOENT</c>, <c>EBUSY</c>, ...), for stand-in file systems and conflicts.</summary>
internal class StorageCodeException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>What a file error is, in the reference's words: the busy rename tried again, the missing file that is no state.</summary>
internal static class StorageErrors
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int FileExists = unchecked((int)0x80070050);
    private const int AlreadyExists = unchecked((int)0x800700B7);
    private const int DiskFull = unchecked((int)0x80070070);

    public static string? CodeOf(Exception error) => error switch
    {
        StorageCodeException coded => coded.Code,
        FileNotFoundException or DirectoryNotFoundException => "ENOENT",
        UnauthorizedAccessException => "EACCES",
        IOException io when io.HResult is SharingViolation or LockViolation => "EBUSY",
        IOException io when io.HResult is FileExists or AlreadyExists => "EEXIST",
        IOException io when io.HResult is DiskFull => "ENOSPC",
        _ => null,
    };

    /// <summary>Errors a busy file gives a rename on Windows (an antivirus scan, an indexer, a backup), worth another try.</summary>
    public static bool Busy(Exception error) => CodeOf(error) is "EBUSY" or "EPERM" or "EACCES";

    public static bool Missing(Exception error) => CodeOf(error) == "ENOENT";
}

/// <summary>The real file system.</summary>
internal sealed class SystemStorageFileSystem : IStorageFileSystem
{
    public static readonly SystemStorageFileSystem Instance = new();

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Opened sharing everything, as libuv opens every file on Windows: a reader or a writer of ours
    /// never refuses another process its own reads and writes. (Windows still refuses a rename OVER a
    /// file someone holds open, whatever its sharing: that comes back access denied, and is tried again
    /// as busy, as the reference tries EPERM again.)
    /// </summary>
    internal const FileShare ShareAll = FileShare.ReadWrite | FileShare.Delete;

    public async Task<string> ReadFileAsync(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ShareAll, 4096, FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }

    public Task<IStorageFile> CreateAsync(string path) =>
        Task.FromResult<IStorageFile>(new SystemStorageFile(new FileStream(path, FileMode.CreateNew, FileAccess.Write, ShareAll)));

    public Task RenameAsync(string from, string to)
    {
        File.Move(from, to, overwrite: true);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Not there: nothing to remove.
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ReadDirAsync(string dir) =>
        Task.FromResult<IReadOnlyList<string>>(Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)).ToList());

    /// <summary>As <c>fs.stat</c>: a symbolic link's target's time, not the link's own.</summary>
    public Task<double> MtimeMsAsync(string path) => Task.FromResult(FileTimes.LastWriteMs(path));

    public Task MkdirAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        return Task.CompletedTask;
    }

    public Task SyncDirAsync(string dir)
    {
        // Windows opens no handle on a folder to flush; NTFS journals the rename itself.
        if (OperatingSystem.IsWindows()) return Task.CompletedTask;
        var fd = Posix.Open(dir, 0 /* O_RDONLY */);
        if (fd < 0) throw new IOException($"the folder could not be opened to flush it (errno {Marshal.GetLastPInvokeError()})");
        try
        {
            if (Posix.Fsync(fd) != 0) throw new IOException($"the folder could not be flushed (errno {Marshal.GetLastPInvokeError()})");
        }
        finally
        {
            _ = Posix.Close(fd);
        }
        return Task.CompletedTask;
    }

    private sealed class SystemStorageFile(FileStream stream) : IStorageFile
    {
        public async Task WriteAsync(string data)
        {
            var bytes = Utf8.GetBytes(data);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
        }

        public Task SyncAsync()
        {
            stream.Flush(flushToDisk: true);
            return Task.CompletedTask;
        }

        public async Task CloseAsync() => await stream.DisposeAsync().ConfigureAwait(false);
    }

    private static class Posix
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        public static extern int Fsync(int fd);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int fd);
    }
}
