using BlockShot.VintageStory.Core;
using Vintagestory.API.Client;

namespace BlockShot.VintageStory;

internal sealed class BlockShotVideoWorkflow : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly BlockShotVideoRecorder recorder;
    private readonly BlockShotApiClient blockShot;
    private readonly BlockShotAccountController account;
    private readonly VintageStoryPackIdentity pack;
    private readonly BlockShotConfiguration configuration;
    private readonly Func<bool> screenshotBusy;
    private readonly string temporaryDirectory;
    private readonly string localDirectory;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? uploadCancellation;
    private string? currentPath;
    private bool uploading;
    private bool observedRecording;
    private bool disposed;

    public BlockShotVideoWorkflow(
        ICoreClientAPI api,
        BlockShotVideoRecorder recorder,
        BlockShotApiClient blockShot,
        BlockShotAccountController account,
        VintageStoryPackIdentity pack,
        BlockShotConfiguration configuration,
        Func<bool> screenshotBusy,
        string temporaryDirectory,
        string localDirectory)
    {
        this.api = api;
        this.recorder = recorder;
        this.blockShot = blockShot;
        this.account = account;
        this.pack = pack;
        this.configuration = configuration;
        this.screenshotBusy = screenshotBusy;
        this.temporaryDirectory = Path.GetFullPath(temporaryDirectory);
        this.localDirectory = Path.GetFullPath(localDirectory);
        recorder.Changed += OnRecorderChanged;
    }

    public event Action? Changed;

    public bool IsRecording => recorder.IsRecording;

    public bool IsEncoding => recorder.IsFinalizing;

    public bool IsUploading => uploading;

    public bool Active => recorder.Active || uploading;

    public TimeSpan Elapsed => recorder.Elapsed;

    public int DroppedFrames => recorder.DroppedFrames;

    public double UploadProgress { get; private set; }

    public string? LastShareUrl { get; private set; }

    public bool ToggleRecording()
    {
        if (recorder.IsRecording) return recorder.Stop();
        if (recorder.IsFinalizing)
        {
            api.ShowChatMessage("BlockShot is encoding the current video.");
            return true;
        }
        if (uploading)
        {
            api.ShowChatMessage("BlockShot is uploading the current video.");
            return true;
        }
        if (account.Session is null || account.Session.ExpiresWithin(TimeSpan.Zero))
        {
            api.ShowChatMessage("Link MineTogether before using BlockShot capture.");
            return true;
        }
        if (configuration.UploadMode == UploadMode.Off)
        {
            api.ShowChatMessage("BlockShot capture is Off. Change Upload in the BlockShot window first.");
            return true;
        }
        if (screenshotBusy())
        {
            api.ShowChatMessage("BlockShot is still processing a screenshot.");
            return true;
        }

        currentPath = Path.Combine(
            temporaryDirectory,
            $"blockshot-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.webm");
        if (!recorder.Start(currentPath, OnRecorded))
        {
            currentPath = null;
            api.ShowChatMessage("BlockShot could not start another recording yet.");
            return true;
        }

        api.ShowChatMessage(
            $"BlockShot video recording started. Press Ctrl+Shift+R again to stop ({BlockShotVideoRecorder.MaximumSeconds}s maximum).");
        return true;
    }

    public bool Cancel()
    {
        if (recorder.Active)
        {
            var path = currentPath;
            currentPath = null;
            recorder.Cancel();
            if (path is not null) DeleteTemporary(path);
            api.ShowChatMessage("BlockShot video recording cancelled.");
            Changed?.Invoke();
            return true;
        }
        if (uploading && uploadCancellation is not null)
        {
            uploadCancellation.Cancel();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        recorder.Changed -= OnRecorderChanged;
        lifetime.Cancel();
        uploadCancellation?.Cancel();
        recorder.Cancel();
        lifetime.Dispose();
    }

    private void OnRecorderChanged()
    {
        var recordingNow = recorder.IsRecording;
        if (observedRecording && !recordingNow && recorder.IsFinalizing)
        {
            api.ShowChatMessage("BlockShot video stopped; preparing it in the background.");
        }
        observedRecording = recordingNow;
        Changed?.Invoke();
    }

    private void OnRecorded(string? path, Exception? error)
    {
        if (error is not null || path is null)
        {
            currentPath = null;
            Changed?.Invoke();
            if (error is not null) api.ShowChatMessage($"BlockShot could not encode the video: {error.Message}");
            return;
        }

        currentPath = path;
        if (configuration.UploadMode == UploadMode.Prompt)
        {
            Changed?.Invoke();
            new BlockShotCapturePromptDialog(
                api,
                () => _ = UploadAsync(path),
                () => SaveLocal(path),
                () => Discard(path),
                "video").TryOpen();
            return;
        }
        if (configuration.UploadMode == UploadMode.Off)
        {
            SaveLocal(path);
            return;
        }

        _ = UploadAsync(path);
    }

    private async Task UploadAsync(string path)
    {
        var session = account.Session;
        if (session is null || session.ExpiresWithin(TimeSpan.Zero))
        {
            var localPath = MoveToLocal(path);
            currentPath = null;
            api.ShowChatMessage(
                $"BlockShot saved {Path.GetFileName(localPath)} locally. Link MineTogether before uploading.");
            Changed?.Invoke();
            return;
        }

        uploadCancellation?.Dispose();
        uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var cancellationToken = uploadCancellation.Token;
        uploading = true;
        UploadProgress = 0;
        Changed?.Invoke();
        try
        {
            var progress = new Progress<double>(value =>
            {
                UploadProgress = value;
                Changed?.Invoke();
            });
            var result = await blockShot.UploadWebmAsync(
                path,
                session,
                account.PlayerUid,
                pack,
                configuration.Anonymous,
                progress,
                cancellationToken).ConfigureAwait(false);
            DeleteTemporary(path);
            currentPath = null;
            LastShareUrl = result.ShareUri.AbsoluteUri;
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (configuration.CopyUrlToClipboard) api.Input.ClipboardText = LastShareUrl;
                api.ShowChatMessage(BlockShotChatText.Uploaded(
                    result.ShareUri,
                    "video",
                    configuration.CopyUrlToClipboard));
                Changed?.Invoke();
            }, "blockshot-video-upload-complete");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                var localPath = MoveToLocal(path);
                currentPath = null;
                if (!lifetime.IsCancellationRequested)
                {
                    api.Event.EnqueueMainThreadTask(() =>
                        api.ShowChatMessage($"BlockShot upload cancelled; saved {Path.GetFileName(localPath)} locally."),
                        "blockshot-video-upload-cancelled");
                }
            }
        }
        catch (Exception error) when (error is BlockShotApiException or IOException or UnauthorizedAccessException)
        {
            var localPath = MoveToLocal(path);
            currentPath = null;
            api.Event.EnqueueMainThreadTask(() =>
            {
                api.ShowChatMessage($"BlockShot video upload failed; saved {Path.GetFileName(localPath)} locally. {error.Message}");
                Changed?.Invoke();
            }, "blockshot-video-upload-failed");
        }
        finally
        {
            uploading = false;
            UploadProgress = 0;
            uploadCancellation?.Dispose();
            uploadCancellation = null;
            Changed?.Invoke();
        }
    }

    private void SaveLocal(string path)
    {
        var localPath = MoveToLocal(path);
        currentPath = null;
        api.ShowChatMessage($"BlockShot saved {Path.GetFileName(localPath)} locally.");
        Changed?.Invoke();
    }

    private void Discard(string path)
    {
        DeleteTemporary(path);
        currentPath = null;
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
}
