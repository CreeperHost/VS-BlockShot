using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlockShot.VintageStory.Core;

/// <summary>Public MineTogether session authority used by MineTogetherSessions 1.2.x.</summary>
public static class MineTogetherSessionAuthority
{
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELx05NN+bNc6euBrIdkS2tdeN0nTK
        1hWCNCCOR2t0RP6xeaOeQsEEinSvMqE4pE6weYcIQT8FcylP+IIV1IlLRw==
        -----END PUBLIC KEY-----
        """;
}

/// <summary>
/// The Java MineTogether session format. Its ES256 signature covers the decoded payload JSON
/// and uses a DER ECDSA signature.
/// </summary>
public sealed record MineTogetherSessionToken(
    string Raw,
    Guid Subject,
    string Username,
    string SubjectHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    private const string ExpectedHeader = "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9";

    public bool ExpiresWithin(TimeSpan duration, DateTimeOffset? now = null) =>
        ExpiresAt <= (now ?? DateTimeOffset.UtcNow) + duration;

    public static MineTogetherSessionToken ParseAndValidate(
        string rawToken,
        string authorityPublicKeyPem = MineTogetherSessionAuthority.PublicKeyPem,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityPublicKeyPem);
        var parts = rawToken.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], ExpectedHeader, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The MineTogether session token header is invalid.");
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = DecodeBase64Url(parts[1]);
            signature = DecodeBase64Url(parts[2]);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("The MineTogether session token encoding is invalid.", error);
        }

        try
        {
            using var authority = ECDsa.Create();
            authority.ImportFromPem(authorityPublicKeyPem);
            if (!authority.VerifyData(
                    payloadBytes,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new InvalidDataException("The MineTogether session token signature is invalid.");
            }

            using var payload = JsonDocument.Parse(payloadBytes);
            var root = payload.RootElement;
            var subjectText = RequiredString(root, "sub");
            if (!Guid.TryParseExact(subjectText, "D", out var subject) || GetGuidVersion(subject) != 4)
            {
                throw new InvalidDataException("The MineTogether session token subject is not a UUIDv4.");
            }

            var username = RequiredString(root, "usn");
            var subjectHash = RequiredString(root, "sha");
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject.ToString("D"))));
            if (!string.Equals(subjectHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The MineTogether session token subject hash is invalid.");
            }

            DateTimeOffset issuedAt;
            DateTimeOffset expiresAt;
            try
            {
                issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(RequiredInt64(root, "iat"));
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(RequiredInt64(root, "exp"));
            }
            catch (ArgumentOutOfRangeException error)
            {
                throw new InvalidDataException("The MineTogether session token timestamps are invalid.", error);
            }

            if (expiresAt <= issuedAt)
            {
                throw new InvalidDataException("The MineTogether session token expiry is invalid.");
            }

            if (expiresAt <= (now ?? DateTimeOffset.UtcNow))
            {
                throw new InvalidDataException("The MineTogether session token has expired.");
            }

            return new MineTogetherSessionToken(rawToken, subject, username, subjectHash, issuedAt, expiresAt);
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("The MineTogether session authority key is invalid.", error);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The MineTogether session token payload is invalid.", error);
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"The MineTogether session token is missing '{name}'.");
        }

        var result = value.GetString();
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidDataException($"The MineTogether session token contains an empty '{name}'.");
        }

        return result;
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException($"The MineTogether session token is missing '{name}'.");
        }

        return result;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Convert.FromBase64String(padded);
    }

    private static int GetGuidVersion(Guid value) => value.ToString("N")[12] - '0';
}

public sealed record MineTogetherPairingRequest(string Code, Uri VerificationUri);

/// <summary>Public website pairing flow used by MineTogether's dedicated-server clients.</summary>
public sealed class MineTogetherPairingClient
{
    public static readonly Uri DefaultSiteOrigin = new("https://minetogether.io/");

    private readonly HttpClient httpClient;
    private readonly Uri siteOrigin;
    private readonly string authorityPublicKeyPem;

    public MineTogetherPairingClient(
        HttpClient httpClient,
        Uri? siteOrigin = null,
        string authorityPublicKeyPem = MineTogetherSessionAuthority.PublicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.siteOrigin = siteOrigin ?? DefaultSiteOrigin;
        this.authorityPublicKeyPem = authorityPublicKeyPem;
        if (!this.siteOrigin.IsAbsoluteUri) throw new ArgumentException("Site origin must be absolute.", nameof(siteOrigin));
    }

    public MineTogetherPairingRequest CreateRequest()
    {
        var code = EncodeBase64Url(RandomNumberGenerator.GetBytes(24));
        return new MineTogetherPairingRequest(code, new Uri(siteOrigin, $"server-connect/{code}"));
    }

    public async Task<MineTogetherSessionToken?> PollOnceAsync(
        MineTogetherPairingRequest pairing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(siteOrigin, $"server-connect/api/poll?code={Uri.EscapeDataString(pairing.Code)}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted) return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"MineTogether pairing returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "success")
            {
                throw new InvalidDataException("MineTogether pairing returned an unexpected response.");
            }

            if (!root.TryGetProperty("token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("MineTogether pairing completed without a token.");
            }

            return MineTogetherSessionToken.ParseAndValidate(tokenValue.GetString()!, authorityPublicKeyPem);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("MineTogether pairing returned invalid JSON.", error);
        }
    }

    public async Task<MineTogetherSessionToken> WaitForCompletionAsync(
        MineTogetherPairingRequest pairing,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        if (pollInterval <= TimeSpan.Zero || pollInterval == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        while (true)
        {
            var token = await PollOnceAsync(pairing, cancellationToken).ConfigureAwait(false);
            if (token is not null) return token;
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
