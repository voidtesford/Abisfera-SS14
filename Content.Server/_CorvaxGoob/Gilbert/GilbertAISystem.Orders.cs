using System.Linq;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server.NPC.Components;
using Content.Shared.Damage;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// v10 (t277u / t278u): the finisher after a point-blank shotgun hit, and the admin orders
/// (<c>gilbertgoto</c>: walk somewhere and hold it; <c>gilberttarget</c>: hunt one target to death).
/// </summary>
public sealed partial class GilbertAISystem
{
    [Dependency] private readonly GilbertFinisherSystem _finisher = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;

    #region Finisher

    /// <summary>His own shot (from the gun-shot hook, or tests): remembered if it was at his combat target at close range.</summary>
    public void NoteOwnShot(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        if (!comp.FinisherEnabled || InBerserk(ent) || comp.State != GilbertAIState.Combat || comp.FinisherVictim != null
            || comp.Target is not { } target || TerminatingOrDeleted(target))
            return;

        if (DistanceTo(ent, target) > comp.FinisherShotRange)
            return;

        comp.FinisherShot = (target, _timing.CurTime);
        comp.NextThink = _timing.CurTime + comp.FinisherShotSettle;
    }

    /// <summary>Damage left before the critical threshold; null without thresholds / damage.</summary>
    public FixedPoint2? CritMargin(EntityUid victim)
    {
        if (!TryComp<DamageableComponent>(victim, out var damageable)
            || !_thresholds.TryGetThresholdForState(victim, MobState.Critical, out var crit))
            return null;
        return crit.Value - damageable.TotalDamage;
    }

    /// <summary>All the conditions for a finisher on <paramref name="victim"/>, except the distance.</summary>
    public bool FinisherEligible(Entity<GilbertAIComponent> ent, EntityUid victim, TimeSpan now, bool checkMargin = true)
    {
        var comp = ent.Comp;
        if (!comp.FinisherEnabled || InBerserk(ent) || TerminatingOrDeleted(victim) || victim == ent.Owner)
            return false;
        if (!HasComp<HumanoidAppearanceComponent>(victim) || HasComp<GilbertAIComponent>(victim))
            return false;
        if (!TryComp<MobStateComponent>(victim, out var state) || state.CurrentState != MobState.Alive)
            return false;
        if (_finisher.IsPlaying(victim) || _finisher.IsPlaying(ent))
            return false;
        if (comp.FinisherCooldowns.TryGetValue(victim, out var until) && now < until)
            return false;
        if (checkMargin && (CritMargin(victim) is not { } margin || margin > comp.FinisherCritMargin))
            return false;
        return !OtherHostileNear(ent, victim);
    }

