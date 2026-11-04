using Content.Shared.Movement.Systems;
using Content.Shared.Weapons.Ranged.Systems;

namespace Content.Shared._CorvaxGoob.Redspace.Anomalies;

/// <summary>Predicted parts of the redspace body effects: inverted movement, mutation slowdown and jammed guns.</summary>
public sealed class RedspaceAnomalySharedSystem : EntitySystem
{
    [Dependency] private readonly MovementSpeedModifierSystem _speed = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceMirroredComponent, RefreshMovementSpeedModifiersEvent>(OnMirroredSpeed);
        SubscribeLocalEvent<RedspaceMirroredComponent, ComponentStartup>((uid, _, _) => _speed.RefreshMovementSpeedModifiers(uid));
        SubscribeLocalEvent<RedspaceMirroredComponent, ComponentShutdown>(OnMirroredShutdown);
        SubscribeLocalEvent<RedspaceMutationComponent, RefreshMovementSpeedModifiersEvent>(OnMutationSpeed);
        SubscribeLocalEvent<RedspaceMutationComponent, AfterAutoHandleStateEvent>((uid, _, _) => _speed.RefreshMovementSpeedModifiers(uid));
        SubscribeLocalEvent<RedspaceMutationComponent, ComponentShutdown>(OnMutationShutdown);
        SubscribeLocalEvent<RedspaceJammedComponent, AttemptShootEvent>(OnJammedShoot);
    }

    private void OnMirroredSpeed(Entity<RedspaceMirroredComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        // A negative multiplier turns the wish direction around: up walks down, left walks right.
        // Skipped while shutting down (the component is still attached then).
        if (ent.Comp.LifeStage <= ComponentLifeStage.Running)
            args.ModifySpeed(-1f, -1f, bypassImmunity: true);
    }

    private void OnMirroredShutdown(Entity<RedspaceMirroredComponent> ent, ref ComponentShutdown args)
    {
        if (TerminatingOrDeleted(ent))
            return;

        _speed.RefreshMovementSpeedModifiers(ent);
    }

    private void OnMutationSpeed(Entity<RedspaceMutationComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (ent.Comp.LifeStage <= ComponentLifeStage.Running && ent.Comp.Slowdown < 1f)
            args.ModifySpeed(ent.Comp.Slowdown, ent.Comp.Slowdown);
    }

    private void OnMutationShutdown(Entity<RedspaceMutationComponent> ent, ref ComponentShutdown args)
    {
        if (TerminatingOrDeleted(ent))
            return;

        ent.Comp.Slowdown = 1f;
        _speed.RefreshMovementSpeedModifiers(ent);
    }

    private void OnJammedShoot(Entity<RedspaceJammedComponent> ent, ref AttemptShootEvent args)
    {
        if (args.Cancelled)
            return;

        args.Cancelled = true;
        args.Message = Loc.GetString("redspace-gun-jammed");
    }

    /// <summary>Re-applies the speed modifiers (after a mutation changed on the server).</summary>
    public void RefreshSpeed(EntityUid uid)
    {
        _speed.RefreshMovementSpeedModifiers(uid);
    }
}
