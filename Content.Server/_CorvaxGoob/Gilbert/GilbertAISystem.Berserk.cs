using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Climbing.Components;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Content.Shared.Prying.Components;
using Content.Shared._Shitmed.DoAfter;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Second phase «Берсерк» (v6): no lines, no gear, no tricks. He goes for the nearest living thing (people, pets,
/// anything with a mob state) and beats it to death with his fists, smashing through whatever stands between them.
/// Frozen during the transition and the dying sequence.
/// </summary>
public sealed partial class GilbertAISystem
{
    /// <summary>How often he looks around for a closer victim (v8: four times a second).</summary>
    private static readonly TimeSpan BerserkRepick = TimeSpan.FromSeconds(0.25);

    /// <summary>Not getting any closer for this long: punch whatever is in the way.</summary>
    private static readonly TimeSpan BerserkStuckTime = TimeSpan.FromSeconds(0.8);

    /// <summary>v8: no progress for this long: throw the path away and ask for a fresh one.</summary>
    private static readonly TimeSpan BerserkRepathTime = TimeSpan.FromSeconds(0.3);

    /// <summary>v8: an obstacle he can't get to within this long is dropped (another one is picked when stuck).</summary>
    private static readonly TimeSpan BerserkObstacleApproachTime = TimeSpan.FromSeconds(3);

    /// <summary>v8: how long he searches around the spot where he lost (or heard) his prey.</summary>
    private static readonly TimeSpan BerserkSearchTime = TimeSpan.FromSeconds(8);

    private void InitializeBerserk()
    {
        SubscribeLocalEvent<GilbertAIComponent, GilbertBerserkPhaseChangedEvent>(OnBerserkPhaseChanged);
        SubscribeLocalEvent<GilbertAIComponent, GetPryTimeModifierEvent>(OnGetPryTime);
        SubscribeLocalEvent<GilbertAIComponent, GetDoAfterDelayMultiplierEvent>(OnGetDoAfterDelay);
    }

    /// <summary>
    /// v9 (t258u): how long he takes to force an airlock. Phase 1: with his bare hands, a flat
    /// <see cref="GilbertAIComponent.HandPryTime"/> (5 s) whatever the airlock (powered or not); bolts still stop
    /// him (the door cancels the pry). The rage: instantly, like a crowbar, bolts or not.
    /// </summary>
    private void OnGetPryTime(Entity<GilbertAIComponent> ent, ref GetPryTimeModifierEvent args)
    {
        if (args.User != ent.Owner)
            return;

        if (InBerserk(ent))
        {
            args.PryTimeModifier = 0f;
            return;
        }

        var tool = CompOrNull<PryingComponent>(ent)?.SpeedModifier ?? 1f;
        // His (human) hands speed every do-after up (0.9 × 0.9); the pry itself must still take the full 5 s.
        var hands = new GetDoAfterDelayMultiplierEvent();
        RaiseLocalEvent(ent, hands);
        var mult = hands.Multiplier > 0.01f ? hands.Multiplier : 1f;
        args.BaseTime = ent.Comp.HandPryTime * tool / mult;
        args.PryTimeModifier = 1f;
    }

    /// <summary>v9 (t259u): in the rage he vaults over tables (his only do-afters then) about three times faster.</summary>
    private void OnGetDoAfterDelay(Entity<GilbertAIComponent> ent, ref GetDoAfterDelayMultiplierEvent args)
    {
        if (InBerserk(ent))
            args.Multiplier *= ent.Comp.BerserkDoAfterMultiplier;
    }

    /// <summary>v9 (t258u): the rage rips airlocks open through bolts (PryingComponent.Force); phase 1 doesn't.</summary>
    private void UpdateBerserkPrying(EntityUid uid)
    {
        var prying = EnsureComp<PryingComponent>(uid);
        var berserk = InBerserk(uid);
        prying.Force = berserk;
        prying.PryPowered = true;
        prying.Enabled = true;
        Dirty(uid, prying);
    }

    /// <summary>
    /// v9 (t258u/t259u): a pry / climb do-after started by his steering (or him standing on a table mid-vault):
    /// waiting for it is not being stuck.
    /// </summary>
    private bool IsBusyWithObstacle(EntityUid uid)
    {
        if (TryComp<ClimbingComponent>(uid, out var climbing) && climbing.NextTransition != null)
            return true;

        return TryComp<NPCSteeringComponent>(uid, out var steering) && steering.DoAfterId is { } id
               && _doAfter.GetStatus(id) == Content.Shared.DoAfter.DoAfterStatus.Running;
    }

    /// <summary>v9 (t258u): an airlock right in front of him in the rage is ripped open, not beaten down.</summary>
    private bool TryRipDoor(EntityUid uid, EntityUid door)
    {
        if (!TryComp<DoorComponent>(door, out var doorComp) || doorComp.State is DoorState.Open or DoorState.Opening)
            return false;

        // Already at it (the pry do-after, 0 s in the rage, finishes next tick).
        if (HasActiveDoAfter(uid))
            return true;

        if (!HasComp<PryingComponent>(uid))
            UpdateBerserkPrying(uid);

        return _prying.TryPry(door, uid, out var id, uid) && id != null;
    }

    public bool InBerserk(EntityUid uid)
    {
        return TryComp<GilbertBerserkComponent>(uid, out var berserk) && berserk.Phase != GilbertBerserkPhase.None;
    }

    private void OnBerserkPhaseChanged(Entity<GilbertAIComponent> ent, ref GilbertBerserkPhaseChangedEvent args)
    {
        // Whatever he was doing (healing, looting, aiming, talking someone down) is over.
        if (ent.Comp.State == GilbertAIState.Heal)
            AbortHealing(ent);
        AbortTask(ent);
        StopCombatComponents(ent);
        _steering.Unregister(ent);
        ReleaseFacing(ent);
        ent.Comp.Target = null;
        ent.Comp.BerserkTarget = null;
        ent.Comp.BerserkObstacle = null;
        ent.Comp.NextBerserkPick = TimeSpan.Zero;
        UpdateBerserkPrying(ent);
        ent.Comp.BerserkLastProgress = _timing.CurTime;
        ent.Comp.State = GilbertAIState.Combat;
        _speed.RefreshMovementSpeedModifiers(ent);
    }

    /// <summary>
    /// Runs instead of the normal think while he is berserk. Returns false if he isn't.
    /// </summary>
    private bool UpdateBerserk(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        if (!TryComp<GilbertBerserkComponent>(ent, out var berserk) || berserk.Phase == GilbertBerserkPhase.None)
            return false;

        if (berserk.Phase != GilbertBerserkPhase.Rage)
        {
            _steering.Unregister(ent);
            return true;
        }

        BerserkThink(ent, (ent, berserk), xform, now);
        return true;
    }

    private void BerserkThink(Entity<GilbertAIComponent> ent, Entity<GilbertBerserkComponent> berserk,
        TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        // Gone out of sight (or no longer a victim): drop them at once and hunt from where they were last seen,
        // rather than tracking them through walls until the next re-pick.
        if (comp.BerserkTarget is { } current
            && (!IsBerserkVictim(ent, current) || !BerserkCanSee(ent, xform, current, berserk.Comp)))
        {
            comp.BerserkTarget = null;
            comp.NextBerserkPick = TimeSpan.Zero;
            // v9: still alive, just out of sight: guess where it ran.
            if (IsBerserkVictim(ent, current) && comp.BerserkLastKnown is { } lost)
                BuildPursuitPlan(ent, lost, comp.LastSeenVelocity);
        }

        // v8: he only goes for what he can see (360°, 10 tiles, line of sight, windows see-through); re-checked
        // twice a second so the nearest reachable one is always the one he's after.
        if (comp.BerserkTarget == null || now >= comp.NextBerserkPick)
        {
            comp.NextBerserkPick = now + BerserkRepick;
            var picked = FindBerserkVictim(ent, xform, berserk.Comp);
            // v9: only a genuinely new victim restarts the stuck clock; the same one flickering in and out of
            // sight (round a window frame) used to keep him "making progress" forever in front of the glass.
            if (picked != null && picked != comp.BerserkLastPicked)
            {
                comp.BerserkLastProgress = now;
                comp.BerserkLastPicked = picked;
            }
            comp.BerserkTarget = picked;
        }

        var myPos = _transform.GetWorldPosition(xform);
        var reach = TryComp<MeleeWeaponComponent>(ent, out var fist) ? fist.Range : 1.5f;

        if (comp.BerserkTarget is not { } target)
        {
            BerserkHunt(ent, xform, myPos, reach, now);
            return;
        }

        // Seen: remember where (for when he loses sight of them).
        comp.BerserkLastKnown = Transform(target).Coordinates;
        comp.BerserkLastSeen = now;
        comp.BerserkSearchUntil = TimeSpan.Zero;
        comp.BerserkRoamGoal = null;
        RecordEnemyMotion(comp, target);

        var distance = (_transform.GetWorldPosition(target) - myPos).Length();
        if (distance <= reach + 0.1f && _interaction.InRangeUnobstructed(ent.Owner, target, reach + 0.1f))
        {
            _steering.Unregister(ent);
            comp.BerserkObstacle = null;
            FaceTowards(ent, target);
            Punch(ent, target);
            comp.BerserkLastProgress = now;
            comp.BerserkLastPos = myPos;
            return;
        }

        // v9: right there but something (a window, a door, a grille) is in between: break it now instead of
        // shuffling about in front of it.
        if (comp.BerserkObstacle == null && distance <= reach + 1.2f
            && !_interaction.InRangeUnobstructed(ent.Owner, target, distance + 0.1f)
            && TryPickObstacle(ent, xform, Transform(target).Coordinates, myPos, _transform.GetWorldPosition(target),
                distance, reach, out var between))
        {
            comp.BerserkObstacle = between;
            comp.BerserkObstacleSince = now;
            SmashObstacle(ent, xform, between, myPos, reach, now);
            return;
        }

        // Follow the victim itself so a moving target doesn't restart the path every tick.
        BerserkApproach(ent, xform, new EntityCoordinates(target, Vector2.Zero), myPos, reach, now);
    }

    /// <summary>
    /// v8/v9: nobody in sight. Go to where he last saw / heard someone, then along the guessed escape route and
    /// through the rooms around it, then roam towards wherever living things are. Never stands still.
    /// </summary>
    private void BerserkHunt(Entity<GilbertAIComponent> ent, TransformComponent xform, Vector2 myPos, float reach,
        TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.BerserkLastKnown is { } spot && spot.IsValid(EntityManager))
        {
            var spotMap = _transform.ToMapCoordinates(spot);
            var toSpot = spotMap.MapId == xform.MapID ? (spotMap.Position - myPos).Length() : float.MaxValue;
            if (toSpot == float.MaxValue)
            {
                comp.BerserkLastKnown = null;
            }
            else if (comp.BerserkSearchUntil == TimeSpan.Zero && toSpot > 1.2f
                     && now - comp.BerserkLastSeen < comp.BerserkRoamTimeout)
            {
                BerserkApproach(ent, xform, spot, myPos, reach, now);
                return;
            }
            else
            {
                if (comp.BerserkSearchUntil == TimeSpan.Zero)
                {
                    comp.BerserkSearchUntil = now + BerserkSearchTime;
                    comp.NextBerserkSearchStep = now;
                    comp.SearchRoomsChecked = 0;
                    comp.BerserkRoamGoal = null;
                }

                if (now < comp.BerserkSearchUntil)
                {
                    // Keep breaking through whatever he started on.
                    if (comp.BerserkObstacle is { } wall && !TerminatingOrDeleted(wall)
                        && SmashObstacle(ent, xform, wall, myPos, reach, now))
                        return;

                    // v9: the guessed route first, then two rooms around it; each step the moment the last one is
                    // reached (or he gets stuck on it).
                    var step = comp.BerserkRoamGoal;
                    var next = step is not { } cur || !cur.IsValid(EntityManager)
                               || _transform.InRange(xform.Coordinates, cur, 1.5f) || now >= comp.NextBerserkSearchStep
                               || CheckOscillation(ent, xform, now, comp.BerserkOscillationWindow, false);
                    if (next)
                    {
                        if (step is { } old && !_transform.InRange(xform.Coordinates, old, 1.5f))
                            BlacklistSpot(comp, old);

                        step = null;
                        if (TryPopPursuit(comp, out var predicted))
                            step = predicted;
                        else if (comp.SearchRoomsChecked++ < 2
                                 && TryPickSearchPoint(ent, xform, comp.SearchCenter ?? spot, 4f, 10f, out var room, comp.SearchBias))
                            step = room;

                        if (step == null)
                        {
                            comp.BerserkSearchUntil = now;
                        }
                        else
                        {
                            comp.BerserkRoamGoal = step;
                            comp.BerserkRoamStarted = now;
                            comp.NextBerserkSearchStep = now + TimeSpan.FromSeconds(6);
                            comp.BerserkSearchUntil = MaxTime(comp.BerserkSearchUntil, now + TimeSpan.FromSeconds(6));
                        }
                    }

                    if (comp.BerserkRoamGoal is { } go && now < comp.BerserkSearchUntil)
                    {
                        BerserkApproach(ent, xform, go, myPos, reach, now);
                        return;
                    }
                }

                comp.BerserkLastKnown = null;
                comp.BerserkSearchUntil = TimeSpan.Zero;
                comp.BerserkRoamGoal = null;
                comp.PursuitPlan.Clear();
                comp.SearchCenter = null;
            }
        }

        // v9: nothing to go on: roam (memory + scent), smashing through anything.
        BerserkRoam(ent, xform, myPos, reach, now);
    }

    private static TimeSpan MaxTime(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// v8: walk to <paramref name="goal"/> through anything: fresh paths whenever he stops gaining ground, and when
    /// he is stuck the first breakable thing in the way (on the line to the goal, or right ahead of him) is
    /// beaten until it breaks. With nothing to break he sidesteps out of the corner.
    /// </summary>
    private void BerserkApproach(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityCoordinates goal,
        Vector2 myPos, float reach, TimeSpan now)
    {
        var comp = ent.Comp;

        // Breaking through something: keep at it until it's gone.
        if (comp.BerserkObstacle is { } wall)
        {
            if (!TerminatingOrDeleted(wall) && SmashObstacle(ent, xform, wall, myPos, reach, now))
                return;

            comp.BerserkObstacle = null;
            comp.BerserkLastPos = myPos;
            comp.BerserkLastProgress = now;
            ResetBerserkPath(ent);
        }

        var steering = BerserkSteer(ent, goal);
        steering.Range = MathF.Max(0.6f, reach - 0.4f);
        steering.DirectMove = false;
        steering.RepathRange = 0.75f;
        steering.Flags = BerserkPathFlags;

        // v9 (t258u/t259u): ripping an airlock open / vaulting a table: that is progress, not being stuck.
        if (IsBusyWithObstacle(ent))
        {
            comp.BerserkLastPos = myPos;
            comp.BerserkLastProgress = now;
            return;
        }

        // v9: progress = actually getting closer to the goal, or really going somewhere (2 tiles from where he last
        // made progress, e.g. round a detour). Shuffling back and forth in front of an obstacle is neither.
        var goalNow = _transform.ToMapCoordinates(goal).Position;
        var goalDist = (goalNow - myPos).Length();
        if ((goalNow - comp.BerserkProgressGoal).LengthSquared() > 1f)
        {
            comp.BerserkProgressGoal = goalNow;
            comp.BerserkBestGoalDist = goalDist;
            comp.BerserkLastPos = myPos;
            comp.BerserkLastProgress = now;
            return;
        }

        if (goalDist < comp.BerserkBestGoalDist - 0.3f || (myPos - comp.BerserkLastPos).LengthSquared() > 4f)
        {
            comp.BerserkBestGoalDist = MathF.Min(comp.BerserkBestGoalDist, goalDist);
            comp.BerserkLastPos = myPos;
            comp.BerserkLastProgress = now;
            return;
        }

        var stuckFor = now - comp.BerserkLastProgress;

        // Not gaining ground: a fresh path (the target moved round a corner, a door closed, the old one was bad).
        if (stuckFor >= BerserkRepathTime && now >= comp.NextBerserkRepath)
        {
            comp.NextBerserkRepath = now + BerserkRepathTime;
            ResetBerserkPath(ent);
        }

        if (stuckFor < BerserkStuckTime)
            return;

        // Stuck (or no path at all, e.g. the victim is walled in): smash the first thing between him and the goal,
        // or whatever he is walking into.
        var goalMap = _transform.ToMapCoordinates(goal);
        var goalDistance = (goalMap.Position - myPos).Length();
        if (TryPickObstacle(ent, xform, goal, myPos, goalMap.Position, goalDistance, reach, out var obstacle))
        {
            comp.BerserkObstacle = obstacle;
            comp.BerserkObstacleSince = now;
            SmashObstacle(ent, xform, obstacle, myPos, reach, now);
            return;
        }

        // Nothing to break: stuck on a corner / another body. Step sideways and try again.
        comp.BerserkUnstucks++;
        comp.BerserkLastProgress = now;
        var dir = goalMap.Position - myPos;
        var side = dir.LengthSquared() > 0.01f ? new Vector2(-dir.Y, dir.X) / dir.Length() : new Vector2(1, 0);
        if (_random.NextFloat() < 0.5f)
            side = -side;
        var grid = xform.GridUid ?? xform.ParentUid;
        var step = BerserkSteer(ent, _transform.ToCoordinates(grid, new MapCoordinates(myPos + side * 1.2f, xform.MapID)));
        step.Range = 0.3f;
        step.DirectMove = true;
    }

    private void ResetBerserkPath(EntityUid uid)
    {
        if (!TryComp<NPCSteeringComponent>(uid, out var steering))
            return;

        steering.CurrentPath.Clear();
        steering.FailedPathCount = 0;
        if (steering.Status == SteeringStatus.NoPath)
            steering.Status = SteeringStatus.Moving;
        Comp<GilbertAIComponent>(uid).BerserkRepaths++;
    }

    /// <summary>
    /// Walk up to <paramref name="obstacle"/> in a straight line and punch it until it breaks. False if it can't
    /// be done (other map, or he couldn't get to it for <see cref="BerserkObstacleApproachTime"/>).
    /// </summary>
    private bool SmashObstacle(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityUid obstacle,
        Vector2 myPos, float reach, TimeSpan now)
    {
        var obstacleXform = Transform(obstacle);
        if (obstacleXform.MapID != xform.MapID)
            return false;

        // v9: an airlock that is (being) opened is no obstacle any more.
        if (TryComp<DoorComponent>(obstacle, out var obstacleDoor) && obstacleDoor.State is DoorState.Open or DoorState.Opening)
            return false;

        var obstaclePos = _transform.GetWorldPosition(obstacleXform);
        var toObstacle = obstaclePos - myPos;
        var length = toObstacle.Length();
        // In reach and nothing else in between (a wall corner would eat the punch).
        if (length <= reach + 0.5f && _interaction.InRangeUnobstructed(ent.Owner, obstacle, reach + 0.5f))
        {
            // v9 (t258u): an airlock is ripped open (instantly, bolts or not) instead of beaten down.
            if (TryRipDoor(ent, obstacle))
            {
                ent.Comp.BerserkDoorsRipped++;
                ent.Comp.BerserkObstacleSince = now;
                ent.Comp.BerserkLastProgress = now;
                return false;
            }

            _steering.Unregister(ent);
            FaceTowards(ent, obstacle);
            if (Punch(ent, obstacle))
                ent.Comp.BerserkSmashPunches++;
            ent.Comp.BerserkObstacleSince = now;
            ent.Comp.BerserkLastProgress = now;
            return true;
        }

        if (now - ent.Comp.BerserkObstacleSince > BerserkObstacleApproachTime)
            return false;

        // Nothing in between: walk straight up to it. Something else in the way (he picked the weaker wall a bit
        // further on, round a corner): let the pathfinder take him there.
        // (Already that close but still blocked means the line only grazes past a corner: go round instead.)
        if (length > reach + 0.5f
            && TryFindObstacle(ent, xform, obstacleXform.Coordinates, length + 0.5f, out var first) && first == obstacle)
        {
            var approach = obstaclePos - toObstacle / length * MathF.Max(0.6f, reach - 0.3f);
            var grid = xform.GridUid ?? xform.ParentUid;
            var walk = BerserkSteer(ent, _transform.ToCoordinates(grid, new MapCoordinates(approach, xform.MapID)));
            walk.Range = 0.3f;
            walk.DirectMove = true;
        }
        else
        {
            // The obstacle's own tile is blocked, so path to the free tile next to it that is closest to him.
            var grid = xform.GridUid ?? xform.ParentUid;
            var spot = obstaclePos;
            var bestSpot = float.MaxValue;
            foreach (var offset in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
            {
                var candidate = obstaclePos + offset;
                if (TileBlocked(xform.MapID, candidate))
                    continue;
                var d = (candidate - myPos).LengthSquared();
                if (d < bestSpot)
                {
                    bestSpot = d;
                    spot = candidate;
                }
            }

            // A clear line to that tile (typically round the corner of a corridor mouth): just walk there, the
            // pathfinder tends to leave him grinding on the corner. Otherwise let it find the way.
            var spotCoords = _transform.ToCoordinates(grid, new MapCoordinates(spot, xform.MapID));
            var clear = !TryFindObstacle(ent, xform, spotCoords, (spot - myPos).Length(), out _);
            if (!clear)
            {
                // Round the corner: first square up one tile further out, in line with it, then step in.
                var staging = spot + (spot - obstaclePos);
                var stagingCoords = _transform.ToCoordinates(grid, new MapCoordinates(staging, xform.MapID));
                if ((staging - myPos).Length() > 0.3f && !TileBlocked(xform.MapID, staging)
                    && !TryFindObstacle(ent, xform, stagingCoords, (staging - myPos).Length(), out _))
                {
                    spotCoords = stagingCoords;
                    clear = true;
                }
            }

            var walk = BerserkSteer(ent, spotCoords);
            walk.Range = 0.3f;
            walk.DirectMove = clear;
        }

        return true;
    }

    /// <summary>
    /// v8: something hard and anchored (wall, window, door, machine) stands on this spot.
    /// </summary>
    private bool TileBlocked(MapId map, Vector2 pos)
    {
        var mask = (int) (CollisionGroup.Impassable | CollisionGroup.MidImpassable | CollisionGroup.LowImpassable);
        foreach (var other in _lookup.GetEntitiesInRange(new MapCoordinates(pos, map), 0.3f, LookupFlags.Static))
        {
            if (TryComp<PhysicsComponent>(other, out var body) && body.Hard && (body.CollisionLayer & mask) != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// v8: what to break to get at <paramref name="goalPos"/>: the first thing on the straight line, the thing he is
    /// walking into, or any other breakable blocker a few tiles ahead towards the goal, whichever is quickest to get
    /// through (what is left of its health, a little more for every tile it is away). So he goes for the door or the
    /// plain wall next to him rather than punching a reinforced wall for a minute.
    /// </summary>
    private bool TryPickObstacle(EntityUid uid, TransformComponent xform, EntityCoordinates goal, Vector2 myPos,
        Vector2 goalPos, float goalDistance, float reach, out EntityUid obstacle)
    {
        obstacle = default;
        var candidates = new HashSet<EntityUid>();
        if (TryFindObstacle(uid, xform, goal, goalDistance, out var onLine))
            candidates.Add(onLine);
        if (TryFindObstacleAhead(uid, xform, myPos, goalPos, reach, out var ahead))
            candidates.Add(ahead);
        if (candidates.Count == 0)
            return false;

        var toGoal = goalPos - myPos;
        if (toGoal.LengthSquared() > 0.01f)
        {
            var dir = Vector2.Normalize(toGoal);
            var mask = (int) (CollisionGroup.Impassable | CollisionGroup.MidImpassable | CollisionGroup.LowImpassable);
            foreach (var other in _lookup.GetEntitiesInRange(xform.Coordinates, MathF.Min(goalDistance, 3.5f), LookupFlags.Static | LookupFlags.Approximate))
            {
                if (other == uid || HasComp<MobStateComponent>(other) || !HasComp<DamageableComponent>(other)
                    || !TryComp<PhysicsComponent>(other, out var body) || !body.Hard || (body.CollisionLayer & mask) == 0)
                    continue;

                var otherPos = _transform.GetWorldPosition(other);
                var delta = otherPos - myPos;
                var dist = delta.Length();
                // Ahead of him, and actually between him and the goal (closer to it than he is).
                if (dist < 0.01f || Vector2.Dot(dir, delta / dist) < 0.5f
                    || (goalPos - otherPos).Length() >= goalDistance - 0.3f)
                    continue;

                candidates.Add(other);
            }
        }

        var best = float.MaxValue;
        foreach (var candidate in candidates)
        {
            // Punches needed (his fist against its armour), plus about a punch for every tile of walking.
            var dist = (_transform.GetWorldPosition(candidate) - myPos).Length();
            var score = PunchesToBreak(uid, candidate) + dist;
            if (score < best)
            {
                best = score;
                obstacle = candidate;
            }
        }

        return best < float.MaxValue;
    }

    /// <summary>
    /// How many of his punches <paramref name="uid"/> still takes to break: what is left of its health over what
    /// his fist does to it after its armour (a reinforced wall shrugs off most of a punch). Huge if it can't break.
    /// </summary>
    private float PunchesToBreak(EntityUid gilbert, EntityUid uid)
    {
        var destroyAt = _destructible.DestroyedAt(uid);
        if (destroyAt == FixedPoint2.MaxValue || destroyAt <= FixedPoint2.Zero
            || !TryComp<DamageableComponent>(uid, out var damageable))
            return 10000f;

        var remaining = MathF.Max(1f, (destroyAt - damageable.TotalDamage).Float());
        var perPunch = 10f;
        if (TryComp<MeleeWeaponComponent>(gilbert, out var fist))
        {
            var damage = fist.Damage;
            if (damageable.DamageModifierSetId is { } setId
                && _proto.TryIndex<Content.Shared.Damage.Prototypes.DamageModifierSetPrototype>(setId, out var set))
                damage = DamageSpecifier.ApplyModifierSet(damage, set);
            perPunch = damage.GetTotal().Float();
        }

        return remaining / MathF.Max(0.5f, perPunch);
    }

    /// <summary>
    /// v8: the breakable solid thing right in front of him (towards <paramref name="goal"/>, or the way he is
    /// walking): what he bumps into on a path round a corner that the straight line to the goal misses.
    /// </summary>
    private bool TryFindObstacleAhead(EntityUid uid, TransformComponent xform, Vector2 myPos, Vector2 goal,
        float reach, out EntityUid obstacle)
    {
        obstacle = default;
        var dirs = new List<Vector2>(2);
        var toGoal = goal - myPos;
        if (toGoal.LengthSquared() > 0.01f)
            dirs.Add(Vector2.Normalize(toGoal));
        if (TryComp<NPCSteeringComponent>(uid, out var steering) && steering.CurrentPath.Count > 0)
        {
            var next = _transform.ToMapCoordinates(steering.CurrentPath.Peek().Coordinates).Position - myPos;
            if (next.LengthSquared() > 0.01f)
                dirs.Add(Vector2.Normalize(next));
        }

        if (dirs.Count == 0)
            return false;

        var best = float.MaxValue;
        var mask = (int) (CollisionGroup.Impassable | CollisionGroup.MidImpassable | CollisionGroup.LowImpassable);
        foreach (var other in _lookup.GetEntitiesInRange(xform.Coordinates, reach + 0.7f, LookupFlags.Static | LookupFlags.Dynamic | LookupFlags.Approximate))
        {
            if (other == uid || HasComp<MobStateComponent>(other) || !HasComp<DamageableComponent>(other)
                || !TryComp<PhysicsComponent>(other, out var body) || !body.Hard || (body.CollisionLayer & mask) == 0)
                continue;

            var delta = _transform.GetWorldPosition(other) - myPos;
            var dist = delta.Length();
            if (dist < 0.01f)
                continue;

            var facing = 0f;
            foreach (var dir in dirs)
                facing = MathF.Max(facing, Vector2.Dot(dir, delta / dist));

            if (facing < 0.3f)
                continue;

            var score = dist * (2f - facing);
            if (score < best)
            {
                best = score;
                obstacle = other;
            }
        }

        return best < float.MaxValue;
    }

    /// <summary>
    /// Register a walk; a "no path" left over from the previous destination (e.g. the walled-in victim) must not
    /// keep him standing still on the new one.
    /// </summary>
    private NPCSteeringComponent BerserkSteer(EntityUid uid, EntityCoordinates coordinates)
    {
        return SteerTo(uid, coordinates, exact: true);
    }

    private bool IsBerserkVictim(EntityUid uid, EntityUid target)
    {
        if (target == uid || TerminatingOrDeleted(target) || !TryComp<MobStateComponent>(target, out var state)
            || _mobState.IsDead(target, state))
            return false;

        // Someone inside a locker / mech / bag can't be punched; he goes for someone he can reach.
        if (_container.IsEntityInContainer(target))
            return false;

        var a = Transform(uid);
        var b = Transform(target);
        return a.MapID == b.MapID;
    }

    /// <summary>
    /// v8: the nearest living thing he can see (people, pets, anything with a mob state): 360° within
    /// <see cref="GilbertBerserkComponent.VisionRange"/>, line of sight with windows see-through. One he can walk
    /// straight up to beats one behind a window / grille that has to be smashed first.
    /// </summary>
    private EntityUid? FindBerserkVictim(EntityUid uid, TransformComponent xform, GilbertBerserkComponent berserk)
    {
        var coords = _transform.GetMapCoordinates(xform);
        EntityUid? best = null;
        var bestScore = float.MaxValue;
        foreach (var mob in _lookup.GetEntitiesInRange<MobStateComponent>(coords, MathF.Min(berserk.VisionRange, berserk.HuntRange)))
        {
            if (!IsBerserkVictim(uid, mob) || !BerserkCanSee(uid, xform, mob, berserk))
                continue;

            var dist = (_transform.GetWorldPosition(mob) - coords.Position).Length();
            var score = dist;
            if (TryFindObstacle(uid, xform, Transform(mob).Coordinates, dist, out _))
                score += 4f;

            if (score < bestScore)
            {
                bestScore = score;
                best = mob;
            }
        }

        return best;
    }

    /// <summary>v8: berserk vision: 360°, <see cref="GilbertBerserkComponent.VisionRange"/>, needs line of sight.</summary>
    public bool BerserkCanSee(EntityUid uid, TransformComponent xform, EntityUid target, GilbertBerserkComponent berserk)
    {
        var targetXform = Transform(target);
        if (targetXform.MapID != xform.MapID)
            return false;

        var dist = (_transform.GetWorldPosition(targetXform) - _transform.GetWorldPosition(xform)).Length();
        if (dist > berserk.VisionRange)
            return false;

        return _interaction.InRangeUnobstructed(uid, target, dist + 0.5f, CollisionGroup.Opaque, IsSeeThrough);
    }

    /// <summary>
    /// v8: whoever hurts him while he's raging becomes the target right away if nobody closer is in sight
    /// (and he goes for where the hit came from if he can't see them).
    /// </summary>
    private void BerserkRetaliate(Entity<GilbertAIComponent> ent, EntityUid? origin)
    {
        if (origin is not { } attacker || attacker == ent.Owner || !IsBerserkVictim(ent, attacker)
            || !TryComp<GilbertBerserkComponent>(ent, out var berserk) || berserk.Phase != GilbertBerserkPhase.Rage)
            return;

        var xform = Transform(ent);
        if (ent.Comp.BerserkTarget is { } current && current != attacker)
        {
            var mine = _transform.GetWorldPosition(xform);
            if ((_transform.GetWorldPosition(current) - mine).Length() <= (_transform.GetWorldPosition(attacker) - mine).Length())
                return;
        }

        if (BerserkCanSee(ent, xform, attacker, berserk))
        {
            ent.Comp.BerserkTarget = attacker;
            ent.Comp.NextBerserkPick = _timing.CurTime + BerserkRepick;
            ent.Comp.BerserkLastProgress = _timing.CurTime;
            return;
        }

        if (ent.Comp.BerserkTarget == null)
        {
            ent.Comp.BerserkLastKnown = Transform(attacker).Coordinates;
            ent.Comp.BerserkLastSeen = _timing.CurTime; // v9
            ent.Comp.BerserkRoamGoal = null;
            ent.Comp.BerserkSearchUntil = TimeSpan.Zero;
            ent.Comp.BerserkLastProgress = _timing.CurTime;
        }
    }

    /// <summary>
    /// v8: something he heard while raging (speech, a scream, a shot, an explosion within
    /// <see cref="GilbertBerserkComponent.HearingRange"/>): with nobody in sight he goes to the spot.
    /// </summary>
    private void BerserkHear(Entity<GilbertAIComponent> ent, EntityCoordinates coords)
    {
        if (!TryComp<GilbertBerserkComponent>(ent, out var berserk) || berserk.Phase != GilbertBerserkPhase.Rage)
            return;

        var mine = _transform.GetMapCoordinates(ent.Owner);
        var spot = _transform.ToMapCoordinates(coords);
        if (spot.MapId != mine.MapId || (spot.Position - mine.Position).Length() > berserk.HearingRange)
            return;

        // Someone in sight comes first.
        if (ent.Comp.BerserkTarget != null)
            return;

        ent.Comp.BerserkLastKnown = coords;
        ent.Comp.BerserkSearchUntil = TimeSpan.Zero;
        ent.Comp.BerserkLastProgress = _timing.CurTime;
        // v9: a sound has no heading: search the rooms around it.
        ent.Comp.BerserkLastSeen = _timing.CurTime;
        ent.Comp.BerserkRoamGoal = null;
        ent.Comp.PursuitPlan.Clear();
        ent.Comp.SearchCenter = coords;
        ent.Comp.SearchBias = Vector2.Zero;
    }

    /// <summary>
    /// The first breakable solid thing on the straight line to <paramref name="target"/> within
    /// <paramref name="maxDistance"/>: wall, door, window, grille, table, crate, machine.
    /// </summary>
    public bool TryFindObstacle(EntityUid uid, TransformComponent xform, EntityCoordinates target, float maxDistance,
        out EntityUid obstacle)
    {
        obstacle = default;
        var from = _transform.GetMapCoordinates(xform);
        var to = _transform.ToMapCoordinates(target);
        if (from.MapId != to.MapId)
            return false;

        var delta = to.Position - from.Position;
        var length = delta.Length();
        if (length < 0.3f)
            return false;

        var mask = (int) (CollisionGroup.Impassable | CollisionGroup.MidImpassable | CollisionGroup.LowImpassable);
        var ray = new CollisionRay(from.Position, delta / length, mask);
        foreach (var hit in _physics.IntersectRay(from.MapId, ray, MathF.Min(length, maxDistance), uid))
        {
            if (HasComp<MobStateComponent>(hit.HitEntity) || !HasComp<DamageableComponent>(hit.HitEntity))
                continue;

            obstacle = hit.HitEntity;
            return true;
        }

        return false;
    }
}
