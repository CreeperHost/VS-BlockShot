using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlockShot.VintageStory.Core;
using Xunit;

namespace BlockShot.VintageStory.Core.Tests;

public sealed class IdentityAndApiTests
{
    [Fact]
    public void Vintage_story_pack_identity_preserves_the_exact_runtime_version()
    {
        var identity = new VintageStoryPackIdentity("1.22.7-rc.1");

        Assert.Equal("VintageStory", VintageStoryPackIdentity.Platform);
        Assert.Equal("vintagestory:1.22.7-rc.1", identity.CompatibilityKey);
        Assert.Equal("{\"p\":\"vintagestory:1.22.7-rc.1\"}", identity.IdentifierJson);
    }

    [Fact]
    public async Task Public_pairing_flow_waits_for_accepted_then_validates_the_java_token()
    {
        using var authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var token = CreateToken(authority, now.AddMinutes(-1), now.AddHours(12));
        var handler = new ScriptedHandler(
            _ => Task.FromResult(Json(HttpStatusCode.Accepted, new { status = "pending" })),
            request =>
            {
                Assert.Contains("server-connect/api/poll?code=", request.RequestUri!.AbsoluteUri);
                return Task.FromResult(Json(HttpStatusCode.OK, new { status = "success", token }));
            });
        using var http = new HttpClient(handler);
        var client = new MineTogetherPairingClient(
            http,
            new Uri("https://pairing.test/"),
            authority.ExportSubjectPublicKeyInfoPem());
        var pairing = client.CreateRequest();

        Assert.Equal(32, pairing.Code.Length);
        Assert.Equal($"https://pairing.test/server-connect/{pairing.Code}", pairing.VerificationUri.AbsoluteUri);
        var result = await client.WaitForCompletionAsync(pairing, TimeSpan.FromMilliseconds(1));

        Assert.Equal("VintageTester", result.Username);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Upload_uses_bearer_auth_and_the_exact_vintage_story_pack_key()
    {
        HttpRequestMessage? observed = null;
        byte[]? uploaded = null;
        var handler = new ScriptedHandler(async request =>
        {
            observed = request;
            uploaded = await request.Content!.ReadAsByteArrayAsync();
            return Json(HttpStatusCode.OK, new { code = "Ab12Cd" });
        });
        using var http = new HttpClient(handler);
        var client = new BlockShotApiClient(http, new Uri("https://api.test/api/v1/"), new Uri("https://share.test/"));
        var path = Path.Combine(Path.GetTempPath(), $"blockshot-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        try
        {
            var result = await client.UploadPngAsync(
                path,
                TestSession(),
                new VintageStoryPackIdentity("1.22.7"),
                anonymous: true);

            Assert.Equal("Bearer", observed!.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", observed.Headers.Authorization.Parameter);
            Assert.Equal("image/png", observed.Headers.GetValues("Screencap-Type").Single());
            Assert.Equal("true", observed.Headers.GetValues("Anonymous").Single());
            Assert.Equal("VintageStory", observed.Headers.GetValues("Modpack-Platform").Single());
            Assert.Equal("vintagestory:1.22.7", observed.Headers.GetValues("Modpack-Id").Single());
            Assert.Equal([1, 2, 3, 4], uploaded);
            Assert.Equal("https://share.test/Ab12Cd", result.ShareUri.AbsoluteUri);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MineTogetherSessionToken TestSession() => new(
        "test-token",
        Guid.NewGuid(),
        "VintageTester",
        "hash",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddHours(1));

    private static string CreateToken(ECDsa authority, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var subject = Guid.NewGuid();
        var subjectHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject.ToString("D"))));
        var payload = JsonSerializer.Serialize(new
        {
            sub = subject.ToString("D"),
            usn = "VintageTester",
            sha = subjectHash,
            iat = issuedAt.ToUnixTimeMilliseconds(),
            exp = expiresAt.ToUnixTimeMilliseconds()
        });
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = authority.SignData(
            payloadBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return string.Join('.',
            "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9",
            Base64Url(payloadBytes),
            Base64Url(signature));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, Task<HttpResponseMessage>>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> remaining = new(responses);
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            if (!remaining.TryDequeue(out var response)) throw new InvalidOperationException("No scripted response remains.");
            return await response(request);
        }
    }
}
