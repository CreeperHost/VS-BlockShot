using Vintagestory.API.Client;

namespace BlockShot.VintageStory;

internal sealed class BlockShotCapturePromptDialog : GuiDialog
{
    private readonly Action upload;
    private readonly Action saveLocal;
    private readonly Action cancel;
    private bool resolved;

    public BlockShotCapturePromptDialog(
        ICoreClientAPI api,
        Action upload,
        Action saveLocal,
        Action cancel,
        string mediaName = "screenshot")
        : base(api)
    {
        this.upload = upload;
        this.saveLocal = saveLocal;
        this.cancel = cancel;
        var bounds = ElementBounds.Fixed(EnumDialogArea.CenterMiddle, 0, 0, 520, 190);
        SingleComposer = capi.Gui
            .CreateCompo("blockshot-capture-prompt", bounds)
            .AddShadedDialogBG(ElementBounds.Fill)
            .AddDialogTitleBar($"BlockShot {mediaName}", OnCancel)
            .AddStaticText(
                $"Upload this {mediaName} to blocks.hot?",
                CairoFont.WhiteMediumText(),
                ElementBounds.Fixed(24, 58, 472, 56))
            .AddButton("Upload", OnUpload, ElementBounds.Fixed(24, 132, 140, 34))
            .AddButton("Save locally", OnSaveLocal, ElementBounds.Fixed(174, 132, 150, 34))
            .AddButton("Discard", OnCancelButton, ElementBounds.Fixed(334, 132, 120, 34))
            .Compose();
    }

    public override string ToggleKeyCombinationCode => "blockshot-capture-prompt";

    public override void OnGuiClosed()
    {
        if (!resolved)
        {
            resolved = true;
            cancel();
        }
        base.OnGuiClosed();
    }

    private bool OnUpload()
    {
        Resolve(upload);
        return true;
    }

    private bool OnSaveLocal()
    {
        Resolve(saveLocal);
        return true;
    }

    private bool OnCancelButton()
    {
        OnCancel();
        return true;
    }

    private void OnCancel() => Resolve(cancel);

    private void Resolve(Action action)
    {
        if (resolved) return;
        resolved = true;
        TryClose();
        action();
        Dispose();
    }
}
