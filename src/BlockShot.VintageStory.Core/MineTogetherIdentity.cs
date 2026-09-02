using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    public string? AccountId { get; init; }

    public string? Game { get; init; }

    public string? GameId { get; init; }

    /// <summary>
    /// Older VS access tokens embedded account and game identity that is already bound to the
    /// purpose-restricted renewal token. New compact access tokens omit these optional claims.
    /// </summary>
    public bool HasEmbeddedAccountIdentity =>
        AccountId is not null || Game is not null || GameId is not null;

    public bool ExpiresWithin(TimeSpan duration, DateTimeOffset? now = null) =>
        ExpiresAt <= (now ?? DateTimeOffset.UtcNow) + duration;

    public static MineTogetherSessionToken ParseAndValidate(
        string rawToken,
        string authorityPublicKeyPem = MineTogetherSessionAuthority.PublicKeyPem,
        DateTimeOffset? now = null,
        string? expectedVintageStoryPlayerUid = null)
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
            var accountId = OptionalString(root, "aid");
            var game = OptionalString(root, "gme");
            var gameId = OptionalString(root, "gid");
            if ((game is null) != (gameId is null))
            {
                throw new InvalidDataException("The MineTogether session token contains an incomplete game identity.");
            }
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

            if (expectedVintageStoryPlayerUid is not null)
            {
                var expectedUid = VintageStorySessionIdentity.NormalizePlayerUid(expectedVintageStoryPlayerUid);
                if (subject != VintageStorySessionIdentity.SubjectFor(expectedUid) ||
                    (game is not null &&
                        (!string.Equals(game, VintageStorySessionIdentity.Game, StringComparison.Ordinal) ||
                         !string.Equals(gameId, expectedUid, StringComparison.Ordinal))))
                {
                    throw new InvalidDataException(
                        "The MineTogether session token belongs to a different Vintage Story player or account.");
                }
            }

            return new MineTogetherSessionToken(rawToken, subject, username, subjectHash, issuedAt, expiresAt)
            {
                AccountId = accountId,
                Game = game,
                GameId = gameId
            };
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

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"The MineTogether session token contains an invalid '{name}'.");
        }

        return value.GetString();
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

public static class VintageStorySessionIdentity
{
    public const string Game = "vintagestory";
    private static readonly Regex PlayerUidPattern = new(
        "^[A-Za-z0-9+/=_-]{1,128}$",
        RegexOptions.CultureInvariant);

    public static string NormalizePlayerUid(string playerUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerUid);
        var normalized = playerUid.Trim();
        if (!PlayerUidPattern.IsMatch(normalized))
        {
            throw new InvalidDataException("The Vintage Story PlayerUID is malformed.");
        }

        return normalized;
    }

    public static Guid SubjectFor(string playerUid)
    {
        var normalized = NormalizePlayerUid(playerUid);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Game}\0{normalized}"));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x40);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(hash.AsSpan(0, 16));
        return Guid.ParseExact(
            $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}",
            "D");
    }
}

public sealed record MineTogetherRenewalToken(
    string Raw,
    Guid Subject,
    string Username,
    string AccountId,
    string GameId,
    string RenewalGeneration,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt)
{
    private const string HeaderJson = "{\"alg\":\"ES256\",\"typ\":\"MT-VS-RENEW\"}";
    private const string Purpose = "vintage-session-renewal";
    private const string Audience = "minetogether-session-service";
    private static readonly string ExpectedHeader = EncodeBase64Url(Encoding.UTF8.GetBytes(HeaderJson));
    private static readonly byte[] SigningDomain = Encoding.UTF8.GetBytes(
        "MineTogether:VintageStory:Renewal:v1\0");
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(5);
    private static readonly Regex AccountIdPattern = new(
        "^[A-Za-z0-9_.:-]{1,128}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RenewalGenerationPattern = new(
        "^[A-Za-z0-9_-]{16,64}$",
        RegexOptions.CultureInvariant);

    public static MineTogetherRenewalToken ParseAndValidate(
        string rawToken,
        string authorityPublicKeyPem = MineTogetherSessionAuthority.PublicKeyPem,
        DateTimeOffset? now = null,
        string? expectedVintageStoryPlayerUid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        var parts = rawToken.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], ExpectedHeader, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The MineTogether renewal-token header is invalid.");
        }

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            var signature = DecodeBase64Url(parts[2]);
            var signed = new byte[SigningDomain.Length + payloadBytes.Length];
            SigningDomain.CopyTo(signed, 0);
            payloadBytes.CopyTo(signed, SigningDomain.Length);
            using var authority = ECDsa.Create();
            authority.ImportFromPem(authorityPublicKeyPem);
            if (!authority.VerifyData(
                    signed,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new InvalidDataException("The MineTogether renewal-token signature is invalid.");
            }

            using var payload = JsonDocument.Parse(payloadBytes);
            var root = payload.RootElement;
            if (!Guid.TryParseExact(RequiredString(root, "sub"), "D", out var subject) ||
                subject.ToString("N")[12] != '4')
            {
                throw new InvalidDataException("The MineTogether renewal-token subject is invalid.");
            }
            var username = RequiredString(root, "usn");
            var accountId = RequiredString(root, "aid");
            var game = RequiredString(root, "gme");
            var gameId = RequiredString(root, "gid");
            var renewalGeneration = RequiredString(root, "rgn");
            if (!string.Equals(RequiredString(root, "pur"), Purpose, StringComparison.Ordinal) ||
                !string.Equals(RequiredString(root, "aud"), Audience, StringComparison.Ordinal) ||
                !string.Equals(game, VintageStorySessionIdentity.Game, StringComparison.Ordinal) ||
                !AccountIdPattern.IsMatch(accountId) ||
                !RenewalGenerationPattern.IsMatch(renewalGeneration))
            {
                throw new InvalidDataException("The MineTogether renewal-token purpose or identity is invalid.");
            }
            var normalizedUid = VintageStorySessionIdentity.NormalizePlayerUid(gameId);
            if (subject != VintageStorySessionIdentity.SubjectFor(normalizedUid) ||
                (expectedVintageStoryPlayerUid is not null &&
                 !string.Equals(
                     normalizedUid,
                     VintageStorySessionIdentity.NormalizePlayerUid(expectedVintageStoryPlayerUid),
                     StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "The MineTogether renewal token belongs to a different Vintage Story player.");
            }

            var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(RequiredInt64(root, "iat"));
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(RequiredInt64(root, "exp"));
            var current = now ?? DateTimeOffset.UtcNow;
            if (expiresAt <= issuedAt || expiresAt - issuedAt > MaximumLifetime ||
                issuedAt > current + FutureSkew || expiresAt <= current)
            {
                throw new InvalidDataException("The MineTogether renewal-token timestamps are invalid.");
            }

            return new MineTogetherRenewalToken(
                rawToken, subject, username, accountId, normalizedUid, renewalGeneration, issuedAt, expiresAt);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("The MineTogether renewal-token encoding is invalid.", error);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException("The MineTogether renewal-token timestamps are invalid.", error);
        }
        catch (CryptographicException error)
        {
            throw new InvalidDataException("The MineTogether renewal-token authority key is invalid.", error);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The MineTogether renewal-token payload is invalid.", error);
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"The MineTogether renewal token is missing '{name}'.");
        }
        return value.GetString()!;
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException($"The MineTogether renewal token is missing '{name}'.");
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

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record MineTogetherPairingRequest(string Code, string PlayerUid, Uri VerificationUri);

