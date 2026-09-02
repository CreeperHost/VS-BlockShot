using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockShot.VintageStory.Core;

public sealed record BlockShotUploadResult(string Code, Uri ShareUri);

public sealed record BlockShotCapture
{
    public required string Code { get; init; }
    public string? Username { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Expiry { get; init; }
    public BlockShotFileMetadata? FileMeta { get; init; }
}

public sealed record BlockShotFileMetadata
{
    public long Size { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed record BlockShotHistoryPage
{
    public IReadOnlyList<BlockShotCapture> Results { get; init; } = [];
    public int Count { get; init; }
    public int Pages { get; init; }
}

public sealed class BlockShotApiException(
    string message,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>Typed client for the existing blocks.hot v1 API.</summary>
public sealed class BlockShotApiClient
{
    private const int MaximumPreviewBytes = 8 * 1024 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static readonly Uri DefaultApiRoot = new("https://blocks.hot/api/v1/");
    public static readonly Uri DefaultSiteRoot = new("https://blocks.hot/");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient httpClient;
    private readonly Uri apiRoot;
    private readonly Uri siteRoot;

    public BlockShotApiClient(HttpClient httpClient, Uri? apiRoot = null, Uri? siteRoot = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.apiRoot = EnsureTrailingSlash(apiRoot ?? DefaultApiRoot);
        this.siteRoot = EnsureTrailingSlash(siteRoot ?? DefaultSiteRoot);
    }

    public async Task<BlockShotUploadResult> UploadPngAsync(
        string filePath,
        MineTogetherSessionToken session,
        string playerUid,
        VintageStoryPackIdentity pack,
        bool anonymous,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        await UploadMediaAsync(
            filePath,
            "image/png",
            session,
            playerUid,
            pack,
            anonymous,
            progress,
            cancellationToken).ConfigureAwait(false);

    public async Task<BlockShotUploadResult> UploadWebmAsync(
        string filePath,
        MineTogetherSessionToken session,
        string playerUid,
        VintageStoryPackIdentity pack,
        bool anonymous,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        await UploadMediaAsync(
            filePath,
            "video/webm",
            session,
            playerUid,
            pack,
            anonymous,
            progress,
            cancellationToken).ConfigureAwait(false);

    private async Task<BlockShotUploadResult> UploadMediaAsync(
        string filePath,
        string mediaType,
        MineTogetherSessionToken session,
        string playerUid,
        VintageStoryPackIdentity pack,
        bool anonymous,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pack);
        var normalizedPlayerUid = VintageStorySessionIdentity.NormalizePlayerUid(playerUid);
        if (session.Subject != VintageStorySessionIdentity.SubjectFor(normalizedPlayerUid))
        {
            throw new InvalidDataException("The Vintage Story PlayerUID does not match the MineTogether session.");
        }

        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("The captured screenshot does not exist.", file.FullName);

        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        using var request = CreateAuthenticatedRequest(HttpMethod.Put, "shares", session);
        request.Headers.TryAddWithoutValidation("Screencap-Type", mediaType);
        request.Headers.TryAddWithoutValidation("Anonymous", anonymous ? "true" : "false");
        request.Headers.TryAddWithoutValidation("Player-Uid", normalizedPlayerUid);

        // MineTogether identifies the running game as vintagestory:<exact ShortGameVersion>.
        // Preserve that complete key in Modpack-Id instead of parsing or normalizing it.
        request.Headers.TryAddWithoutValidation("Modpack-Platform", VintageStoryPackIdentity.Platform);
        request.Headers.TryAddWithoutValidation("Modpack-Id", pack.CompatibilityKey);
        request.Content = new ProgressStreamContent(stream, file.Length, mediaType, progress);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiError(response.StatusCode, body);
        }

        try
        {
            var document = JsonSerializer.Deserialize<UploadDocument>(body, JsonOptions);
            if (string.IsNullOrWhiteSpace(document?.Code))
            {
                throw new BlockShotApiException("BlockShot completed the upload without a share code.");
            }

            return new BlockShotUploadResult(document.Code, new Uri(siteRoot, document.Code));
        }
        catch (JsonException error)
        {
            throw new BlockShotApiException("BlockShot returned an invalid upload response.", innerException: error);
        }
    }

    public async Task<BlockShotHistoryPage> GetHistoryAsync(
        MineTogetherSessionToken session,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, $"list/{page}", session);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw CreateApiError(response.StatusCode, body);

        try
        {
            return JsonSerializer.Deserialize<BlockShotHistoryPage>(body, JsonOptions)
                ?? new BlockShotHistoryPage();
        }
        catch (JsonException error)
        {
            throw new BlockShotApiException("BlockShot returned invalid capture history.", innerException: error);
        }
    }

    public async Task DeleteAsync(
        string code,
        MineTogetherSessionToken session,
        CancellationToken cancellationToken = default)
    {
        ValidateShareCode(code);
        ArgumentNullException.ThrowIfNull(session);
        using var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"shares/{code}", session);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw CreateApiError(response.StatusCode, body);
    }

    public Uri ShareUri(string code)
    {
        ValidateShareCode(code);
        return new Uri(siteRoot, code);
    }

    public Uri PreviewUri(string code)
    {
        ValidateShareCode(code);
        return new Uri(apiRoot, $"shares/{code}/preview/smol");
    }

    public async Task<byte[]> GetPreviewPngAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        ValidateShareCode(code);
        using var request = new HttpRequestMessage(HttpMethod.Get, PreviewUri(code));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateApiError(response.StatusCode, body);
        }

