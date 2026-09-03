using System.Reflection;
using BlockShot.VintageStory.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace BlockShot.VintageStory;

public sealed class BlockShotModSystem : ModSystem
{
    internal const string DialogHotkey = "blockshot-dialog";
    private const string CaptureHotkey = "blockshot-capture";
    private const string VideoHotkey = "blockshot-record";
    private const string ObsoleteDialogHotkey = "blockshot-dialog-v2";
    private const string ObsoleteCaptureHotkey = "blockshot-capture-v2";
    private const string EscapeMenuToggleCode = "escapemenudialog";
    private ICoreClientAPI? api;
    private HttpClient? httpClient;
    private BlockShotAccountController? account;
    private BlockShotCaptureRenderer? captureRenderer;
    private BlockShotCaptureWorkflow? captureWorkflow;
    private BlockShotVideoRecorder? videoRecorder;
    private BlockShotVideoWorkflow? videoWorkflow;
    private BlockShotDialog? dialog;
    private BlockShotPauseButtonDialog? pauseButton;
    private string? activePlayerUid;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI clientApi)
    {
        api = clientApi;
        var exactVersion = GetExactRuntimeGameVersion();
        var pack = new VintageStoryPackIdentity(exactVersion);

        var blockShotData = clientApi.GetOrCreateDataPath("BlockShot");
        var mineTogetherData = clientApi.GetOrCreateDataPath("MineTogether");
        var configurationStore = new BlockShotConfigurationStore(Path.Combine(blockShotData, "blockshot.json"));
        var configuration = configurationStore.Load();

        // Video uploads can legitimately take longer than a screenshot on a slow uplink.
        // User cancellation and mod disposal still abort requests through their tokens.
        httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var pairing = new MineTogetherPairingClient(httpClient);
        var blockShot = new BlockShotApiClient(httpClient);
        account = new BlockShotAccountController(
            pairing,
            Path.Combine(mineTogetherData, "session.token"),
            () => Volatile.Read(ref activePlayerUid),
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
        videoRecorder = new BlockShotVideoRecorder(clientApi);
        videoWorkflow = new BlockShotVideoWorkflow(
            clientApi,
            videoRecorder,
            blockShot,
            account,
            pack,
            configuration,
            () => captureWorkflow.Busy,
            Path.Combine(blockShotData, "temporary"),
            Path.Combine(blockShotData, "captures"));
        captureWorkflow.IsExternallyBusy = () => videoWorkflow.Active;
        dialog = new BlockShotDialog(clientApi, account, captureWorkflow, videoWorkflow, blockShot);
        pauseButton = new BlockShotPauseButtonDialog(clientApi, OpenDialog);

        // 0.1.1 briefly registered replacement codes. Remove them before reusing the original
        // codes so Vintage Story exposes exactly one configurable action for each command.
        clientApi.Input.HotKeys.Remove(ObsoleteDialogHotkey);
        clientApi.Input.HotKeys.Remove(ObsoleteCaptureHotkey);
        clientApi.Input.RegisterHotKey(
            DialogHotkey,
            "Open BlockShot",
            GlKeys.B,
            HotkeyType.GUIOrOtherControls,
            ctrlPressed: true,
            shiftPressed: true);
        MigratePreviousDefault(
            clientApi.Input.GetHotKeyByCode(DialogHotkey),
            GlKeys.B,
            ctrl: true,
            shift: true);
        clientApi.Input.SetHotKeyHandler(DialogHotkey, _ =>
        {
            OpenDialog();
            return true;
        });
        clientApi.Input.RegisterHotKey(
            CaptureHotkey,
            "BlockShot screenshot",
            GlKeys.S,
            HotkeyType.GUIOrOtherControls,
            ctrlPressed: true,
            shiftPressed: true);
        MigratePreviousDefault(
            clientApi.Input.GetHotKeyByCode(CaptureHotkey),
            GlKeys.S,
            ctrl: true,
            shift: true);
        clientApi.Input.SetHotKeyHandler(CaptureHotkey, _ => captureWorkflow.Capture());
        clientApi.Input.RegisterHotKey(
            VideoHotkey,
            "BlockShot video recording",
            GlKeys.R,
            HotkeyType.GUIOrOtherControls,
            ctrlPressed: true,
            shiftPressed: true);
        clientApi.Input.SetHotKeyHandler(VideoHotkey, _ => videoWorkflow.ToggleRecording());
        clientApi.Event.PauseResume += OnPauseResume;
        clientApi.Event.LevelFinalize += OnLevelFinalize;
    }

    public override void Dispose()
    {
        if (api is not null)
        {
            api.Event.PauseResume -= OnPauseResume;
            api.Event.LevelFinalize -= OnLevelFinalize;
        }
        pauseButton?.Dispose();
        pauseButton = null;
        dialog?.Dispose();
        dialog = null;
        videoWorkflow?.Dispose();
        videoWorkflow = null;
        videoRecorder?.Dispose();
        videoRecorder = null;
        captureWorkflow?.Dispose();
        captureWorkflow = null;
        captureRenderer?.Dispose();
        captureRenderer = null;
        account?.Dispose();
        account = null;
        httpClient?.Dispose();
        httpClient = null;
        Volatile.Write(ref activePlayerUid, null);
        api = null;
        base.Dispose();
    }

    private void OpenDialog()
    {
        pauseButton?.TryClose();
        CloseEscapeMenu();
        if (dialog?.IsOpened() == true) dialog.TryClose();
        else dialog?.TryOpen();
    }

    private void CloseEscapeMenu()
    {
        if (api is null) return;

        var escapeMenu = api.LoadedGuis
            .OfType<GuiDialog>()
            .FirstOrDefault(candidate =>
                candidate.IsOpened() &&
                string.Equals(candidate.ToggleKeyCombinationCode, EscapeMenuToggleCode, StringComparison.Ordinal));
        escapeMenu?.TryClose();
    }

    private void OnPauseResume(bool _) => RefreshPauseButton();

    private void OnLevelFinalize()
    {
        // Vintage Story's world/player objects belong to the game thread. Capture the UID here
        // and let upload/renewal tasks use this immutable snapshot instead of consulting them.
        var playerUid = api?.World?.Player?.PlayerUID?.Trim();
        Volatile.Write(ref activePlayerUid, string.IsNullOrWhiteSpace(playerUid) ? null : playerUid);
        account?.ReloadSharedSession();
    }

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

    private static void MigratePreviousDefault(
        HotKey? hotKey,
        GlKeys key,
        bool ctrl = false,
        bool shift = false)
    {
        if (hotKey?.CurrentMapping is not { } current || current.KeyCode != (int)key) return;

        var isOldUnmodifiedDefault = !current.Ctrl && !current.Alt && !current.Shift;
        var isOldControlDefault = current.Ctrl && !current.Shift;
        if (!isOldUnmodifiedDefault && !isOldControlDefault) return;

        var replacement = new KeyCombination
        {
            KeyCode = (int)key,
            Ctrl = ctrl,
            Shift = shift
        };
        hotKey.DefaultMapping = replacement.Clone();
        hotKey.CurrentMapping = replacement;
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
