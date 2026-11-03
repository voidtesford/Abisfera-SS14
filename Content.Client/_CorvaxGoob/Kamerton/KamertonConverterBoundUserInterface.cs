using Content.Shared._CorvaxGoob.Kamerton;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._CorvaxGoob.Kamerton;

[UsedImplicitly]
public sealed class KamertonConverterBoundUserInterface : BoundUserInterface
{
    private KamertonConverterWindow? _window;

    public KamertonConverterBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<KamertonConverterWindow>();
        _window.Start += () => SendMessage(new KamertonConverterStartMessage());
        _window.Charge += () => SendMessage(new KamertonConverterChargeCellMessage());
        _window.Print += () => SendMessage(new KamertonConverterPrintCrystalMessage());
        _window.Eject += () => SendMessage(new KamertonConverterEjectMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is KamertonConverterBoundUserInterfaceState current)
            _window?.UpdateState(current);
    }
}
