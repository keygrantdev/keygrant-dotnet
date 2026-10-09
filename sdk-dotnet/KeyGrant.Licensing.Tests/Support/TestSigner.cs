using System.Text;
using System.Text.Json.Nodes;
using KeyGrant.Licensing.Internal;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace KeyGrant.Licensing.Tests.Support;

/// <summary>What a lease is signed with, as the server's signer takes it (iss, iat and exp are filled in).</summary>
internal sealed record LeaseInput
{
    public required string Sub { get; init; }
    public string Product { get; init; } = Harness.Product;
    public string Aid { get; init; } = "act_1";
    public string? Fp { get; init; }
    public string? Fpk { get; init; }
    public string Model { get; init; } = "perpetual";
    public long Lim { get; init; } = 3;
    public long Major { get; init; } = 1;
    public long? MinMajor { get; init; }
    public long? UpgradeUntil { get; init; }
    public bool? Test { get; init; }
    public bool? Grace { get; init; }
    public long TtlSeconds { get; init; } = 30 * 24 * 3600;

    /// <summary>The <c>ent</c> claim as signed, any JSON (null signs <c>"ent":null</c>): the server signs only a well-formed one, but an SDK still has to read whatever is signed.</summary>
    public JsonNode? Ent { get; init; } = new JsonObject();
}

/// <summary>An Ed25519 key that signs leases as the server does (a compact JWS, EdDSA).</summary>
internal sealed class TestSigner
{
    private readonly Ed25519PrivateKeyParameters key;

    public TestSigner()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        key = (Ed25519PrivateKeyParameters)generator.GenerateKeyPair().Private;
    }

    /// <summary>A signer from a JWK's private scalar <c>d</c> (base64url).</summary>
    public TestSigner(string d) => key = new Ed25519PrivateKeyParameters(Base64Url.Decode(d)!, 0);

    public string PublicX => Base64Url.Encode(key.GeneratePublicKey().GetEncoded());

    public PublicJwk Jwk => new() { Kty = "OKP", Crv = "Ed25519", X = PublicX };

    /// <summary>Sign any payload, valid claims or not.</summary>
    public string Sign(JsonNode payload) => SignRaw(payload.ToJsonString(Json.Options));

    /// <summary>Sign a payload given as its exact text.</summary>
    public string SignRaw(string payloadText)
    {
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("{\"alg\":\"EdDSA\",\"typ\":\"JWT\"}"));
        return SignInput($"{header}.{Base64Url.Encode(Encoding.UTF8.GetBytes(payloadText))}");
    }

    /// <summary>The id a lease names this key by (its thumbprint).</summary>
    public string Kid => KeyIds.Of(Base64Url.Decode(PublicX)!);

    /// <summary>A lease's payload signed again by this key, under a header that names <paramref name="kid"/>.</summary>
    public string Named(string lease, string kid)
    {
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes($"{{\"alg\":\"EdDSA\",\"typ\":\"JWT\",\"kid\":\"{kid}\"}}"));
        return SignInput($"{header}.{lease.Split('.')[1]}");
    }

    /// <summary>Sign a signing input (<c>header.payload</c>) exactly as written, canonical or not.</summary>
    public string SignInput(string input)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        var data = Encoding.UTF8.GetBytes(input);
        signer.BlockUpdate(data, 0, data.Length);
        return $"{input}.{Base64Url.Encode(signer.GenerateSignature())}";
    }

    /// <summary>A lease as the server signs one at <paramref name="nowMs"/>: iat its whole second, exp iat + ttl.</summary>
    public string SignLease(LeaseInput input, long nowMs)
    {
        var iat = (long)Math.Floor(nowMs / 1000.0);
        var claims = new JsonObject
        {
            ["sub"] = input.Sub,
            ["product"] = input.Product,
            ["aid"] = input.Aid,
        };
        if (input.Fp is not null) claims["fp"] = input.Fp;
        if (input.Fpk is not null) claims["fpk"] = input.Fpk;
        claims["model"] = input.Model;
        claims["lim"] = input.Lim;
        claims["major"] = input.Major;
        if (input.MinMajor is { } min) claims["minMajor"] = min;
        if (input.UpgradeUntil is { } until) claims["upgradeUntil"] = until;
        claims["ent"] = input.Ent?.DeepClone();
        if (input.Grace is { } grace) claims["grace"] = grace;
        if (input.Test is { } test) claims["test"] = test;
        claims["iss"] = "keygrant";
        claims["iat"] = iat;
        claims["exp"] = iat + input.TtlSeconds;
        return Sign(claims);
    }
}
