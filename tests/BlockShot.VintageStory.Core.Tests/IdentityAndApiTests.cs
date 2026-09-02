using System.Net;
using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlockShot.VintageStory.Core;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;
using Xunit;

namespace BlockShot.VintageStory.Core.Tests;

public sealed class IdentityAndApiTests
{
    private const string PlayerUid = "5V8plKl/kzjk1OoOseVKCz2h";

    [Fact]
    public void Uploaded_share_chat_text_is_a_safe_vtml_hyperlink()
    {
        var message = BlockShotChatText.Uploaded(
            new Uri("https://blocks.hot/Ab12?one=1&two=2"),
            "video",
            copied: true);

        Assert.Equal(
            "BlockShot uploaded video: <a href=\"https://blocks.hot/Ab12?one=1&amp;two=2\">" +
            "https://blocks.hot/Ab12?one=1&amp;two=2</a> (copied)",
            message);
    }

    [Theory]
    [InlineData(1280, 680, 1280, 680, 1280, 688, 0, 4)]
    [InlineData(1920, 1020, 1280, 680, 1280, 688, 0, 4)]
    [InlineData(1920, 1080, 1280, 720, 1280, 720, 0, 0)]
    [InlineData(3440, 1440, 1280, 534, 1280, 544, 0, 5)]
    public void Video_layout_centres_even_black_padding_for_vp8(
        int sourceWidth,
        int sourceHeight,
        int contentWidth,
        int contentHeight,
        int canvasWidth,
        int canvasHeight,
        int paddingLeft,
        int paddingTop)
    {
        var layout = VideoFrameLayout.FitInside(sourceWidth, sourceHeight, 1280, 720);

        Assert.Equal(contentWidth, layout.ContentWidth);
        Assert.Equal(contentHeight, layout.ContentHeight);
        Assert.Equal(canvasWidth, layout.CanvasWidth);
        Assert.Equal(canvasHeight, layout.CanvasHeight);
        Assert.Equal(paddingLeft, layout.PaddingLeft);
        Assert.Equal(paddingTop, layout.PaddingTop);
        Assert.Equal(0, layout.CanvasWidth % 16);
        Assert.Equal(0, layout.CanvasHeight % 16);
        Assert.Equal(layout.PaddingLeft, layout.CanvasWidth - layout.ContentWidth - layout.PaddingLeft);
        Assert.Equal(layout.PaddingTop, layout.CanvasHeight - layout.ContentHeight - layout.PaddingTop);
    }

