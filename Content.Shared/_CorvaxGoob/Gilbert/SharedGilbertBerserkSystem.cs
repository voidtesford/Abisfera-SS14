using Content.Shared.ActionBlocker;
using Content.Shared.Body.Part;
using Content.Shared.Damage;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Movement.Events;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// Rules of Gilbert's second phase that both sides need: from the trigger on he can't be hurt or healed (body and
/// every body part), he stands still and can't turn or attack while charging up and while dying, and he never
/// picks anything up again (fists only). The server's GilbertBerserkSystem runs the phases.
/// </summary>
public abstract class SharedGilbertBerserkSystem : EntitySystem
{
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertBerserkComponent, BeforeDamageChangedEvent>(OnBeforeDamage);
        SubscribeLocalEvent<GilbertBerserkPartComponent, BeforeDamageChangedEvent>(OnPartBeforeDamage);
        SubscribeLocalEvent<GilbertBerserkComponent, UpdateCanMoveEvent>(OnCanMove);
        SubscribeLocalEvent<GilbertBerserkComponent, ChangeDirectionAttemptEvent>(OnChangeDirection);
        SubscribeLocalEvent<GilbertBerserkComponent, AttackAttemptEvent>(OnAttackAttempt);
        SubscribeLocalEvent<GilbertBerserkComponent, PickupAttemptEvent>(OnPickupAttempt);
        SubscribeLocalEvent<GilbertBerserkComponent, AfterAutoHandleStateEvent>(OnHandleState);
    }

    /// <summary>Is he in the second phase (transition, rage, dying or exploded)?</summary>
    public bool IsBerserk(EntityUid uid)
    {
        return TryComp<GilbertBerserkComponent>(uid, out var comp) && comp.Phase != GilbertBerserkPhase.None;
    }

    /// <summary>Raging: hunting with his fists (not charging up, dying or dead).</summary>
    public bool IsRaging(EntityUid uid)
    {
        return TryComp<GilbertBerserkComponent>(uid, out var comp) && comp.Phase == GilbertBerserkPhase.Rage;
    }

    /// <summary>Standing still: charging up or dying.</summary>
    public static bool IsFrozen(GilbertBerserkComponent comp)
    {
        return comp.Phase is GilbertBerserkPhase.Transition or GilbertBerserkPhase.Dying;
    }

    protected void SetPhase(Entity<GilbertBerserkComponent> ent, GilbertBerserkPhase phase, TimeSpan now)
    {
        ent.Comp.Phase = phase;
        ent.Comp.PhaseStart = now;
        Dirty(ent);
        _blocker.UpdateCanMove(ent);
    }

    /// <summary>First-phase damage: the server checks it against the trigger.</summary>
    protected virtual void OnFirstPhaseDamage(Entity<GilbertBerserkComponent> ent, ref BeforeDamageChangedEvent args)
    {
    }

    private void OnBeforeDamage(Entity<GilbertBerserkComponent> ent, ref BeforeDamageChangedEvent args)
    {
        if (args.Cancelled || ent.Comp.AllowDamage)
            return;

        if (ent.Comp.Phase != GilbertBerserkPhase.None)
        {
            args.Cancelled = true;
            return;
        }

        OnFirstPhaseDamage(ent, ref args);
    }

    private void OnPartBeforeDamage(Entity<GilbertBerserkPartComponent> ent, ref BeforeDamageChangedEvent args)
    {
        if (!TryComp<BodyPartComponent>(ent, out var part) || part.Body is not { } body
            || !TryComp<GilbertBerserkComponent>(body, out var comp))
            return;

        if (comp.Phase != GilbertBerserkPhase.None && !comp.AllowDamage)
            args.Cancelled = true;
    }

    private void OnCanMove(Entity<GilbertBerserkComponent> ent, ref UpdateCanMoveEvent args)
    {
        if (IsFrozen(ent.Comp))
            args.Cancel();
    }

    private void OnChangeDirection(Entity<GilbertBerserkComponent> ent, ref ChangeDirectionAttemptEvent args)
    {
        if (IsFrozen(ent.Comp))
            args.Cancel();
    }

    private void OnAttackAttempt(Entity<GilbertBerserkComponent> ent, ref AttackAttemptEvent args)
    {
        if (IsFrozen(ent.Comp) || ent.Comp.Phase == GilbertBerserkPhase.Dead)
            args.Cancel();
    }

    private void OnPickupAttempt(Entity<GilbertBerserkComponent> ent, ref PickupAttemptEvent args)
    {
        if (ent.Comp.Phase != GilbertBerserkPhase.None)
            args.Cancel();
    }

    private void OnHandleState(Entity<GilbertBerserkComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        _blocker.UpdateCanMove(ent);
    }
}

/// <summary>
/// On each of Gilbert's body parts once the second phase starts, so damage aimed straight at a part is blocked too.
/// </summary>
[RegisterComponent]
public sealed partial class GilbertBerserkPartComponent : Component;
