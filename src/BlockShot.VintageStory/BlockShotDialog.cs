using BlockShot.VintageStory.Core;
using Vintagestory.API.Client;

namespace BlockShot.VintageStory;

internal sealed class BlockShotDialog : GuiDialog
{
    private const int VisibleHistoryRows = 4;
    private readonly BlockShotAccountController account;
    private readonly BlockShotCaptureWorkflow capture;
    private readonly BlockShotVideoWorkflow video;
    private readonly BlockShotApiClient blockShot;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, LoadedTexture> previewTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockShotPreviewElement> previewElements = new(StringComparer.Ordinal);
    private readonly HashSet<string> previewLoads = new(StringComparer.Ordinal);
    private readonly HashSet<string> previewFailures = new(StringComparer.Ordinal);
    private IReadOnlyList<BlockShotCapture> history = [];
    private string historyMessage = "Open BlockShot to load recent captures.";
    private int stateRefreshQueued;
    private bool disposed;

    public BlockShotDialog(
        ICoreClientAPI api,
        BlockShotAccountController account,
        BlockShotCaptureWorkflow capture,
        BlockShotVideoWorkflow video,
        BlockShotApiClient blockShot)
        : base(api)
    {
        this.account = account;
        this.capture = capture;
        this.video = video;
        this.blockShot = blockShot;
        account.Changed += OnStateChanged;
        capture.Changed += OnStateChanged;
        video.Changed += OnStateChanged;
        ComposeDialog();
    }

    public override string ToggleKeyCombinationCode => BlockShotModSystem.DialogHotkey;

    public override bool TryOpen()
    {
        ComposeDialog();
        var opened = base.TryOpen();
        if (opened) _ = RefreshHistoryAsync();
        return opened;
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        account.Changed -= OnStateChanged;
        capture.Changed -= OnStateChanged;
        video.Changed -= OnStateChanged;
        SingleComposer?.Dispose();
        SingleComposer = null;
        previewElements.Clear();
        DisposePreviewTextures();
        lifetime.Dispose();
        base.Dispose();
    }

    private void ComposeDialog()
    {
        SingleComposer?.Dispose();
        previewElements.Clear();
        PrunePreviewState();
        var bounds = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, 820, 550);
        var composer = capi.Gui
            .CreateCompo("blockshot", bounds)
            .AddShadedDialogBG(ElementBounds.Fill)
            .AddDialogTitleBar("BlockShot", () => TryClose())
            .AddStaticText(AccountTitle(), CairoFont.WhiteMediumText(), ElementBounds.Fixed(24, 52, 490, 30))
            .AddStaticText(AccountDescription(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, 82, 490, 44))
            .AddButton(AccountButtonText(), OnAccount, ElementBounds.Fixed(540, 66, 240, 34))
            .AddStaticText("Capture settings", CairoFont.WhiteMediumText(), ElementBounds.Fixed(24, 136, 250, 30))
            .AddButton(capture.Busy ? UploadStatus() : "Screenshot", OnCapture, ElementBounds.Fixed(408, 132, 178, 34))
            .AddButton(VideoButtonText(), OnVideo, ElementBounds.Fixed(596, 132, 184, 34))
            .AddButton($"Upload: {capture.Configuration.UploadMode}", OnCycleMode, ElementBounds.Fixed(24, 174, 240, 34))
            .AddButton($"Anonymous: {OnOff(capture.Configuration.Anonymous)}", OnToggleAnonymous, ElementBounds.Fixed(276, 174, 240, 34))
            .AddButton($"Copy URL: {OnOff(capture.Configuration.CopyUrlToClipboard)}", OnToggleClipboard, ElementBounds.Fixed(528, 174, 252, 34))
            .AddStaticText(VideoStatus(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, 220, 620, 34))
            .AddStaticText("Recent uploads", CairoFont.WhiteMediumText(), ElementBounds.Fixed(24, 258, 250, 30))
            .AddButton("Refresh", OnRefresh, ElementBounds.Fixed(660, 254, 120, 34));

        if (video.Active)
        {
            composer.AddButton("Cancel", OnVideoCancel, ElementBounds.Fixed(660, 214, 120, 34));
        }

