using Content.Shared.ActionBlocker;
using Content.Shared.Hands;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Throwing;

namespace Content.Shared._CorvaxGoob.ProcAnim;

/// <summary>Input / movement lock for every actor of a ProcAnim cutscene (predicted on the client).</summary>
public abstract class SharedProcAnimSystem : EntitySystem
{
    [Dependency] protected readonly ActionBlockerSystem Blocker = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ProcAnimComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ProcAnimComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<ProcAnimComponent, AfterAutoHandleStateEvent>(OnHandleState);
        SubscribeLocalEvent<ProcAnimComponent, UpdateCanMoveEvent>(OnCanMove);
        SubscribeLocalEvent<ProcAnimComponent, ChangeDirectionAttemptEvent>(OnCancellable<ChangeDirectionAttemptEvent>);
        SubscribeLocalEvent<ProcAnimComponent, AttackAttemptEvent>(OnCancellable<AttackAttemptEvent>);
        SubscribeLocalEvent<ProcAnimComponent, UseAttemptEvent>(OnCancellable<UseAttemptEvent>);
        SubscribeLocalEvent<ProcAnimComponent, DropAttemptEvent>(OnCancellable<DropAttemptEvent>);
        SubscribeLocalEvent<ProcAnimComponent, PickupAttemptEvent>(OnCancellable<PickupAttemptEvent>);
        SubscribeLocalEvent<ProcAnimComponent, ThrowAttemptEvent>(OnCancellable<ThrowAttemptEvent>);
        SubscribeLocalEvent<ProcAnimComponent, InteractionAttemptEvent>(OnInteractAttempt);
        SubscribeLocalEvent<ProcAnimComponent, AttemptMobCollideEvent>(OnMobCollide);
        SubscribeLocalEvent<ProcAnimComponent, AttemptMobTargetCollideEvent>(OnMobTargetCollide);
    }

    public static bool Locking(ProcAnimComponent comp)
    {
        return comp.LifeStage <= ComponentLifeStage.Running;
    }

    private void OnStartup(Entity<ProcAnimComponent> ent, ref ComponentStartup args)
    {
        Blocker.UpdateCanMove(ent);
    }

    private void OnShutdown(Entity<ProcAnimComponent> ent, ref ComponentShutdown args)
    {
        Blocker.UpdateCanMove(ent);
        OnSceneShutdown(ent);
    }

    /// <summary>Called when the component is removed (the client restores the sprite here).</summary>
    protected virtual void OnSceneShutdown(Entity<ProcAnimComponent> ent)
    {
    }

    private void OnHandleState(Entity<ProcAnimComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        Blocker.UpdateCanMove(ent);
    }

    private void OnCanMove(Entity<ProcAnimComponent> ent, ref UpdateCanMoveEvent args)
    {
        if (Locking(ent.Comp))
            args.Cancel();
    }

    /// <summary>Set while the scene itself moves the actors' items (stow / draw / return): pickups and drops go through.</summary>
    protected bool ItemOps;

    private void OnCancellable<T>(EntityUid uid, ProcAnimComponent comp, T args) where T : CancellableEntityEventArgs
    {
        if (ItemOps && args is DropAttemptEvent or PickupAttemptEvent)
            return;
        if (Locking(comp))
            args.Cancel();
    }

    private void OnInteractAttempt(Entity<ProcAnimComponent> ent, ref InteractionAttemptEvent args)
    {
        if (Locking(ent.Comp))
            args.Cancelled = true;
    }

    private void OnMobCollide(Entity<ProcAnimComponent> ent, ref AttemptMobCollideEvent args)
    {
        if (Locking(ent.Comp))
            args.Cancelled = true;
    }

    private void OnMobTargetCollide(Entity<ProcAnimComponent> ent, ref AttemptMobTargetCollideEvent args)
    {
        if (Locking(ent.Comp))
            args.Cancelled = true;
    }
}
