using Content.Shared._Shitmed.DoAfter;
using Content.Shared.Gravity;
using Content.Shared.Movement.Events;
using Content.Shared.Movement.Systems;

namespace Content.Shared._CorvaxGoob.Redspace.Physics;

/// <summary>
/// Predicted half of the redspace physics: turns the values in <see cref="RedspaceAffectedComponent"/>
/// into friction, speed, weightlessness and do-after modifiers.
/// </summary>
public sealed class RedspaceAffectedSystem : EntitySystem
{
    [Dependency] private readonly MovementSpeedModifierSystem _speed = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceAffectedComponent, RefreshFrictionModifiersEvent>(OnFriction);
        SubscribeLocalEvent<RedspaceAffectedComponent, RefreshMovementSpeedModifiersEvent>(OnSpeed);
        SubscribeLocalEvent<RedspaceAffectedComponent, IsWeightlessEvent>(OnWeightless);
        SubscribeLocalEvent<RedspaceAffectedComponent, GetDoAfterDelayMultiplierEvent>(OnDoAfter);
        SubscribeLocalEvent<RedspaceAffectedComponent, TileFrictionEvent>(OnMobTileFriction);
        SubscribeLocalEvent<RedspaceAffectedComponent, AfterAutoHandleStateEvent>(OnHandleState);
        SubscribeLocalEvent<RedspaceAffectedComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<RedspaceSlideComponent, TileFrictionEvent>(OnSlideFriction);
    }

    private void OnFriction(Entity<RedspaceAffectedComponent> ent, ref RefreshFrictionModifiersEvent args)
    {
        args.ModifyFriction(ent.Comp.Friction);
        args.ModifyAcceleration(ent.Comp.Acceleration);
    }

    private void OnSpeed(Entity<RedspaceAffectedComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        args.ModifySpeed(ent.Comp.Speed, ent.Comp.Speed);
    }

    private void OnWeightless(Entity<RedspaceAffectedComponent> ent, ref IsWeightlessEvent args)
    {
        // Magboots and the like handle it first and win.
        if (!ent.Comp.Weightless || args.Handled)
            return;

        args.IsWeightless = true;
        args.Handled = true;
    }

    private void OnDoAfter(Entity<RedspaceAffectedComponent> ent, ref GetDoAfterDelayMultiplierEvent args)
    {
        args.Multiplier *= ent.Comp.DoAfter;
    }

    private void OnMobTileFriction(Entity<RedspaceAffectedComponent> ent, ref TileFrictionEvent args)
    {
        args.Modifier *= ent.Comp.Friction;
    }

    private void OnSlideFriction(Entity<RedspaceSlideComponent> ent, ref TileFrictionEvent args)
    {
        args.Modifier *= ent.Comp.Friction;
    }

    private void OnHandleState(Entity<RedspaceAffectedComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        Refresh(ent);
    }

    private void OnShutdown(Entity<RedspaceAffectedComponent> ent, ref ComponentShutdown args)
    {
        if (TerminatingOrDeleted(ent))
            return;

        // The component is still attached during shutdown, so reset the values before refreshing.
        ent.Comp.Friction = 1f;
        ent.Comp.Acceleration = 1f;
        ent.Comp.Speed = 1f;
        Refresh(ent);
    }

    /// <summary>Re-applies friction and speed after the values changed.</summary>
    public void Refresh(EntityUid uid)
    {
        _speed.RefreshFrictionModifiers(uid);
        _speed.RefreshMovementSpeedModifiers(uid);
    }
}
