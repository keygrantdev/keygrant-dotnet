# KeyGrant for .NET

`KeyGrant.Licensing` licenses .NET desktop apps with [KeyGrant](https://keygrant.dev): signed leases
verified offline, activation, trials and renewals, failing in favour of the paying customer.

```sh
dotnet add package KeyGrant.Licensing
```

How to use it is in [sdk-dotnet/README.md](sdk-dotnet/README.md), and the full guide is at
https://keygrant.dev/docs.

## Building from source

```sh
cd sdk-dotnet
dotnet test KeyGrant.Licensing.sln
```

The tests reproduce `conformance/vectors.json`, the behaviour every KeyGrant SDK shares.

## Releases

Each release is a version tag (`0.2.0`). The tag runs `.github/workflows/publish.yml`, which tests and
packs the package and pushes it to nuget.org through NuGet's trusted publishing.

## License

MIT: see [LICENSE](LICENSE).
