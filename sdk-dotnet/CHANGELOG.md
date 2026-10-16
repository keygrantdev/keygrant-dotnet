# Changelog

## 0.7.0

- Metered uses: `License.UseAsync(meter, n = 1, cancellationToken)` spends `n` uses of a meter the key's licence
  model counts (1,000 exports for good, 50 renders each billing period) and answers a `UseResult`: `Ok` with
  `Left`, or `Error` (a `UseError`) with `Left` where it says one. The errors are `InvalidAmount`, `NoMeter`,
  `UsedUp` (with `Left`), `OfflineLimit` (with `Left`), `NotLicensed`, `Storage`, or the server's verdict on the
  key, the build or the device (`Revoked`, `Upgrade`, `Outdated`, `Device`). Online it is exact and atomic. When
  KeyGrant gives no answer (no network, a timeout, a 5xx, a 429) the device spends from its offline allowance for
  the meter, never past what it last heard is left, and answers `Ok` with `Offline` true; past the allowance it is
  `OfflineLimit`. A spend the allowance would refuse asks the server, whatever the last outage left behind, once a
  minute while it stays out of reach; one it covers waits a minute after an outage, doubling to an hour. It
  throws for no outcome (the one exception is a `CancellationToken` cancelled while the call waits its turn or
  before its request is sent: nothing is spent; cancelled while the request is out it stops waiting, spends as
  for no answer and returns its result, so read it even after cancelling), and its request waits at most 5 seconds (less when `HttpTimeout`
  is shorter). `n` is a `long`: anything outside 1 to 1,000,000,000 is `InvalidAmount`, asking nothing. A meter
  never takes a licence away: nothing in `StatusAsync()` reads one to decide whether the app is licensed.
- `LicenseStatus.Meters`, by meter code: `Left` (the newest figure, the lease's or the last `UseAsync` answer's, net
  of this device's unsynced spends), `Total`, `Offline` (this device's allowance, from the same figure), `Unsynced`
  and `Resets`. Offline, from the lease and the stored figures; on a `Licensed` status from a key's lease that
  names meters only, empty on every other status. It counts in the status's equality and
  its `ToString()`. The new types are `MeterStatus`, `UseResult` and `UseError`.
- The spends a device made with no answer are kept in the stored state (`StoredState.Spends`, with the newest
  figures a `UseAsync` answer gave in `MeterFigures`), merged across processes as the rest of the state is, and
  reported with the next `validate` (a check-in or renewal), `use` or `deactivate`, which now also names the
  device's fingerprints when it carries a report. They are dropped only when the server says it recorded them;
  a device back online reports at its next `StatusAsync()`. A journal never fails a renewal or a deactivation.
  Entries not reported within 180 days are dropped, and at most 64 are kept. A spend the SDK could not save (a
  disk that refuses its writes) is kept in memory, counted in `Left` and `Unsynced`, and reported once it has
  been saved (a spend whose request got no answer, and the take-back of one that was refused, are reported with
  the next request). `DeactivateAsync()` clears them with the licence even when KeyGrant could not be reached, so
  what the server did not hear is lost: at most the offline allowance of each meter.
- `StoredState` has `Spends` and `MeterFigures`, and `SpendEntry`, `SpendKind` and `MeterFigure` are new. A custom
  `IStorageAdapter` keeps them with the rest of the state; the file is the Electron SDK's, field for field. A
  stored journal or figure of another shape is read as empty, or left out.

## 0.6.0

- User-locked keys: `License.ActivateAsync(key, options)` and `License.ImportLicenseFileAsync(content, options)`,
  overloads that take an `ActivateOptions`. A licence model can let only the person a key was sold to activate
  a new device on it, known by the address on the key (`Email`), and, for a key that mails a code to that
  address, by the code too (`Code`). Both are optional; the existing calls are as they were and send neither.
  Only a new device is ever asked: `RefreshAsync()`, `StatusAsync()`, a renewal, a seat the key already holds
  and offline behaviour are unchanged. Nothing about the address or the code is stored.
