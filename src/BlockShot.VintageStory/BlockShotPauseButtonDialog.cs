using Vintagestory.API.Client;

namespace BlockShot.VintageStory;

/// <summary>Public-API companion button shown beside the vanilla escape menu.</summary>
internal sealed class BlockShotPauseButtonDialog : GuiDialog
{
    private readonly Action openBlockShot;

    public BlockShotPauseButtonDialog(ICoreClientAPI api, Action openBlockShot)
        : base(api)
    {
        this.openBlockShot = openBlockShot;
        var bounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, 20, 20, 132, 40);
        SingleComposer = capi.Gui
            .CreateCompo("blockshot-pause-button", bounds)
            .AddButton("BlockShot", OnOpen, ElementBounds.Fixed(0, 0, 132, 40))
            .Compose();
    }

    public override string ToggleKeyCombinationCode => "blockshot-pause-button";

    public override double DrawOrder => 0.9;

    public override double InputOrder => -0.1;

    public override bool ShouldReceiveKeyboardEvents() => false;

    private bool OnOpen()
    {
        TryClose();
        openBlockShot();
        return true;
    }
}
