using System.Globalization;
using System.Text.RegularExpressions;
using KeyGrant.Licensing.Internal;

namespace KeyGrant.Licensing;

/// <summary>Options for <see cref="FileStorage"/>.</summary>
public sealed class FileStorageOptions
{
    /// <summary>
    /// A path the state was kept at before (an app moving its own storage): read, and copied to the new
    /// path once, the first time the new path is missing. The old file is left where it is.
    /// </summary>
    public string? MigrateFrom { get; init; }
}

/// <summary>
/// The default storage: one JSON file, read and written so that neither a crash nor a busy disk ever
/// costs a customer their licence. The same file, in the same format, as the Electron SDK's.
/// <para>
/// Read: only a MISSING file is "no state"; then the newest whole temp file a crash left is read,
/// else the <see cref="FileStorageOptions.MigrateFrom"/> state, copied over once. Any other failure to
/// read is tried again for about a second and then THROWS: a save built on a read that failed would
/// write it over the real state. A file that reads but does not parse is set aside
/// (<c>&lt;file&gt;.&lt;time&gt;.corrupt</c>, the newest three kept) and an empty state written in its place.
/// </para>
/// <para>
/// Written atomically: to a temp file beside it, flushed to the disk, then renamed over it (tried again
/// for a while when Windows says the file is busy), and on macOS and Linux the folder flushed after.
/// A write that lands clears temp files a crash left (older than a minute).
/// </para>
/// <para>
/// Saved (<see cref="UpdateAsync"/>) without losing another process's save: every save stamps the
/// next revision (<c>rev</c>) and reads the file again just before its rename; when another process
/// saved since, the change is merged onto that newer state and tried again, five times at most, then
/// it fails with <see cref="StateConflictException"/>.
/// </para>
/// </summary>
public sealed class FileStorage : IStorageAdapter
{
    /// <summary>How many set-aside <c>.corrupt</c> files of a state file are kept: the newest.</summary>
    public const int KeepCorrupt = 3;

    /// <summary>How old a temp file must be (ms) to be a crash's leftover rather than another instance's write in flight.</summary>
    public const long StaleTempMs = 60_000;

    /// <summary>How many times a save is merged onto a state another process changed under it before it fails.</summary>
    public const int UpdateAttempts = 5;

    /// <summary>Waits between tries of a read that failed other than for a missing file: about a second in all.</summary>
    internal static readonly int[] ReadBackoffMs = [50, 100, 200, 300, 350];

    /// <summary>Waits between tries of a rename a busy file refused: about a second and a half in all.</summary>
    internal static readonly int[] RenameBackoffMs = [50, 100, 200, 400, 800];

    private static readonly Regex CorruptName = new("^([0-9]+)\\.corrupt$", RegexOptions.CultureInvariant);

    private readonly string dir;
    private readonly string prefix;
    private readonly string? migrateFrom;
    private readonly IStorageFileSystem fs;
    private readonly Func<int, Task> sleep;
    private readonly Func<long> wallNow;

    /// <summary>A JSON file storage at <paramref name="filePath"/>.</summary>
    /// <param name="filePath">The state file, e.g. <see cref="DefaultPath(string)"/>.</param>
    /// <param name="options">Options, or null.</param>
    public FileStorage(string filePath, FileStorageOptions? options = null)
        : this(filePath, options?.MigrateFrom, null, null, null)
    {
    }

