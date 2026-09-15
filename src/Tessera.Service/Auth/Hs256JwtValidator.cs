using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tessera.Service.Auth;

/// <summary>
/// Validates HS256 (HMAC-SHA256, shared-secret) JWTs by hand, using only
/// System.Security.Cryptography and System.Text.Json, both part of the
/// runtime. No NuGet dependency, deliberately: RS256 and JWKS-based OIDC
/// validation, key rotation, discovery documents, that's real complexity
/// with real ways to get it wrong, and belongs to a maintained library
/// (Microsoft.AspNetCore.Authentication.JwtBearer) once this repo can
/// restore NuGet packages again. HS256 with one configured shared secret
/// is a small, fully specified surface (RFC 7519 plus the HS256 case of
/// RFC 7515) that's realistic to own directly. Do not extend this class
/// to other algorithms without the same scrutiny that went into this one.
///
/// This intentionally validates only alg, signature, iss, aud, exp, and
/// nbf. It does not implement JWKS, key rotation, or multiple signing
/// keys, if you need those, this is the wrong tool and you want the real
/// library.
///
/// Every property read off the token's header and payload is type-checked
/// before use (TryGetString below), and the whole thing runs inside a
/// catch-all. This class is called on every request to an unauthenticated
/// caller's Authorization header, before any signature has been verified,
/// so a caller can hand it whatever malformed or oddly-typed JSON they
/// want. That must produce a Failure result, never an unhandled exception,
/// or an attacker gets a way to crash the auth gate itself.
/// </summary>
public sealed class Hs256JwtValidator
{
    private readonly string _expectedIssuer;
    private readonly string _expectedAudience;
    private readonly byte[] _signingKey;
    private readonly TimeSpan _clockSkew;
    private readonly TimeProvider _clock;

    public Hs256JwtValidator(string expectedIssuer, string expectedAudience, byte[] signingKey, TimeSpan? clockSkew = null, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(expectedIssuer))
            throw new ArgumentException("expectedIssuer is required.", nameof(expectedIssuer));
        if (string.IsNullOrWhiteSpace(expectedAudience))
            throw new ArgumentException("expectedAudience is required.", nameof(expectedAudience));
        if (signingKey.Length < 32)
            throw new ArgumentException("signingKey must be at least 32 bytes (256 bits) for HS256.", nameof(signingKey));

        _expectedIssuer = expectedIssuer;
        _expectedAudience = expectedAudience;
        _signingKey = signingKey;
        _clockSkew = clockSkew ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? TimeProvider.System;
    }

    public JwtValidationResult Validate(string? token)
    {
        try
        {
            return ValidateCore(token);
        }
        catch (Exception)
        {
            // Fail closed on anything unforeseen. This runs on every
            // request before the caller has proven anything, an
            // unauthenticated caller must never be able to turn a
            // malformed token into a 500 or a bypass. The failure reason
            // returned to the caller stays generic, on purpose, the real
            // exception isn't ours to hand back over the wire.
            return JwtValidationResult.Failure("token validation failed");
        }
    }

    private JwtValidationResult ValidateCore(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return JwtValidationResult.Failure("missing token");

        var parts = token.Split('.');
        if (parts.Length != 3)
            return JwtValidationResult.Failure("malformed token: expected header.payload.signature");

        byte[] headerBytes, payloadBytes, signatureBytes;
        try
        {
            headerBytes = Base64UrlDecode(parts[0]);
            payloadBytes = Base64UrlDecode(parts[1]);
            signatureBytes = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return JwtValidationResult.Failure("malformed token: invalid base64url encoding");
        }

        JsonElement header, payload;
        try
        {
            header = JsonSerializer.Deserialize<JsonElement>(headerBytes);
            payload = JsonSerializer.Deserialize<JsonElement>(payloadBytes);
        }
        catch (JsonException)
        {
            return JwtValidationResult.Failure("malformed token: header or payload is not valid JSON");
        }

        // A JWT header/payload must be a JSON object. Either segment
        // parsing to something else (a bare number, string, or array is
        // still valid JSON) would make every TryGetProperty call below
        // throw InvalidOperationException; reject it here explicitly
        // instead of relying on the outer catch to paper over it.
        if (header.ValueKind != JsonValueKind.Object || payload.ValueKind != JsonValueKind.Object)
            return JwtValidationResult.Failure("malformed token: header or payload is not a JSON object");

        // Pin to exactly HS256. Never trust the token to say what algorithm
        // to use, that's how "alg: none" and RS256-to-HS256 downgrade
        // attacks happen, the whole reason to check this explicitly rather
        // than branch on whatever the header claims.
        if (!TryGetString(header, "alg", out var alg) || !string.Equals(alg, "HS256", StringComparison.Ordinal))
            return JwtValidationResult.Failure("unsupported or missing alg, only HS256 is accepted");

        var signedInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var expectedSignature = HMACSHA256.HashData(_signingKey, signedInput);
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
            return JwtValidationResult.Failure("invalid signature");

        if (!TryGetString(payload, "iss", out var iss) || !string.Equals(iss, _expectedIssuer, StringComparison.Ordinal))
            return JwtValidationResult.Failure("issuer does not match");

        if (!AudienceMatches(payload, _expectedAudience))
            return JwtValidationResult.Failure("audience does not match");

        var now = _clock.GetUtcNow();

        if (!payload.TryGetProperty("exp", out var expEl) || !expEl.TryGetInt64(out var exp))
            return JwtValidationResult.Failure("missing or invalid exp claim");
        if (now > DateTimeOffset.FromUnixTimeSeconds(exp) + _clockSkew)
            return JwtValidationResult.Failure("token expired");

        if (payload.TryGetProperty("nbf", out var nbfEl) && nbfEl.TryGetInt64(out var nbf))
        {
            if (now < DateTimeOffset.FromUnixTimeSeconds(nbf) - _clockSkew)
                return JwtValidationResult.Failure("token not yet valid");
        }

        if (!TryGetString(payload, "sub", out var subject) || string.IsNullOrWhiteSpace(subject))
            return JwtValidationResult.Failure("missing or blank sub claim, required as the operator identity for audit attribution");

        return JwtValidationResult.Success(subject);
    }

    private static bool AudienceMatches(JsonElement payload, string expected)
    {
        if (!payload.TryGetProperty("aud", out var audEl)) return false;

        return audEl.ValueKind switch
        {
            JsonValueKind.String => string.Equals(audEl.GetString(), expected, StringComparison.Ordinal),
            JsonValueKind.Array => audEl.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), expected, StringComparison.Ordinal)),
            _ => false,
        };
    }

    /// <summary>
    /// Reads a string property without throwing: missing, wrong-typed
    /// (a number or object where a string was expected), or a non-object
    /// container all come back as false rather than an exception. Every
    /// claim read in ValidateCore goes through this rather than a direct
    /// GetString(), a validly-signed token can still carry a claim of the
    /// wrong JSON type, and that must fail as "doesn't match", not crash.
    /// </summary>
    private static bool TryGetString(JsonElement obj, string propertyName, out string? value)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(propertyName, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString();
        return value is not null;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(s);
    }
}

public sealed record JwtValidationResult(bool IsValid, string? Subject, string? FailureReason)
{
    public static JwtValidationResult Success(string subject) => new(true, subject, null);
    public static JwtValidationResult Failure(string reason) => new(false, null, reason);
}
