using Content.Shared.ActionBlocker;
using Content.Shared.Hands;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Throwing;

namespace Content.Shared._CorvaxGoob.Gilbert.Finisher;

/// <summary>Input / movement lock for both participants of a finisher cutscene (predicted on the client).</summary>
public abstract class SharedGilbertFinisherSystem : EntitySystem
{
    [Dependency] protected readonly ActionBlockerSystem Blocker = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertFinisherComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<GilbertFinisherComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<GilbertFinisherComponent, AfterAutoHandleStateEvent>(OnHandleState);
        SubscribeLocalEvent<GilbertFinisherComponent, UpdateCanMoveEvent>(OnCanMove);
        SubscribeLocalEvent<GilbertFinisherComponent, ChangeDirectionAttemptEvent>(OnCancellable<ChangeDirectionAttemptEvent>);
        SubscribeLocalEvent<GilbertFinisherComponent, AttackAttemptEvent>(OnCancellable<AttackAttemptEvent>);
        SubscribeLocalEvent<GilbertFinisherComponent, UseAttemptEvent>(OnCancellable<UseAttemptEvent>);
        SubscribeLocalEvent<GilbertFinisherComponent, DropAttemptEvent>(OnCancellable<DropAttemptEvent>);
        SubscribeLocalEvent<GilbertFinisherComponent, PickupAttemptEvent>(OnCancellable<PickupAttemptEvent>);
        SubscribeLocalEvent<GilbertFinisherComponent, ThrowAttemptEvent>(OnCancellable<ThrowAttemptEvent>);
        SubscribeLocalEvent<GilbertFinisherComponent, InteractionAttemptEvent>(OnInteractAttempt);
        SubscribeLocalEvent<GilbertFinisherComponent, AttemptMobCollideEvent>(OnMobCollide);
        SubscribeLocalEvent<GilbertFinisherComponent, AttemptMobTargetCollideEvent>(OnMobTargetCollide);
    }

    public static bool Locking(GilbertFinisherComponent comp)
    {
        return comp.LifeStage <= ComponentLifeStage.Running;
    }

    private void OnStartup(Entity<GilbertFinisherComponent> ent, ref ComponentStartup args)
    {
        Blocker.UpdateCanMove(ent);
    }

    private void OnShutdown(Entity<GilbertFinisherComponent> ent, ref ComponentShutdown args)
    {
        Blocker.UpdateCanMove(ent);
        OnFinisherShutdown(ent);
    }

    /// <summary>Called when the component is removed (the client restores the sprite here).</summary>
    protected virtual void OnFinisherShutdown(Entity<GilbertFinisherComponent> ent)
    {
    }

    private void OnHandleState(Entity<GilbertFinisherComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        Blocker.UpdateCanMove(ent);
    }

    private void OnCanMove(Entity<GilbertFinisherComponent> ent, ref UpdateCanMoveEvent args)
    {
        if (Locking(ent.Comp))
            args.Cancel();
    }

    private void OnCancellable<T>(EntityUid uid, GilbertFinisherComponent comp, T args) where T : CancellableEntityEventArgs
    {
        if (Locking(comp))
            args.Cancel();
    }

    private void OnInteractAttempt(Entity<GilbertFinisherComponent> ent, ref InteractionAttemptEvent args)
    {
        if (Locking(ent.Comp))
            args.Cancelled = true;
    }

    private void OnMobCollide(Entity<GilbertFinisherComponent> ent, ref AttemptMobCollideEvent args)
    {
        if (Locking(ent.Comp))
            args.Cancelled = true;
    }

    private void OnMobTargetCollide(Entity<GilbertFinisherComponent> ent, ref AttemptMobTargetCollideEvent args)
    {
        if (Locking(ent.Comp))
            args.Cancelled = true;
    }
}