    /// <summary>A file storage over stand-ins (tests).</summary>
    internal FileStorage(string filePath, string? migrateFrom, IStorageFileSystem? fs, Func<int, Task>? sleep, Func<long>? wallNow)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = Path.GetFullPath(filePath);
        dir = Path.GetDirectoryName(FilePath) ?? throw new ArgumentException("a state file needs a folder", nameof(filePath));
        prefix = $"{Path.GetFileName(FilePath)}.";
        this.migrateFrom = migrateFrom is null ? null : Path.GetFullPath(migrateFrom);
        this.fs = fs ?? SystemStorageFileSystem.Instance;
        this.sleep = sleep ?? (ms => Task.Delay(ms));
        this.wallNow = wallNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>The state file.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Where the default storage keeps a product's state: a folder of this user's on THIS machine,
    /// never the working directory and never a folder that roams between machines:
    /// Windows <c>%LOCALAPPDATA%\KeyGrant</c> (else <c>~\AppData\Local\KeyGrant</c>); macOS
    /// <c>~/Library/Application Support/KeyGrant</c>; Linux and others <c>$XDG_CONFIG_HOME/keygrant</c>
    /// (else <c>~/.config/keygrant</c>). An empty or relative variable counts as unset.
    /// </summary>
    /// <param name="product">The product slug.</param>
    /// <returns>The path of <c>&lt;product&gt;.json</c>.</returns>
    public static string DefaultPath(string product)
    {
        ArgumentException.ThrowIfNullOrEmpty(product);
        var home = NodeHomeDir();
        return DefaultPath(product, NodeNames.Platform, home, Environment.GetEnvironmentVariable);
    }

    /// <summary>
    /// Where the Electron SDK kept a product's state before its per-user default: <c>.keygrant</c> in
    /// the working directory. Give it as <see cref="FileStorageOptions.MigrateFrom"/> to read such a state.
    /// </summary>
    /// <param name="product">The product slug.</param>
    /// <returns>The path.</returns>
    public static string LegacyPath(string product) => Path.GetFullPath(Path.Combine(".keygrant", $"{product}.json"));

    /// <summary><see cref="DefaultPath(string)"/> for a given system (tests).</summary>
    internal static string DefaultPath(string product, string platform, string homedir, Func<string, string?> env)
    {
        var file = $"{product}.json";
        if (platform == "win32")
        {
            var local = env("LOCALAPPDATA") is { Length: > 0 } value && IsWin32Absolute(value) ? value : JoinWin32(homedir, "AppData", "Local");
            return JoinWin32(local, "KeyGrant", file);
        }
        if (platform == "darwin") return JoinPosix(homedir, "Library", "Application Support", "KeyGrant", file);
        var config = env("XDG_CONFIG_HOME") is { Length: > 0 } xdg && xdg.StartsWith('/') ? xdg : JoinPosix(homedir, ".config");
        return JoinPosix(config, "keygrant", file);
    }

