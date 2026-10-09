# KeyGrant.Licensing (.NET)

KeyGrant licensing for .NET desktop apps (WPF, WinForms, Avalonia, MAUI, console): a licence is a
signed lease (a JWT, EdDSA / Ed25519) the app verifies **offline** against the product's public key.
The app only talks to KeyGrant to activate, to renew a lease near or past its end, to report a
newer major version, to pick up a key the customer bought from the app, and to report a running
trial's launch (at most once a day, in the background).

The rule every line serves: **licensing failure fails in favour of the paying customer.** No network,
an outage, a timeout, a 5xx, a 429, an unrecognised 4xx, an answer that cannot be used, or a licence
file that cannot be read just now never takes a licence away while the device holds a valid lease.
Only the server's explicit verdicts (revoked, upgrade or update required, another device) and the
lease's own end do.

This is a port of the Electron SDK (`@keygrant/electron`), which is the reference: it behaves
the same, reads and writes the same state file, computes the same fingerprints, and its test suite
reproduces every answer in `conformance/vectors.json`. See [Parity with the reference](#parity-with-the-reference).

## Install

```bash
dotnet add package KeyGrant.Licensing
```

Targets `net8.0` (runs on .NET 8 and later, on Windows, macOS and Linux). One dependency:
`BouncyCastle.Cryptography` (MIT), for Ed25519, which .NET 8 does not have.

## Quick start

Create **one** `License` per app process and share it (calls on it run one at a time).

```csharp
using KeyGrant.Licensing;

var license = new License(new LicenseConfig
{
    Product = "sluice",                         // the product slug in KeyGrant
    ApiBaseUrl = "https://api.keygrant.dev",
    PublicJwk = KeyGrantKeys.Sluice,            // the product's PUBLIC JWK, embedded (below)
    Major = 3,                                  // this build's major version: set it (a 0.x build is 1)
    DeviceName = Environment.MachineName,       // optional: a label in the customer's device list
});

// At launch: offline-first, never throws for storage.
LicenseStatus status = await license.StatusAsync();

// The customer pastes a key:
ActivateResult result = await license.ActivateAsync(keyTextBox.Text);
if (!result.Ok) ShowError(result.Error);         // LicenseError.InvalidKey, Limit, Revoked, Network, ...

// "I've paid" / "Try again" on a lockout screen: ask the server now.
status = await license.RefreshAsync();

// Free this device's seat (the local licence is dropped either way):
DeactivateResult released = await license.DeactivateAsync();
if (!released.Released) ShowSeatMayStillBeHeld(); // KeyGrant did not confirm it: say so, never "the seat is free"

// A trial, begun once (StatusAsync reports a running trial's launch by itself):
status = await license.StartTrialAsync();       // State Trial, TrialDaysLeft, TrialEndsAt

// A Buy button for a customer with no key, picked up on return with nothing typed (below):
string? buyUrl = await license.PurchaseUrlAsync("https://buy.stripe.com/your-link");
```

### Console

```csharp
var status = await license.StatusAsync();
switch (status.State)
{
    case LicenseState.Licensed:
    case LicenseState.Trial:
    case LicenseState.Unknown:   // the licence file is busy just now: keep running
        RunTheApp();
        break;
    default:
        Console.WriteLine($"Not licensed ({status.State}, {status.Reason}). Enter a key:");
        var result = await license.ActivateAsync(Console.ReadLine() ?? "");
        Console.WriteLine(result.Ok ? "Activated." : $"Could not activate: {result.Error}");
        break;
}
```

### WPF

```csharp
public partial class App : Application
{
    public static License License { get; } = new(new LicenseConfig
    {
        Product = "sluice",
        ApiBaseUrl = "https://api.keygrant.dev",
        PublicJwk = KeyGrantKeys.Sluice,
        Major = 3,
        Adapters = new LicenseAdapters { Registry = new WindowsRegistryStash("sluice") }, // optional
    });

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var status = await License.StatusAsync();      // safe to await on the UI thread
        Window window = status.State is LicenseState.Licensed or LicenseState.Trial or LicenseState.Unknown
            ? new MainWindow()
            : new ActivationWindow(status);
        window.Show();
    }
}
```

### WinForms

```csharp
internal static class Program
{
    public static readonly License License = new(new LicenseConfig { /* as above */ });

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public partial class MainForm : Form
{
    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var status = await Program.License.StatusAsync();
        if (status.State is not (LicenseState.Licensed or LicenseState.Trial or LicenseState.Unknown))
        {
            using var dialog = new ActivationDialog(Program.License, status);
            dialog.ShowDialog(this);
        }
    }
}
```

The SDK's own code never captures a synchronization context (`ConfigureAwait(false)` throughout), so
it does not deadlock a UI thread that blocks on it; awaiting it is still the right way.

## Configuration

| `LicenseConfig` | Meaning |
|---|---|
| `Product` (required) | The product slug, matching the KeyGrant product. |
| `ApiBaseUrl` (required) | The API base, e.g. `https://api.keygrant.dev` (one trailing slash is dropped). |
| `PublicJwk` (required) | The product's Ed25519 public key as a JWK: a JSON string converts implicitly, or `new PublicJwk { X = "..." }`. |
| `Major` | This build's major version (default 1). Keys are bound to, and cover, majors: set it. Read as KeyGrant's server reads a version: a 0.x build (`Major = 0`) is major 1, and so is any value below 1. |
| `DeviceName` | A label for this device in the customer's device list; cut to 120 characters. |
| `DeviceHold` | Whether a licence is held to the device it was activated on (default `true`). See below. |
| `HttpTimeout` | How long a request may take before it is abandoned (default 15 s; at most a day). The SDK keeps it whatever the HTTP adapter does. An adapter's token is cancelled only when the SDK gives up on its call (its bound passed, or the caller stopped waiting), never after it answered: an answer stands, whatever the token's callbacks do. |
| `Adapters` | Overrides for `Storage`, `Registry`, `Clock`, `Fingerprint` and `Http`; any left null uses the default. |
| `TimeProvider` | What the SDK's timers run on (default `TimeProvider.System`); for tests. |

The configuration is checked when the `License` is created: an empty product or API, a bound that is
not positive, or a public key that is not an Ed25519 public JWK throws `ArgumentException`.

## Trials: what the app calls

```csharp
var status = await license.StatusAsync();
if (status.State == LicenseState.Unlicensed) status = await license.StartTrialAsync(); // begins the trial, once
```

A running trial reports its launch to KeyGrant by itself: `StatusAsync()` sends it at most once a day,
on a thread-pool thread, answering without waiting for the request and never failing offline (it
saves the day's stamp first), so the dashboard's trial funnel sees the trial come back. The app does not need to call `StartTrialAsync()` on every launch for
that. `StartTrialAsync()` still re-syncs the trial when called: a change to the trial's length or
entitlements reaches a running trial there.

## Buying from the app: no key to type

Give a customer who holds no key a Buy button that opens your Stripe Payment Link through
`PurchaseUrlAsync`. When they come back to the app, the key they bought is picked up and this device
activated on it, with nothing typed:

```csharp
using System.Diagnostics;

var checkoutOpened = false;
var url = await license.PurchaseUrlAsync("https://buy.stripe.com/your-link");
if (url is not null) // null while a key is held: use UpgradeUrlAsync for an upgrade
{
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    checkoutOpened = true;
}

// When the customer is back (the window is activated again, say), and only while a checkout is
// open: RefreshAsync on a licensed install validates online every time.
if (checkoutOpened && (await license.StatusAsync()).State != LicenseState.Licensed)
{
    var status = await license.RefreshAsync(); // Licensed once the checkout has gone through
    if (status.State == LicenseState.Licensed) checkoutOpened = false;
}
```

- **How:** `PurchaseUrlAsync` keeps a one-time secret for this install (256 random bits from the
  platform's CSPRNG, never the device's fingerprint) and sets the link's `client_reference_id` to a
  hash of it, so a link that leaks (browser history, a screenshot) claims nothing. The checkout keeps
  that reference on the key it mints, and the SDK asks for the key with the secret (the claim): at
  once on `RefreshAsync()` or `StartTrialAsync()`, and from `StatusAsync()` at most every five minutes
  for a day after the link (after that, `RefreshAsync()` or `StartTrialAsync()` still ask). Until the
  key is bought, the status is what it was (the trial, or unlicensed). Once it is, the device is
  activated on it exactly as `ActivateAsync(key)` would, and licensed.
- **Calling it again** hands out the same secret's link, so the key bought through either link is
  picked up (paying twice buys two keys; the second comes by email). A secret is dropped 30 days after
  the last link, once its key is picked up, when a key is activated or the device deactivated, or when
  the server answers that it never will be picked up (another device took it, or the product's
  "Converts in place" switch is off: the customer then types the key from the delivery email). A drop
  is this device's alone: it revokes nothing on the server.
- **It throws** `ArgumentException` when the link is not an absolute URL (before anything is read),
  and throws when the licence file cannot be read or the secret cannot be saved (a link whose secret is
  lost could never be picked up). Behind a call that has not finished, a held secret's link is handed
  out as it is, and with none held it throws `InvalidOperationException`.
- **Turn the product's "Converts in place" switch on** once your app uses `PurchaseUrlAsync`: it is
  off by default, and until it is on every pickup is refused and the customer types the key from the
  delivery email. Know the trade before you do: whoever opens a checkout owns its pickup. Your Payment
  Link is public, so anyone can add a `client_reference_id` of their own to it and get buyers to pay
  through their copy of the link; the key bought that way is picked up by the link's maker, not the
  buyer. A link from the app is the buyer's own; a leaked one claims nothing, as it carries only a hash
  of the app's secret. A planted pickup shows in the key's history, and you can free the seat for the
  buyer.
- **What the claim sends:** the secret, the fingerprint a first activation would use (the device's
  own; at `StartTrialAsync()` the host name when the machine id does not read), and every fingerprint
  the run read (its own first, then the host name and any other alternates, held to its device or not),
  keeping those of 1 to 200 characters and at most 4, so the server knows this device again on a
  repeated claim.

## What each status means

`StatusAsync()` answers a `LicenseStatus` (`State`, `Reason`, `Key`, `TrialDaysLeft`, `TrialEndsAt`,
`Test`, `Entitlements`, `Stalled`):

| State | Reason | Meaning | What to show |
|---|---|---|---|
| `Licensed` | | A valid lease licenses this build on this device. | The app, in the edition its `Entitlements` name (below). Check `Test` (below). |
| `Trial` | | A trial is running on its signed trial lease: `TrialEndsAt` (ms) is the end that lease was signed to, `TrialDaysLeft` the days to it, rounded up. | The app, with a trial banner; its `Entitlements` are the trial lease's. |
| `Unknown` | `Storage` | The licence file could not be read just now (an antivirus scan holding it). Nothing is known either way. | Keep running as before; never the activation screen for it. Ask again shortly. |
| `Expired` | `Offline` | The lease ran out and the server could not be reached to renew it. | "Connect to the internet to renew your licence", and `RefreshAsync()` on a retry. |
| `Expired` | `Trial` | The trial has ended, or its trial lease is not valid here. A trial end stored in the licence file is unsigned and never runs a trial on its own. | Buy / enter a key. |
| `Invalid` | `Revoked` | The key was revoked, refunded or expired, or this device was released. | Buy / enter a key; `RefreshAsync()` once they say they renewed. |
| `Invalid` | `Upgrade` | This build is NEWER than the key covers. | Offer the upgrade (`UpgradeUrlAsync`), or the older version. |
| `Invalid` | `Outdated` | This build is OLDER than the key's lowest version. | "Update the app": the key is for a later version. |
| `Invalid` | `Device` | The licence was activated on another device (this machine changed, or the licence file was copied). | "Activate again on this device" (`ActivateAsync(key)`). |
| `Invalid` | `Tampered` | The stored lease is not for this licence at all (another key, activation or product, a bad signature). | Enter a key. |
| `Unlicensed` | | No licence and no trial on this device. | Enter a key or start a trial. |

`Stalled` is true when an earlier call on this `License` has not finished (a storage adapter stuck on a
dead disk, say): the answer then comes from what is stored, with no ask and no write.

`ActivateAsync` answers `ActivateResult` (`Ok`, `Error`): `InvalidKey` (the server knows no such key,
or what was entered cannot be one: nothing, or over 120 characters), `Limit` (activated on as many
devices as it allows), `Revoked`, `Upgrade`, `Outdated`, `Network` (no answer, or an answer it could
not use, or the device could not be identified in time, or an earlier call has not finished: try
again), `InvalidLease` (the server's lease failed verification: a wrong public key, usually). The key
is trimmed and upper-cased before it is sent and stored. `ActivateAsync` and `DeactivateAsync` THROW
when the licence file cannot be read or saved (the customer asked, and can try again);
`DeactivateAsync` also throws `InvalidOperationException` behind a call that has not finished.

`DeactivateAsync` drops the local licence (keeping only trial evidence) and answers `DeactivateResult`:
`Released` is true when KeyGrant confirmed the release (a 200), or when the device held no activation;
false when it could not be asked, or answered anything else (a 5xx, a 429, a proxy's page). Then the
seat may still be held: tell the customer so (the developer's dashboard, or support, can release it),
never that it is free. The local licence is dropped either way.

It never releases another device's seat. A licence file copied or restored from another PC (a roaming
profile, a backup, a cloned disk) carries that PC's activation: its lease, valid or run out, is held to
that machine's id, or a device refusal stands for this one. Then `DeactivateAsync` sends nothing, only
drops it here, and answers `Released` true, as this device holds no seat. Releasing it would lock the
other PC out at its next check-in.

### When it goes online

`StatusAsync()` answers from what the device holds, and asks the server only:

1. on a valid lease, when this build is a newer major than the server last heard (on an open-ended
   lease), or the lease is near its end (less than 7 days left, or less than half its life): bounded
   to 5 s, and the valid lease is the answer when that gets no verdict;
2. on a lease past its end or not valid here, so the server can issue a fresh one;
3. on a stored refusal that still stands for this build, so a renewed or reinstated key heals;
4. on a trial whose lease does not verify here while its stored end is still ahead (a lease signed
   before the product's key was rotated, or one signed to end short of the trial's end), so the server
   can sign it again: asked as `StartTrialAsync()` asks, bounded to 5 s, and its answer saved as that
   saves one. Until a trial lease this build can verify comes back, it is `Expired`/`Trial`;
5. with no key held and a purchase secret held (`PurchaseUrlAsync`), for the key bought with it, for a
   day after the link: bounded to 5 s, the device read for 2 s, only under the device's own
   fingerprint (when it does not read, nothing is asked and nothing saved), and asked again 5 minutes
   after an answer that brought no key. `RefreshAsync()` asks at once, under `HttpTimeout`, until the
   secret's 30 days are up; `StartTrialAsync()` asks at once, bounded to 5 s, under the one device read
   it makes for both its asks (10 s), the host name allowed when the machine id does not read;

and in each case only once the wait after the last ask is over: **1 minute** after an ask nothing
answered (offline), **1 hour** after one the server answered without a lease (a refusal, a 5xx, a 429)
or without a trial this build can verify. `RefreshAsync()` asks at once. A new lease ends the wait.

Besides those asks, a running trial (its trial lease valid, no key held) reports its launch at most
once a day: its stamp saved first, then the report sent on a thread-pool thread, bounded to 5 s, never
awaited and its answer not read. Since it runs beside the calls, a custom `IHttpAdapter` or
`IFingerprinter` may be called from more than one thread at once (the defaults may).

Calls on one `License` run one at a time. A call waits for the one before it, but at most its
`HttpTimeout` + 15 s from when that one started (+ 5 s more after a `StartTrialAsync`, which may make
a claim and then its trial ask); then it runs read-only (`Stalled`).

Every call but `UpgradeUrlAsync` takes an optional `CancellationToken`. A call cancelled while it waits
its turn throws `OperationCanceledException` and steps out of the queue. Once it runs, cancelling
`StatusAsync`, `RefreshAsync` or `StartTrialAsync` only stops it waiting on the server or the device: it
answers from what the device holds, as when nothing answers, and records nothing of the ask it cut
short (a claim included: the secret is kept, with no wait). `PurchaseUrlAsync` can be cancelled only
while it waits its turn: once it runs, it only reads and saves. A licensed answer never becomes an
exception. `ActivateAsync` and `DeactivateAsync` throw
`OperationCanceledException` until the server has answered, and save nothing.

## Embedding the public key

The product's **public** key is the `publicJwk` field of
`GET https://api.keygrant.dev/v1/products/<slug>/pubkey`, which is public and needs no API key (the
dashboard shows the key only as SPKI and a `.pem`). Fetch it once and ship it with the app, e.g.

```bash
curl -s https://api.keygrant.dev/v1/products/sluice/pubkey
# {"publicJwk":{"kty":"OKP","crv":"Ed25519","x":"..."},"keys":[...]}
```

and embed that `publicJwk` value at build time:

```csharp
internal static class KeyGrantKeys
{
    public const string Sluice = """{"kty":"OKP","crv":"Ed25519","x":"Ohg4h8SrYQ0m1su_zVacJLLwf_6rNsvOtV5oWY6CzJs"}""";
}
```

(that `x` is the conformance vectors' TEST key: use your product's own). An embedded resource or a
`PublicJwk { X = ... }` works as well. Never ship the private key: a JWK carrying `d` is refused.
The public key only verifies; holding it lets nobody sign a lease.

## `DeviceHold`: holding a licence to its device

By default a lease whose seat is keyed by the operating system's machine id is held to that device:
a licence file copied to another machine reads `Invalid`/`Device` there, and the server refuses its
renewal. An honest device is never refused for how it is identified: a host-name seat, a seat of
unknown kind, and a run that cannot read the machine id in time are not judged.

Set `DeviceHold = false` for a product deployed on **non-persistent, pooled VDI** (or a home folder
shared between machines), where every session may be another machine with the same licence file: no
fingerprint kind is reported, no lease is judged by its device, and no fingerprints are sent to the
server to check. The device limit still counts seats.

The default fingerprint is the machine id, hashed so it never leaves the machine:
`MachineGuid` (Windows, 64-bit registry view), `IOPlatformUUID` (macOS), `/etc/machine-id` or
`/var/lib/dbus/machine-id` (Linux, unless on a tmpfs/ramfs or written at this boot). With none to read,
the host name, platform and architecture, hashed. The same machine gives the same fingerprint under
this SDK and the Electron one. A custom `IFingerprinter` with only `GetAsync` is never held to its
device; implement `IdentifyAsync` and report `DeviceKind.Machine` for a stable machine id that should be.

## Test licences

A test licence (made from the dashboard, a `kg_test_` API key, or a Stripe test-mode checkout) is
signed with the product's real key and licenses the app like any other; it lapses within 30 days and
is never billed. `LicenseStatus.Test` is true for one. Refuse them in a release build if you want:

```csharp
#if !DEBUG
if (status.Test) status = status with { State = LicenseState.Invalid, Reason = LicenseReason.Tampered };
#endif
```

## Entitlements

A licence can grant more than the base product: an edition, a number of seats, a feature. The lease
signs them (its `ent` claim) and the status hands them to the app as `Entitlements`, read offline like
everything else on the lease:

```csharp
var status = await license.StatusAsync();
var pro = status.Entitlements.TryGetValue("edition", out var edition) && edition.Text == "pro";
var seats = status.Entitlements.TryGetValue("seats", out var granted) ? granted.Number ?? 1 : 1;
var beta = status.Entitlements.TryGetValue("beta", out var flag) && flag.Flag == true;
```

- **Type:** `IReadOnlyDictionary<string, Entitlement>`, never null. An `Entitlement` is exactly one of
  a flag (`Flag`, a `bool?`), a number (`Number`, a `double?`) or a text (`Text`, a `string?`); the
  accessor of any other kind is null. Two are equal when they are the same kind with the same value
  (numbers as doubles, so `5` and `5.0` alike), and `ToString()` writes the value. The map is
  read-only, and each status holds its own copy: change a copy, not the status.
- **When it holds them:** on a `Licensed` status (from the licence's lease: straight from the device,
  or after an activation, a renewal or `RefreshAsync()`, and on a `Stalled` answer from the lease
  stored) and on a `Trial` status (from the trial's lease), when that lease grants at least one.
- **When it is empty:** on every other status (`Expired`, `Invalid` for any reason, a stored refusal
  included, `Unlicensed`, `Unknown`), and when the lease grants none. `LicenseStatus` compares its
  entitlements by content, not by reference, so two statuses from the same lease are equal.
- **A malformed claim never costs the licence:** a member that is not a flag, a number or a text (an
  object, a list, null) is left out, and an `ent` that is not an object at all reads as none. The SDK
  enforces none of the server's caps on them (how many, how long); the signature is what it trusts.

## Where the state is kept

Unless the app gives its own `Storage` adapter, one `<product>.json` per product, per user, on this
machine (`FileStorage.DefaultPath(product)`):

- Windows: `%LOCALAPPDATA%\KeyGrant\` (else `%USERPROFILE%\AppData\Local\KeyGrant\`); never the roaming AppData.
- macOS: `~/Library/Application Support/KeyGrant/`
- Linux and others: `$XDG_CONFIG_HOME/keygrant/` (else `~/.config/keygrant/`)

It is the Electron SDK's file, field for field, so an Electron and a .NET build of the same product
on one machine share one licence. `new FileStorage(path, new FileStorageOptions { MigrateFrom = old })`
keeps it elsewhere (copying the state at `old` over the first time the new path is missing).

- Written atomically: a temp file beside it, flushed to the disk, renamed over it (a rename Windows
  refuses as busy is tried again for about 1.5 s); the folder flushed after on macOS and Linux. A write
  that lands clears temp files a crash left (over a minute old).
- Only a MISSING file is no state, and then the newest whole temp file beside it is read first. Any
  other read failure is tried again for about a second, then `StatusAsync` answers `Unknown`/`Storage`.
- A file that does not parse is set aside as `<product>.json.<ms>.corrupt` (the newest three kept)
  and an empty state written in its place.
- Two processes (two instances of the app) never lose each other's saves: each save stamps a revision
  (`rev`), checks it again just before the rename and merges onto a newer state (up to 5 times, then
  `StateConflictException`); answers are ordered by when they were asked for, so an older answer never
  replaces a newer one.
- A call running read-only writes nothing at all.

The trial start also goes to the `Registry` stash when one is given: `WindowsRegistryStash` (opt-in,
`HKCU\Software\KeyGrant\<product>`, value `TrialStart`) survives an uninstall, so a reinstall does not
reset the trial. It keeps the format every KeyGrant SDK keeps, so a trial another SDK stashed for the
product counts: milliseconds since the epoch, written as a `REG_QWORD`; read from a `REG_QWORD` or a
`REG_SZ` of decimal digits. Anything else is no evidence: another type (a `REG_DWORD` read as a time is
1970), 0 or less, or 2^53 or more. The stash can cost the licence nothing: one that cannot be read is
no evidence, and one that cannot be written is ignored (the server's trial is still saved).

A custom `IStorageAdapter` must return null only for a state that is truly missing, write atomically,
and write nothing on a `readOnly` read; implementing `UpdateAsync` (merge onto the state as stored at
the moment of saving) makes saves from several processes safe.

## Upgrades

`await license.UpgradeUrlAsync(paymentLink)` returns the upgrade offer's payment link with this
device's ACTIVATION id as `client_reference_id` (never the key), or null without an activation; a
checkout through it extends the key the customer holds instead of minting another. The link is built
as `PurchaseUrlAsync` builds its own: the link's other parameters and its fragment kept as written,
any `client_reference_id` already on it replaced.

## Building and testing

```bash
dotnet build sdk-dotnet/KeyGrant.Licensing.sln -c Release
dotnet test sdk-dotnet/KeyGrant.Licensing.sln
```

The suite loads `conformance/vectors.json` from the repo root (or the file `KEYGRANT_VECTORS` names) and
answers every group from the vector's own JSON as the reference does, through the same functions the
engine itself calls: lease verification (canonical base64url, the claims as parsed) and `checkLease`,
trial leases, fingerprints, claims, machine and host fingerprints, kept majors, early renewal, asks,
standing refusals, trial statuses, refusal and lease patches, `keyedBy`, merges, the clock guard, its
correction and the time a save records as seen, the wire (error words, refusals, keys, device names,
no-answer statuses, lease and trial bodies), mountinfo, the machine-id reads, what a renewal's answer
saves and says, when a status asks on a valid lease, who a device is (install memory, `DeviceHold`),
what the default fingerprinter answers for each machine-id reading, the status a stored lease gives with
no verdict, a trial's status from its start and its trial lease (entitlements included in every status),
the bits of the double each entitlement number reads as, and, for buying from the app: the major a build
runs as, the purchase link, how long a secret is held, when a claim falls due, a claim's body and what
its answer saves and says, and when a running trial reports its launch, the reference a link carries for
a secret, and the body a claim sends. That is every group and every vector but the Windows `machineIds`
cases, which script `reg.exe`'s output, which this SDK does not parse (it reads the registry), and the
`majors` cases holding a fraction, which an `int` major cannot hold. A group the file holds that the
harness does not answer fails the suite.

Its behaviour tests cover activation, offline status, renewal, refusals and their waits, outages that
never lock out, the clock guard, trials, deactivation, `DeviceHold`, storage that cannot be read, calls
stuck behind one another, entitlements on every status that carries them, the purchase link and the
pickup, the launch report, a 0.x major, the file storage (atomic writes, temp recovery, corrupt files,
revision conflicts between two processes) and the machine-id readers.

`SystemTests` drive the real readers and writers on the system the suite runs on: the registry, a
file another process holds and a rename over it (Windows); `/etc/machine-id`, `/proc` and the folder
`fsync` (Linux); `ioreg` (macOS); symbolic and hard links everywhere. A test for another system, or for
something this machine cannot do (symbolic links on Windows need Developer Mode or an elevated shell;
the host-name check needs a `node` of this architecture), is reported skipped with its reason.
[`github-workflow-example.yml`](github-workflow-example.yml) runs the suite on Linux, macOS and Windows
(copy it to `.github/workflows/` to enable it). On a Windows machine, the Linux half runs in Docker,
from the repo root:

```bash
docker run --rm -v "$PWD:/repo:ro" mcr.microsoft.com/dotnet/sdk:8.0 bash -c \
  "mkdir /work && cp -r /repo/conformance /repo/sdk-dotnet /work && cd /work/sdk-dotnet && rm -rf */bin */obj && dotnet test KeyGrant.Licensing.sln"
```

## Parity with the reference

Behaviour is the Electron SDK's; these are the differences, each on purpose:

| Difference | Why |
|---|---|
| Windows' `MachineGuid` is read with the registry API (`RegistryView.Registry64`), not by running `reg.exe`. Same value and view; only a `REG_SZ` counts and the first word is taken, as `reg query`'s output gives it. Access denied is "no id" (as `reg` exiting 1); any other registry error is "cannot tell this time", where the reference read any non-zero `reg` exit as "no id". | No child process, no `%SystemRoot%` question. "Cannot tell" never flips a device's fingerprint; "no id" switches it to the host name for good. |
| The host name is `GetHostNameW` on Windows and `gethostname` elsewhere (what Node's `os.hostname()` calls), the platform and architecture are Node's names (`win32`/`darwin`/`linux`, `x64`/`arm64`/`ia32`/`arm`) for this PROCESS. | So the host-name fingerprint is the Electron SDK's on the same machine (checked against `node` in the tests when it is installed). An app built for another architecture than its Electron twin hashes another arch, as two Electron builds would. |
| The public key is checked when the `License` is created (`ArgumentException`); a JWK holding `d` is refused. | The reference imports it lazily and a bad key makes every `status()` reject. A configuration error belongs at startup. |
| The default storage does not migrate `<working directory>/.keygrant/<product>.json`. | That path is the legacy location older Electron SDK versions used. `FileStorage.LegacyPath(product)` + `MigrateFrom` reads it if you need to. |
| Every call (but `UpgradeUrlAsync`) takes a `CancellationToken`. Cancelled while it waits its turn, a call throws `OperationCanceledException`; once running, `StatusAsync`, `RefreshAsync` and `StartTrialAsync` stop waiting and answer what the device holds (never an exception, and no wait recorded for the ask they cut short), while `ActivateAsync` and `DeactivateAsync` throw until the server has answered. | .NET convention; the reference cannot be cancelled. A status is awaited at launch, so a cancellation never turns a licensed answer into an exception. |
| `HttpTimeout` must be positive and at most a day. | .NET timers take no more; an unbounded request would defeat the bound. |
| `Major` is an `int`: a fractional major cannot be configured (the reference cuts one to its whole number), so the `majors` vectors holding a fraction are skipped. A negative one runs as 1, as in the reference. | Idiomatic C#; a version's major is a whole number. |
| A payment link is read by `System.Uri`, whose normal form differs from the WHATWG parser's in two ways that keep the URL the same: a percent-encoded letter, digit or `-._~` in the query is written as that character, and `'` in the query is left as it is (WHATWG writes `%27`). | The link is read as the platform's URL parser reads it; the query is then edited as text as the reference edits it. |
| `PurchaseUrlAsync` throws `ArgumentException` for a link that is not an absolute URL, and `InvalidOperationException` behind a call that has not finished with no secret held, where the reference rejects with a `TypeError` and an `Error`. | .NET convention. |
| The launch report runs on a thread-pool thread (`Task.Run`), so the HTTP adapter and the fingerprinter may be called from two threads at once; the tests wait for it through an internal `LaunchReportSettled`. | An SDK with blocking I/O sends it on a thread of its own, never on the caller's. |
| State files are read leniently: a known field of the wrong type (a refusal word from a later SDK, a number written as text, a string holding an escaped lone surrogate) reads as absent, a byte-order mark is forgiven, and fractional milliseconds are floored. What such a field held is written back as it was, unless a save sets or clears it; unknown fields are kept and written back, as the reference does. A field NAME holding a lone surrogate is dropped. | The reference keeps whatever `JSON.parse` gives, so a damaged field misbehaves later; reading it as absent keeps the rest of the licence, and writing it back keeps what an Electron build of the same product stored. The reference sets a file with a byte-order mark aside as corrupt. |
| JSON is read to any depth `JSON.parse` reads, in one pass, but a member nested more than 64 levels deep is never held as a `JsonElement`: a state file's is kept as its text and written back (it is not in `StoredState.AdditionalFields`), a server answer's is left out of the body, and a lease's unknown claim is never read, nor a member of `ent` that is not a flag, a number or a text (dropped, however deep; any other claim the schema checks cannot nest, so one that deep is `bad-claims` there too). | System.Text.Json's `JsonDocument` takes time that grows with the square of the nesting: a state file with a member 100 000 levels deep would take seconds to read, and a million minutes, which would hang a status at launch. Nothing the SDK reads nests. |
| A trial body's `trialStart` and `trialEndsAt` are floored to whole milliseconds, and one that is not finite (`1e400`) is no trial body: the trial evidence the device holds is answered. | Times are `long` here. `JSON.parse` reads `1e400` as `Infinity`, which the reference would save as `null`. The server sends whole milliseconds. |
| On macOS and Linux, `ioreg` exiting with a code above 128 is read as killed by a signal ("cannot tell this time"), where `execFile` tells an exit code ("no id") from a signal. | .NET reports a signal as exit code 128 + the signal and cannot tell the two apart. Reading it as "cannot tell" keeps the device's fingerprint (it is read again next time); "no id" would switch it to the host name for good. |
| `WindowsRegistryStash` is this SDK's own (the Electron SDK ships only a no-op stash), in the format every KeyGrant SDK keeps: `REG_QWORD` milliseconds written; a `REG_QWORD` or a decimal `REG_SZ` read; another type, 0 or less, or 2^53 or more is no evidence. | So a reinstall cannot reset a trial on Windows, whichever SDK stashed it. A value that cannot be a time would start the trial in 1970 on the server, or have it refused. |
| A string in a server body, or in a lease claim the SDK reads (`sub`, `aid`, ...), holding an escaped lone surrogate is no string: no verdict, or `bad-claims`. Anywhere else in a body (a member the SDK does not read) it costs nothing else in the answer; unknown claims are fine. An entitlement whose name or text holds one is dropped, as a malformed member is: the licence and the other entitlements stay, where the reference would keep it. The default HTTP adapter reads a body as UTF-8 whatever charset the response names, as `fetch` does. | System.Text.Json cannot hold one, and an exception there would fail a status. The server never signs an entitlement name or text holding one. |
| A key is upper-cased with `ToUpperInvariant` (simple case mapping); JavaScript's `toUpperCase` maps fully (`ß` to `SS`). Trimming uses JavaScript's white space exactly. | The key alphabet is ASCII, where they agree. |
| `Test` and `Stalled` are `false` rather than absent, and `Entitlements` empty rather than absent, with `LicenseStatus` comparing it by content; times are `long` milliseconds; `HttpResult.Json` is a `JsonNode`; `IStorageAdapter.UpdateAsync` and `IFingerprinter.IdentifyAsync` are default interface methods (returning `false` / `null` for "not implemented"). | Idiomatic C# for the reference's optional members. |
| The default storage opens files sharing read, write and delete, as libuv opens every file on Windows. | A reader of ours never refuses another process its own reads and writes. It does not let a rename over the file through: Windows refuses a rename over a file anyone holds open, whatever its sharing (access denied), and that is tried again as busy for about 1.5 s, as the reference tries `EPERM` again. |

## Dependencies

`BouncyCastle.Cryptography` (MIT) for Ed25519 verification; everything else is in .NET 8.