        if (response.Content.Headers.ContentLength is > MaximumPreviewBytes)
        {
            throw new BlockShotApiException("BlockShot returned an oversized preview.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > MaximumPreviewBytes)
            {
                throw new BlockShotApiException("BlockShot returned an oversized preview.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var png = destination.ToArray();
        if (!png.AsSpan().StartsWith(PngSignature))
        {
            throw new BlockShotApiException("BlockShot returned an invalid preview image.");
        }
        return png;
    }

    private HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string relativePath,
        MineTogetherSessionToken session)
    {
        var request = new HttpRequestMessage(method, new Uri(apiRoot, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Raw);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BlockShotApiException("BlockShot did not respond before the request timed out.");
        }
        catch (HttpRequestException error)
        {
            throw new BlockShotApiException("BlockShot could not be reached.", innerException: error);
        }
    }

    private static BlockShotApiException CreateApiError(HttpStatusCode statusCode, string body)
    {
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => "Your MineTogether session is no longer authorized.",
            HttpStatusCode.PaymentRequired => "This capture exceeds the limit for the current account.",
            HttpStatusCode.Conflict => "The upload connection closed before BlockShot completed it.",
            HttpStatusCode.RequestEntityTooLarge => "This capture is too large for BlockShot.",
            HttpStatusCode.UnsupportedMediaType => "BlockShot does not support this capture format.",
            HttpStatusCode.UnprocessableEntity => "This capture exceeds BlockShot's duration limit.",
            _ => $"BlockShot returned HTTP {(int)statusCode}."
        };
        if (!string.IsNullOrWhiteSpace(body) && body.Length <= 300)
        {
            message += " " + body.Trim();
        }
        return new BlockShotApiException(message, statusCode);
    }

    private static Uri EnsureTrailingSlash(Uri value) =>
        value.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? value : new Uri(value.AbsoluteUri + "/");

    private static void ValidateShareCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 64 || code.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The BlockShot share code is invalid.", nameof(code));
        }
    }

    private sealed record UploadDocument
    {
        public string? Code { get; init; }
    }
}

internal sealed class ProgressStreamContent : HttpContent
{
    private readonly Stream source;
    private readonly long length;
    private readonly IProgress<double>? progress;

    public ProgressStreamContent(Stream source, long length, string mediaType, IProgress<double>? progress)
    {
        this.source = source;
        this.length = length;
        this.progress = progress;
        Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    }

    protected override bool TryComputeLength(out long computedLength)
    {
        computedLength = length;
        return true;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => await CopySourceToAsync(stream, CancellationToken.None).ConfigureAwait(false);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
        => await CopySourceToAsync(stream, cancellationToken).ConfigureAwait(false);

    private async Task CopySourceToAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            progress?.Report(length == 0 ? 1 : copied / (double)length);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }

}