    /// <summary>Node's <c>os.homedir()</c>: <c>%USERPROFILE%</c> or <c>$HOME</c> first, else the account's.</summary>
    private static string NodeHomeDir()
    {
        var variable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        return Environment.GetEnvironmentVariable(variable) is { Length: > 0 } home
            ? home
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary><c>path.win32.isAbsolute</c>: a leading separator, or a drive letter, colon and separator.</summary>
    private static bool IsWin32Absolute(string path) =>
        path.Length > 0 && (path[0] is '\\' or '/' || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'));

    private static string JoinWin32(params string[] parts) =>
        string.Join('\\', parts.Select((p, i) => i == 0 ? p.Replace('/', '\\').TrimEnd('\\') : p.Replace('/', '\\').Trim('\\')).Where(p => p.Length > 0));

    private static string JoinPosix(params string[] parts) =>
        (parts[0].StartsWith('/') ? "/" : "") + string.Join('/', parts.SelectMany(p => p.Split('/')).Where(p => p.Length > 0));

    /// <inheritdoc />
    public async Task<StoredState?> ReadAsync(bool readOnly) => (await LoadAsync(readOnly).ConfigureAwait(false)).State;

    /// <inheritdoc />
    public Task WriteAsync(StoredState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return PlaceAsync(state, null);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Func<StoredState, StoredState> merge)
    {
        ArgumentNullException.ThrowIfNull(merge);
        for (var attempt = 1; ; attempt++)
        {
            var (state, rev) = await LoadAsync(false).ConfigureAwait(false);
            var next = StateFields.Set(merge(state ?? new StoredState()), StateField.Rev, (state?.Rev ?? 0) + 1);
            try
            {
                await PlaceAsync(next, () => UnchangedAsync(rev)).ConfigureAwait(false);
                return true;
            }
            catch (StateConflictException) when (attempt < UpdateAttempts)
            {
                // Another process saved since this one read: merge onto its state and try again.
            }
        }
    }

    /// <summary>A whole state put in place, once <paramref name="allowed"/> (when given) has not thrown, just before the rename.</summary>
    private async Task PlaceAsync(StoredState state, Func<Task>? allowed)
    {
        var temp = await WriteTempAsync(state).ConfigureAwait(false);
        try
        {
            await RetryingAsync(
                async () =>
                {
                    if (allowed is not null) await allowed().ConfigureAwait(false);
                    await fs.RenameAsync(temp, FilePath).ConfigureAwait(false);
                    return true;
                },
                RenameBackoffMs,
                StorageErrors.Busy).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await Quietly(() => fs.RemoveAsync(temp)).ConfigureAwait(false);
            throw;
        }
        await Quietly(() => fs.SyncDirAsync(dir)).ConfigureAwait(false);
        await ClearStaleAsync().ConfigureAwait(false);
    }

    /// <summary>The state, and the revision of the FILE it was read from (none when it came from anywhere else).</summary>
    private async Task<(StoredState? State, Rev Rev)> LoadAsync(bool readOnly)
    {
        var text = await ReadTextAsync().ConfigureAwait(false);
        if (text is null)
        {
            var state = await LeftoverAsync().ConfigureAwait(false) ?? await MigratedAsync(copy: !readOnly).ConfigureAwait(false);
            return (state, Rev.None);
        }
        if (StoredStateJson.TryRead(text, out var parsed)) return (parsed, new Rev(true, parsed?.Rev));
        if (readOnly) return (null, Rev.None);
        // Kept aside before anything is written in its place; a rename that cannot be made throws,
        // as a read that cannot be made does.
        var aside = $"{FilePath}.{wallNow().ToString(CultureInfo.InvariantCulture)}.corrupt";
        await RetryingAsync(async () => { await fs.RenameAsync(FilePath, aside).ConfigureAwait(false); return true; }, RenameBackoffMs, StorageErrors.Busy)
            .ConfigureAwait(false);
        await Quietly(() => WriteAsync(new StoredState())).ConfigureAwait(false);
        await PruneCorruptAsync().ConfigureAwait(false);
        return (null, Rev.None);
    }

    /// <summary>A file's revision: none when missing or unstamped; one that never matches when it does not parse.</summary>
    private readonly record struct Rev(bool Parsed, long? Value)
    {
        public static readonly Rev None = new(true, null);

        public static readonly Rev Unparseable = new(false, null);

        public bool Matches(Rev other) => Parsed && other.Parsed && Value == other.Value;
    }

    /// <summary>Throws a conflict when the file's revision is no longer <paramref name="rev"/>: another process saved since it was read.</summary>
    private async Task UnchangedAsync(Rev rev)
    {
        string? text;
        try
        {
            text = await fs.ReadFileAsync(FilePath).ConfigureAwait(false);
        }
        catch (Exception error) when (StorageErrors.Missing(error))
        {
            text = null;
        }
        var now = text is null ? Rev.None : StoredStateJson.TryRead(text, out var state) ? new Rev(true, state?.Rev) : Rev.Unparseable;
        if (now.Matches(rev)) return;
        throw new StateConflictException();
    }

    /// <summary>The file's text, tried again for about a second on any failure but a missing file; null when it is missing.</summary>
    private async Task<string?> ReadTextAsync()
    {
        try
        {
            return await RetryingAsync(() => fs.ReadFileAsync(FilePath), ReadBackoffMs, error => !StorageErrors.Missing(error)).ConfigureAwait(false);
        }
        catch (Exception error) when (StorageErrors.Missing(error))
        {
            return null;
        }
    }

    /// <summary><paramref name="work"/>, tried again after each wait in <paramref name="backoff"/> while its error is worth another try.</summary>
    private async Task<T> RetryingAsync<T>(Func<Task<T>> work, int[] backoff, Func<Exception, bool> worthAgain)
    {
        foreach (var wait in backoff)
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (Exception error) when (worthAgain(error))
            {
                await sleep(wait).ConfigureAwait(false);
            }
        }
        return await work().ConfigureAwait(false);
    }

    /// <summary>A state written whole to a new temp file and flushed to the disk before it is closed; removed when it fails.</summary>
    private async Task<string> WriteTempAsync(StoredState state)
    {
        await fs.MkdirAsync(dir).ConfigureAwait(false);
        var name = $"{prefix}{Environment.ProcessId}.{wallNow().ToString(CultureInfo.InvariantCulture)}.{RandomSuffix()}.tmp";
        var temp = Path.Combine(dir, name);
        try
        {
            var file = await fs.CreateAsync(temp).ConfigureAwait(false);
            try
            {
                await file.WriteAsync(state.ToJson()).ConfigureAwait(false);
                await file.SyncAsync().ConfigureAwait(false);
            }
            finally
            {
                await file.CloseAsync().ConfigureAwait(false);
            }
            return temp;
        }
        catch (Exception)
        {
            await Quietly(() => fs.RemoveAsync(temp)).ConfigureAwait(false);
            throw;
        }
    }

    private static string RandomSuffix()
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> chars = stackalloc char[11];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }

