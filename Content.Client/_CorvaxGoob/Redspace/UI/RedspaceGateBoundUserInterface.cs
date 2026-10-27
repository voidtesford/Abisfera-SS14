using Content.Shared._CorvaxGoob.Redspace;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._CorvaxGoob.Redspace.UI;

[UsedImplicitly]
public sealed class RedspaceGateBoundUserInterface : BoundUserInterface
{
    private RedspaceGateWindow? _window;

    public RedspaceGateBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<RedspaceGateWindow>();
        _window.OpenPortal += (x, y) => SendMessage(new RedspaceGateOpenPortalMessage(x, y));
        _window.ClosePortal += () => SendMessage(new RedspaceGateClosePortalMessage());
        _window.EjectDisk += () => SendMessage(new RedspaceGateEjectDiskMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is RedspaceGateBoundUserInterfaceState current)
            _window?.UpdateState(current);
    }
}
