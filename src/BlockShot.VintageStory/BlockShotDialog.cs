using BlockShot.VintageStory.Core;
using Vintagestory.API.Client;

namespace BlockShot.VintageStory;

internal sealed class BlockShotDialog : GuiDialog
{
    private readonly BlockShotAccountController account;
    private readonly BlockShotCaptureWorkflow capture;
    private readonly BlockShotApiClient blockShot;
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<BlockShotCapture> history = [];
    private string historyMessage = "Open BlockShot to load recent captures.";
    private bool disposed;

    public BlockShotDialog(
        ICoreClientAPI api,
        BlockShotAccountController account,
        BlockShotCaptureWorkflow capture,
        BlockShotApiClient blockShot)
        : base(api)
    {
        this.account = account;
        this.capture = capture;
        this.blockShot = blockShot;
        account.Changed += OnStateChanged;
        capture.Changed += OnStateChanged;
        ComposeDialog();
    }

    public override string ToggleKeyCombinationCode => "blockshot-dialog";

    public override bool TryOpen()
    {
        var opened = base.TryOpen();
        if (opened) _ = RefreshHistoryAsync();
        return opened;
    }

    public override void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        account.Changed -= OnStateChanged;
        capture.Changed -= OnStateChanged;
        base.Dispose();
    }

    private void ComposeDialog()
    {
        SingleComposer?.Dispose();
        var bounds = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, 820, 520);
        var composer = capi.Gui
            .CreateCompo("blockshot", bounds)
            .AddShadedDialogBG(ElementBounds.Fill)
            .AddDialogTitleBar("BlockShot", () => TryClose())
            .AddStaticText(AccountTitle(), CairoFont.WhiteMediumText(), ElementBounds.Fixed(24, 52, 530, 30))
            .AddStaticText(AccountDescription(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(24, 82, 530, 44))
            .AddButton(AccountButtonText(), OnAccount, ElementBounds.Fixed(590, 66, 190, 34))
            .AddStaticText("Capture settings", CairoFont.WhiteMediumText(), ElementBounds.Fixed(24, 136, 250, 30))
            .AddButton($"F12: {capture.Configuration.UploadMode}", OnCycleMode, ElementBounds.Fixed(24, 174, 210, 34))
            .AddButton($"Anonymous: {OnOff(capture.Configuration.Anonymous)}", OnToggleAnonymous, ElementBounds.Fixed(244, 174, 180, 34))
            .AddButton($"Copy URL: {OnOff(capture.Configuration.CopyUrlToClipboard)}", OnToggleClipboard, ElementBounds.Fixed(434, 174, 170, 34))
            .AddButton(capture.Busy ? UploadStatus() : "Capture now", OnCapture, ElementBounds.Fixed(614, 174, 166, 34))
            .AddStaticText("Recent uploads", CairoFont.WhiteMediumText(), ElementBounds.Fixed(24, 228, 250, 30))
            .AddButton("Refresh", OnRefresh, ElementBounds.Fixed(674, 224, 106, 34));

        if (history.Count == 0)
        {
            composer.AddStaticText(
                historyMessage,
                CairoFont.WhiteSmallText().WithLineHeightMultiplier(1.25),
                ElementBounds.Fixed(24, 272, 756, 80));
        }
        else
        {
            for (var index = 0; index < Math.Min(5, history.Count); index++)
            {
                var item = history[index];
                var y = 270 + (index * 44);
                composer
                    .AddStaticText(
                        $"{item.Created.LocalDateTime:g}   {item.Code}   {Size(item.FileMeta?.Size)}",
                        CairoFont.WhiteSmallText(),
                        ElementBounds.Fixed(24, y + 7, 500, 28))
                    .AddButton("Copy", () => OnCopy(item.Code), ElementBounds.Fixed(530, y, 76, 32))
                    .AddButton("Open", () => OnOpen(item.Code), ElementBounds.Fixed(614, y, 76, 32))
                    .AddButton("Delete", () => OnDelete(item.Code), ElementBounds.Fixed(698, y, 82, 32));
            }
        }

        SingleComposer = composer.Compose();
    }

    private void OnStateChanged()
    {
        capi.Event.EnqueueMainThreadTask(() =>
        {
            if (!disposed) ComposeDialog();
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

    private bool OnRefresh()
    {
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
        BlockShotAccountState.SignedIn => "Using the shared MineTogether session.token also used by MineTogether for VS.",
        BlockShotAccountState.Pairing => "Approve the server-connect request in your browser. This window updates automatically.",
        BlockShotAccountState.Failed => account.Failure ?? "Pairing failed. Try again.",
        _ => "Link once in your browser; no password or private signing key is stored by the mod."
    };

    private string AccountButtonText() => account.State switch
    {
        BlockShotAccountState.SignedIn => "Renew account link",
        BlockShotAccountState.Pairing => "Open approval page",
        _ => "Link MineTogether"
    };

    private string UploadStatus() => capture.UploadProgress > 0
        ? $"Uploading {capture.UploadProgress:P0}"
        : "Capturing…";

    private static string OnOff(bool value) => value ? "On" : "Off";

    private static string Size(long? bytes) => bytes switch
    {
        null => string.Empty,
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes / (1024d * 1024d):0.0} MiB"
    };
}