        if (history.Count == 0)
        {
            composer.AddStaticText(
                historyMessage,
                CairoFont.WhiteSmallText().WithLineHeightMultiplier(1.25),
                ElementBounds.Fixed(24, 302, 756, 80));
        }
        else
        {
            for (var index = 0; index < Math.Min(VisibleHistoryRows, history.Count); index++)
            {
                var item = history[index];
                var y = 300 + (index * 54);
                var preview = new BlockShotPreviewElement(capi, ElementBounds.Fixed(24, y, 92, 52));
                if (previewTextures.TryGetValue(item.Code, out var texture)) preview.Texture = texture;
                previewElements[item.Code] = preview;
                composer
                    .AddInteractiveElement(preview, $"blockshot-preview-{index}")
                    .AddStaticText(
                        item.Created.ToLocalTime().ToString("ddd d MMM, HH:mm"),
                        CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(132, y + 12, 340, 28))
                    .AddButton("Copy", () => OnCopy(item.Code), ElementBounds.Fixed(496, y + 9, 86, 34))
                    .AddButton("Open", () => OnOpen(item.Code), ElementBounds.Fixed(592, y + 9, 86, 34))
                    .AddButton("Delete", () => OnDelete(item.Code), ElementBounds.Fixed(688, y + 9, 92, 34));
            }
        }

