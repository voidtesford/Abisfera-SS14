using System.Linq;
using Content.Shared.CombatMode;
using Content.Shared.Humanoid;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Physics.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Cuffed loiterers. A cuffed person who spends <see cref="GilbertAIComponent.LingerPunchTime"/> (in total) near him
/// gets walked up to, punched with his bare hand and told «Убирайтесь!». He then stays at that spot facing them;
/// still near and in view for <see cref="GilbertAIComponent.ExpelWatchTime"/> (in total) and he fights them even
/// though they are cuffed. Someone lying still on the floor is ignored (the timers pause) until they move or speak.
/// </summary>
public sealed partial class GilbertAISystem
{
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;

    /// <summary>He goes this close to throw the punch (his fist reaches 1.5).</summary>
    private const float PunchRange = 1.1f;

    /// <summary>Walking up to the loiterer for the punch gives up after this long.</summary>
    private static readonly TimeSpan ExpelApproachTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Someone he fights: an uncuffed living humanoid, or a cuffed one who ignored his punch.
    /// </summary>
    public bool IsHostile(Entity<GilbertAIComponent> ent, EntityUid uid)
    {
        if (IsEnemy(uid))
            return true;

        if (IsHuntTarget(ent.Comp, uid))
            return true; // v10: gilberttarget - hostile until dead (crit included)

        return ent.Comp.ForcedHostile.Contains(uid) && !TerminatingOrDeleted(uid) && _mobState.IsAlive(uid);
    }

    /// <summary>
    /// Lying on the floor and neither moving nor speaking for <see cref="GilbertAIComponent.DormantDelay"/>.
    /// </summary>
    public bool IsDormant(Entity<GilbertAIComponent> ent, EntityUid victim, TimeSpan now)
    {
        if (!_standing.IsDown(victim))
            return false;

        return !ent.Comp.LastActive.TryGetValue(victim, out var active) || now - active >= ent.Comp.DormantDelay;
    }

    /// <summary>Moving or on their feet counts as active (speech is recorded by the hearing code).</summary>
    private void TrackActivity(Entity<GilbertAIComponent> ent, EntityUid victim, TimeSpan now)
    {
        var moving = TryComp<PhysicsComponent>(victim, out var body) && body.LinearVelocity.Length() >= ent.Comp.ArrestStillSpeed;
        if (moving || !_standing.IsDown(victim))
            ent.Comp.LastActive[victim] = now;
    }

    public void MarkActive(Entity<GilbertAIComponent> ent, EntityUid victim, TimeSpan now)
    {
        if (IsCuffed(victim))
            ent.Comp.LastActive[victim] = now;
    }

    /// <summary>
    /// Within <see cref="GilbertAIComponent.LingerRange"/> with nothing solid (windows included) in between;
    /// v9 (t262u): beyond <see cref="GilbertAIComponent.LingerCloseRange"/> he must also actually see them.
    /// </summary>
    public bool IsNearby(Entity<GilbertAIComponent> ent, EntityUid other)
    {
        var range = ent.Comp.LingerRange;
        var dist = DistanceTo(ent, other);
        if (dist > range || !_interaction.InRangeUnobstructed(ent.Owner, other, dist + 0.5f))
            return false;

        return dist <= ent.Comp.LingerCloseRange || CanSee(ent.Owner, other, ent.Comp);
    }

    /// <summary>
    /// Counts the time cuffed people spend near him; returns true if he went to punch one.
    /// Only while he's free (idle / investigating / searching, no task).
    /// </summary>
    private bool UpdateLoiterers(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        var free = comp.State is GilbertAIState.Idle or GilbertAIState.Investigate or GilbertAIState.Search;

        foreach (var other in _lookup.GetEntitiesInRange<HumanoidAppearanceComponent>(xform.Coordinates, comp.LingerRange + 1f))
        {
            if (other.Owner == ent.Owner || HasComp<GilbertAIComponent>(other) || !IsCuffed(other) || !_mobState.IsAlive(other))
                continue;

            TrackActivity(ent, other, now);

            // The person he's searching right now isn't loitering.
            if (!free || comp.Task == GilbertTask.Loot && comp.TaskTarget == other.Owner || !IsNearby(ent, other))
                continue;

            comp.LingerLastNear[other] = now;
            if (IsDormant(ent, other, now))
                continue;

            var total = comp.Linger.GetValueOrDefault(other) + comp.ThinkDelta;
            comp.Linger[other] = total;

            if (total >= comp.LingerPunchTime && comp.Task == GilbertTask.None)
            {
                BeginExpel(ent, other, now);
                return true;
            }
        }

        if (comp.LingerLastNear.Count > 0)
        {
            foreach (var (uid, last) in comp.LingerLastNear.ToList())
            {
                if (now - last < comp.LingerForget && !TerminatingOrDeleted(uid))
                    continue;

                comp.LingerLastNear.Remove(uid);
                comp.Linger.Remove(uid);
                comp.LastActive.Remove(uid);
            }
        }

        return false;
    }

