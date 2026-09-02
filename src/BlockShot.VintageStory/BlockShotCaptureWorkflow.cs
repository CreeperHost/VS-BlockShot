using BlockShot.VintageStory.Core;
using Vintagestory.API.Client;

namespace BlockShot.VintageStory;

internal sealed class BlockShotCaptureWorkflow : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly BlockShotCaptureRenderer renderer;
    private readonly BlockShotApiClient blockShot;
    private readonly BlockShotAccountController account;
    private readonly VintageStoryPackIdentity pack;
    private readonly BlockShotConfigurationStore configurationStore;
    private readonly string temporaryDirectory;
    private readonly string localDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;

    public BlockShotCaptureWorkflow(
        ICoreClientAPI api,
        BlockShotCaptureRenderer renderer,
        BlockShotApiClient blockShot,
        BlockShotAccountController account,
        VintageStoryPackIdentity pack,
        BlockShotConfigurationStore configurationStore,
        BlockShotConfiguration configuration,
        string temporaryDirectory,
        string localDirectory)
    {
        this.api = api;
        this.renderer = renderer;
        this.blockShot = blockShot;
        this.account = account;
        this.pack = pack;
        this.configurationStore = configurationStore;
        Configuration = configuration;
        this.temporaryDirectory = Path.GetFullPath(temporaryDirectory);
        this.localDirectory = Path.GetFullPath(localDirectory);
    }

    public event Action? Changed;

    public BlockShotConfiguration Configuration { get; }

    public bool Busy => busy;

    public Func<bool>? IsExternallyBusy { private get; set; }

    public double UploadProgress { get; private set; }

    public string? LastShareUrl { get; private set; }

    public bool Capture()
    {
        if (busy) return true;
        if (account.Session is null || account.Session.ExpiresWithin(TimeSpan.Zero))
        {
            api.ShowChatMessage("Link MineTogether before using BlockShot capture.");
            return true;
        }
        if (IsExternallyBusy?.Invoke() == true)
        {
            api.ShowChatMessage("BlockShot is still processing a video.");
            return true;
        }
        if (Configuration.UploadMode == UploadMode.Off) return false;

        busy = true;
        UploadProgress = 0;
        Changed?.Invoke();
        var path = Path.Combine(temporaryDirectory, $"blockshot-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png");
        if (!renderer.Queue(path, OnCaptured))
        {
            busy = false;
            Changed?.Invoke();
            api.ShowChatMessage("BlockShot is already taking a screenshot.");
        }
        return true;
    }

    public void CycleUploadMode()
    {
        Configuration.CycleUploadMode();
        SaveConfiguration();
    }

    public void ToggleAnonymous()
    {
        Configuration.Anonymous = !Configuration.Anonymous;
        SaveConfiguration();
    }

    public void ToggleClipboard()
    {
        Configuration.CopyUrlToClipboard = !Configuration.CopyUrlToClipboard;
        SaveConfiguration();
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void OnCaptured(string? path, Exception? error)
    {
        if (error is not null || path is null)
        {
            busy = false;
            Changed?.Invoke();
            api.ShowChatMessage($"BlockShot could not capture the frame: {error?.Message ?? "unknown error"}");
            return;
        }

        if (Configuration.UploadMode == UploadMode.Prompt)
        {
            busy = false;
            Changed?.Invoke();
            new BlockShotCapturePromptDialog(
                api,
                () => _ = UploadAsync(path),
                () => SaveLocal(path),
                () => DeleteTemporary(path)).TryOpen();
            return;
        }

        _ = UploadAsync(path);
    }

    private async Task UploadAsync(string path)
    {
        var session = account.Session;
        if (session is null || session.ExpiresWithin(TimeSpan.Zero))
        {
            SaveLocal(path);
            api.ShowChatMessage("BlockShot saved the screenshot locally. Link MineTogether before uploading.");
            return;
        }

        busy = true;
        UploadProgress = 0;
        Changed?.Invoke();
        try
        {
            var progress = new Progress<double>(value =>
            {
                UploadProgress = value;
                Changed?.Invoke();
            });
            var result = await blockShot.UploadPngAsync(
                path,
                session,
                account.PlayerUid,
                pack,
                Configuration.Anonymous,
                progress,
                lifetime.Token).ConfigureAwait(false);
            DeleteTemporary(path);
            LastShareUrl = result.ShareUri.AbsoluteUri;
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (Configuration.CopyUrlToClipboard) api.Input.ClipboardText = LastShareUrl;
                api.ShowChatMessage(BlockShotChatText.Uploaded(
                    result.ShareUri,
                    copied: Configuration.CopyUrlToClipboard));
                Changed?.Invoke();
            }, "blockshot-upload-complete");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is BlockShotApiException or IOException or UnauthorizedAccessException)
        {
            var localPath = MoveToLocal(path);
            api.Event.EnqueueMainThreadTask(() =>
            {
                api.ShowChatMessage($"BlockShot upload failed; saved {Path.GetFileName(localPath)} locally. {error.Message}");
                Changed?.Invoke();
            }, "blockshot-upload-failed");
        }
        finally
        {
            busy = false;
            UploadProgress = 0;
            Changed?.Invoke();
        }
    }

    private void SaveLocal(string path)
    {
        var localPath = MoveToLocal(path);
        api.ShowChatMessage($"BlockShot saved {Path.GetFileName(localPath)} locally.");
        busy = false;
        Changed?.Invoke();
    }

    private string MoveToLocal(string path)
    {
        Directory.CreateDirectory(localDirectory);
        var destination = Path.Combine(localDirectory, Path.GetFileName(path));
        if (File.Exists(destination))
        {
            destination = Path.Combine(
                localDirectory,
                $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        }
        File.Move(path, destination);
        return destination;
    }

    private static void DeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private void SaveConfiguration()
    {
        _ = configurationStore.SaveAsync(Configuration, lifetime.Token).ContinueWith(
            task =>
            {
                if (task.Exception is not null) api.Logger.Warning("BlockShot could not save settings: {0}", task.Exception.GetBaseException().Message);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        Changed?.Invoke();
    }
}