public sealed record MineTogetherSessionCredentials(
    MineTogetherSessionToken Session,
    string RefreshToken);

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

    public MineTogetherPairingRequest CreateRequest(string playerUid)
    {
        var normalizedUid = VintageStorySessionIdentity.NormalizePlayerUid(playerUid);
        var code = EncodeBase64Url(RandomNumberGenerator.GetBytes(24));
        return new MineTogetherPairingRequest(
            code,
            normalizedUid,
            new Uri(siteOrigin, $"vintage-connect/{code}"));
    }

    public async Task RegisterAsync(
        MineTogetherPairingRequest pairing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(siteOrigin, "vintage-connect/api/start"),
            new { code = pairing.Code, playerUid = pairing.PlayerUid },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MineTogether pairing registration returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
    }

    public async Task<MineTogetherSessionCredentials?> PollOnceAsync(
        MineTogetherPairingRequest pairing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(siteOrigin, $"vintage-connect/api/poll?code={Uri.EscapeDataString(pairing.Code)}"));
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

            return ParseCredentials(root, pairing.PlayerUid);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("MineTogether pairing returned invalid JSON.", error);
        }
    }

    public async Task<MineTogetherSessionCredentials> WaitForCompletionAsync(
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
            var credentials = await PollOnceAsync(pairing, cancellationToken).ConfigureAwait(false);
            if (credentials is not null) return credentials;
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<MineTogetherSessionCredentials> RenewAsync(
        string refreshToken,
        string expectedVintageStoryPlayerUid,
        CancellationToken cancellationToken = default)
    {
        var expectedUid = VintageStorySessionIdentity.NormalizePlayerUid(expectedVintageStoryPlayerUid);
        ValidateRefreshToken(refreshToken, expectedUid);
        using var response = await httpClient.PostAsJsonAsync(
            new Uri(siteOrigin, "v1/api/session/vintage/refresh"),
            new { refreshToken },
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MineTogether session renewal returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.GetString() != "success")
            {
                throw new InvalidDataException("MineTogether session renewal returned an unexpected response.");
            }
            var credentials = ParseCredentials(root, expectedUid);
            if (!string.Equals(credentials.RefreshToken, refreshToken, StringComparison.Ordinal))
            {
                throw new InvalidDataException("MineTogether unexpectedly replaced the reusable renewal token.");
            }
            return credentials;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("MineTogether session renewal returned invalid JSON.", error);
        }
    }

    private MineTogetherSessionCredentials ParseCredentials(JsonElement root, string expectedPlayerUid)
    {
        if (!root.TryGetProperty("token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("MineTogether completed without an access token.");
        }
        if (!root.TryGetProperty("refreshToken", out var refreshValue) ||
            refreshValue.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("MineTogether completed without a renewal credential.");
        }

        var refreshToken = refreshValue.GetString()!;
        var renewal = MineTogetherRenewalToken.ParseAndValidate(
            refreshToken,
            authorityPublicKeyPem,
            expectedVintageStoryPlayerUid: expectedPlayerUid);
        var session = MineTogetherSessionToken.ParseAndValidate(
            tokenValue.GetString()!,
            authorityPublicKeyPem,
            expectedVintageStoryPlayerUid: expectedPlayerUid);
        if (session.Subject != renewal.Subject)
        {
            throw new InvalidDataException("The MineTogether access and renewal identities do not match.");
        }
        return new MineTogetherSessionCredentials(session, refreshToken);
    }

    private void ValidateRefreshToken(string refreshToken, string expectedPlayerUid)
    {
        MineTogetherRenewalToken.ParseAndValidate(
            refreshToken,
            authorityPublicKeyPem,
            expectedVintageStoryPlayerUid: expectedPlayerUid);
    }

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