- `LicenseError` has seven more members, the refusals an app can act on: `UserEmailRequired` (ask for the
  address), `UserEmailMismatch`, `UserCodeRequired` (a code was just mailed: ask for it), `UserCodeInvalid`,
  `TooManyCodes`, `TooManyAttempts` and `TryAgain` (the code could not be sent: nothing was activated, ask
  again). A `switch` over every `LicenseError` has seven more cases.
- An address longer than 254 UTF-16 units, or a code that is not exactly six ASCII digits (spaces inside it
  are dropped first), is refused by the SDK itself, for any key, before the server is asked
  (`UserEmailMismatch`, `UserCodeInvalid`). The conformance vectors' two new groups, `typedFor` and
  `userLockErrors`, hold this to the reference.

## 0.5.0

- Licence files: `License.ImportLicenseFileAsync(content)` takes a `.kglicense` file's content (bytes, or
  text in an overload; never a path) and verifies it offline. A key file activates its key, as
  `ActivateAsync(key)` would. An offline licence file is stored and licenses the app with no network,
  ever: `StatusAsync()` answers it with `Source = LicenseSource.File` and `LicensedTo`, before any key or
  trial, and `RefreshAsync()` asks nothing for it. It answers an `ImportLicenseFileResult` (`Ok`, `Kind`,
  `LicensedTo`, `Error`, `ActivateError`). `RemoveLicenseFileAsync()` drops it; `DeactivateAsync()`
  leaves it.
- `LicenseConfig.RevokedLicenseFiles` lists the ids of files this build refuses: a stored one reads as
  `Invalid` with the new reason `LicenseReason.RevokedFile`. A `switch` over every `LicenseReason` has
  one more case to handle.
- `LicenseStatus` has `Source` and `LicensedTo` (null on every status not from a file), and they count in
  its equality and its `ToString()`; `StoredState` has `LicenseFile`, the stored offline file's token.
- `LicenseFileFormat` holds the extension (`.kglicense`), the media type and the most a file may be, and
  the types `LicenseFileKind`, `LicenseFileError`, `LicenseSource` and `LicensedTo` are new.
- `Entitlement` moved to a file of its own; nothing about it changed.

## 0.4.0

- `License.PurchaseUrlAsync` is removed, and the SDK no longer asks for a bought key at `StatusAsync`,
  `RefreshAsync` or `StartTrialAsync`. To sell from the app, open your Payment Link from your own Buy
  button; the customer types the key from the delivery email (`ActivateAsync`). A purchase secret an
  earlier version stored is ignored, and left out the next time the SDK saves its state.
- `StoredState` no longer has `PurchaseToken`, `PurchaseTokenAt` or `ClaimAskedAt`: a storage adapter or
  test code that sets them stops compiling, and the same names among its `AdditionalFields` are never written.

## 0.3.0

- Key sets: `LicenseConfig.PublicJwks` takes the product's public keys (the key that signs its leases, and
  the next one once staged), and `PublicJwk.ParseSet` reads a JWK set. A lease naming its key (`kid`) is
  verified by that key, one naming none by each key in turn. `PublicJwk` alone is a key set of one, so an
  app configured before keeps working.
- A lease signed by a key the build does not carry is `Invalid` with the new reason
  `LicenseReason.UnknownKey`, rather than `Tampered`.
- Each activation, validate and claim sends the ids of the keys the build carries (`kids`).
- `License.HasAsync(code)` and `License.RequireAsync(code)` answer, offline from the stored lease, whether
  the licence grants an entitlement; `RequireAsync` throws `MissingEntitlementException`.
  `EntitlementGrants.Grants` answers it of a status's entitlements.
- `PublicJwk` is no longer `required` on `LicenseConfig`: give it or `PublicJwks`.
- `PublicJwk.Parse` reads a public key as every KeyGrant SDK does: `kty` "OKP" and `crv` "Ed25519" are
  required, and a JWK whose `use` is not "sig", whose `key_ops` do not name "verify" or whose `alg` is not
  "EdDSA" or "Ed25519" is refused.
- A public key of 32 bytes that are no point on the curve no longer throws when the license is made, as
  every KeyGrant SDK reads one: it verifies no lease.
