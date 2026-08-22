using System.Reflection;
using BlockShot.VintageStory.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace BlockShot.VintageStory;

public sealed class BlockShotModSystem : ModSystem
{
    private const string DialogHotkey = "blockshot-dialog";
    private const string CaptureHotkey = "blockshot-capture";
    private const string EscapeMenuToggleCode = "escapemenudialog";
    private ICoreClientAPI? api;
    private HttpClient? httpClient;
    private BlockShotAccountController? account;
    private BlockShotCaptureRenderer? captureRenderer;
    private BlockShotCaptureWorkflow? captureWorkflow;
    private BlockShotDialog? dialog;
    private BlockShotPauseButtonDialog? pauseButton;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI clientApi)
    {
        api = clientApi;
        var exactVersion = GetExactRuntimeGameVersion();
        var pack = new VintageStoryPackIdentity(exactVersion);
        clientApi.Logger.Notification(
            "BlockShot is using exact Vintage Story MineTogether compatibility key '{0}'.",
            pack.CompatibilityKey);

        var blockShotData = clientApi.GetOrCreateDataPath("BlockShot");
        var mineTogetherData = clientApi.GetOrCreateDataPath("MineTogether");
        var configurationStore = new BlockShotConfigurationStore(Path.Combine(blockShotData, "blockshot.json"));
        var configuration = configurationStore.Load();

        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var pairing = new MineTogetherPairingClient(httpClient);
        var blockShot = new BlockShotApiClient(httpClient);
        account = new BlockShotAccountController(
            pairing,
            Path.Combine(mineTogetherData, "session.token"),
            uri => clientApi.Gui.OpenLink(uri.AbsoluteUri),
            message => clientApi.Logger.Warning("{0}", message));
        captureRenderer = new BlockShotCaptureRenderer(clientApi);
        captureWorkflow = new BlockShotCaptureWorkflow(
            clientApi,
            captureRenderer,
            blockShot,
            account,
            pack,
            configurationStore,
            configuration,
            Path.Combine(blockShotData, "temporary"),
            Path.Combine(blockShotData, "captures"));
        dialog = new BlockShotDialog(clientApi, account, captureWorkflow, blockShot);
        pauseButton = new BlockShotPauseButtonDialog(clientApi, OpenDialog);

        clientApi.Input.RegisterHotKey(
            DialogHotkey,
            "Open BlockShot",
            GlKeys.B,
            HotkeyType.GUIOrOtherControls,
            ctrlPressed: true);
        clientApi.Input.SetHotKeyHandler(DialogHotkey, _ =>
        {
            OpenDialog();
            return true;
        });
        clientApi.Input.RegisterHotKeyFirst(
            CaptureHotkey,
            "BlockShot screenshot",
            GlKeys.F12,
            HotkeyType.GUIOrOtherControls);
        clientApi.Input.SetHotKeyHandler(CaptureHotkey, _ => captureWorkflow.Capture());
        clientApi.Event.PauseResume += OnPauseResume;
    }

    public override void Dispose()
    {
        if (api is not null) api.Event.PauseResume -= OnPauseResume;
        pauseButton?.Dispose();
        pauseButton = null;
        dialog?.Dispose();
        dialog = null;
        captureWorkflow?.Dispose();
        captureWorkflow = null;
        captureRenderer?.Dispose();
        captureRenderer = null;
        account?.Dispose();
        account = null;
        httpClient?.Dispose();
        httpClient = null;
        api = null;
        base.Dispose();
    }

    private void OpenDialog()
    {
        pauseButton?.TryClose();
        if (dialog?.IsOpened() == true) dialog.TryClose();
        else dialog?.TryOpen();
    }

    private void OnPauseResume(bool _) => RefreshPauseButton();

    private void RefreshPauseButton()
    {
        if (api is null || pauseButton is null) return;
        var escapeMenuOpen = api.LoadedGuis
            .OfType<GuiDialog>()
            .Any(candidate =>
                candidate.IsOpened() &&
                string.Equals(candidate.ToggleKeyCombinationCode, EscapeMenuToggleCode, StringComparison.Ordinal));
        if (escapeMenuOpen && dialog?.IsOpened() != true)
        {
            if (!pauseButton.IsOpened()) pauseButton.TryOpen();
        }
        else if (pauseButton.IsOpened())
        {
            pauseButton.TryClose();
        }
    }

    private static string GetExactRuntimeGameVersion()
    {
        // ShortGameVersion is const, so direct access would inline the SDK build's value.
        var field = typeof(GameVersion).GetField(
            nameof(GameVersion.ShortGameVersion),
            BindingFlags.Public | BindingFlags.Static);
        if (field?.GetValue(null) is not string exactVersion || exactVersion.Length == 0)
        {
            throw new InvalidOperationException("Vintage Story did not expose its runtime ShortGameVersion.");
        }
        return exactVersion;
    }
}
