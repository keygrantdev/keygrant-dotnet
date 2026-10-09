# Changelog

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
