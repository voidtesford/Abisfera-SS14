using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Interaction.Events;
using Content.Shared.Movement.Events;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// While Gilbert's spawn sequence plays (<see cref="GilbertComponent.Active"/> is false) he can't be hurt,
/// can't move, turn or attack. The server's <c>GilbertSystem</c> runs the sequence and flips him active.
/// </summary>
public sealed class GilbertSpawnSequenceSystem : EntitySystem
{
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertComponent, BeforeDamageChangedEvent>(OnBeforeDamage);
        SubscribeLocalEvent<GilbertComponent, UpdateCanMoveEvent>(OnCanMove);
        SubscribeLocalEvent<GilbertComponent, ChangeDirectionAttemptEvent>(OnChangeDirection);
        SubscribeLocalEvent<GilbertComponent, AttackAttemptEvent>(OnAttackAttempt);
        SubscribeLocalEvent<GilbertComponent, AfterAutoHandleStateEvent>(OnHandleState);
    }

    /// <summary>True once the spawn sequence is over (or for anything that isn't Gilbert).</summary>
    public bool IsActive(EntityUid uid)
    {
        return !TryComp<GilbertComponent>(uid, out var comp) || comp.Active;
    }

    /// <summary>Flips him active: he can be hurt, move and attack again.</summary>
    public void SetActive(Entity<GilbertComponent> ent, bool active)
    {
        if (ent.Comp.Active == active)
            return;

        ent.Comp.Active = active;
        Dirty(ent);
        _blocker.UpdateCanMove(ent);
    }

    private void OnBeforeDamage(Entity<GilbertComponent> ent, ref BeforeDamageChangedEvent args)
    {
        if (!ent.Comp.Active)
            args.Cancelled = true;
    }

    private void OnCanMove(Entity<GilbertComponent> ent, ref UpdateCanMoveEvent args)
    {
        if (!ent.Comp.Active)
            args.Cancel();
    }

    private void OnChangeDirection(Entity<GilbertComponent> ent, ref ChangeDirectionAttemptEvent args)
    {
        if (!ent.Comp.Active)
            args.Cancel();
    }

    private void OnAttackAttempt(Entity<GilbertComponent> ent, ref AttackAttemptEvent args)
    {
        if (!ent.Comp.Active)
            args.Cancel();
    }

    private void OnHandleState(Entity<GilbertComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _blocker.UpdateCanMove(ent);
    }
}
