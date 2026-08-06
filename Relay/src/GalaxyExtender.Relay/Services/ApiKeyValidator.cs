using System.Security.Cryptography;
using System.Text;
using GalaxyExtender.Relay.Options;
using Microsoft.Extensions.Options;

namespace GalaxyExtender.Relay.Services;

/// <summary>
/// Validates the <c>X-Relay-Key</c> header against every configured secret.
///
/// Authentication is "does the presented key equal ANY configured secret", which is what allows a
/// single shared key for the whole guild plus overlapping keys during a rotation. The dictionary
/// label is returned for logging; it is never sent by the client and is not matched against
/// anything in the request body.
/// </summary>
public sealed class ApiKeyValidator(IOptionsMonitor<RelayOptions> options)
{
    public const string HeaderName = "X-Relay-Key";

    private const int Sha256ByteCount = 32;

    public ApiKeyResult Validate(HttpRequest request)
    {
        var presented = request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(presented))
        {
            return ApiKeyResult.Invalid;
        }

        var configured = options.CurrentValue.ApiKeys;

        if (configured.Count == 0)
        {
            // Fail closed: an unconfigured relay accepts nothing rather than everything.
            return ApiKeyResult.Invalid;
        }

        // Compare SHA-256 digests rather than the raw strings. FixedTimeEquals requires equal
        // lengths and returns early when they differ, which would leak the secret's length;
        // hashing makes both sides a constant 32 bytes.
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var candidateHash = new byte[Sha256ByteCount];

        var matched = false;
        string? label = null;

        foreach (var entry in configured)
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value ?? string.Empty), candidateHash);

            var isMatch = CryptographicOperations.FixedTimeEquals(presentedHash, candidateHash);

            // Deliberately no `break`: the loop always runs to completion so response time does not
            // reveal which key matched, or how many were checked before a match.
            matched |= isMatch;
            label = isMatch ? entry.Key : label;
        }

        return matched ? new ApiKeyResult(true, label) : ApiKeyResult.Invalid;
    }

    /// <summary>
    /// Short, non-reversible fingerprint of a presented key, safe to log. Lets a failing client be
    /// correlated across requests without recording the secret.
    /// </summary>
    public static string Fingerprint(string presentedKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey)))[..8];
}

public readonly record struct ApiKeyResult(bool IsValid, string? Label)
{
    public static readonly ApiKeyResult Invalid = new(false, null);
}