    private bool IsTemp(string name) => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal);

    /// <summary>The newest temp file of this state file that parses, when the file itself is missing. Never throws.</summary>
    private async Task<StoredState?> LeftoverAsync()
    {
        try
        {
            var temps = (await fs.ReadDirAsync(dir).ConfigureAwait(false)).Where(IsTemp).ToList();
            var dated = new List<(string Name, double At)>();
            foreach (var name in temps) dated.Add((name, await fs.MtimeMsAsync(Path.Combine(dir, name)).ConfigureAwait(false)));
            foreach (var (name, _) in dated.OrderByDescending(d => d.At))
            {
                try
                {
                    if (StoredStateJson.TryRead(await fs.ReadFileAsync(Path.Combine(dir, name)).ConfigureAwait(false), out var state)) return state;
                }
                catch (Exception)
                {
                    // Torn or gone: the next older one.
                }
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The state kept at <see cref="FileStorageOptions.MigrateFrom"/>, copied to this path once (unless
    /// <paramref name="copy"/> is false: a read-only read) and returned. Null when there is none or it
    /// does not parse. Never throws: a failed copy only means it is read again next time.
    /// </summary>
    private async Task<StoredState?> MigratedAsync(bool copy)
    {
        if (migrateFrom is null) return null;
        try
        {
            if (!StoredStateJson.TryRead(await fs.ReadFileAsync(migrateFrom).ConfigureAwait(false), out var state) || state is null) return null;
            if (copy) await Quietly(() => WriteAsync(state)).ConfigureAwait(false);
            return state;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Remove this state file's temp files older than <see cref="StaleTempMs"/>. Best-effort.</summary>
    private async Task ClearStaleAsync()
    {
        try
        {
            var now = wallNow();
            foreach (var name in (await fs.ReadDirAsync(dir).ConfigureAwait(false)).Where(IsTemp).ToList())
            {
                var path = Path.Combine(dir, name);
                double at;
                try
                {
                    at = await fs.MtimeMsAsync(path).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    at = now;
                }
                if (now - at >= StaleTempMs) await Quietly(() => fs.RemoveAsync(path)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Left for the next write.
        }
    }

    /// <summary>Remove all but the newest <see cref="KeepCorrupt"/> set-aside files of this state file, by the time in their names. Best-effort.</summary>
    private async Task PruneCorruptAsync()
    {
        try
        {
            var aside = (await fs.ReadDirAsync(dir).ConfigureAwait(false))
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(name => (Name: name, Match: CorruptName.Match(name[prefix.Length..])))
                .Where(file => file.Match.Success)
                .OrderByDescending(file => double.Parse(file.Match.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();
            foreach (var (name, _) in aside.Skip(KeepCorrupt)) await Quietly(() => fs.RemoveAsync(Path.Combine(dir, name))).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Kept until the next one is set aside.
        }
    }

    private static async Task Quietly(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }
}

/// <summary>
/// A save failed because another process kept saving the state under it
/// (<see cref="FileStorage.UpdateAttempts"/> times): the other's state is kept; try again.
/// </summary>
public sealed class StateConflictException : IOException
{
    /// <summary>The error.</summary>
    public StateConflictException()
        : base("the licence state was saved by another process meanwhile")
    {
    }
}
