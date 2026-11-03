using System.Linq;
using System.Numerics;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server.Hands.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Tag;
using Content.Shared.Whitelist;
using Content.Shared._CorvaxGoob.ShogkkOperative;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

/// <summary>
/// AI of the SHOGKK operatives. A lean sibling of <see cref="GilbertAISystem"/> built from the same engine pieces
/// (NPC ranged / melee combat, NPC steering through doors, his vision rule and see-through check, a novelty memory of
/// explored cells), but none of Gilbert's own stuff (surrender, cuffing, finishers, abilities, voice lines).
/// <list type="bullet">
/// <item>Patrol: walks to the least explored nearby spots, opening doors with his all-access card.</item>
/// <item>Combat: anyone Gilbert would go for in his first phase (an alive, uncuffed humanoid) plus Gilbert himself, but
/// never another operative; shoots at range, reloads from the bag, knife when the ammo is gone.</item>
/// <item>Search / Assist: last known position, or a spot from the radio.</item>
/// <item>Heal: first-aid kit on himself when hurt and safe.</item>
/// <item>Radio (<c>.Radio.cs</c>): «Вижу противника», «Нужна помощь», «Докладываю».</item>
/// </list>
/// Performance: each operative thinks every <see cref="ShogkkOperativeAIComponent.ThinkInterval"/> with a random
/// phase; vision is one lookup per think; radio sharing touches only operatives in range and is throttled by the
/// phrase cooldowns; the explored-cell memory is capped.
/// </summary>
public sealed partial class ShogkkOperativeAISystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly GilbertAISystem _gilbert = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoorSystem _door = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly GunSystem _gun = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;

    /// <summary>Doors are walked through (all-access card), tables climbed. No prying: they're ordinary humans.</summary>
    public const PathFlags OperativePathFlags = PathFlags.Interact | PathFlags.Access | PathFlags.Climbing;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShogkkOperativeAIComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ShogkkOperativeAIComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<ShogkkOperativeAIComponent, DamageChangedEvent>(OnDamageChanged);
    }

    private void OnMapInit(Entity<ShogkkOperativeAIComponent> ent, ref MapInitEvent args)
    {
        var now = _timing.CurTime;
        var comp = ent.Comp;
        // Random phase: a squad spawned together never thinks on the same tick.
        comp.NextThink = now + TimeSpan.FromSeconds(0.3 + _random.NextDouble() * comp.ThinkInterval.TotalSeconds);
        comp.NextReport = now + comp.ReportInterval;
        comp.LastSeenTarget = TimeSpan.Zero;
        EnsureComp<ActiveNPCComponent>(ent);
    }

    private void OnMobStateChanged(Entity<ShogkkOperativeAIComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
            return;

        AbortTask(ent);
        StopCombatComponents(ent);
        _steering.Unregister(ent);
        RemCompDeferred<ActiveNPCComponent>(ent);
    }

    private void OnDamageChanged(Entity<ShogkkOperativeAIComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || !_mobState.IsAlive(ent))
            return;

        var comp = ent.Comp;
        comp.LastDamaged = _timing.CurTime;

        // Shot or hit from somewhere: whoever did it is the target now (unless he's already fighting someone in view).
        if (args.Origin is not { } origin || origin == ent.Owner || HasComp<ShogkkOperativeComponent>(origin)
            || !HasComp<MobStateComponent>(origin) || !_mobState.IsAlive(origin))
            return;

        if (comp.State == ShogkkOperativeAIState.Combat && comp.Target is { } current && current != origin
            && _timing.CurTime - comp.LastSeenTarget < TimeSpan.FromSeconds(1))
            return;

        EnterCombat(ent, origin);
    }

    /// <summary>Called by ShogkkOperativeSystem once his weapon is issued.</summary>
    public void SetGun(EntityUid uid, EntityUid gun)
    {
        if (TryComp<ShogkkOperativeAIComponent>(uid, out var comp))
            comp.Gun = gun;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ShogkkOperativeAIComponent, TransformComponent>();
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

    private void Think(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        MarkVisited(ent, xform, now);
        ForgetOld(comp, now);

        var seen = FindVisibleEnemy(ent, xform);
        // Every enemy in view is remembered, and the first one nobody called in yet goes out on the radio
        // (not only the one he is shooting at).
        foreach (var enemy in _visibleScratch)
        {
            comp.KnownEnemies[enemy] = new ShogkkKnownEnemy(Transform(enemy).Coordinates, now);
            TryCallEnemy(ent, enemy, now);
        }

        TryCallHelp(ent, xform, now);

        if (comp.Task != ShogkkOperativeTask.None)
        {
            UpdateTask(ent, now, seen);
            OpenDoorsAhead(ent, xform);
            return;
        }

        if (seen != null && (comp.State != ShogkkOperativeAIState.Combat || comp.Target is not { } t || !IsEnemy(t)
                             || now - comp.LastSeenTarget > TimeSpan.FromSeconds(1)))
        {
            EnterCombat(ent, seen.Value);
        }

        // Weapon in order (racked, the gun rather than the knife), or off to a weapon on the floor.
        if (UpdateArmament(ent, xform, now))
        {
            OpenDoorsAhead(ent, xform);
            return;
        }

        switch (comp.State)
        {
            case ShogkkOperativeAIState.Combat:
                UpdateCombat(ent, xform, now);
                break;
            case ShogkkOperativeAIState.Search:
                UpdateSearch(ent, xform, now);
                break;
            case ShogkkOperativeAIState.Assist:
                UpdateAssist(ent, xform, now);
                break;
            case ShogkkOperativeAIState.Heal:
                SetState(ent, ShogkkOperativeAIState.Patrol);
                break;
            default:
                UpdatePatrol(ent, xform, now);
                break;
        }

        OpenDoorsAhead(ent, xform);
    }

    #region Vision / enemies

    /// <summary>
    /// The targets: everyone Gilbert goes for in his first phase (alive, uncuffed humanoids) and Gilbert himself
    /// (<see cref="GilbertAISystem.IsEnemy"/> leaves him out only because he is Gilbert), but no other operative.
    /// </summary>
    public bool IsEnemy(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || !HasComp<HumanoidAppearanceComponent>(uid) || HasComp<ShogkkOperativeComponent>(uid))
            return false;

        if (!TryComp<MobStateComponent>(uid, out var state) || !_mobState.IsAlive(uid, state))
            return false;

        if (TryComp<CuffableComponent>(uid, out var cuffable) && cuffable.CuffedHandCount > 0)
            return false;

        return true;
    }

    /// <summary>
    /// Gilbert's vision rule: a forward cone (<see cref="ShogkkOperativeAIComponent.VisionAngle"/>) out to
    /// <see cref="ShogkkOperativeAIComponent.VisionRange"/>, 360° up close, blocked by walls but not by windows.
    /// </summary>
    public bool CanSee(EntityUid uid, EntityUid target, ShogkkOperativeAIComponent? comp = null, TransformComponent? xform = null)
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
            var forward = rot.ToWorldVec();
            var cos = Vector2.Dot(forward, delta / dist);
            if (cos < MathF.Cos(MathHelper.DegreesToRadians(comp.VisionAngle / 2f)) - 0.001f)
                return false;
        }

        return _interaction.InRangeUnobstructed(uid, target, dist + 0.5f, CollisionGroup.Opaque, _gilbert.IsSeeThrough);
    }

    private readonly List<EntityUid> _visibleScratch = new();

    /// <summary>
    /// Fills <see cref="_visibleScratch"/> with every enemy in view and returns the one to fight: the current target
    /// while he is in view (so a crowd doesn't make him flick between targets), else the nearest.
    /// </summary>
    private EntityUid? FindVisibleEnemy(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform)
    {
        _visibleScratch.Clear();
        var pos = _transform.GetWorldPosition(xform);
        EntityUid? best = null;
        var bestDist = float.MaxValue;
        var current = ent.Comp.Target;

        foreach (var other in _lookup.GetEntitiesInRange<HumanoidAppearanceComponent>(xform.Coordinates, ent.Comp.VisionRange))
        {
            if (other.Owner == ent.Owner || !IsEnemy(other) || !CanSee(ent, other, ent.Comp, xform))
                continue;

            _visibleScratch.Add(other);
            var dist = (_transform.GetWorldPosition(other) - pos).LengthSquared();
            if (other.Owner == current)
                dist = -1f;

            if (dist >= bestDist)
                continue;

            best = other;
            bestDist = dist;
        }

        return best;
    }

    private void ForgetOld(ShogkkOperativeAIComponent comp, TimeSpan now)
    {
        if (comp.KnownEnemies.Count == 0)
            return;

        List<EntityUid>? stale = null;
        foreach (var (uid, known) in comp.KnownEnemies)
        {
            if (now - known.Time > comp.EnemyMemory || !IsEnemy(uid))
                (stale ??= new()).Add(uid);
        }

        if (stale == null)
            return;

        foreach (var uid in stale)
        {
            comp.KnownEnemies.Remove(uid);
            comp.ReportedEnemies.Remove(uid);
        }
    }

    #endregion

    #region States

    private void SetState(Entity<ShogkkOperativeAIComponent> ent, ShogkkOperativeAIState state)
    {
        if (ent.Comp.State == state)
            return;

        ent.Comp.State = state;
        ent.Comp.Destination = null;
        if (state != ShogkkOperativeAIState.Combat)
        {
            StopCombatComponents(ent);
            RemComp<NoRotateOnMoveComponent>(ent);
        }
    }

    public void EnterCombat(Entity<ShogkkOperativeAIComponent> ent, EntityUid target)
    {
        var comp = ent.Comp;
        if (comp.Task == ShogkkOperativeTask.Heal)
            AbortTask(ent);

        SetState(ent, ShogkkOperativeAIState.Combat);
        comp.Target = target;
        comp.LastSeenTarget = _timing.CurTime;
        comp.LastKnownPosition = Transform(target).Coordinates;
        comp.AssistAlly = null;
        comp.AssistPosition = null;
    }

    private void EnterSearch(Entity<ShogkkOperativeAIComponent> ent, EntityCoordinates? where, TimeSpan now)
    {
        SetState(ent, ShogkkOperativeAIState.Search);
        ent.Comp.StateUntil = now + ent.Comp.SearchDuration;
        ent.Comp.Destination = where;
        ent.Comp.DestinationTimeout = now + TimeSpan.FromSeconds(30);
        if (where != null)
            SteerTo(ent, where.Value, 1.0f);
    }

    /// <summary>Off to a spot from the radio, or to follow an ally who called for help.</summary>
    public void BeginAssist(Entity<ShogkkOperativeAIComponent> ent, EntityCoordinates where, EntityUid? ally)
    {
        var now = _timing.CurTime;
        var comp = ent.Comp;
        if (comp.State == ShogkkOperativeAIState.Combat || comp.Task is ShogkkOperativeTask.Reload or ShogkkOperativeTask.PickupWeapon)
            return;

        if (comp.Task == ShogkkOperativeTask.Heal)
            AbortTask(ent);

        SetState(ent, ShogkkOperativeAIState.Assist);
        comp.AssistAlly = ally;
        comp.AssistPosition = where;
        comp.StateUntil = now + comp.AssistTimeout;
        comp.ProgressTime = now;
        comp.ProgressPosition = _transform.GetWorldPosition(ent);
        SteerTo(ent, where, ally != null ? 2f : 1.2f);
    }

    private void UpdateSearch(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now >= comp.StateUntil)
        {
            comp.Target = null;
            SetState(ent, ShogkkOperativeAIState.Patrol);
            return;
        }

        // Hurt and nobody in sight: patch up first.
        if (TryStartHealing(ent, now))
            return;

        // Last known position first, then random spots around it.
        if (comp.Destination is { } dest && now < comp.DestinationTimeout && !ReachedOrStuck(ent, xform, dest, 1.2f, now))
            return;

        var center = comp.LastKnownPosition ?? xform.Coordinates;
        if (PickPatrolPoint(ent, xform, center, 2f, 7f, now) is { } next)
        {
            comp.Destination = next;
            comp.DestinationTimeout = now + TimeSpan.FromSeconds(12);
            SteerTo(ent, next, 1.0f);
        }
    }

    private void UpdateAssist(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now >= comp.StateUntil || comp.AssistPosition is not { } where)
        {
            EnterSearch(ent, comp.AssistPosition, now);
            return;
        }

        // Following an ally: his live position (until he is dead / gone).
        if (comp.AssistAlly is { } ally && !TerminatingOrDeleted(ally) && _mobState.IsAlive(ally)
            && Transform(ally).MapID == xform.MapID)
        {
            var allyCoords = Transform(ally).Coordinates;
            if (!_transform.InRange(allyCoords, where, 1.5f))
            {
                where = allyCoords;
                comp.AssistPosition = where;
            }
        }

        var range = comp.AssistAlly != null ? 2f : 1.2f;
        if (_transform.InRange(xform.Coordinates, where, range + 0.5f))
        {
            // Arrived: look around there.
            comp.LastKnownPosition = where;
            EnterSearch(ent, null, now);
            comp.StateUntil = now + comp.SearchDuration / 2;
            return;
        }

        if (ReachedOrStuck(ent, xform, where, range, now))
        {
            EnterSearch(ent, null, now);
            return;
        }

        SteerTo(ent, where, range);
    }

    #endregion

    #region Helpers

    private void StopCombatComponents(EntityUid uid)
    {
        RemComp<NPCRangedCombatComponent>(uid);
        RemComp<NPCMeleeCombatComponent>(uid);
    }

    private float DistanceTo(EntityUid uid, EntityUid other)
    {
        return (_transform.GetWorldPosition(other) - _transform.GetWorldPosition(uid)).Length();
    }

    /// <summary>Every operative on the same map within <paramref name="range"/> of <paramref name="uid"/>, alive.</summary>
    private List<(Entity<ShogkkOperativeAIComponent> Ent, float Distance)> AlliesInRange(EntityUid uid, float range)
    {
        var result = new List<(Entity<ShogkkOperativeAIComponent>, float)>();
        var xform = Transform(uid);
        var pos = _transform.GetWorldPosition(xform);
        var query = EntityQueryEnumerator<ShogkkOperativeAIComponent, TransformComponent>();
        while (query.MoveNext(out var other, out var comp, out var otherXform))
        {
            if (other == uid || otherXform.MapID != xform.MapID || !_mobState.IsAlive(other))
                continue;

            var dist = (_transform.GetWorldPosition(otherXform) - pos).Length();
            if (dist <= range)
                result.Add(((other, comp), dist));
        }

        result.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return result;
    }

    private bool HasActiveDoAfter(EntityUid uid)
    {
        if (!TryComp<DoAfterComponent>(uid, out var doAfter))
            return false;

        foreach (var da in doAfter.DoAfters.Values)
        {
            if (!da.Cancelled && !da.Completed)
                return true;
        }

        return false;
    }

    private void OpenDoorsAhead(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform)
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

            _door.TryOpen(door, door.Comp, ent, quiet: true);
        }
    }

    #endregion
}
