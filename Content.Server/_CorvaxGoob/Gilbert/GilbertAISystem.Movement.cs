using Content.Server.NPC.Components;
using Content.Shared.Doors.Components;
using Content.Shared.Movement.Components;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Facing, door opening and the anti-stall watchdogs.
/// </summary>
public sealed partial class GilbertAISystem
{
    /// <summary>How recently the target must have been seen for him to keep facing it.</summary>
    private static readonly TimeSpan FacingMemory = TimeSpan.FromSeconds(0.6);

    #region Facing

    /// <summary>
    /// Whenever he has detected someone he deals with, he faces them every tick: the enemy he fights or holds at
    /// gunpoint (seen within <see cref="FacingMemory"/>), the loiterer he is expelling / watching, the person he is
    /// searching. Movement does not rotate him then (no walking backwards with the enemy behind him), and his
    /// vision cone stays on them. Otherwise he faces where he walks.
    /// </summary>
    private void UpdateFacing(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        if (HasComp<ActorComponent>(ent) || !_mobState.IsAlive(ent))
        {
            ReleaseFacing(ent);
            return;
        }

        if (GetFacingTarget(ent.Comp, now) is { } target)
        {
            if (!ent.Comp.HoldingFacing)
            {
                ent.Comp.HoldingFacing = !HasComp<NoRotateOnMoveComponent>(ent);
                EnsureComp<NoRotateOnMoveComponent>(ent);
            }

            FaceTowards(ent, target);
            return;
        }

        ReleaseFacing(ent);
    }

    /// <summary>Who he should be looking at right now, if anyone.</summary>
    public EntityUid? GetFacingTarget(GilbertAIComponent comp, TimeSpan now)
    {
        if (comp.State is GilbertAIState.Combat or GilbertAIState.Surrender
            && comp.Target is { } target && !TerminatingOrDeleted(target)
            && now - comp.LastSeenTarget < FacingMemory)
            return target;

        if (comp.State == GilbertAIState.Expel && comp.ExpelTarget is { } loiterer && !TerminatingOrDeleted(loiterer)
            && (comp.ExpelPunched || now - comp.ExpelLastSeen < FacingMemory))
            return loiterer;

        // Searching someone: eyes on them once he's next to them.
        if (comp.Task == GilbertTask.Loot && comp.TaskStep > 0 && comp.TaskTarget is { } victim && !TerminatingOrDeleted(victim))
            return victim;

        return null;
    }

    private void ReleaseFacing(Entity<GilbertAIComponent> ent)
    {
        if (!ent.Comp.HoldingFacing)
            return;

        ent.Comp.HoldingFacing = false;
        RemComp<NoRotateOnMoveComponent>(ent);
    }

    #endregion

    #region Doors

    /// <summary>
    /// Opens closed doors right in front of him while he walks somewhere (his ID has all access).
    /// </summary>
    private void OpenDoorsAhead(Entity<GilbertAIComponent> ent, TransformComponent xform)
    {
        if (!TryComp<NPCSteeringComponent>(ent, out var steering))
            return;

        var pos = _transform.GetWorldPosition(xform);
        // v9: the way he is actually walking (next path node), not the straight line to the final destination:
        // round corners the door ahead is often "behind" that line.
        var next = steering.CurrentPath.TryPeek(out var node) ? node.Coordinates : steering.Coordinates;
        var dest = _transform.ToMapCoordinates(next).Position;
        var dir = dest - pos;

        foreach (var door in _lookup.GetEntitiesInRange<DoorComponent>(xform.Coordinates, ent.Comp.DoorOpenRange))
        {
            if (door.Comp.State != DoorState.Closed)
                continue;

            var toDoor = _transform.GetWorldPosition(door) - pos;
            if (dir.LengthSquared() > 0.01f && toDoor.LengthSquared() > 0.04f
                && System.Numerics.Vector2.Dot(dir, toDoor) <= 0f)
                continue;

            _door.TryOpen(door, door.Comp, ent, quiet: true);
        }
    }

    /// <summary>
    /// Stuck right at a closed airlock he can't open (bolted, welded, unpowered): C4 on it.
    /// </summary>
    private bool TryBreachStuckDoor(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityCoordinates dest, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now < comp.NextC4 || comp.Task != GilbertTask.None)
            return false;

        foreach (var door in _lookup.GetEntitiesInRange<DoorComponent>(xform.Coordinates, comp.DoorOpenRange))
        {
            if (door.Comp.State == DoorState.Open || _door.CanOpen(door, door.Comp, ent))
                continue;

            if (!TryFindStored(ent, IsC4, out var c4))
                return false;

            comp.NextC4 = now + comp.C4Cooldown;
            comp.BreachTarget = dest;
            StartTask(ent, GilbertTask.PlantC4, c4, door, TimeSpan.FromSeconds(30));
            return true;
        }