    [Fact]
    public void Vintage_story_uid_uses_the_shared_deterministic_uuidv4_vector()
    {
        Assert.Equal(
            Guid.Parse("5ed37432-4944-4265-ac8d-a4d64e9d4844"),
            VintageStorySessionIdentity.SubjectFor(PlayerUid));
    }

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
        var token = CreateToken(authority, now.AddMinutes(-1), now.AddHours(12), includeAccountIdentity: false);
        var refreshToken = CreateRenewalToken(authority, now.AddMinutes(-1), now.AddDays(90).AddMinutes(-1));
        var handler = new ScriptedHandler(
            async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("vintage-connect/api/start", request.RequestUri!.AbsoluteUri);
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains(PlayerUid.Replace("/", "\\/"), body.Replace("/", "\\/"));
                return Json(HttpStatusCode.OK, new { status = "pending" });
            },
            _ => Task.FromResult(Json(HttpStatusCode.Accepted, new { status = "pending" })),
            request =>
            {
                Assert.Contains("vintage-connect/api/poll?code=", request.RequestUri!.AbsoluteUri);
                return Task.FromResult(Json(HttpStatusCode.OK, new { status = "success", token, refreshToken }));
            });
        using var http = new HttpClient(handler);
        var client = new MineTogetherPairingClient(
            http,
            new Uri("https://pairing.test/"),
            authority.ExportSubjectPublicKeyInfoPem());
        var pairing = client.CreateRequest(PlayerUid);

        Assert.Equal(32, pairing.Code.Length);
        Assert.Equal(PlayerUid, pairing.PlayerUid);
        Assert.Equal($"https://pairing.test/vintage-connect/{pairing.Code}", pairing.VerificationUri.AbsoluteUri);
        await client.RegisterAsync(pairing);
        var result = await client.WaitForCompletionAsync(pairing, TimeSpan.FromMilliseconds(1));

        Assert.Equal("VintageTester", result.Session.Username);
        Assert.Null(result.Session.AccountId);
        Assert.Null(result.Session.GameId);
        Assert.False(result.Session.HasEmbeddedAccountIdentity);
        Assert.Equal(refreshToken, result.RefreshToken);
        Assert.Equal(3, handler.RequestCount);

        Assert.Throws<InvalidDataException>(() => MineTogetherSessionToken.ParseAndValidate(
            token,
            authority.ExportSubjectPublicKeyInfoPem(),
            now,
            "different-player"));

        var tampered = token.Split('.');
        tampered[1] = tampered[1][..^1] + (tampered[1][^1] == 'A' ? 'B' : 'A');
        Assert.Throws<InvalidDataException>(() => MineTogetherSessionToken.ParseAndValidate(
            string.Join('.', tampered),
            authority.ExportSubjectPublicKeyInfoPem(),
            now,
            PlayerUid));
    }

    [Fact]
    public async Task Renewal_uses_the_purpose_restricted_token_and_requires_the_same_identity()
    {
        using var authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var access = CreateToken(authority, now.AddMinutes(-1), now.AddHours(18), includeAccountIdentity: false);
        var refresh = CreateRenewalToken(authority, now.AddMinutes(-1), now.AddDays(90).AddMinutes(-1));
        var handler = new ScriptedHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("v1/api/session/vintage/refresh", request.RequestUri!.AbsoluteUri);
            Assert.Contains(refresh, await request.Content!.ReadAsStringAsync());
            return Json(HttpStatusCode.OK, new
            {
                status = "success",
                token = access,
                refreshToken = refresh
            });
        });
        using var http = new HttpClient(handler);
        var client = new MineTogetherPairingClient(
            http,
            new Uri("https://pairing.test/"),
            authority.ExportSubjectPublicKeyInfoPem());

        var renewed = await client.RenewAsync(refresh, PlayerUid);

        Assert.Equal(access, renewed.Session.Raw);
        Assert.Null(renewed.Session.AccountId);
        Assert.False(renewed.Session.HasEmbeddedAccountIdentity);
        Assert.Equal(refresh, renewed.RefreshToken);
        Assert.Throws<InvalidDataException>(() => MineTogetherSessionToken.ParseAndValidate(
            refresh,
            authority.ExportSubjectPublicKeyInfoPem(),
            now,
            PlayerUid));
        Assert.Throws<InvalidDataException>(() => MineTogetherRenewalToken.ParseAndValidate(
            refresh,
            authority.ExportSubjectPublicKeyInfoPem(),
            now,
            "different-player"));
    }

    [Fact]
    public async Task Shared_credential_file_lock_renews_an_expiring_session_only_once()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"blockshot-renew-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var authority = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var now = DateTimeOffset.UtcNow;
            var expiring = CreateToken(authority, now.AddMinutes(-1), now.AddMinutes(10));
            var fresh = CreateToken(authority, now, now.AddHours(18), includeAccountIdentity: false);
            var refresh = CreateRenewalToken(authority, now.AddMinutes(-1), now.AddDays(90).AddMinutes(-1));
            var tokenPath = Path.Combine(directory, "session.token");
            await File.WriteAllTextAsync(tokenPath, expiring);
            await File.WriteAllTextAsync(Path.Combine(directory, "session.refresh"), refresh);
            var handler = new ScriptedHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, new
            {
                status = "success",
                token = fresh,
                refreshToken = refresh
            })));
            using var http = new HttpClient(handler);
            var client = new MineTogetherPairingClient(
                http,
                new Uri("https://pairing.test/"),
                authority.ExportSubjectPublicKeyInfoPem());
            var publicKey = authority.ExportSubjectPublicKeyInfoPem();
            var firstStore = new MineTogetherCredentialFiles(tokenPath, publicKey);
            var secondStore = new MineTogetherCredentialFiles(tokenPath, publicKey);

            var results = await Task.WhenAll(
                firstStore.RenewIfNeededAsync(client, PlayerUid, TimeSpan.FromMinutes(30)),
                secondStore.RenewIfNeededAsync(client, PlayerUid, TimeSpan.FromMinutes(30)));

            Assert.Equal(1, handler.RequestCount);
            Assert.Single(results, result => result?.Renewed == true);
            Assert.All(results, result => Assert.Equal(fresh, result?.Session.Raw));
            Assert.Equal(fresh, await File.ReadAllTextAsync(tokenPath));
            Assert.Equal(refresh, await File.ReadAllTextAsync(Path.Combine(directory, "session.refresh")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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
                PlayerUid,
                new VintageStoryPackIdentity("1.22.7"),
                anonymous: true);

            Assert.Equal("Bearer", observed!.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", observed.Headers.Authorization.Parameter);
            Assert.Equal("image/png", observed.Headers.GetValues("Screencap-Type").Single());
            Assert.Equal("true", observed.Headers.GetValues("Anonymous").Single());
            Assert.Equal(PlayerUid, observed.Headers.GetValues("Player-Uid").Single());
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

    [Fact]
    public async Task Webm_upload_uses_the_video_media_contract()
    {
        HttpRequestMessage? observed = null;
        byte[]? uploaded = null;
        var handler = new ScriptedHandler(async request =>
        {
            observed = request;
            uploaded = await request.Content!.ReadAsByteArrayAsync();
            return Json(HttpStatusCode.OK, new { code = "WebM42" });
        });
        using var http = new HttpClient(handler);
        var client = new BlockShotApiClient(http, new Uri("https://api.test/api/v1/"), new Uri("https://share.test/"));
        var path = Path.Combine(Path.GetTempPath(), $"blockshot-{Guid.NewGuid():N}.webm");
        await File.WriteAllBytesAsync(path, [0x1A, 0x45, 0xDF, 0xA3]);
        try
        {
            var result = await client.UploadWebmAsync(
                path,
                TestSession(),
                PlayerUid,
                new VintageStoryPackIdentity("1.22.6"),
                anonymous: false);

            Assert.Equal("video/webm", observed!.Headers.GetValues("Screencap-Type").Single());
            Assert.Equal("video/webm", observed.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("false", observed.Headers.GetValues("Anonymous").Single());
            Assert.Equal([0x1A, 0x45, 0xDF, 0xA3], uploaded);
            Assert.Equal("https://share.test/WebM42", result.ShareUri.AbsoluteUri);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Preview_download_uses_the_public_small_endpoint_and_png_bytes()
    {
        HttpRequestMessage? observed = null;
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var handler = new ScriptedHandler(request =>
        {
            observed = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(png)
            };
            // The deployed endpoint currently misdeclares this PNG as WebP. The client must
            // trust the file signature rather than that response header.
            response.Content.Headers.ContentType = new("image/webp");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new BlockShotApiClient(http, new Uri("https://api.test/api/v1/"));

        var result = await client.GetPreviewPngAsync("Ab12Cd");

        Assert.Equal(png, result);
        Assert.Equal("https://api.test/api/v1/shares/Ab12Cd/preview/smol", observed!.RequestUri!.AbsoluteUri);
        Assert.Null(observed.Headers.Authorization);
        Assert.Contains(observed.Headers.Accept, value => value.MediaType == "image/png");
    }

    [Fact]
    public async Task Preview_download_rejects_non_png_content()
    {
        var handler = new ScriptedHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x52, 0x49, 0x46, 0x46])
        }));
        using var http = new HttpClient(handler);
        var client = new BlockShotApiClient(http, new Uri("https://api.test/api/v1/"));

        var error = await Assert.ThrowsAsync<BlockShotApiException>(() => client.GetPreviewPngAsync("Ab12Cd"));

        Assert.Contains("invalid preview image", error.Message);
    }

    [Fact]
    public void Webm_writer_emits_vp8_track_frames_and_patches_duration()
    {
        using var stream = new MemoryStream();
        using (var writer = new WebMVideoWriter(stream, 1280, 720, 15, leaveOpen: true))
        {
            writer.WriteFrame([0x10, 0x20, 0x30], TimeSpan.Zero, keyFrame: true);
            writer.WriteFrame([0x11, 0x21], TimeSpan.FromMilliseconds(67), keyFrame: false);
            writer.Complete(TimeSpan.FromMilliseconds(134));
            Assert.Equal(2, writer.FrameCount);
        }

        var bytes = stream.ToArray();
        Assert.Equal([0x1A, 0x45, 0xDF, 0xA3], bytes[..4]);
        Assert.True(Find(bytes, Encoding.UTF8.GetBytes("webm")) >= 0);
        Assert.True(Find(bytes, Encoding.UTF8.GetBytes("V_VP8")) >= 0);
        Assert.True(Find(bytes, [0x1F, 0x43, 0xB6, 0x75]) >= 0);

        var durationElement = Find(bytes, [0x44, 0x89, 0x88]);
        Assert.True(durationElement >= 0);
        var durationBits = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(durationElement + 3, 8));
        Assert.Equal(134d, BitConverter.Int64BitsToDouble(durationBits));

        Assert.True(Find(bytes, [0xA3, 0x87, 0x81, 0x00, 0x00, 0x80, 0x10, 0x20, 0x30]) >= 0);
        Assert.True(Find(bytes, [0xA3, 0x86, 0x81, 0x00, 0x43, 0x00, 0x11, 0x21]) >= 0);
    }

    [Fact]
    public void Pinned_vp8_encoder_produces_frames_for_the_webm_writer()
    {
        const int width = 64;
        const int height = 64;
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)(offset / 4);
            pixels[offset + 1] = (byte)(offset / 16);
            pixels[offset + 2] = (byte)(offset / 32);
            pixels[offset + 3] = 0xFF;
        }

        using var encoder = new VP8Codec { BaseQIndex = 32, KeyframeIntervalFrames = 30 };
        var first = encoder.EncodeVideo(width, height, pixels, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);
        pixels[0] ^= 0xFF;
        var second = encoder.EncodeVideo(width, height, pixels, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);

        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Equal(0, first[0] & 1);

        using var stream = new MemoryStream();
        using var writer = new WebMVideoWriter(stream, width, height, 15, leaveOpen: true);
        writer.WriteFrame(first, TimeSpan.Zero, keyFrame: true);
        writer.WriteFrame(second, TimeSpan.FromMilliseconds(67), keyFrame: (second[0] & 1) == 0);
        writer.Complete(TimeSpan.FromMilliseconds(134));
        Assert.True(stream.Length > first.Length + second.Length);
    }

    [Fact]
    public void Pinned_vp8_encoder_accepts_the_padded_failure_case()
    {
        var layout = VideoFrameLayout.FitInside(1280, 680, 1280, 720);
        var pixels = ArrayPool<byte>.Shared.Rent(layout.CanvasWidth * layout.CanvasHeight * 4);
        try
        {
            using var encoder = new VP8Codec { BaseQIndex = 32, KeyframeIntervalFrames = 30 };
            var encoded = encoder.EncodeVideo(
                layout.CanvasWidth,
                layout.CanvasHeight,
                pixels,
                VideoPixelFormatsEnum.Bgra,
                VideoCodecsEnum.VP8);

            Assert.Equal(1280, layout.CanvasWidth);
            Assert.Equal(688, layout.CanvasHeight);
            Assert.NotEmpty(encoded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static MineTogetherSessionToken TestSession() => new(
        "test-token",
        VintageStorySessionIdentity.SubjectFor(PlayerUid),
        "VintageTester",
        "hash",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddHours(1));

    private static string CreateToken(
        ECDsa authority,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        bool includeAccountIdentity = true)
    {
        var subject = VintageStorySessionIdentity.SubjectFor(PlayerUid);
        var subjectHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject.ToString("D"))));
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject.ToString("D"),
            ["usn"] = "VintageTester",
            ["sha"] = subjectHash,
            ["iat"] = issuedAt.ToUnixTimeMilliseconds(),
            ["exp"] = expiresAt.ToUnixTimeMilliseconds()
        };
        if (includeAccountIdentity)
        {
            claims["aid"] = "account-123";
            claims["gme"] = "vintagestory";
            claims["gid"] = PlayerUid;
        }
        var payload = JsonSerializer.Serialize(claims);
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

    private static string CreateRenewalToken(
        ECDsa authority,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var subject = VintageStorySessionIdentity.SubjectFor(PlayerUid);
        var payload = JsonSerializer.Serialize(new
        {
            sub = subject.ToString("D"),
            usn = "VintageTester",
            aid = "account-123",
            gme = "vintagestory",
            gid = PlayerUid,
            rgn = "generation_0123456789",
            pur = "vintage-session-renewal",
            aud = "minetogether-session-service",
            iat = issuedAt.ToUnixTimeMilliseconds(),
            exp = expiresAt.ToUnixTimeMilliseconds()
        });
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var domain = Encoding.UTF8.GetBytes("MineTogether:VintageStory:Renewal:v1\0");
        var signed = new byte[domain.Length + payloadBytes.Length];
        domain.CopyTo(signed, 0);
        payloadBytes.CopyTo(signed, domain.Length);
        var signature = authority.SignData(
            signed,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return string.Join('.',
            Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"MT-VS-RENEW\"}")),
            Base64Url(payloadBytes),
            Base64Url(signature));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int Find(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack[offset..].StartsWith(needle)) return offset;
        }
        return -1;
    }

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
