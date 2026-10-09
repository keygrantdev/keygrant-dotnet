using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>An operation of the file system under <see cref="FileStorage"/>.</summary>
internal enum Op
{
    ReadFile,
    Create,
    Write,
    Sync,
    Close,
    Rename,
    Remove,
    ReadDir,
    Mtime,
    Mkdir,
    SyncDir,
}

/// <summary>
/// Files in memory, as one or more processes see them, with failures queued per operation
/// (<see cref="FailNext"/>) or given every time (<see cref="FailAlways"/>), an operation held until
/// released (<see cref="HoldNext"/>), something another process does just before an operation
/// (<see cref="Before"/>), each file's mtime, every operation in order, and every one that changes a file.
/// </summary>
internal sealed class MemoryFileSystem : IStorageFileSystem
{
    private readonly object gate = new();
    private readonly Dictionary<Op, Queue<string>> queued = new();
    private readonly Dictionary<Op, string> always = new();
    private readonly Dictionary<Op, Queue<Hold>> holds = new();

    public MemoryFileSystem(Dictionary<string, string>? files = null) => Files = files ?? new Dictionary<string, string>();

    public Dictionary<string, string> Files { get; }

    public Dictionary<string, double> Mtimes { get; } = new();

    public List<string> Ops { get; } = new();

    public List<string> Changes { get; } = new();

    public Dictionary<Op, Action> Before { get; } = new();

    /// <summary>What "now" is for a file with no mtime set.</summary>
    public Func<double> Now { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Dictionary<string, string> Snapshot()
    {
        lock (gate) return new Dictionary<string, string>(Files);
    }

    public void FailNext(Op op, params string[] codes)
    {
        lock (gate)
        {
            if (!queued.TryGetValue(op, out var queue)) queued[op] = queue = new Queue<string>();
            foreach (var code in codes) queue.Enqueue(code);
        }
    }

    public void FailAlways(Op op, string code)
    {
        lock (gate) always[op] = code;
    }

    /// <summary>Hold the next <paramref name="op"/> until released; Reached once something waits on it.</summary>
    public Hold HoldNext(Op op)
    {
        var hold = new Hold();
        lock (gate)
        {
            if (!holds.TryGetValue(op, out var queue)) holds[op] = queue = new Queue<Hold>();
            queue.Enqueue(hold);
        }
        return hold;
    }

    public sealed class Hold
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool reached;

        public bool Reached => reached;

        public Task Wait()
        {
            reached = true;
            return released.Task;
        }

        public void Release() => released.TrySetResult();
    }

    private async Task Enter(Op op, string path, bool changing = false)
    {
        Action? before;
        Hold? hold = null;
        lock (gate)
        {
            Before.TryGetValue(op, out before);
            if (holds.TryGetValue(op, out var queue) && queue.Count > 0) hold = queue.Dequeue();
        }
        before?.Invoke();
        if (hold is not null) await hold.Wait();
        lock (gate)
        {
            string? code = null;
            if (queued.TryGetValue(op, out var codes) && codes.Count > 0) code = codes.Dequeue();
            else if (always.TryGetValue(op, out var every)) code = every;
            if (code is not null) throw new StorageCodeException(code, $"{code}: the file system said no");
            Ops.Add($"{op} {path}");
            if (changing) Changes.Add($"{op} {path}");
        }
    }

    public async Task<string> ReadFileAsync(string path)
    {
        await Enter(Op.ReadFile, path);
        lock (gate) return Files.TryGetValue(path, out var text) ? text : throw new StorageCodeException("ENOENT", "missing");
    }

    public async Task<IStorageFile> CreateAsync(string path)
    {
        await Enter(Op.Create, path, changing: true);
        lock (gate)
        {
            if (Files.ContainsKey(path)) throw new StorageCodeException("EEXIST", "exists");
            Files[path] = "";
        }
        return new MemoryFile(this, path);
    }

    private sealed class MemoryFile(MemoryFileSystem fs, string path) : IStorageFile
    {
        public async Task WriteAsync(string data)
        {
            await fs.Enter(Op.Write, path, changing: true);
            lock (fs.gate) fs.Files[path] = data;
        }

        public Task SyncAsync() => fs.Enter(Op.Sync, path);

        public Task CloseAsync() => fs.Enter(Op.Close, path);
    }

    public async Task RenameAsync(string from, string to)
    {
        await Enter(Op.Rename, $"{from} {to}", changing: true);
        lock (gate)
        {
            if (!Files.TryGetValue(from, out var text)) throw new StorageCodeException("ENOENT", "missing");
            Files[to] = text;
            Files.Remove(from);
            if (Mtimes.Remove(from, out var mtime)) Mtimes[to] = mtime;
        }
    }

    public async Task RemoveAsync(string path)
    {
        await Enter(Op.Remove, path, changing: true);
        lock (gate) Files.Remove(path);
    }

    public async Task<IReadOnlyList<string>> ReadDirAsync(string dir)
    {
        await Enter(Op.ReadDir, dir);
        lock (gate) return Files.Keys.Where(p => Path.GetDirectoryName(p) == dir).Select(p => Path.GetFileName(p)).ToList();
    }

    public async Task<double> MtimeMsAsync(string path)
    {
        await Enter(Op.Mtime, path);
        lock (gate) return Mtimes.TryGetValue(path, out var at) ? at : Now();
    }

    public Task MkdirAsync(string dir) => Enter(Op.Mkdir, dir);

    public Task SyncDirAsync(string dir) => Enter(Op.SyncDir, dir);
}