        return false;
    }

    #endregion

    #region Watchdogs

    /// <summary>
    /// Makes sure he never stands around for long without a reason: stuck walks, unreachable targets,
    /// stuck tasks and stalled do-afters all get resolved into a new action.
    /// </summary>
    private void UpdateWatchdog(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        var pos = _transform.GetWorldPosition(xform);
        TryComp<NPCSteeringComponent>(ent, out var steering);
        var sinceLast = comp.LastWatchdog == TimeSpan.Zero ? TimeSpan.Zero : now - comp.LastWatchdog;
        comp.LastWatchdog = now;

        // v9 (t258u/t259u): prying an airlock open (5 s) or vaulting a table: waiting for that do-after is not
        // being stuck, and it doesn't eat into the walk's timeout either.
        if (IsBusyWithObstacle(ent))
        {
            comp.MoveStarted += sinceLast;
            comp.LastProgress = now;
            comp.ProgressPosition = pos;
            return;
        }

        // v8: the reload cover spot turned out unreachable: just reload where he stands.
        if (steering?.Status == SteeringStatus.NoPath && comp.Task == GilbertTask.Reload)
        {
            comp.CoverPoint = null;
            _steering.Unregister(ent);
            comp.LastProgress = now;
            return;
        }

        // Unreachable destination in a state that doesn't handle it itself.
        if (steering?.Status == SteeringStatus.NoPath && comp.State is GilbertAIState.Idle or GilbertAIState.Combat or GilbertAIState.Surrender)
        {
            // v9: standing in an airlock's tile the pathfinder can briefly find no path (the door node);
            // replan a few times instead of blacklisting a perfectly reachable target.
            if (comp.DoorwayRepaths < 3 && InDoorway(xform))
            {
                comp.DoorwayRepaths++;
                steering.Status = SteeringStatus.Moving;
                steering.FailedPathCount = 0;
                comp.LastProgress = now;
                return;
            }

            OnStalled(ent, xform, now);
            return;
        }

        // A walk that never arrives (circling, blocked by a crowd...).
        if (steering != null && comp.State is GilbertAIState.Idle or GilbertAIState.Investigate or GilbertAIState.Search
            && comp.Task == GilbertTask.None && now - comp.MoveStarted > comp.MoveTimeout)
        {
            OnStalled(ent, xform, now);
            return;
        }

        if ((pos - comp.ProgressPosition).LengthSquared() > 0.3f * 0.3f)
        {
            comp.ProgressPosition = pos;
            comp.LastProgress = now;
            return;
        }

        if (HasStandingReason(ent, steering, now))
        {
            comp.LastProgress = now;
            return;
        }

        var limit = comp.Task != GilbertTask.None ? comp.TaskStallTimeout : comp.StallTimeout;
        if (now - comp.LastProgress < limit)
            return;

        OnStalled(ent, xform, now);
    }

    private bool HasStandingReason(Entity<GilbertAIComponent> ent, NPCSteeringComponent? steering, TimeSpan now)
    {
        var comp = ent.Comp;

        // In the middle of a do-after (cuffing, healing, sticking C4, stripping...).
        if (HasActiveDoAfter(ent))
            return true;

        // Steering is running a door / climb do-after of its own.
        if (steering?.DoAfterId != null)
            return true;

        // Searching someone next to him (between the frisk and the strip do-afters).
        if (comp.Task == GilbertTask.Loot && comp.TaskStep > 0)
            return true;

        switch (comp.State)
        {
            case GilbertAIState.Expel:
                // Standing at the punch spot watching the loiterer is the whole point; the state has its own timeouts.
                return true;

            case GilbertAIState.Combat:
            case GilbertAIState.Surrender:
                // Shooting from cover / covering someone he sees is a reason to stand.
                return now - comp.LastSeenTarget < TimeSpan.FromSeconds(1);

            case GilbertAIState.Investigate:
                // Looking around at the spot.
                return comp.InvestigateArrived;

            case GilbertAIState.Search:
                return comp.SearchLookUntil > now;

            case GilbertAIState.Heal:
                // Healing itself is a task with a deadline; only count a stall when no task runs.
                return comp.Task == GilbertTask.HealItem && now < comp.TaskDeadline;
        }

        // C4 planted: waiting at a safe distance for it to go off.
        return comp.Task == GilbertTask.PlantC4 && comp.TaskStep == 2;
    }

    private void OnStalled(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        comp.StallCount++;
        comp.LastProgress = now;
        comp.ProgressPosition = _transform.GetWorldPosition(xform);

        if (comp.Task != GilbertTask.None)
        {
            var wasHealing = comp.State == GilbertAIState.Heal;
            AbortTask(ent);
            if (wasHealing)
            {
                comp.NextHealAttempt = now + TimeSpan.FromSeconds(30);
                FinishHealing(ent);
            }
            return;
        }

        var dest = comp.MoveTarget;
        TryComp<NPCSteeringComponent>(ent, out var steering);
        if (steering != null)
            dest = steering.Coordinates;
        _steering.Unregister(ent);
        comp.MoveTarget = null;

        // Hunting (or chasing gunfire) and the way is shut by a wall / an airlock he can't open: blow it.
        var hostile = comp.State is GilbertAIState.Combat or GilbertAIState.Search
                      || comp.State == GilbertAIState.Investigate
                      && comp.LastNoise is GilbertNoise.Gunshot or GilbertNoise.Explosion or GilbertNoise.Scream;
        if (hostile && dest is { } blocked)
        {
            if (TryBreachStuckDoor(ent, xform, blocked, now) || TryStartBreach(ent, xform, blocked, now))
                return;
        }

        switch (comp.State)
        {
            case GilbertAIState.Idle:
                comp.NextWander = now;
                PickIdleDestination(ent, xform, now, failed: dest);
                break;

            case GilbertAIState.Investigate:
                ArriveInvestigate(ent, now);
                break;

            case GilbertAIState.Search:
                comp.ArrivedAtLastKnown = true;
                comp.NextWander = now;
                break;

            case GilbertAIState.Combat:
            case GilbertAIState.Surrender:
                if (now - comp.LastSeenTarget >= TimeSpan.FromSeconds(1))
                    EnterSearch(ent);
                break;

            case GilbertAIState.Heal:
                comp.NextHealAttempt = now + TimeSpan.FromSeconds(30);
                FinishHealing(ent);
                break;
        }
    }

    #endregion

    /// <summary>v9: is he standing in (or right at) a doorway?</summary>
    private bool InDoorway(TransformComponent xform)
    {
        return _lookup.GetEntitiesInRange<DoorComponent>(xform.Coordinates, 1.2f).Count > 0;
    }
}