    private bool OtherHostileNear(Entity<GilbertAIComponent> ent, EntityUid victim)
    {
        var radius = ent.Comp.FinisherClearRadius;
        if (radius <= 0f)
            return false;

        foreach (var center in new[] { ent.Owner, victim })
        {
            foreach (var other in _lookup.GetEntitiesInRange<HumanoidAppearanceComponent>(Transform(center).Coordinates, radius))
            {
                if (other.Owner != ent.Owner && other.Owner != victim && IsHostile(ent, other))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Weighted pick among the configured variants (the shotgun ones only with the shotgun in hand).</summary>
    public string PickFinisher(Entity<GilbertAIComponent> ent)
    {
        var hasGun = GetGun(ent, out _, out _);
        var options = ent.Comp.FinisherWeights
            .Where(kv => kv.Value > 0f && _proto.HasIndex<Content.Shared._CorvaxGoob.Gilbert.Finisher.GilbertFinisherPrototype>(kv.Key)
                         && (hasGun || !ent.Comp.FinisherNeedsShotgun.Contains(kv.Key)))
            .ToList();
        if (options.Count == 0)
            return GilbertFinisherSystem.DefaultFinisher;

        var roll = _random.NextFloat() * options.Sum(kv => kv.Value);
        foreach (var (id, weight) in options)
        {
            roll -= weight;
            if (roll <= 0f)
                return id;
        }

        return options[^1].Key;
    }

    /// <summary>
    /// In combat, before anything else: turns a remembered point-blank shot into a run-up, runs up and starts the
    /// cutscene. True while it has taken over the think.
    /// </summary>
    private bool UpdateFinisher(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;

        if (comp.FinisherVictim is null && comp.FinisherShot is { } shot)
        {
            if (now - shot.At < comp.FinisherShotSettle)
                return false;

            comp.FinisherShot = null;
            if (now - shot.At <= comp.FinisherShotWindow
                && DistanceTo(ent, shot.Target) <= comp.FinisherShotRange + 0.5f
                && FinisherEligible(ent, shot.Target, now))
            {
                comp.FinisherVictim = shot.Target;
                comp.FinisherApproachStart = now;
                AbortTask(ent);
                StopCombatComponents(ent);
            }
        }

        if (comp.FinisherVictim is not { } victim)
            return false;

        // Run-up: still valid? (margin not re-checked: being shot again only makes it more so; healing up is fine too)
        if (now - comp.FinisherApproachStart > comp.FinisherApproachTimeout
            || !FinisherEligible(ent, victim, now, checkMargin: false)
            || DistanceTo(ent, victim) > comp.FinisherAbortRange)
        {
            AbortFinisher(ent, victim, now);
            return false;
        }

        if (DistanceTo(ent, victim) > comp.FinisherStartRange)
        {
            RegisterMove(ent, Transform(victim).Coordinates, comp.FinisherStartRange * 0.6f);
            return true;
        }

        var variant = PickFinisher(ent);
        StopCombatComponents(ent);
        _steering.Unregister(ent);
        comp.FinisherVictim = null;
        comp.FinisherCooldowns[victim] = now + comp.FinisherVictimCooldown;
        if (!_finisher.TryStart(ent, victim, out _, variant))
            return false;

        comp.LastFinisher = variant;
        return true;
    }

    private void AbortFinisher(Entity<GilbertAIComponent> ent, EntityUid victim, TimeSpan now)
    {
        ent.Comp.FinisherVictim = null;
        ent.Comp.FinisherCooldowns[victim] = now + ent.Comp.FinisherVictimCooldown;
    }

    #endregion

    #region Orders

    /// <summary><c>gilbertgoto</c>: walk to <paramref name="coords"/> and hold / patrol there (null clears).</summary>
    public void SetGotoOrder(Entity<GilbertAIComponent> ent, EntityCoordinates? coords)
    {
        ent.Comp.GotoOrder = coords;
        ent.Comp.GotoArrived = false;
        ent.Comp.Route.Clear();
        ent.Comp.RouteLeg = null;
        ent.Comp.NextWander = _timing.CurTime;
        ent.Comp.NextThink = _timing.CurTime;
        if (ent.Comp.State == GilbertAIState.Idle)
            _steering.Unregister(ent);
    }

    /// <summary><c>gilberttarget</c>: hunt <paramref name="target"/> to death (null clears).</summary>
    public void SetHuntTarget(Entity<GilbertAIComponent> ent, EntityUid? target)
    {
        var old = ent.Comp.HuntTarget;
        ent.Comp.HuntTarget = target;
        ent.Comp.NextThink = _timing.CurTime;
        if (old is { } o && o != target)
        {
            ent.Comp.ForcedHostile.Remove(o);
            if (ent.Comp.Target == o && !IsEnemy(o))
                ent.Comp.Target = null;
        }
    }

    private bool IsHuntAlive(EntityUid target)
    {
        return !TerminatingOrDeleted(target) && TryComp<MobStateComponent>(target, out var state)
               && state.CurrentState != MobState.Dead;
    }

    /// <summary>
    /// Every think: the hunted target is always hostile and its position is always known; he fights it straight away
    /// (no warning / arrest). When it is visible it beats anyone else he sees; when it isn't, someone he sees still gets
    /// fought first (a threat on the way). Returns the possibly overridden visible enemy.
    /// </summary>
    private EntityUid? UpdateHunt(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now, EntityUid? seen)
    {
        var comp = ent.Comp;
        if (comp.HuntTarget is not { } hunt)
            return seen;

        if (!IsHuntAlive(hunt) || Transform(hunt).MapID != xform.MapID)
        {
            // Dead (or gone): order done, back to normal.
            SetHuntTarget(ent, null);
            return seen;
        }

        // Crit still counts as alive here: he keeps hitting until it is dead.
        comp.ForcedHostile.Add(hunt);
        var huntVisible = seen == hunt || CanSee(ent, hunt, comp, xform);
        if (huntVisible || seen == null)
        {
            seen = hunt;
            if (comp.State != GilbertAIState.Combat && comp.State != GilbertAIState.Heal
                || comp.State == GilbertAIState.Combat && comp.Target != hunt && comp.Task == GilbertTask.None)
            {
                if (comp.State == GilbertAIState.Surrender && comp.Task == GilbertTask.Cuff)
                    AbortTask(ent);
                EnterCombat(ent, hunt, "found");
            }
        }

        if (comp.State == GilbertAIState.Combat && comp.Target == hunt)
        {
            // He knows where it is: never "lost".
            comp.LastSeenTarget = now;
            comp.LastKnownPosition = Transform(hunt).Coordinates;
        }

        return huntVisible ? hunt : seen == hunt ? null : seen;
    }

    /// <summary>Hunt targets stay hostile in crit (finished off) - a plain enemy in crit is not.</summary>
    private bool IsHuntTarget(GilbertAIComponent comp, EntityUid uid) => comp.HuntTarget == uid && IsHuntAlive(uid);

    /// <summary>Idle with a goto order: walk there (normal pathing), then patrol a small circle around it.</summary>
    private bool UpdateGoto(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.GotoOrder is not { } order)
            return false;

        if (!order.IsValid(EntityManager))
        {
            SetGotoOrder(ent, null);
            return false;
        }

        var dist = _transform.InRange(xform.Coordinates, order, comp.GotoReturnDistance);
        if (comp.GotoArrived && !dist)
            comp.GotoArrived = false;

        TryComp<NPCSteeringComponent>(ent, out var steering);
        if (!comp.GotoArrived)
        {
            if (steering is { Status: SteeringStatus.InRange } || _transform.InRange(xform.Coordinates, order, 0.9f))
            {
                _steering.Unregister(ent);
                comp.MoveTarget = null;
                comp.GotoArrived = true;
                comp.NextWander = now + TimeSpan.FromSeconds(_random.NextFloat(
                    (float) comp.ExplorePauseMin.TotalSeconds, (float) comp.ExplorePauseMax.TotalSeconds));
                return true;
            }

            if (steering == null || comp.MoveTarget is not { } mt || !_transform.InRange(mt, order, 0.5f))
                RegisterMove(ent, order, 0.7f);
            return true;
        }

        // Holding the point: short walks around it with pauses.
        if (steering != null)
        {
            if (steering.Status is SteeringStatus.InRange or SteeringStatus.NoPath)
            {
                _steering.Unregister(ent);
                comp.MoveTarget = null;
                comp.NextWander = now + TimeSpan.FromSeconds(_random.NextFloat(
                    (float) comp.ExplorePauseMin.TotalSeconds, (float) comp.ExplorePauseMax.TotalSeconds));
            }
            return true;
        }

        if (now >= comp.NextWander && comp.GotoHoldRadius > 1.5f)
            Wander(ent, order, comp.GotoHoldRadius);
        return true;
    }

    #endregion
}
