# Changelog

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