        SingleComposer = composer.Compose();
        QueueMissingPreviews();
    }

    private void OnStateChanged()
    {
        if (Interlocked.Exchange(ref stateRefreshQueued, 1) != 0) return;
        capi.Event.EnqueueMainThreadTask(() =>
        {
            Interlocked.Exchange(ref stateRefreshQueued, 0);
            if (!disposed && IsOpened()) ComposeDialog();
        }, "blockshot-dialog-state");
    }

    private bool OnAccount()
    {
        if (account.State == BlockShotAccountState.Pairing && account.PairingUri is not null)
        {
            account.OpenPairingLink();
        }
        else
        {
            account.LinkAccount();
        }
        return true;
    }

    private bool OnCycleMode()
    {
        capture.CycleUploadMode();
        return true;
    }

    private bool OnToggleAnonymous()
    {
        capture.ToggleAnonymous();
        return true;
    }

    private bool OnToggleClipboard()
    {
        capture.ToggleClipboard();
        return true;
    }

    private bool OnCapture()
    {
        capture.Capture();
        TryClose();
        return true;
    }

    private bool OnVideo()
    {
        var wasRecording = video.IsRecording;
        video.ToggleRecording();
        if (!wasRecording && video.IsRecording) TryClose();
        return true;
    }

    private bool OnVideoCancel() => video.Cancel();

    private bool OnRefresh()
    {
        previewFailures.Clear();
        _ = RefreshHistoryAsync();
        return true;
    }

    private bool OnCopy(string code)
    {
        capi.Input.ClipboardText = blockShot.ShareUri(code).AbsoluteUri;
        capi.ShowChatMessage("BlockShot URL copied.");
        return true;
    }

    private bool OnOpen(string code)
    {
        capi.Gui.OpenLink(blockShot.ShareUri(code).AbsoluteUri);
        return true;
    }

    private bool OnDelete(string code)
    {
        _ = DeleteAsync(code);
        return true;
    }

    private async Task RefreshHistoryAsync()
    {
        var session = account.Session;
        if (session is null)
        {
            history = [];
            historyMessage = "Link MineTogether to see captures uploaded by this account.";
            OnStateChanged();
            return;
        }

        historyMessage = "Loading…";
        OnStateChanged();
        try
        {
            var page = await blockShot.GetHistoryAsync(session, cancellationToken: lifetime.Token).ConfigureAwait(false);
            history = page.Results;
            historyMessage = page.Count == 0 ? "No captures have been uploaded by this account yet." : string.Empty;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (BlockShotApiException error)
        {
            history = [];
            historyMessage = error.Message;
        }
        OnStateChanged();
    }

    private async Task DeleteAsync(string code)
    {
        var session = account.Session;
        if (session is null) return;
        try
        {
            await blockShot.DeleteAsync(code, session, lifetime.Token).ConfigureAwait(false);
            await RefreshHistoryAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (BlockShotApiException error)
        {
            historyMessage = error.Message;
            OnStateChanged();
        }
    }

    private string AccountTitle() => account.State switch
    {
        BlockShotAccountState.SignedIn => $"MineTogether: {account.Session!.Username}",
        BlockShotAccountState.Pairing => "Waiting for MineTogether approval",
        BlockShotAccountState.Failed => "MineTogether sign-in failed",
        _ => "MineTogether account not linked"
    };

    private string AccountDescription() => account.State switch
    {
        BlockShotAccountState.SignedIn => "Captures are linked to this MineTogether account.",
        BlockShotAccountState.Pairing => "Approve the Vintage Story connection in your browser. This window updates automatically.",
        BlockShotAccountState.Failed => account.Failure ?? "Pairing failed. Try again.",
        _ => "Link once in your browser; no password or private signing key is stored by the mod."
    };

    private string AccountButtonText() => account.State switch
    {
        BlockShotAccountState.SignedIn => "Renew link",
        BlockShotAccountState.Pairing => "Open approval",
        _ => "Link account"
    };

    private string UploadStatus() => capture.UploadProgress > 0
        ? $"Uploading {capture.UploadProgress:P0}"
        : "Capturing…";

    private string VideoButtonText()
    {
        if (video.IsRecording) return $"Stop video ({Math.Min(BlockShotVideoRecorder.MaximumSeconds, (int)video.Elapsed.TotalSeconds)}s)";
        if (video.IsEncoding) return "Encoding video…";
        if (video.IsUploading) return video.UploadProgress > 0 ? $"Uploading {video.UploadProgress:P0}" : "Uploading video…";
        return "Record video";
    }

    private string VideoStatus()
    {
        if (video.IsRecording)
        {
            var dropped = video.DroppedFrames;
            return dropped == 0
                ? "Recording video — Ctrl+Shift+R stops."
                : $"Recording video — {dropped} frame{(dropped == 1 ? string.Empty : "s")} dropped to keep the game responsive.";
        }
        if (video.IsEncoding) return "Encoding queued frames in the background; the game remains playable.";
        if (video.IsUploading) return "Uploading video in the background; Cancel keeps a local copy.";
        return "Ctrl+Shift+S: screenshot   •   Ctrl+Shift+R: start/stop video (30s maximum).";
    }

    private static string OnOff(bool value) => value ? "On" : "Off";

    private void QueueMissingPreviews()
    {
        foreach (var item in history.Take(VisibleHistoryRows))
        {
            if (previewTextures.ContainsKey(item.Code) ||
                previewLoads.Contains(item.Code) ||
                previewFailures.Contains(item.Code)) continue;
            previewLoads.Add(item.Code);
            _ = LoadPreviewAsync(item.Code, lifetime.Token);
        }
    }

    private async Task LoadPreviewAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            var png = await blockShot.GetPreviewPngAsync(code, cancellationToken).ConfigureAwait(false);
            var pixels = await Task.Run(
                () => BlockShotPreviewPixels.DecodePng(png, capi.Logger),
                cancellationToken).ConfigureAwait(false);
            capi.Event.EnqueueMainThreadTask(
                () => ApplyPreview(code, pixels),
                $"blockshot-preview-ready-{code}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            capi.Event.EnqueueMainThreadTask(() =>
            {
                if (disposed) return;
                previewLoads.Remove(code);
                previewFailures.Add(code);
                capi.Logger.Warning("BlockShot could not load preview {0}: {1}", code, error.Message);
            }, $"blockshot-preview-failed-{code}");
        }
    }

    private void ApplyPreview(string code, BlockShotPreviewPixels pixels)
    {
        previewLoads.Remove(code);
        if (disposed || !DisplayedCodes().Contains(code)) return;

        var texture = new LoadedTexture(capi, 0, pixels.Width, pixels.Height);
        try
        {
            capi.Render.LoadOrUpdateTextureFromBgra(pixels.Bgra, linearMag: true, clampMode: 0, ref texture);
        }
        catch (Exception error)
        {
            texture.Dispose();
            previewFailures.Add(code);
            capi.Logger.Warning("BlockShot could not create preview texture {0}: {1}", code, error.Message);
            return;
        }

        if (previewTextures.Remove(code, out var previous)) previous.Dispose();
        previewTextures[code] = texture;
        if (previewElements.TryGetValue(code, out var element)) element.Texture = texture;
    }

    private HashSet<string> DisplayedCodes() =>
        history.Take(VisibleHistoryRows).Select(item => item.Code).ToHashSet(StringComparer.Ordinal);

    private void PrunePreviewState()
    {
        var displayed = DisplayedCodes();
        foreach (var code in previewTextures.Keys.Where(code => !displayed.Contains(code)).ToArray())
        {
            previewTextures.Remove(code, out var texture);
            texture?.Dispose();
        }
        previewFailures.RemoveWhere(code => !displayed.Contains(code));
    }

    private void DisposePreviewTextures()
    {
        foreach (var texture in previewTextures.Values) texture.Dispose();
        previewTextures.Clear();
    }
}