    private void BeginExpel(Entity<GilbertAIComponent> ent, EntityUid victim, TimeSpan now)
    {
        var comp = ent.Comp;
        StopCombatComponents(ent);
        SetState(ent, GilbertAIState.Expel);
        comp.ExpelTarget = victim;
        comp.ExpelPunched = false;
        comp.ExpelStarted = now;
        comp.ExpelLastSeen = now;
        comp.ExpelWatch = TimeSpan.Zero;
        comp.Linger.Remove(victim);
        FaceTowards(ent, victim);
    }

    private void EndExpel(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.ExpelTarget is { } victim)
            comp.Linger.Remove(victim);

        comp.ExpelTarget = null;
        comp.ExpelPunched = false;
        comp.ExpelWatch = TimeSpan.Zero;
        EnterIdle(ent);
    }

    private void UpdateExpel(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.ExpelTarget is not { } victim || TerminatingOrDeleted(victim) || !_mobState.IsAlive(victim))
        {
            EndExpel(ent);
            return;
        }

        // Uncuffed: a normal enemy now, the vision code picks them up.
        if (!IsCuffed(victim))
        {
            EndExpel(ent);
            return;
        }

        TrackActivity(ent, victim, now);
        var visible = CanSee(ent, victim, comp, xform);
        if (visible)
            comp.ExpelLastSeen = now;

        if (!comp.ExpelPunched)
        {
            if (now - comp.ExpelStarted > ExpelApproachTimeout || DistanceTo(ent, victim) > comp.LingerRange + 3f
                || TryComp<Content.Server.NPC.Components.NPCSteeringComponent>(ent, out var steering)
                && steering.Status == Content.Server.NPC.Components.SteeringStatus.NoPath)
            {
                // They walked off (or he can't get there): that's what he wanted.
                EndExpel(ent);
                return;
            }

            if (DistanceTo(ent, victim) > PunchRange || !_interaction.InRangeUnobstructed(ent.Owner, victim, 1.5f))
            {
                RegisterMove(ent, Transform(victim).Coordinates, PunchRange - 0.2f);
                return;
            }

            _steering.Unregister(ent);
            FaceTowards(ent, victim);
            if (!Punch(ent, victim))
                return; // fist still on cooldown: next think

            Say(ent, "getout", 1);
            comp.ExpelPunched = true;
            comp.ExpelSpot = xform.Coordinates;
            comp.ExpelLastSeen = now;
            comp.ExpelWatch = TimeSpan.Zero;
            comp.LastActive[victim] = now;
            return;
        }

        // Waiting at the punch spot: never walks off, never follows.
        _steering.Unregister(ent);

        if (visible && IsNearby(ent, victim))
        {
            if (!IsDormant(ent, victim, now))
                comp.ExpelWatch += comp.ThinkDelta;
        }
        else if (now - comp.ExpelLastSeen > comp.ExpelGiveUp)
        {
            EndExpel(ent);
            return;
        }

        if (comp.ExpelWatch >= comp.ExpelWatchTime)
        {
            comp.ForcedHostile.Add(victim);
            comp.ExpelTarget = null;
            comp.ExpelPunched = false;
            EnterCombat(ent, victim, "noncomply");
            return;
        }

        if (now - comp.ExpelStarted > comp.ExpelMaxWait)
            EndExpel(ent);
    }

    /// <summary>A bare-handed light attack with his own fist, in combat mode for the swing.</summary>
    private bool Punch(Entity<GilbertAIComponent> ent, EntityUid target)
    {
        if (!TryComp<MeleeWeaponComponent>(ent, out var fist))
            return false;

        var wasInCombat = _combatMode.IsInCombatMode(ent);
        if (!wasInCombat)
            _combatMode.SetInCombatMode(ent, true);

        var hit = _melee.AttemptLightAttack(ent, ent, fist, target);

        if (!wasInCombat)
            _combatMode.SetInCombatMode(ent, false);

        return hit;
    }
}
