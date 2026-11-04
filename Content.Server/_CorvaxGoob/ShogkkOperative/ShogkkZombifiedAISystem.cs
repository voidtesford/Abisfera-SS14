using System.Numerics;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server.Atmos.Components;
using Content.Server.Chat.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared._CorvaxGoob.ShogkkOperative;
using Content.Shared.Chat;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

/// <summary>
/// AI of the ordinary SHOGKK zombified. Kept cheap: a think every half second with a random phase, one lookup for
/// vision, a horde check every couple of seconds; walking, door handling and melee are the engine's NPC steering and
/// NPC melee combat (the operatives' and Gilbert's pieces), nothing else.
/// <list type="bullet">
/// <item>Wander: a random reachable floor spot 8-40 tiles away, a short pause, the next one. Opens unlocked doors
/// (no access: he is no longer staff), climbs tables.</item>
/// <item>Victims: alive humanoids that are neither operatives nor zombified (so players, crew and Gilbert). He goes
/// for the one he sees, hits him with fists or the tool in his hand, and groans: every zombified within
/// <see cref="ShogkkZombifiedAIComponent.GroanRange"/> joins the hunt. A lost victim is looked for where last seen.</item>
/// <item>Hordes: zombified who see each other within <see cref="ShogkkZombifiedAIComponent.HordeRange"/> merge into
/// one loose crowd trailing the leader, each at his own offset.</item>
/// </list>
/// Never picks up anything, guns included, and has no radio.
/// </summary>
public sealed class ShogkkZombifiedAISystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly GilbertAISystem _gilbert = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly SharedDoorSystem _door = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    /// <summary>Unlocked doors and tables; no access (he is nobody's staff any more), no prying.</summary>
    public const PathFlags ZombifiedPathFlags = PathFlags.Interact | PathFlags.Climbing;

    private const int BlacklistCell = 4;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShogkkZombifiedAIComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ShogkkZombifiedAIComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<ShogkkZombifiedAIComponent, DamageChangedEvent>(OnDamageChanged);
    }

    private void OnMapInit(Entity<ShogkkZombifiedAIComponent> ent, ref MapInitEvent args)
    {
        var now = _timing.CurTime;
        var comp = ent.Comp;
        comp.NextThink = now + TimeSpan.FromSeconds(0.3 + _random.NextDouble() * comp.ThinkInterval.TotalSeconds);
        // The first look round comes quickly, before the newly spawned wander apart.
        comp.NextHordeCheck = now + TimeSpan.FromSeconds(_random.NextDouble() * 0.5);
        comp.FollowOffset = _random.NextAngle().ToVec() * _random.NextFloat(1.2f, 2.8f);
        EnsureComp<ActiveNPCComponent>(ent);
    }

    private void OnMobStateChanged(Entity<ShogkkZombifiedAIComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
        {
            EnsureComp<ActiveNPCComponent>(ent);
            return;
        }

        RemComp<NPCMeleeCombatComponent>(ent);
        _steering.Unregister(ent);
        RemCompDeferred<ActiveNPCComponent>(ent);
    }

    private void OnDamageChanged(Entity<ShogkkZombifiedAIComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.Origin is not { } origin || origin == ent.Owner || !IsVictim(origin)
            || !_mobState.IsAlive(ent))
            return;

        var comp = ent.Comp;
        // Hit from somewhere: him then, unless busy with a victim right in view.
        if (comp.State == ShogkkZombifiedAIState.Chase && comp.Target is { } current && current != origin
            && _timing.CurTime - comp.LastSeenTarget < TimeSpan.FromSeconds(1))
            return;

        EnterChase(ent, origin, _timing.CurTime);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ShogkkZombifiedAIComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (now < comp.NextThink)
                continue;

            comp.NextThink = now + comp.ThinkInterval;

            // Possessed by an admin / player: hands off.
            if (HasComp<ActorComponent>(uid) || !_mobState.IsAlive(uid))
                continue;

            Think((uid, comp), xform, now);
        }
    }

    private void Think(Entity<ShogkkZombifiedAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;

        if (now >= comp.NextHordeCheck)
        {
            comp.NextHordeCheck = now + comp.HordeCheckInterval;
            UpdateHorde(ent, xform);
        }

        var seen = FindVisibleVictim(ent, xform);
        if (seen is { } victim && (comp.State != ShogkkZombifiedAIState.Chase || comp.Target != victim))
            EnterChase(ent, victim, now);

        switch (comp.State)
        {
            case ShogkkZombifiedAIState.Chase:
                UpdateChase(ent, seen != null, now);
                break;
            case ShogkkZombifiedAIState.Hunt:
                UpdateHunt(ent, xform, now);
                break;
            default:
                UpdateWander(ent, xform, now);
                break;
        }

        OpenDoorsAhead(ent, xform);
    }

    #region Victims and vision

    /// <summary>
    /// Whom he goes for: an alive humanoid that is neither an operative nor another zombified (players, crew, Gilbert).
    /// </summary>
    public bool IsVictim(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || !HasComp<HumanoidAppearanceComponent>(uid)
            || HasComp<ShogkkZombifiedComponent>(uid) || HasComp<ShogkkOperativeComponent>(uid))
            return false;

        return TryComp<MobStateComponent>(uid, out var state) && _mobState.IsAlive(uid, state);
    }

    /// <summary>The operatives' (and Gilbert's) vision rule: forward cone, 360° up close, walls block, windows don't.</summary>
    public bool CanSee(EntityUid uid, EntityUid target, ShogkkZombifiedAIComponent? comp = null, TransformComponent? xform = null)
    {
        if (!Resolve(uid, ref comp, ref xform, false))
            return false;

        var targetXform = Transform(target);
        if (targetXform.MapID != xform.MapID)
            return false;

        var (pos, rot) = _transform.GetWorldPositionRotation(xform);
        var delta = _transform.GetWorldPosition(targetXform) - pos;
        var dist = delta.Length();
        if (dist > comp.VisionRange)
            return false;

        if (dist > comp.ProximityAwarenessRange)
        {
            var cos = Vector2.Dot(rot.ToWorldVec(), delta / dist);
            if (cos < MathF.Cos(MathHelper.DegreesToRadians(comp.VisionAngle / 2f)) - 0.001f)
                return false;
        }

        return _interaction.InRangeUnobstructed(uid, target, dist + 0.5f, CollisionGroup.Opaque, _gilbert.IsSeeThrough);
    }

    /// <summary>The current victim while in view, else the nearest one in view.</summary>
    private EntityUid? FindVisibleVictim(Entity<ShogkkZombifiedAIComponent> ent, TransformComponent xform)
    {
        var pos = _transform.GetWorldPosition(xform);
        EntityUid? best = null;
        var bestDist = float.MaxValue;
        foreach (var other in _lookup.GetEntitiesInRange<HumanoidAppearanceComponent>(xform.Coordinates, ent.Comp.VisionRange))
        {
            if (other.Owner == ent.Owner || !IsVictim(other) || !CanSee(ent, other, ent.Comp, xform))
                continue;

            var dist = other.Owner == ent.Comp.Target ? -1f : (_transform.GetWorldPosition(other) - pos).LengthSquared();
            if (dist >= bestDist)
                continue;

            best = other;
            bestDist = dist;
        }

        return best;
    }

    #endregion

    #region Chase / hunt

    /// <summary>After <paramref name="victim"/>; a groan calls the zombified around (at most every GroanCooldown).</summary>
    public void EnterChase(Entity<ShogkkZombifiedAIComponent> ent, EntityUid victim, TimeSpan now)
    {
        var comp = ent.Comp;
        comp.State = ShogkkZombifiedAIState.Chase;
        comp.Target = victim;
        comp.LastSeenTarget = now;
        comp.LastKnownPosition = Transform(victim).Coordinates;
        comp.Destination = null;
        Groan(ent, victim, now);
    }

    /// <summary>
    /// A wordless groan (an emote and a sound, never the radio): every zombified within
    /// <see cref="ShogkkZombifiedAIComponent.GroanRange"/> not already busy with a victim of his own hunts this one.
    /// </summary>
    public bool Groan(Entity<ShogkkZombifiedAIComponent> ent, EntityUid victim, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now < comp.NextGroan)
            return false;

        comp.NextGroan = now + comp.GroanCooldown;
        _audio.PlayPvs(comp.GroanSound, ent, AudioParams.Default.WithVolume(2f).WithMaxDistance(comp.GroanRange));
        _chat.TrySendInGameICMessage(ent, Loc.GetString(comp.GroanEmote), InGameICChatType.Emote,
            ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false, ignoreActionBlocker: true);

        var where = Transform(victim).Coordinates;
        var xform = Transform(ent);
        var pos = _transform.GetWorldPosition(xform);
        var query = EntityQueryEnumerator<ShogkkZombifiedAIComponent, TransformComponent>();
        while (query.MoveNext(out var other, out var otherComp, out var otherXform))
        {
            if (other == ent.Owner || otherXform.MapID != xform.MapID || HasComp<ActorComponent>(other)
                || !_mobState.IsAlive(other))
                continue;

            if ((_transform.GetWorldPosition(otherXform) - pos).Length() > comp.GroanRange)
                continue;

            // Busy with a victim he sees himself: carries on.
            if (otherComp.State == ShogkkZombifiedAIState.Chase && otherComp.Target is { } own && IsVictim(own)
                && now - otherComp.LastSeenTarget < otherComp.LoseTargetDelay)
                continue;

            otherComp.Target = victim;
            otherComp.LastKnownPosition = where;
            otherComp.Destination = null;
            otherComp.State = ShogkkZombifiedAIState.Hunt;
            otherComp.HuntUntil = now + otherComp.HuntDuration;
            otherComp.ProgressTime = now;
            otherComp.ProgressPosition = _transform.GetWorldPosition(otherXform);
            // He heard it: no echo of the same groan back.
            otherComp.NextGroan = TimeSpan.FromTicks(Math.Max(otherComp.NextGroan.Ticks, (now + TimeSpan.FromSeconds(2)).Ticks));
            RemComp<NPCMeleeCombatComponent>(other);
            SteerTo(other, where, 1.0f);
        }

        return true;
    }

    private void UpdateChase(Entity<ShogkkZombifiedAIComponent> ent, bool targetVisible, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.Target is not { } target || !IsVictim(target))
        {
            BackToWander(ent);
            return;
        }

        if (targetVisible)
        {
            comp.LastSeenTarget = now;
            comp.LastKnownPosition = Transform(target).Coordinates;
        }
        else if (now - comp.LastSeenTarget > comp.LoseTargetDelay)
        {
            // Out of sight for a while: look where he was last.
            RemComp<NPCMeleeCombatComponent>(ent);
            comp.State = ShogkkZombifiedAIState.Hunt;
            comp.HuntUntil = now + comp.HuntDuration;
            comp.ProgressTime = now;
            comp.ProgressPosition = _transform.GetWorldPosition(ent);
            return;
        }

        // NPC melee combat swings whatever is in the active hand (his job tool) or the fists.
        var melee = EnsureComp<NPCMeleeCombatComponent>(ent);
        melee.Target = target;
        SteerTo(ent, targetVisible ? Transform(target).Coordinates : comp.LastKnownPosition ?? Transform(target).Coordinates, 0.9f);
    }

    private void UpdateHunt(Entity<ShogkkZombifiedAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.LastKnownPosition is not { } where || now > comp.HuntUntil || comp.Target is { } t && !IsVictim(t))
        {
            BackToWander(ent);
            return;
        }

        if (ReachedOrStuck(ent, xform, where, 1.0f, now))
        {
            BackToWander(ent);
            comp.PauseUntil = now + TimeSpan.FromSeconds(_random.NextFloat(1f, 3f));
            return;
        }

        SteerTo(ent, where, 1.0f);
    }

    private void BackToWander(Entity<ShogkkZombifiedAIComponent> ent)
    {
        var comp = ent.Comp;
        comp.State = ShogkkZombifiedAIState.Wander;
        comp.Target = null;
        comp.LastKnownPosition = null;
        comp.Destination = null;
        RemComp<NPCMeleeCombatComponent>(ent);
        _steering.Unregister(ent);
    }

    #endregion

    #region Horde

    /// <summary>The leader of his horde (himself when alone); cuts links to dead, gone or far leaders on the way.</summary>
    public EntityUid HordeRoot(EntityUid uid)
    {
        var cur = uid;
        for (var i = 0; i < 16; i++)
        {
            if (!TryComp<ShogkkZombifiedAIComponent>(cur, out var comp) || comp.Leader is not { } leader)
                return cur;

            if (!IsHordeMate(cur, leader, comp))
            {
                comp.Leader = null;
                return cur;
            }

            cur = leader;
        }

        return cur;
    }

    private bool IsHordeMate(EntityUid uid, EntityUid leader, ShogkkZombifiedAIComponent comp)
    {
        if (TerminatingOrDeleted(leader) || !HasComp<ShogkkZombifiedAIComponent>(leader) || !_mobState.IsAlive(leader))
            return false;

        var a = Transform(uid);
        var b = Transform(leader);
        return a.MapID == b.MapID
               && (_transform.GetWorldPosition(a) - _transform.GetWorldPosition(b)).Length() <= comp.HordeLeaveRange;
    }

    private void UpdateHorde(Entity<ShogkkZombifiedAIComponent> ent, TransformComponent xform)
    {
        var comp = ent.Comp;
        var root = HordeRoot(ent);
        // Path compression: he trails the leader himself, not the one in front.
        if (root != ent.Owner)
            comp.Leader = root;

        foreach (var other in _lookup.GetEntitiesInRange<ShogkkZombifiedAIComponent>(xform.Coordinates, comp.HordeRange))
        {
            if (other.Owner == ent.Owner || !_mobState.IsAlive(other) || HasComp<ActorComponent>(other))
                continue;

            var otherRoot = HordeRoot(other);
            if (otherRoot == root)
                continue;

            // Sees him, or hears him shuffling right next to him.
            var close = (_transform.GetWorldPosition(other) - _transform.GetWorldPosition(xform)).Length() <= comp.HordeHearRange
                        && _interaction.InRangeUnobstructed(ent.Owner, other.Owner, comp.HordeHearRange + 0.5f);
            if (!close && !CanSee(ent, other, comp, xform))
                continue;

            // The lower uid leads: the two hordes become one.
            if (otherRoot.Id < root.Id)
            {
                if (TryComp<ShogkkZombifiedAIComponent>(root, out var rootComp))
                    rootComp.Leader = otherRoot;
            }
            else if (TryComp<ShogkkZombifiedAIComponent>(otherRoot, out var otherRootComp))
            {
                otherRootComp.Leader = root;
            }

            root = HordeRoot(ent);
            if (root != ent.Owner)
                comp.Leader = root;
        }
    }

    #endregion

    #region Wander

    private void UpdateWander(Entity<ShogkkZombifiedAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;

        // In a horde: trails the leader at his own spot.
        if (comp.Leader != null && HordeRoot(ent) is var leader && leader != ent.Owner)
        {
            comp.Leader = leader;
            comp.Destination = null;
            var spot = Transform(leader).Coordinates.Offset(comp.FollowOffset);
            if (_transform.InRange(xform.Coordinates, spot, 1.5f))
            {
                _steering.Unregister(ent);
                return;
            }

            SteerTo(ent, spot, 1.0f);
            return;
        }

        if (now < comp.PauseUntil)
            return;

        if (comp.Destination is { } dest && now < comp.DestinationTimeout && !ReachedOrStuck(ent, xform, dest, 1.0f, now))
        {
            SteerTo(ent, dest, 1.0f);
            return;
        }

        if (comp.Destination != null)
        {
            // There (or gave up): stands swaying a little before the next leg.
            comp.Destination = null;
            _steering.Unregister(ent);
            comp.PauseUntil = now + TimeSpan.FromSeconds(_random.NextFloat(1.5f, 5f));
            return;
        }

        if (PickWanderPoint(ent, xform, now) is not { } next)
        {
            comp.PauseUntil = now + TimeSpan.FromSeconds(2);
            return;
        }

        comp.Destination = next;
        comp.DestinationTimeout = now + TimeSpan.FromSeconds(45);
        comp.ProgressTime = now;
        comp.ProgressPosition = _transform.GetWorldPosition(xform);
        SteerTo(ent, next, 1.0f);
    }

    /// <summary>A random floor spot <see cref="ShogkkZombifiedAIComponent.WanderMinDistance"/>-Max tiles away.</summary>
    private EntityCoordinates? PickWanderPoint(Entity<ShogkkZombifiedAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var center = _transform.GetWorldPosition(xform);
        for (var i = 0; i < 12; i++)
        {
            // Shorter legs are tried more as the long ones keep landing in space or walls.
            var max = MathHelper.Lerp(comp.WanderMaxDistance, comp.WanderMinDistance, i / 12f);
            var dist = _random.NextFloat(comp.WanderMinDistance, MathF.Max(comp.WanderMinDistance, max));
            var world = center + _random.NextAngle().ToVec() * dist;
            var tile = _map.WorldToTile(gridUid, grid, world);
            if (!IsFreeFloor(gridUid, grid, tile))
                continue;

            if (comp.Blacklist.TryGetValue((gridUid, CellOf(tile)), out var until) && until > now)
                continue;

            return _map.GridTileToLocal(gridUid, grid, tile);
        }

        return null;
    }

    private bool IsFreeFloor(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty)
            return false;

        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var uid))
        {
            if (TryComp<AirtightComponent>(uid, out var airtight) && airtight.AirBlocked)
                return false;
        }

        return true;
    }

    private static Vector2i CellOf(Vector2i tile)
    {
        return new Vector2i((int) MathF.Floor(tile.X / (float) BlacklistCell), (int) MathF.Floor(tile.Y / (float) BlacklistCell));
    }

    #endregion

    #region Steering

    private void SteerTo(EntityUid uid, EntityCoordinates coordinates, float range)
    {
        if (TryComp<NPCSteeringComponent>(uid, out var existing))
        {
            if (existing.Status != SteeringStatus.NoPath && _transform.InRange(existing.Coordinates, coordinates, 0.75f))
            {
                existing.Range = range;
                return;
            }

            if (existing.Status == SteeringStatus.NoPath && !existing.Coordinates.Equals(coordinates))
            {
                existing.Status = SteeringStatus.Moving;
                existing.FailedPathCount = 0;
            }
        }

        var steering = _steering.Register(uid, coordinates, existing);
        steering.Range = range;
        steering.Flags |= ZombifiedPathFlags;
    }

    /// <summary>Within range of the spot, or hopeless (no path / no progress): then that cell is avoided a minute.</summary>
    private bool ReachedOrStuck(Entity<ShogkkZombifiedAIComponent> ent, TransformComponent xform, EntityCoordinates dest,
        float range, TimeSpan now)
    {
        var comp = ent.Comp;
        if (_transform.InRange(xform.Coordinates, dest, range + 0.3f))
            return true;

        var pos = _transform.GetWorldPosition(xform);
        if ((pos - comp.ProgressPosition).LengthSquared() > 0.25f)
        {
            comp.ProgressPosition = pos;
            comp.ProgressTime = now;
        }

        var noPath = TryComp<NPCSteeringComponent>(ent, out var steering) && steering.Status == SteeringStatus.NoPath;
        if (!noPath && now - comp.ProgressTime < comp.StallTimeout)
            return false;

        if (dest.EntityId is var gridUid && TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var tile = _map.TileIndicesFor(gridUid, grid, dest);
            comp.Blacklist[(gridUid, CellOf(tile))] = now + TimeSpan.FromSeconds(60);
            if (comp.Blacklist.Count > 64)
                comp.Blacklist.Clear();
        }

        comp.ProgressTime = now;
        _steering.Unregister(ent);
        return true;
    }

    private void OpenDoorsAhead(Entity<ShogkkZombifiedAIComponent> ent, TransformComponent xform)
    {
        if (!TryComp<NPCSteeringComponent>(ent, out var steering))
            return;

        var pos = _transform.GetWorldPosition(xform);
        var next = steering.CurrentPath.TryPeek(out var node) ? node.Coordinates : steering.Coordinates;
        var dir = _transform.ToMapCoordinates(next).Position - pos;

        foreach (var door in _lookup.GetEntitiesInRange<DoorComponent>(xform.Coordinates, ent.Comp.DoorOpenRange))
        {
            if (door.Comp.State != DoorState.Closed)
                continue;

            var toDoor = _transform.GetWorldPosition(door) - pos;
            if (dir.LengthSquared() > 0.01f && toDoor.LengthSquared() > 0.04f && Vector2.Dot(dir, toDoor) <= 0f)
                continue;

            // Access is checked as usual: only unlocked doors (or ones his old ID would open, he has none).
            _door.TryOpen(door, door.Comp, ent, quiet: true);
        }
    }

    #endregion
}
