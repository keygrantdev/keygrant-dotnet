using System.Text.Json.Nodes;

namespace KeyGrant.Licensing;

/// <summary>
/// Where the licence state is kept. The default is <see cref="FileStorage"/>. Its calls are never cut
/// short by the SDK (a write cut short could be left half-written).
/// </summary>
public interface IStorageAdapter
{
    /// <summary>
    /// The stored state, or null for none. Only a state that is truly MISSING is null: any other
    /// failure to read must throw, or a save built on the read would write it over the real state.
    /// </summary>
    /// <param name="readOnly">
    /// True for a call behind one that has not finished: read and write nothing, not even to repair
    /// or migrate.
    /// </param>
    /// <returns>The state, or null.</returns>
    Task<StoredState?> ReadAsync(bool readOnly);

    /// <summary>Write the whole state, atomically: after a crash, the old state or the new one, whole.</summary>
    /// <param name="state">The state to keep.</param>
    /// <returns>A task that completes once the state is written.</returns>
    Task WriteAsync(StoredState state);

    /// <summary>
    /// Optional: save <paramref name="merge"/> of the state as stored at the moment of saving, merged
    /// again onto a newer one when another process saved in between. Used for every save when
    /// implemented; return false (the default) to have the SDK read, merge and write instead.
    /// </summary>
    /// <param name="merge">What to save, given the state as stored now (an empty one for none).</param>
    /// <returns>True once saved; false when this storage has no such update.</returns>
    Task<bool> UpdateAsync(Func<StoredState, StoredState> merge) => Task.FromResult(false);
}

/// <summary>
/// Somewhere that survives an uninstall (e.g. the Windows registry), keeping the earliest trial start
/// so a reinstall cannot reset a trial. The default does nothing; see <see cref="WindowsRegistryStash"/>.
/// </summary>
public interface IRegistryStash
{
    /// <summary>The trial start kept, in milliseconds since the Unix epoch, or null for none.</summary>
    /// <returns>The trial start, or null.</returns>
    Task<long?> ReadTrialStartAsync();

    /// <summary>Keep <paramref name="ms"/> as the trial start (the SDK only ever passes the earliest it knows).</summary>
    /// <param name="ms">Milliseconds since the Unix epoch.</param>
    /// <returns>A task that completes once kept.</returns>
    Task WriteTrialStartAsync(long ms);
}

/// <summary>The wall clock.</summary>
public interface IClock
{
    /// <summary>Now, in milliseconds since the Unix epoch.</summary>
    /// <returns>Milliseconds since the Unix epoch.</returns>
    long Now();
}

/// <summary>
/// Who this device is. The default, <see cref="MachineFingerprinter"/>, hashes the operating system's
/// machine id. May be called from more than one thread at once: a running trial's launch report reads
/// the device on a thread-pool thread, beside the calls.
/// </summary>
public interface IFingerprinter
{
    /// <summary>
    /// This device's fingerprint. A failure, or no answer in time, is read as "cannot tell this time".
    /// A fingerprinter with only this says nothing of what its fingerprint hashes, so its seats are
    /// NEVER held to their device: implement <see cref="IdentifyAsync"/> and report
    /// <see cref="DeviceKind.Machine"/> for a stable machine id that should be.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the SDK stops waiting.</param>
    /// <returns>The fingerprint.</returns>
    Task<string> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Optional, and what the SDK uses when implemented: this device's fingerprint (null when it cannot
    /// be read this time), what it hashes, and any other fingerprints this device has been activated
    /// under. The default returns null, meaning "not implemented": the SDK then calls
    /// <see cref="GetAsync"/>. A wrapper around <see cref="MachineFingerprinter"/> should delegate this,
    /// to keep its kind and its host-name alternate.
    /// </summary>
    /// <param name="timeout">How long the SDK gives the read (it stops waiting about a second after).</param>
    /// <param name="cancellationToken">Cancelled when the SDK stops waiting.</param>
    /// <returns>Who this device is, or null when not implemented.</returns>
    Task<DeviceIdentity?> IdentifyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult<DeviceIdentity?>(null);
}

/// <summary>An HTTP answer: its status, and its body parsed as JSON (null when it is none).</summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Json">The body as JSON, or null when empty or not JSON.</param>
public sealed record HttpResult(int Status, JsonNode? Json);

/// <summary>
/// The minimal HTTP surface the SDK needs. The default is <see cref="HttpClientAdapter"/>. May be called
/// from more than one thread at once: a running trial's launch report is sent on a thread-pool thread,
/// beside the calls.
/// </summary>
public interface IHttpAdapter
{
    /// <summary>
    /// POST <paramref name="body"/> as JSON. Must NOT throw for a 4xx or 5xx: return its status. Throws
    /// when nothing answered (no network, a timeout), which the SDK treats as no answer. The SDK keeps
    /// <paramref name="timeout"/> itself whatever the adapter does with it.
    /// </summary>
    /// <param name="url">The endpoint.</param>
    /// <param name="body">The request body.</param>
    /// <param name="timeout">How long this call may take before it is abandoned.</param>
    /// <param name="cancellationToken">Cancelled when the call is abandoned or the caller cancels.</param>
    /// <returns>The status and the body.</returns>
    Task<HttpResult> PostAsync(string url, JsonObject body, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Adapter overrides: any left null uses the default.</summary>
public sealed class LicenseAdapters
{
    /// <summary>Where the state is kept. Default: <see cref="FileStorage"/> at <see cref="FileStorage.DefaultPath(string)"/>.</summary>
    public IStorageAdapter? Storage { get; init; }

    /// <summary>The trial-start stash. Default: none (<see cref="NoopRegistryStash"/>).</summary>
    public IRegistryStash? Registry { get; init; }

    /// <summary>The wall clock. Default: <see cref="SystemClock"/>.</summary>
    public IClock? Clock { get; init; }

    /// <summary>Who this device is. Default: <see cref="MachineFingerprinter"/>.</summary>
    public IFingerprinter? Fingerprint { get; init; }

    /// <summary>HTTP. Default: <see cref="HttpClientAdapter"/>.</summary>
    public IHttpAdapter? Http { get; init; }
}
