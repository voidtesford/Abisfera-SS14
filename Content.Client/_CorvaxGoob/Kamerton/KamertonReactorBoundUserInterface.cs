using Content.Shared._CorvaxGoob.Kamerton;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._CorvaxGoob.Kamerton;

[UsedImplicitly]
public sealed class KamertonReactorBoundUserInterface : BoundUserInterface
{
    private KamertonReactorWindow? _window;

    public KamertonReactorBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<KamertonReactorWindow>();
        _window.Start += () => SendMessage(new KamertonReactorStartMessage());
        _window.Stop += () => SendMessage(new KamertonReactorStopMessage());
        _window.Scram += () => SendMessage(new KamertonReactorScramMessage());
        _window.SzkCommand += command => SendMessage(new KamertonSzkCommandMessage(command));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is KamertonReactorBoundUserInterfaceState current)
            _window?.UpdateState(current);
    }
}
