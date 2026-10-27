using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server.Chat.Systems;
using Content.Server.Hands.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.TTS;
using Content.Shared._Shitmed.Medical.Surgery.Tools;
using Content.Shared.Chat;
using Content.Shared.Chemistry.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.DoAfter;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Explosion.Components;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Medical.Healing;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Sticky.Components;
using Content.Shared.Tag;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Gilbert's AI: vision cone, passive wandering with idle lines, surrender-and-cuff, combat with his real gear
/// (shotgun with manual racking and box reloads, grenades, truncheon + shield, C4 on walls), searching the last
/// known position for 3 minutes before going passive again, and self-healing with the real contents of his medkits.
/// </summary>
public sealed partial class GilbertAISystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedDoorSystem _door = default!;
    [Dependency] private readonly GilbertAbilitiesSystem _abilities = default!;
    [Dependency] private readonly GunSystem _gun = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly ItemToggleSystem _toggle = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _speed = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly SharedCuffableSystem _cuffable = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly GilbertSpawnSequenceSystem _sequence = default!;
    [Dependency] private readonly Content.Server.Destructible.DestructibleSystem _destructible = default!; // v8: pick the weakest thing to smash

    private static readonly ProtoId<TagPrototype> WallTag = "Wall";
    private static readonly ProtoId<TagPrototype> WindowTag = "Window";
    private const string SuitStorageSlot = "suitstorage";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertAIComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<GilbertAIComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<GilbertAIComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<GilbertAIComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
        SubscribeLocalEvent<GilbertAIComponent, GilbertFriskDoAfterEvent>(OnFriskDoAfter);
        InitializeHearing();
        InitializeBerserk();
    }

    #region Events

    private void OnMapInit(Entity<GilbertAIComponent> ent, ref MapInitEvent args)
    {
        var now = _timing.CurTime;
        ent.Comp.NextIdleLine = now + TimeSpan.FromSeconds(_random.NextFloat(20f, 60f));
        ent.Comp.NextWander = now + TimeSpan.FromSeconds(1);
        // v8: was 2 s; the spawn sequence already holds the AI ~2.2 s, so only a short settle-in (gear, pathfinding) is kept.
        ent.Comp.NextThink = now + TimeSpan.FromSeconds(0.3);
        ent.Comp.LastSeenEnemy = now - ent.Comp.SearchTimeout;

        // Gilbert speaks with his own recorded voice, not TTS.
        RemCompDeferred<TTSComponent>(ent);
        EnsureComp<ActiveNPCComponent>(ent);
        _speed.RefreshMovementSpeedModifiers(ent);
    }

    private void OnMobStateChanged(Entity<GilbertAIComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
            return;

        AbortTask(ent);
        StopCombatComponents(ent);
        _steering.Unregister(ent);
        RemCompDeferred<ActiveNPCComponent>(ent);
    }

    private void OnDamageChanged(Entity<GilbertAIComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || _mobState.IsDead(ent))
            return;

        var comp = ent.Comp;

        // v8: react on the very next tick, not at the next scheduled think.
        comp.NextThink = _timing.CurTime;

        if (InBerserk(ent))
        {
            BerserkRetaliate(ent, args.Origin);
            return;
        }

        // Getting hurt by someone (or by anything sizeable) counts as being under fire.
        // Tiny unattributed ticks (chemical side effects of his own medicine, etc.) do not.
        var increase = FixedPoint2.Zero;
        if (args.DamageDelta != null)
        {
            foreach (var value in args.DamageDelta.DamageDict.Values)
            {
                if (value > 0)
                    increase += value;
            }
        }

        var underFire = args.Origin is { } attacker && attacker != ent.Owner || increase >= comp.UnderFireDamage;
        if (underFire)
        {
            comp.LastDamaged = _timing.CurTime;

            // Never heal under fire.
            if (comp.State == GilbertAIState.Heal)
                AbortHealing(ent);
        }

        if (TryComp<DamageableComponent>(ent, out var damageable))
        {
            if (!comp.SaidHalfHealth && damageable.TotalDamage >= comp.HealStartDamage)
            {
                comp.SaidHalfHealth = true;
                SayRandom(ent, "halfhealth");
            }
        }

        // He turns towards whoever hurt him, so they end up in his vision cone.
        if (args.Origin is { } origin && origin != ent.Owner && underFire)
        {
            comp.LastAttacker = origin;
            comp.LastAttackedAt = _timing.CurTime;
        }

        if (args.Origin is { } origin2 && origin2 != ent.Owner && (IsHostile(ent, origin2) || comp.State == GilbertAIState.Expel && origin2 == comp.ExpelTarget))
        {
            var xform = Transform(ent);
            var delta = _transform.GetWorldPosition(origin2) - _transform.GetWorldPosition(xform);
            if (delta.LengthSquared() > 0.01f)
                _transform.SetWorldRotation(ent, delta.ToWorldAngle());

            if (comp.State == GilbertAIState.Expel || !IsEnemy(origin2))
                comp.ForcedHostile.Add(origin2); // a cuffed person hitting him is fair game

            if (comp.State is GilbertAIState.Idle or GilbertAIState.Surrender or GilbertAIState.Search or GilbertAIState.Expel
                or GilbertAIState.Investigate)
                EnterCombat(ent, origin2, comp.State != GilbertAIState.Search ? "noncomply" : "found");
        }
    }

    private void OnRefreshSpeed(Entity<GilbertAIComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        // Berserk: his own (faster) speed, never the idle stroll.
        if (InBerserk(ent))
            return;

        // Running from a bomb: full speed whatever he was doing.
        if (ent.Comp.State is GilbertAIState.Idle or GilbertAIState.Surrender && !ent.Comp.FleeingDanger)
            args.ModifySpeed(ent.Comp.IdleSpeedModifier, ent.Comp.IdleSpeedModifier);
    }

    #endregion

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        UpdateHearing(now);

        var query = EntityQueryEnumerator<GilbertAIComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            // Still assembling from red energy (spawn sequence): the AI starts once he is active.
            if (!_sequence.IsActive(uid))
                continue;

            // Every tick, not just on think: keep facing the enemy he's fighting (berserk: he faces his victim himself).
            var berserk = InBerserk(uid);
            if (!berserk)
                UpdateFacing((uid, comp), now);

            if (now < comp.NextThink)
                continue;

            comp.NextThink = now + comp.ThinkInterval;

            // Possessed by an admin / player: hands off.
            if (HasComp<ActorComponent>(uid) || !_mobState.IsAlive(uid))
            {
                ReleaseFacing((uid, comp));
                continue;
            }

            if (berserk)
            {
                UpdateBerserk((uid, comp), xform, now);
                OpenDoorsAhead((uid, comp), xform);
                continue;
            }

            Think((uid, comp), xform, now);
            OpenDoorsAhead((uid, comp), xform);
            UpdateWatchdog((uid, comp), xform, now);
        }
    }

    private void Think(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        comp.ThinkDelta = comp.LastThink == TimeSpan.Zero ? comp.ThinkInterval
            : TimeSpan.FromTicks(Math.Min((now - comp.LastThink).Ticks, TimeSpan.FromSeconds(1).Ticks));
        comp.LastThink = now;
        MarkVisited(ent, xform, now);
        var seen = FindVisibleEnemy(ent, xform);
        if (seen != null)
        {
            comp.LastSeenEnemy = now;
            // Whoever he noticed (even right behind him) gets faced, unless his hands are busy aiming a throw.
            if (comp.Task != GilbertTask.Grenade)
                FaceTowards(ent, seen.Value);

            // An enemy beats loot.
            if (comp.Task is GilbertTask.Pickup or GilbertTask.Loot)
                AbortTask(ent);
        }

        // An armed charge about to go off next to him beats everything else.
        if (UpdateDanger(ent, xform, now))
            return;

        // Cuffed people hanging around him (counts only while he's free; may start the punch).
        if (UpdateLoiterers(ent, xform, now) && seen == null)
        {
            UpdateExpel(ent, xform, now);
            return;
        }

        switch (comp.State)
        {
            case GilbertAIState.Expel:
                if (seen != null)
                {
                    // A real enemy beats a loiterer.
                    comp.ExpelTarget = null;
                    comp.ExpelPunched = false;
                    if (IsArmed(seen.Value) || comp.ForcedHostile.Contains(seen.Value))
                        EnterCombat(ent, seen.Value, "found");
                    else
                        BeginSurrender(ent, seen.Value);
                    return;
                }
                UpdateExpel(ent, xform, now);
                break;

            case GilbertAIState.Idle:
                if (seen != null)
                {
                    BeginSurrender(ent, seen.Value);
                    return;
                }
                if (comp.Task != GilbertTask.None)
                {
                    UpdateTask(ent, now);
                    return;
                }
                if (TryPendingInvestigate(ent, now))
                    return;
                if (TryStartHealing(ent, now) || TryStartScavenge(ent, xform, now))
                    return;
                UpdateIdle(ent, xform, now);
                break;

            case GilbertAIState.Surrender:
                UpdateSurrender(ent, xform, now, seen);
                break;

            case GilbertAIState.Combat:
                UpdateCombat(ent, xform, now, seen);
                break;

            case GilbertAIState.Search:
                if (seen != null)
                {
                    EnterCombat(ent, seen.Value, "found");
                    return;
                }
                if (TryStartHealing(ent, now) || TryStartScavenge(ent, xform, now))
                    return;
                UpdateSearch(ent, xform, now);
                break;

            case GilbertAIState.Investigate:
                if (seen != null)
                {
                    BeginSurrender(ent, seen.Value);
                    return;
                }
                // v8: no detours for loot on the way to a sound; only once he is looking around the spot.
                if (TryStartHealing(ent, now) || comp.InvestigateArrived && TryStartScavenge(ent, xform, now))
                    return;
                UpdateInvestigate(ent, xform, now);
                break;

            case GilbertAIState.Heal:
                if (seen != null)
                {
                    // An enemy showed up mid-heal: drop it and fight.
                    AbortHealing(ent);
                    EnterCombat(ent, seen.Value, "found");
                    return;
                }
                UpdateHealing(ent, now);
                break;
        }
    }

    #region Perception

    /// <summary>
    /// Is this something Gilbert hunts? All living, uncuffed humanoids except Gilbert himself.
    /// </summary>
    public bool IsEnemy(EntityUid uid)
    {
        if (!HasComp<HumanoidAppearanceComponent>(uid) || HasComp<GilbertAIComponent>(uid))
            return false;

        if (!TryComp<MobStateComponent>(uid, out var state) || !_mobState.IsAlive(uid, state))
            return false;

        if (TryComp<CuffableComponent>(uid, out var cuffable) && cuffable.CuffedHandCount > 0)
            return false;

        return true;
    }

    /// <summary>
    /// 180° cone in front of him (his actual facing), 15 tiles, blocked by walls and other opaque things but not by
    /// windows (<see cref="IsSeeThrough"/>);
    /// plus 360° awareness within <see cref="GilbertAIComponent.ProximityAwarenessRange"/>.
    /// </summary>
    public bool CanSee(EntityUid uid, EntityUid target, GilbertAIComponent? comp = null, TransformComponent? xform = null)
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

        // 360° awareness of his own and the adjacent tiles; beyond that only the forward cone.
        if (dist > comp.ProximityAwarenessRange)
        {
            var forward = rot.ToWorldVec();
            var cos = Vector2.Dot(forward, delta / dist);
            var halfAngle = MathHelper.DegreesToRadians(comp.VisionAngle / 2f);
            if (cos < MathF.Cos(halfAngle) - 0.001f)
                return false;
        }

        return _interaction.InRangeUnobstructed(uid, target, dist + 0.5f, CollisionGroup.Opaque, IsSeeThrough);
    }

    /// <summary>
    /// Things Gilbert's line of sight passes through: every window (plain, reinforced, plasma, uranium, tinted,
    /// directional - all carry the Window tag), windoors and glass airlocks (doors that do not occlude), and other
    /// mobs (a crowd does not hide the person behind it). Walls, opaque doors and machines still block.
    /// Hearing is not affected.
    /// </summary>
    public bool IsSeeThrough(EntityUid uid)
    {
        if (_tag.HasTag(uid, WindowTag))
            return true;

        if (TryComp<DoorComponent>(uid, out var door) && !door.Occludes)
            return true;

        return HasComp<MobStateComponent>(uid);
    }

    private float DistanceTo(EntityUid uid, EntityUid other)
    {
        return (_transform.GetWorldPosition(other) - _transform.GetWorldPosition(uid)).Length();
    }

    #endregion

    #region State transitions

    private void SetState(Entity<GilbertAIComponent> ent, GilbertAIState state)
    {
        if (ent.Comp.State == state)
            return;

        ent.Comp.State = state;
        _speed.RefreshMovementSpeedModifiers(ent);
    }

    private void BeginSurrender(Entity<GilbertAIComponent> ent, EntityUid target)
    {
        var comp = ent.Comp;

        // An armed target gets no warning, nor does a cuffed one who ignored his punch.
        if (IsArmed(target) || comp.ForcedHostile.Contains(target))
        {
            EnterCombat(ent, target, "found");
            return;
        }

        SetState(ent, GilbertAIState.Surrender);
        comp.Target = target;
        comp.SurrenderOrdered = false;
        comp.LastSeenTarget = _timing.CurTime;
        comp.LastKnownPosition = Transform(target).Coordinates;
    }

    public void EnterCombat(Entity<GilbertAIComponent> ent, EntityUid target, string? line = "found")
    {
        var comp = ent.Comp;
        var wasFighting = comp.State == GilbertAIState.Combat;

        if (comp.State == GilbertAIState.Heal)
            AbortHealing(ent);
        else if (comp.Task is GilbertTask.Cuff)
            AbortTask(ent);

        SetState(ent, GilbertAIState.Combat);
        comp.Target = target;
        comp.LastSeenTarget = _timing.CurTime;
        comp.LastSeenEnemy = _timing.CurTime;
        comp.LastKnownPosition = Transform(target).Coordinates;
        comp.LastTargetDistance = DistanceTo(ent, target);

        if (!wasFighting && line != null)
            SayRandom(ent, line);
    }

    private void EnterSearch(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        StopCombatComponents(ent);
        SetState(ent, GilbertAIState.Search);
        comp.Target = null;
        comp.ArrivedAtLastKnown = false;
        comp.SearchRoomsChecked = 0;
        comp.SearchLookUntil = TimeSpan.Zero;
        comp.NextWander = _timing.CurTime;

        if (comp.LastKnownPosition is { } last)
            RegisterMove(ent, last, 0.8f);
    }

    private void EnterIdle(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        StopCombatComponents(ent);
        _steering.Unregister(ent);
        SetState(ent, GilbertAIState.Idle);
        comp.Target = null;
        comp.LastKnownPosition = null;
        comp.ExpelTarget = null;
        comp.ExpelPunched = false;
        // Fight over: cuffed people are captives again (the loitering rules start from scratch).
        comp.ForcedHostile.Clear();
        comp.NextWander = _timing.CurTime + comp.ExplorePauseMin;
        comp.NextIdleLine = _timing.CurTime + comp.IdleLineInterval;

        // Back to carrying the shotgun.
        if (comp.WeaponMode == GilbertWeaponMode.Melee)
            SwitchToRanged(ent);
    }

    #endregion

    #region Idle

    private void UpdateIdle(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;

        if (now >= comp.NextIdleLine)
        {
            comp.NextIdleLine = now + comp.IdleLineInterval;
            SayRandom(ent, "idle");
        }

        // Patrol: walk to a distant, reachable, preferably unvisited spot; short pause on arrival; repeat.
        if (TryComp<NPCSteeringComponent>(ent, out var steering))
        {
            if (steering.Status == SteeringStatus.InRange)
            {
                _steering.Unregister(ent);
                comp.MoveTarget = null;
                comp.NextWander = now + TimeSpan.FromSeconds(_random.NextFloat(
                    (float) comp.ExplorePauseMin.TotalSeconds, (float) comp.ExplorePauseMax.TotalSeconds));
            }
            return;
        }

        if (now >= comp.NextWander)
            PickIdleDestination(ent, xform, now);
    }

    private void Wander(Entity<GilbertAIComponent> ent, EntityCoordinates around, float radius)
    {
        var offset = _random.NextAngle().ToVec() * _random.NextFloat(1.5f, radius);
        RegisterMove(ent, around.Offset(offset), 0.5f);
    }

    private void RegisterMove(EntityUid uid, EntityCoordinates coords, float range)
    {
        // Never walk into a blast zone he knows about (unless he's running out of one and replanning).
        if (TryComp<GilbertAIComponent>(uid, out var danger) && danger.KnownDangers.Count > 0 && !danger.FleeingDanger
            && InDanger((uid, danger), _transform.ToMapCoordinates(coords).Position))
        {
            _steering.Unregister(uid);
            return;
        }

        var steering = _steering.Register(uid, coords);
        steering.Range = range;
        // He isn't an HTN NPC, so the pathfinder would treat every closed door as a wall. He opens doors
        // (all-access ID, see OpenDoorsAhead) and plans through access airlocks.
        steering.Flags |= PathFlags.Interact | PathFlags.Prying;

        if (TryComp<GilbertAIComponent>(uid, out var comp)
            && (comp.MoveTarget is not { } old || !_transform.InRange(old, coords, 2f)))
        {
            comp.MoveTarget = coords;
            comp.MoveStarted = _timing.CurTime;
        }
    }

    #endregion

    #region Surrender

    private void UpdateSurrender(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now, EntityUid? seen)
    {
        var comp = ent.Comp;
        if (comp.Target is not { } target || TerminatingOrDeleted(target))
        {
            EnterIdle(ent);
            return;
        }

        // Already cuffed (by us or anyone): job done.
        if (!IsEnemy(target))
        {
            if (comp.Task == GilbertTask.Cuff)
                FinishTask(ent);
            EnterIdle(ent);
            return;
        }

        var visible = CanSee(ent, target, comp, xform);
        if (visible)
        {
            comp.LastSeenTarget = now;
            comp.LastKnownPosition = Transform(target).Coordinates;
        }
        else if (now - comp.LastSeenTarget > comp.LoseTargetDelay && comp.Task != GilbertTask.Cuff)
        {
            // They slipped away during the order: hunt them.
            EnterCombat(ent, target, "noncomply");
            EnterSearch(ent);
            return;
        }

        if (IsArmed(target))
        {
            EnterCombat(ent, target, "noncomply");
            return;
        }

        var dist = DistanceTo(ent, target);

        // Ordered to stop and walking off: warn, then engage (also while he's walking up to cuff them).
        if (comp.SurrenderOrdered && UpdateArrestEscalation(ent, target, now))
            return;

        if (comp.Task == GilbertTask.Cuff)
        {
            UpdateCuffTask(ent, target, now);
            return;
        }

        if (!comp.SurrenderOrdered)
        {
            if (dist > comp.SurrenderRange)
            {
                RegisterMove(ent, Transform(target).Coordinates, comp.SurrenderRange - 0.5f);
                return;
            }

            _steering.Unregister(ent);
            FaceTowards(ent, target);
            comp.SurrenderOrdered = true;
            comp.SurrenderOrderedAt = now;
            comp.SurrenderTargetStart = Transform(target).Coordinates;
            comp.ArrestWarned = false;
            Say(ent, "surrender", 1);
            return;
        }

        FaceTowards(ent, target);

        if (now - comp.SurrenderOrderedAt < comp.SurrenderWait)
            return;

        // Complied: go and cuff them.
        if (!TryFindStored(ent, e => HasComp<HandcuffComponent>(e), out var cuffs))
        {
            // Nothing to cuff with, treat as non-compliant threat.
            EnterCombat(ent, target, "noncomply");
            return;
        }

        StartTask(ent, GilbertTask.Cuff, cuffs, target, TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// Arrest escalation. 3 tiles from the arrest spot: «Почему ты не слушаешь? Работаю!» as a warning.
    /// Still moving 3 s later, or 5 more tiles: he engages (combat). Stopping after the warning makes
    /// the new spot the arrest spot. Returns true if he engaged.
    /// </summary>
    private bool UpdateArrestEscalation(Entity<GilbertAIComponent> ent, EntityUid target, TimeSpan now)
    {
        var comp = ent.Comp;
        var pos = Transform(target).Coordinates;

        if (!comp.ArrestWarned)
        {
            if (_transform.InRange(comp.SurrenderTargetStart, pos, comp.ArrestWarnDistance))
                return false;

            comp.ArrestWarned = true;
            comp.ArrestWarnedAt = now;
            comp.ArrestWarnPosition = pos;
            Say(ent, "noncomply", 1);
            // Give them the full time to stop before he goes for the cuffs.
            comp.SurrenderOrderedAt = now;
            return false;
        }

        if (!_transform.InRange(comp.ArrestWarnPosition, pos, comp.ArrestChaseDistance))
        {
            if (comp.Task == GilbertTask.Cuff)
                AbortTask(ent);
            EnterCombat(ent, target, null);
            return true;
        }

        if (now - comp.ArrestWarnedAt < comp.ArrestStopTime)
            return false;

        var speed = TryComp<PhysicsComponent>(target, out var body) ? body.LinearVelocity.Length() : 0f;
        if (speed >= comp.ArrestStillSpeed)
        {
            if (comp.Task == GilbertTask.Cuff)
                AbortTask(ent);
            EnterCombat(ent, target, null);
            return true;
        }

        // They stopped: this is the new arrest spot; another walk-off gets another warning.
        if (comp.Task != GilbertTask.Cuff)
        {
            comp.SurrenderTargetStart = pos;
            comp.ArrestWarned = false;
        }

        return false;
    }

    private void UpdateCuffTask(Entity<GilbertAIComponent> ent, EntityUid target, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now > comp.TaskDeadline || comp.TaskItem is not { } cuffs || TerminatingOrDeleted(cuffs))
        {
            AbortTask(ent);
            EnterCombat(ent, target, "noncomply");
            return;
        }

        switch (comp.TaskStep)
        {
            case 0:
                // The cuffing do-after only allows 1 tile between the two.
                if (DistanceTo(ent, target) > 0.85f)
                {
                    RegisterMove(ent, Transform(target).Coordinates, 0.6f);
                    return;
                }

                _steering.Unregister(ent);
                if (!TakeIntoFreeHand(ent, cuffs))
                {
                    AbortTask(ent);
                    EnterCombat(ent, target, "noncomply");
                    return;
                }

                // Let him come to a stop first, the do-after breaks on movement.
                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.4);
                return;

            case 1:
                if (now < comp.TaskNextStep)
                    return;

                if (DistanceTo(ent, target) > 0.95f)
                {
                    comp.TaskStep = 0;
                    return;
                }

                FaceTowards(ent, target);
                if (!_cuffable.TryCuffing(ent, target, cuffs))
                {
                    AbortTask(ent);
                    EnterCombat(ent, target, "noncomply");
                    return;
                }

                comp.TaskStep = 2;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.5);
                return;

            case 2:
                if (now < comp.TaskNextStep || HasActiveDoAfter(ent))
                    return;

                if (TryComp<CuffableComponent>(target, out var cuffable) && cuffable.CuffedHandCount > 0)
                {
                    FinishTask(ent);
                    EnterIdle(ent);
                    return;
                }

                // Cuffing got interrupted: they resisted.
                AbortTask(ent);
                EnterCombat(ent, target, "noncomply");
                return;
        }
    }

    #endregion

    #region Combat

    private void UpdateCombat(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now, EntityUid? seen)
    {
        var comp = ent.Comp;
        var target = comp.Target;

        // Target dropped: dead/crit/cuffed -> kill line.
        if (target is { } t && (TerminatingOrDeleted(t) || !IsHostile(ent, t)))
        {
            if (!TerminatingOrDeleted(t) && TryComp<MobStateComponent>(t, out var state) && !_mobState.IsAlive(t, state))
                SayRandom(ent, "kill");

            comp.Target = null;
            target = null;
        }

        if (target == null)
        {
            if (seen != null)
            {
                comp.Target = seen;
                target = seen;
            }
            else
            {
                // v8: finish putting shells in instead of dropping the half-done reload.
                if (comp.Task == GilbertTask.Reload)
                {
                    UpdateTask(ent, now);
                    return;
                }

                AbortTask(ent);
                EnterSearch(ent);
                return;
            }
        }

        var tgt = target.Value;
        var visible = seen == tgt || CanSee(ent, tgt, comp, xform);
        if (seen != null && seen != tgt && comp.Task is not (GilbertTask.Grenade or GilbertTask.PlantC4))
        {
            // The vision code already applied the threat ranking (with hysteresis): go for that one.
            comp.Target = seen;
            tgt = seen.Value;
            visible = true;
        }

        var dist = DistanceTo(ent, tgt);

        if (visible)
        {
            comp.LastSeenTarget = now;
            comp.LastKnownPosition = Transform(tgt).Coordinates;
        }
        // v8: reloading behind cover takes him out of sight on purpose; losing the target must not cancel the
        // reload (that aborted the fill, stuffed the box into his bag and restarted the whole thing: the loop).
        else if (now - comp.LastSeenTarget > comp.LoseTargetDelay && comp.Task != GilbertTask.Reload)
        {
            SayRandom(ent, "lost");
            AbortTask(ent);
            EnterSearch(ent);
            return;
        }

        // A manual job in progress (reload / grenade) takes priority.
        if (comp.Task != GilbertTask.None)
        {
            UpdateTask(ent, now);
            comp.LastTargetDistance = dist;
            return;
        }

        // Dash after a fleeing enemy.
        if (visible && dist > comp.LastTargetDistance + 0.05f && dist >= comp.DashMinRange && dist <= comp.DashMaxRange
            && _abilities.CanDash(ent.Owner))
        {
            _abilities.TryDash(ent.Owner, tgt);
        }

        comp.LastTargetDistance = dist;

        // Weapon choice.
        if (comp.WeaponMode == GilbertWeaponMode.Ranged && dist <= comp.MeleeSwitchRange && now >= comp.NextWeaponSwitch)
            SwitchToMelee(ent);
        else if (comp.WeaponMode == GilbertWeaponMode.Melee && dist >= comp.RangedSwitchRange && now >= comp.NextWeaponSwitch
                 && HasAnyShotgunAmmo(ent))
            SwitchToRanged(ent);

        if (comp.WeaponMode == GilbertWeaponMode.Ranged && !HasAnyShotgunAmmo(ent))
            SwitchToMelee(ent);

        if (comp.WeaponMode == GilbertWeaponMode.Melee)
        {
            var melee = EnsureComp<NPCMeleeCombatComponent>(ent);
            melee.Target = tgt;
            RemComp<NPCRangedCombatComponent>(ent);
            RegisterMove(ent, Transform(tgt).Coordinates, 0.9f);
            return;
        }

        // Ranged.
        RemComp<NPCMeleeCombatComponent>(ent);

        if (!GetGun(ent, out var gun, out var ballistic))
        {
            SwitchToMelee(ent);
            return;
        }

        EnsureWielded(ent, gun);

        // Grenade at a group / someone at medium range.
        if (visible && now >= comp.NextGrenade && dist >= comp.GrenadeMinRange && dist <= comp.GrenadeMaxRange
            && TryFindStored(ent, IsGrenade, out var grenade) && !GrenadeTooClose(ent, grenade, tgt))
        {
            comp.NextGrenade = now + comp.GrenadeCooldown;
            StopCombatComponents(ent);
            StartTask(ent, GilbertTask.Grenade, grenade, tgt, TimeSpan.FromSeconds(4));
            return;
        }

        // Pump-action: rack out the spent shell.
        if (ChamberedIsSpent(ballistic))
        {
            _interaction.UseInHandInteraction(ent, gun, checkCanInteract: false, checkUseDelay: false);
        }

        if (CountLiveShells(ballistic) == 0)
        {
            if (TryFindReloadBox(ent, now, out var box))
            {
                StopCombatComponents(ent);
                BeginReload(ent, box, gun, ballistic);
                PlanReloadCover(ent, xform, tgt, visible, now);
                return;
            }

            // No usable shells (or the last reload failed): fight with the truncheon for now.
            SwitchToMelee(ent);
            return;
        }

        var ranged = EnsureComp<NPCRangedCombatComponent>(ent);
        ranged.Target = tgt;
        if (visible && dist <= comp.RangedKeepDistance)
            _steering.Unregister(ent);
        else
            RegisterMove(ent, comp.LastKnownPosition ?? Transform(tgt).Coordinates, comp.RangedKeepDistance - 0.5f);
    }

    private void StopCombatComponents(EntityUid uid)
    {
        RemComp<NPCRangedCombatComponent>(uid);
        RemComp<NPCMeleeCombatComponent>(uid);
    }

    #endregion

    #region Search

    /// <summary>
    /// Lost them: go to the last known (or heard) position, look around, then check the adjacent rooms,
    /// then keep sweeping wider until <see cref="GilbertAIComponent.SearchTimeout"/> runs out.
    /// </summary>
    private void UpdateSearch(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;

        if (comp.Task != GilbertTask.None)
        {
            UpdateTask(ent, now);
            return;
        }

        // 3 minutes without anybody in sight: back to passive.
        if (now - comp.LastSeenEnemy >= comp.SearchTimeout)
        {
            EnterIdle(ent);
            return;
        }

        // Looking around at the current spot.
        if (comp.SearchLookUntil > now)
        {
            if (now >= comp.NextLookTurn)
            {
                comp.NextLookTurn = now + TimeSpan.FromSeconds(0.4);
                _transform.SetWorldRotation(ent, _transform.GetWorldRotation(xform) + Angle.FromDegrees(90));
            }
            return;
        }

        if (TryComp<NPCSteeringComponent>(ent, out var steering))
        {
            switch (steering.Status)
            {
                case SteeringStatus.InRange:
                    _steering.Unregister(ent);
                    comp.MoveTarget = null;
                    comp.ArrivedAtLastKnown = true;
                    StartSearchLook(ent, now);
                    break;

                case SteeringStatus.NoPath:
                    _steering.Unregister(ent);
                    comp.MoveTarget = null;
                    // A wall or an airlock he can't open between him and where they were? Blow it open.
                    if (!comp.ArrivedAtLastKnown && comp.LastKnownPosition is { } last
                        && TryStartBreach(ent, xform, last, now))
                        return;

                    comp.ArrivedAtLastKnown = true;
                    StartSearchLook(ent, now);
                    break;
            }

            return;
        }

        if (!comp.ArrivedAtLastKnown && comp.LastKnownPosition is { } lastKnown)
        {
            RegisterMove(ent, lastKnown, 0.8f);
            return;
        }

        // Next: an adjacent room, then wider sweeps.
        var center = comp.LastKnownPosition ?? xform.Coordinates;
        var rooms = comp.SearchRoomsChecked < comp.SearchRooms;
        comp.SearchRoomsChecked++;
        if (TryPickSearchPoint(ent, xform, center, rooms ? 4f : 8f, rooms ? 10f : 20f, out var point))
            RegisterMove(ent, point, 1f);
        else
            Wander(ent, center, 5f);
    }

    private void StartSearchLook(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        ent.Comp.SearchLookUntil = now + ent.Comp.SearchLookTime;
        ent.Comp.NextLookTurn = now + TimeSpan.FromSeconds(0.35);
    }

    /// <summary>
    /// A floor spot near <paramref name="center"/>; prefers spots in another room (walls/doors in between)
    /// and areas he hasn't checked yet.
    /// </summary>
    private bool TryPickSearchPoint(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityCoordinates center,
        float minDist, float maxDist, out EntityCoordinates point)
    {
        point = default;
        var gridUid = _transform.GetGrid(center);
        if (gridUid is not { } g || !TryComp<Robust.Shared.Map.Components.MapGridComponent>(g, out var grid))
            return false;

        var origin = _transform.WithEntityId(center, g).Position;
        var centerMap = _transform.ToMapCoordinates(center);
        var bestScore = float.MinValue;
        var found = false;

        for (var i = 0; i < 24; i++)
        {
            var local = origin + _random.NextAngle().ToVec() * _random.NextFloat(minDist, maxDist);
            var tile = _map.TileIndicesFor(g, grid, new EntityCoordinates(g, local));
            if (!_map.TryGetTileRef(g, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty
                || _turf.IsSpace(tileRef) || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                continue;

            var candidate = _map.GridTileToLocal(g, grid, tile);
            var score = _random.NextFloat(0f, 5f);
            if (!IsVisited(ent, candidate))
                score += 20f;

            // Something solid between the last known spot and the candidate: it's a neighbouring room.
            var candMap = _transform.ToMapCoordinates(candidate);
            var delta = candMap.Position - centerMap.Position;
            if (delta.LengthSquared() > 0.25f && candMap.MapId == centerMap.MapId)
            {
                var ray = new CollisionRay(centerMap.Position, Vector2.Normalize(delta), (int) CollisionGroup.Impassable);
                if (_physics.IntersectRay(centerMap.MapId, ray, delta.Length(), ent.Owner).Any())
                    score += 30f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                point = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// The way to <paramref name="target"/> is blocked by a wall or by an airlock he can't open (bolted, welded,
    /// unpowered): plant C4 on it, back off, then go on to the target.
    /// </summary>
    private bool TryStartBreach(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityCoordinates target, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now < comp.NextC4 || comp.Task != GilbertTask.None)
            return false;

        if (!TryFindBlocker(ent, xform, target, out var blocker) || !TryFindStored(ent, IsC4, out var c4))
            return false;

        comp.NextC4 = now + comp.C4Cooldown;
        comp.BreachTarget = target;
        StartTask(ent, GilbertTask.PlantC4, c4, blocker, TimeSpan.FromSeconds(30));
        return true;
    }

    /// <summary>
    /// First solid thing on the straight line to <paramref name="target"/>, if it's a wall or an airlock he can't open.
    /// </summary>
    public bool TryFindBlocker(EntityUid uid, TransformComponent xform, EntityCoordinates target, out EntityUid blocker)
    {
        blocker = default;
        var from = _transform.GetMapCoordinates(xform);
        var to = _transform.ToMapCoordinates(target);
        if (from.MapId != to.MapId)
            return false;

        var delta = to.Position - from.Position;
        var length = delta.Length();
        if (length < 0.5f)
            return false;

        var ray = new CollisionRay(from.Position, delta / length, (int) CollisionGroup.Impassable);
        foreach (var hit in _physics.IntersectRay(from.MapId, ray, MathF.Min(length, 12f), uid))
        {
            if (_tag.HasTag(hit.HitEntity, WallTag))
            {
                blocker = hit.HitEntity;
                return true;
            }

            if (TryComp<DoorComponent>(hit.HitEntity, out var door) && door.State != DoorState.Open
                && !_door.CanOpen(hit.HitEntity, door, uid))
            {
                blocker = hit.HitEntity;
                return true;
            }

            return false;
        }

        return false;
    }

    #endregion

    #region Healing

    private bool TryStartHealing(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.Task != GilbertTask.None || now < comp.NextHealAttempt || now - comp.LastDamaged < comp.HealSafeDelay)
            return false;

        if (!TryComp<DamageableComponent>(ent, out var damageable) || damageable.TotalDamage < comp.HealStartDamage)
            return false;

        comp.HealBlacklist.RemoveWhere(e => TerminatingOrDeleted(e));
        if (!TryPickHealingItem(ent, damageable, out _))
        {
            // Out of medicine: he just can't heal. Check again later in case he picks some up.
            comp.NextHealAttempt = now + TimeSpan.FromSeconds(30);
            return false;
        }

        StopCombatComponents(ent);
        _steering.Unregister(ent);
        SetState(ent, GilbertAIState.Heal);
        return true;
    }

    private void UpdateHealing(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        if (!TryComp<DamageableComponent>(ent, out var damageable))
        {
            EnterIdle(ent);
            return;
        }

        if (comp.Task == GilbertTask.HealItem)
        {
            UpdateTask(ent, now);
            return;
        }

        if (damageable.TotalDamage <= comp.HealStopDamage || !TryPickHealingItem(ent, damageable, out var item))
        {
            comp.NextHealAttempt = now + TimeSpan.FromSeconds(30);
            FinishHealing(ent);
            return;
        }

        StartTask(ent, GilbertTask.HealItem, item, ent.Owner, TimeSpan.FromSeconds(40));
    }

    private void FinishHealing(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.Task == GilbertTask.HealItem)
            FinishTask(ent);

        // Back to whatever he was doing: searching if the fight was recent, otherwise passive.
        if (_timing.CurTime - comp.LastSeenEnemy < comp.SearchTimeout)
        {
            SetState(ent, GilbertAIState.Search);
            comp.ArrivedAtLastKnown = true;
            comp.NextWander = _timing.CurTime;
        }
        else
        {
            EnterIdle(ent);
        }
    }

    private void AbortHealing(Entity<GilbertAIComponent> ent)
    {
        if (ent.Comp.Task == GilbertTask.HealItem)
            AbortTask(ent);

        if (ent.Comp.State == GilbertAIState.Heal)
        {
            SetState(ent, GilbertAIState.Search);
            ent.Comp.ArrivedAtLastKnown = true;
        }
    }

    /// <summary>
    /// Picks the stored medical item that treats the damage he has the most of.
    /// </summary>
    public bool TryPickHealingItem(Entity<GilbertAIComponent> ent, DamageableComponent damageable, out EntityUid item)
    {
        item = default;
        var bestScore = FixedPoint2.Zero;

        foreach (var candidate in EnumerateStored(ent))
        {
            var score = ScoreHealingItem(ent.Comp, candidate, damageable);
            if (score > bestScore)
            {
                bestScore = score;
                item = candidate;
            }
        }

        return bestScore > 0;
    }

    /// <summary>
    /// What a use of this item should consume: stack count, injector volume, or 1 for single-use items.
    /// Null for items that would not be consumed at all (reusable tools), which he must never heal with.
    /// </summary>
    public float? HealCharges(EntityUid item)
    {
        if (TerminatingOrDeleted(item))
            return 0f;

        if (TryComp<StackComponent>(item, out var stack))
            return stack.Count;

        if (TryComp<HyposprayComponent>(item, out var hypo))
            return _solutions.TryGetSolution(item, hypo.SolutionName, out _, out var sol) ? sol.Volume.Float() : 0f;

        // A HealingComponent without a stack is deleted on use; surgical stitches & co. are not.
        if (HasComp<HealingComponent>(item) && !HasComp<StitchesComponent>(item) && !HasComp<SurgeryToolComponent>(item))
            return 1f;

        return null;
    }

    /// <summary>True if using this item would actually spend something.</summary>
    public bool IsConsumableHealItem(GilbertAIComponent comp, EntityUid item)
    {
        return !comp.HealBlacklist.Contains(item) && HealCharges(item) is > 0f;
    }

    private FixedPoint2 ScoreHealingItem(GilbertAIComponent comp, EntityUid item, DamageableComponent damageable)
    {
        if (!IsConsumableHealItem(comp, item))
            return FixedPoint2.Zero;

        // Topicals: bruise packs, sutures, ointment, mesh...
        if (TryComp<HealingComponent>(item, out var healing))
        {
            if (healing.DamageContainers is not null
                && damageable.DamageContainerID is { } container
                && !healing.DamageContainers.Contains(container))
                return FixedPoint2.Zero;

            var score = FixedPoint2.Zero;
            foreach (var (type, amount) in healing.Damage.DamageDict)
            {
                if (amount >= 0 || !damageable.Damage.DamageDict.TryGetValue(type, out var have) || have <= 0)
                    continue;
                score += FixedPoint2.Min(have, -amount);
            }

            return score;
        }

        // Autoinjectors: matched by the damage group they treat; only if they still hold something.
        if (HasComp<HyposprayComponent>(item)
            && MetaData(item).EntityPrototype?.ID is { } protoId
            && comp.InjectorGroups.TryGetValue(protoId, out var group)
            && _proto.TryIndex<DamageGroupPrototype>(group, out var groupProto)
            && TryComp<HyposprayComponent>(item, out var hypo)
            && _solutions.TryGetSolution(item, hypo.SolutionName, out _, out var solution)
            && solution.Volume > 0)
        {
            var score = FixedPoint2.Zero;
            foreach (var type in groupProto.DamageTypes)
            {
                if (damageable.Damage.DamageDict.TryGetValue(type, out var have))
                    score += have;
            }

            // A pen is a big single dose: worth it only for serious damage of its kind.
            return score >= 40 ? score : FixedPoint2.Zero;
        }

        return FixedPoint2.Zero;
    }

    #endregion

    #region Tasks

    private void StartTask(Entity<GilbertAIComponent> ent, GilbertTask task, EntityUid item, EntityUid? target, TimeSpan timeout)
    {
        var comp = ent.Comp;
        comp.Task = task;
        comp.TaskStep = 0;
        comp.TaskItem = item;
        comp.TaskTarget = target;
        comp.TaskNextStep = _timing.CurTime;
        comp.TaskDeadline = _timing.CurTime + timeout;
    }

    private void FinishTask(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.TaskItem is { } item && !TerminatingOrDeleted(item) && _hands.IsHolding(ent.Owner, item))
        {
            if (comp.Task == GilbertTask.Reload)
                ReturnReloadBox(ent, item);
            else
                StowOrDrop(ent, item);
        }

        comp.Task = GilbertTask.None;
        comp.TaskItem = null;
        comp.TaskTarget = null;
        comp.TaskStep = 0;
    }

    private void AbortTask(Entity<GilbertAIComponent> ent)
    {
        if (ent.Comp.Task is GilbertTask.Pickup && ent.Comp.TaskItem is { } loot)
            IgnoreForScavenge(ent.Comp, loot);

        if (ent.Comp.Task is GilbertTask.Loot)
        {
            // Whatever he was in the middle of taking off goes into his bag; the victim keeps their cooldown.
            if (ent.Comp.LootItem is { } held && !TerminatingOrDeleted(held) && _hands.IsHolding(ent.Owner, held))
                StowOrDrop(ent, held);
            ent.Comp.LootItem = null;
            ent.Comp.LootBag = null;
            ent.Comp.TaskItem = null;
        }

        CancelDoAfters(ent);
        FinishTask(ent);
    }

    private void UpdateTask(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now < comp.TaskNextStep)
            return;

        // Waiting out his own planted charge: the charge is gone once it blows, that's the point.
        if (comp.Task == GilbertTask.PlantC4 && comp.TaskStep == 2)
        {
            UpdateC4Wait(ent, now);
            return;
        }

        if (now > comp.TaskDeadline || comp.TaskItem is not { } item || TerminatingOrDeleted(item) && comp.Task != GilbertTask.HealItem)
        {
            AbortTask(ent);
            return;
        }

        switch (comp.Task)
        {
            case GilbertTask.Reload:
                UpdateReload(ent, item, now);
                break;
            case GilbertTask.Grenade:
                UpdateGrenade(ent, item, now);
                break;
            case GilbertTask.PlantC4:
                UpdateC4(ent, item, now);
                break;
            case GilbertTask.HealItem:
                UpdateHealItem(ent, item, now);
                break;
            case GilbertTask.Pickup:
                UpdatePickup(ent, item, now);
                break;
            case GilbertTask.Loot:
                UpdateLoot(ent, item, now);
                break;
        }
    }

    private void UpdateReload(Entity<GilbertAIComponent> ent, EntityUid box, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.TaskTarget is not { } gun || !TryComp<BallisticAmmoProviderComponent>(gun, out var ballistic))
        {
            AbortTask(ent);
            return;
        }

        switch (comp.TaskStep)
        {
            case 0:
                // Enemy close by: get behind something first.
                if (MoveToCover(ent, now))
                {
                    comp.TaskNextStep = now + TimeSpan.FromSeconds(0.25);
                    return;
                }

                // Eject every spent shell first, then free a hand for the ammo box.
                if (ballistic.Entities.Count > 0 && ChamberedIsSpent(ballistic))
                {
                    _interaction.UseInHandInteraction(ent, gun, checkCanInteract: false, checkUseDelay: false);
                    comp.TaskNextStep = now + TimeSpan.FromSeconds(0.3);
                    return;
                }

                // v8: stand still while loading (the cover walk is over).
                _steering.Unregister(ent);
                comp.CoverPoint = null;

                if (TryComp<WieldableComponent>(gun, out var wield) && wield.Wielded)
                    _wieldable.TryUnwield(gun, wield, ent, force: true);

                if (!TakeIntoFreeHand(ent, box))
                {
                    FailReload(ent, box, now);
                    return;
                }

                // Box on gun: the normal continuous-fill do-after.
                comp.ReloadShellsBefore = CountLiveShells(ballistic);
                _interaction.InteractUsing(ent, box, gun, Transform(gun).Coordinates, checkCanInteract: false);
                if (!HasActiveDoAfter(ent) && CountLiveShells(ballistic) <= comp.ReloadShellsBefore)
                {
                    // Gun full of something else / box refused: no point standing there.
                    FailReload(ent, box, now);
                    return;
                }

                comp.ReloadLastGain = now;
                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.25);
                return;

            case 1:
            {
                var live = CountLiveShells(ballistic);
                if (live > comp.ReloadShellsBefore)
                {
                    comp.ReloadShellsBefore = live;
                    comp.ReloadLastGain = now;

                    // The line only once shells actually go in, and not every reload.
                    if (!comp.ReloadAnnounced)
                    {
                        comp.ReloadAnnounced = true;
                        if (now >= comp.NextReloadLine)
                        {
                            comp.NextReloadLine = now + comp.ReloadLineCooldown;
                            SayRandom(ent, "reload");
                        }
                    }
                }

                if (HasActiveDoAfter(ent))
                {
                    // The fill keeps repeating without putting anything in (wrong shells bounce back): stop it.
                    if (now - comp.ReloadLastGain <= comp.ReloadNoGainTimeout)
                    {
                        comp.TaskNextStep = now + TimeSpan.FromSeconds(0.25);
                        return;
                    }

                    CancelDoAfters(ent);
                }

                if (live == 0)
                {
                    FailReload(ent, box, now);
                    return;
                }

                // Box is done with; back into the webbing if it still has shells, tossed if empty.
                ReturnReloadBox(ent, box);
                comp.TaskItem = null;
                comp.ReloadFailures = 0;
                comp.ReloadsSucceeded++;
                _interaction.UseInHandInteraction(ent, gun, checkCanInteract: false, checkUseDelay: false); // chamber a round
                EnsureWielded(ent, gun);
                FinishTask(ent);
                return;
            }
        }
    }

    /// <summary>v8: a usable shell box anywhere on him (webbing first, pockets, bag), skipping ones that failed.</summary>
    private bool TryFindReloadBox(Entity<GilbertAIComponent> ent, TimeSpan now, out EntityUid box)
    {
        box = default;
        var comp = ent.Comp;
        if (now < comp.NextReloadAttempt)
            return false;

        foreach (var (bad, until) in comp.BadReloadBoxes.ToList())
        {
            if (until <= now || TerminatingOrDeleted(bad))
                comp.BadReloadBoxes.Remove(bad);
        }

        return TryFindStored(ent, e => IsShotgunBox(e) && !comp.BadReloadBoxes.ContainsKey(e), out box);
    }

    private void BeginReload(Entity<GilbertAIComponent> ent, EntityUid box, EntityUid gun, BallisticAmmoProviderComponent ballistic)
    {
        var comp = ent.Comp;
        comp.ReloadBoxOrigin = null;
        comp.ReloadBoxSlot = null;
        if (_container.TryGetContainingContainer((box, null, null), out var container))
        {
            comp.ReloadBoxOrigin = container.Owner;
            if (container.Owner == ent.Owner)
                comp.ReloadBoxSlot = container.ID;
        }

        comp.ReloadAnnounced = false;
        comp.ReloadShellsBefore = CountLiveShells(ballistic);
        comp.ReloadsStarted++;
        StartTask(ent, GilbertTask.Reload, box, gun, TimeSpan.FromSeconds(20));
    }

    /// <summary>Nothing went in: remember the box as bad, put it back, fight in melee for a while.</summary>
    private void FailReload(Entity<GilbertAIComponent> ent, EntityUid box, TimeSpan now)
    {
        var comp = ent.Comp;
        CancelDoAfters(ent);
        comp.ReloadFailures++;
        comp.NextReloadAttempt = now + comp.ReloadRetryCooldown;
        if (!TerminatingOrDeleted(box))
        {
            comp.BadReloadBoxes[box] = now + comp.BadBoxTime;
            if (_hands.IsHolding(ent.Owner, box))
                ReturnReloadBox(ent, box);
        }

        comp.TaskItem = null;
        FinishTask(ent);
        if (comp.State == GilbertAIState.Combat)
            SwitchToMelee(ent);
    }

    /// <summary>v8: the box goes back into the webbing / pocket / bag it came out of (not always the bag).</summary>
    private void ReturnReloadBox(Entity<GilbertAIComponent> ent, EntityUid box)
    {
        var comp = ent.Comp;
        if (TerminatingOrDeleted(box) || !_hands.IsHolding(ent.Owner, box))
            return;

        if (TryComp<BallisticAmmoProviderComponent>(box, out var boxAmmo) && boxAmmo.Count == 0)
        {
            _hands.TryDrop(ent.Owner, box);
            return;
        }

        if (comp.ReloadBoxSlot is { } slot && comp.ReloadBoxOrigin == ent.Owner
            && _inventory.TryEquip(ent, box, slot, silent: true, force: true))
            return;

        if (comp.ReloadBoxOrigin is { } origin && origin != ent.Owner && !TerminatingOrDeleted(origin)
            && TryComp<StorageComponent>(origin, out var storage)
            && _storage.Insert(origin, box, out _, user: ent, storageComp: storage, playSound: false))
            return;

        StowOrDrop(ent, box);
    }

    private void UpdateGrenade(Entity<GilbertAIComponent> ent, EntityUid grenade, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.TaskTarget is not { } target || TerminatingOrDeleted(target))
        {
            AbortTask(ent);
            return;
        }

        switch (comp.TaskStep)
        {
            case 0:
                if (GetGun(ent, out var gun, out _) && TryComp<WieldableComponent>(gun, out var wield) && wield.Wielded)
                    _wieldable.TryUnwield(gun, wield, ent, force: true);

                if (!TakeIntoFreeHand(ent, grenade))
                {
                    AbortTask(ent);
                    return;
                }

                // Pull the pin.
                _interaction.UseInHandInteraction(ent, grenade, checkCanInteract: false, checkUseDelay: false);
                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.6);
                return;

            case 1:
                FaceTowards(ent, target);
                _hands.TrySelect(ent, grenade);
                // The target closed in while he pulled the pin: throw it as far as he can past them instead.
                var throwAt = Transform(target).Coordinates;
                if (GrenadeTooClose(ent, grenade, target))
                {
                    var dir = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(ent);
                    if (dir.LengthSquared() < 0.01f)
                        dir = _transform.GetWorldRotation(ent).ToWorldVec();
                    throwAt = Transform(ent).Coordinates.Offset(Vector2.Normalize(dir) * comp.GrenadeMaxRange);
                }
                _hands.ThrowHeldItem(ent, throwAt);
                TrackOwnCharge(ent, grenade);
                comp.TaskItem = null;
                if (GetGun(ent, out var gun2, out _))
                    EnsureWielded(ent, gun2);
                FinishTask(ent);
                return;
        }
    }

    private void UpdateC4(Entity<GilbertAIComponent> ent, EntityUid c4, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.TaskTarget is not { } wall || TerminatingOrDeleted(wall))
        {
            AbortTask(ent);
            return;
        }

        switch (comp.TaskStep)
        {
            case 0:
                if (!_interaction.InRangeUnobstructed(ent.Owner, wall, 1.3f))
                {
                    RegisterMove(ent, Transform(wall).Coordinates, 1.1f);
                    return;
                }

                _steering.Unregister(ent);
                if (GetGun(ent, out var gun, out _) && TryComp<WieldableComponent>(gun, out var wield) && wield.Wielded)
                    _wieldable.TryUnwield(gun, wield, ent, force: true);

                if (!TakeIntoFreeHand(ent, c4))
                {
                    AbortTask(ent);
                    return;
                }

                // Timer long enough to walk clear of the blast, then stick it to the wall
                // (sticky do-after; the timer starts once it's stuck).
                if (TryComp<OnUseTimerTriggerComponent>(c4, out var c4Timer))
                    c4Timer.Delay = PlanChargeDelay(ent, c4);
                _interaction.InteractUsing(ent, c4, wall, Transform(wall).Coordinates, checkCanInteract: false);
                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.5);
                return;

            case 1:
                if (HasActiveDoAfter(ent))
                    return;

                if (GetGun(ent, out var gun2, out _))
                    EnsureWielded(ent, gun2);

                if (_hands.IsHolding(ent.Owner, c4))
                {
                    // Didn't stick: put it away.
                    AbortTask(ent);
                    return;
                }

                // Armed: from now on it's a known danger zone and the danger logic walks him clear of it
                // (behind a corner if there is one) before the timer runs out.
                TrackOwnCharge(ent, c4);
                var delay = TryComp<ActiveTimerTriggerComponent>(c4, out var active) ? active.TimeRemaining : 10f;
                comp.TaskStep = 2;
                comp.TaskNextStep = now;
                comp.TaskDeadline = now + TimeSpan.FromSeconds(delay + 5f);
                return;
        }
    }

    /// <summary>Planted charge armed: stay clear until it blows (or the deadline passes), then go through.</summary>
    private void UpdateC4Wait(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.TaskItem is { } c4 && !TerminatingOrDeleted(c4) && HasComp<ActiveTimerTriggerComponent>(c4)
            && now <= comp.TaskDeadline)
        {
            comp.TaskNextStep = now + TimeSpan.FromSeconds(0.25);
            return;
        }

        // Charge went off (or not); go through.
        comp.TaskItem = null;
        FinishTask(ent);
        comp.ArrivedAtLastKnown = false;
        if (comp.BreachTarget is { } breach)
            RegisterMove(ent, breach, 0.8f);
        else if (comp.LastKnownPosition is { } last)
            RegisterMove(ent, last, 0.8f);
        comp.BreachTarget = null;
    }

    private void UpdateHealItem(Entity<GilbertAIComponent> ent, EntityUid item, TimeSpan now)
    {
        var comp = ent.Comp;
        switch (comp.TaskStep)
        {
            case 0:
                if (TerminatingOrDeleted(item))
                {
                    FinishTask(ent);
                    return;
                }

                if (GetGun(ent, out var gun, out _) && TryComp<WieldableComponent>(gun, out var wield) && wield.Wielded)
                    _wieldable.TryUnwield(gun, wield, ent, force: true);

                if (!TakeIntoFreeHand(ent, item))
                {
                    // Can't get it out: skip healing for now.
                    comp.NextHealAttempt = now + TimeSpan.FromSeconds(30);
                    FinishTask(ent);
                    FinishHealing(ent);
                    return;
                }

                // Use it on himself like a player would (normal do-after / injection).
                comp.HealChargesBefore = HealCharges(item) ?? 0f;
                comp.HealDamageBefore = CompOrNull<DamageableComponent>(ent)?.TotalDamage ?? FixedPoint2.Zero;
                _interaction.UseInHandInteraction(ent, item, checkCanInteract: false, checkUseDelay: false);
                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.5);
                return;

            case 1:
                if (HasActiveDoAfter(ent))
                    return;

                CheckHealConsumed(ent, item);

                if (!TerminatingOrDeleted(item))
                {
                    // Spent pens are thrown away; the rest of a stack goes back in the bag.
                    if (TryComp<HyposprayComponent>(item, out var hypo)
                        && _solutions.TryGetSolution(item, hypo.SolutionName, out _, out var sol) && sol.Volume <= 0)
                        _hands.TryDrop(ent.Owner, item);
                    else
                        StowOrDrop(ent, item);
                }

                comp.TaskItem = null;
                FinishTask(ent);
                if (GetGun(ent, out var gun2, out _))
                    EnsureWielded(ent, gun2);
                return;
        }
    }

    /// <summary>
    /// Every use must spend a charge. Healed without spending anything: that item is reusable, never again.
    /// Nothing spent and nothing healed twice in a row: give up on it as well.
    /// </summary>
    private void CheckHealConsumed(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        var comp = ent.Comp;
        var after = HealCharges(item) ?? comp.HealChargesBefore;
        if (after < comp.HealChargesBefore)
        {
            comp.HealFailures = 0;
            return;
        }

        var damage = CompOrNull<DamageableComponent>(ent)?.TotalDamage ?? FixedPoint2.Zero;
        if (damage < comp.HealDamageBefore || ++comp.HealFailures >= 2)
        {
            comp.HealBlacklist.Add(item);
            comp.HealFailures = 0;
        }
    }

    #endregion

    #region Equipment helpers

    private bool GetGun(EntityUid uid, out EntityUid gun, out BallisticAmmoProviderComponent ballistic)
    {
        gun = default;
        ballistic = default!;
        foreach (var held in _hands.EnumerateHeld(uid))
        {
            if (HasComp<GunComponent>(held) && TryComp<BallisticAmmoProviderComponent>(held, out var b))
            {
                gun = held;
                ballistic = b;
                return true;
            }
        }

        return false;
    }

    private void EnsureWielded(EntityUid uid, EntityUid gun)
    {
        if (TryComp<WieldableComponent>(gun, out var wield) && !wield.Wielded)
            _wieldable.TryWield(gun, wield, uid, showMessage: false);

        _hands.TrySelect(uid, gun);
    }

    private bool ChamberedIsSpent(BallisticAmmoProviderComponent ballistic)
    {
        if (ballistic.Entities.Count == 0)
            return false;

        return TryComp<CartridgeAmmoComponent>(ballistic.Entities[^1], out var cart) && cart.Spent;
    }

    public int CountLiveShells(BallisticAmmoProviderComponent ballistic)
    {
        var count = ballistic.UnspawnedCount;
        foreach (var ent in ballistic.Entities)
        {
            if (!TryComp<CartridgeAmmoComponent>(ent, out var cart) || !cart.Spent)
                count++;
        }

        return count;
    }

    private bool HasAnyShotgunAmmo(EntityUid uid)
    {
        if (FindShotgun(uid) is { } gun && TryComp<BallisticAmmoProviderComponent>(gun, out var b) && CountLiveShells(b) > 0)
            return true;

        if (TryComp<GilbertAIComponent>(uid, out var comp))
            return TryFindReloadBox((uid, comp), _timing.CurTime, out _);

        return TryFindStored(uid, IsShotgunBox, out _);
    }

    private EntityUid? FindShotgun(EntityUid uid)
    {
        if (GetGun(uid, out var gun, out _))
            return gun;

        if (_inventory.TryGetSlotEntity(uid, SuitStorageSlot, out var stored) && HasComp<GunComponent>(stored))
            return stored;

        return null;
    }

    private bool IsShotgunBox(EntityUid uid)
    {
        return !HasComp<GunComponent>(uid)
               && TryComp<BallisticAmmoProviderComponent>(uid, out var box) && box.Count > 0
               && MetaData(uid).EntityPrototype?.ID.StartsWith("MagazineBoxShotgun") == true;
    }

    /// <summary>Any hand-thrown timed grenade (frag, flash, smoke, EMP, cluster, minibomb...), not C4.</summary>
    public bool IsGrenade(EntityUid uid)
    {
        if (!HasComp<OnUseTimerTriggerComponent>(uid) || HasComp<StickyComponent>(uid) || HasComp<GunComponent>(uid))
            return false;

        var id = MetaData(uid).EntityPrototype?.ID;
        if (id == null)
            return false;

        return id.Contains("Grenade", StringComparison.OrdinalIgnoreCase)
               || id.Contains("Bang", StringComparison.OrdinalIgnoreCase)
               || id.Contains("Bomb", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsC4(EntityUid uid)
    {
        return HasComp<StickyComponent>(uid) && HasComp<OnUseTimerTriggerComponent>(uid);
    }

    private bool IsArmed(EntityUid uid)
    {
        foreach (var held in _hands.EnumerateHeld(uid))
        {
            if (HasComp<GunComponent>(held))
                return true;
        }

        return false;
    }

    private void SwitchToMelee(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.WeaponMode == GilbertWeaponMode.Melee)
            return;

        comp.WeaponMode = GilbertWeaponMode.Melee;
        comp.NextWeaponSwitch = _timing.CurTime + comp.WeaponSwitchCooldown;
        RemComp<NPCRangedCombatComponent>(ent);

        // Shotgun goes on the vest's suit storage, or in the bag.
        if (GetGun(ent, out var gun, out _))
        {
            if (TryComp<WieldableComponent>(gun, out var wield) && wield.Wielded)
                _wieldable.TryUnwield(gun, wield, ent, force: true);

            if (!_inventory.TryEquip(ent, gun, SuitStorageSlot, silent: true, force: true))
                StowOrDrop(ent, gun);
        }

        if (TryFindStored(ent, e => IsTruncheon(e), out var truncheon))
            TakeIntoFreeHand(ent, truncheon);

        if (TryFindStored(ent, e => IsShield(e), out var shield) && TakeIntoFreeHand(ent, shield))
            _toggle.TryActivate(shield, ent, predicted: false);

        if (truncheon != default && _hands.IsHolding(ent.Owner, truncheon))
            _hands.TrySelect(ent, truncheon);
    }

    // His own crackling variants (v5), plus the stock ones in case an admin hands him those.
    private static readonly HashSet<string> Truncheons = new() { "TruncheonShogkk", "Truncheon" };
    private static readonly HashSet<string> Shields = new() { "TelescopicShieldShogkk", "TelescopicShield" };

    private bool IsTruncheon(EntityUid uid) => MetaData(uid).EntityPrototype?.ID is { } id && Truncheons.Contains(id);

    private bool IsShield(EntityUid uid) => MetaData(uid).EntityPrototype?.ID is { } id && Shields.Contains(id);

    private void SwitchToRanged(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.WeaponMode == GilbertWeaponMode.Ranged)
            return;

        comp.WeaponMode = GilbertWeaponMode.Ranged;
        comp.NextWeaponSwitch = _timing.CurTime + comp.WeaponSwitchCooldown;
        RemComp<NPCMeleeCombatComponent>(ent);

        foreach (var held in _hands.EnumerateHeld(ent.Owner).ToList())
        {
            if (HasComp<GunComponent>(held))
                continue;

            if (IsShield(held))
                _toggle.TryDeactivate(held, ent, predicted: false);

            StowOrDrop(ent, held);
        }

        if (_inventory.TryUnequip(ent, SuitStorageSlot, out var gun, silent: true, force: true))
            _hands.TryPickupAnyHand(ent, gun.Value);
        else if (TryFindStored(ent, e => HasComp<GunComponent>(e), out var stored))
            TakeIntoFreeHand(ent, stored);

        if (GetGun(ent, out var g, out _))
            EnsureWielded(ent, g);
    }

    /// <summary>
    /// Takes an item out of his storage into a free hand (unwielding the shotgun if he must).
    /// </summary>
    private bool TakeIntoFreeHand(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        if (_hands.IsHolding(ent.Owner, item))
        {
            _hands.TrySelect(ent, item);
            return true;
        }

        if (!_hands.TryGetEmptyHand(ent.Owner, out var hand))
        {
            if (GetGun(ent, out var gun, out _) && TryComp<WieldableComponent>(gun, out var wield) && wield.Wielded)
                _wieldable.TryUnwield(gun, wield, ent, force: true);

            if (!_hands.TryGetEmptyHand(ent.Owner, out hand))
                return false;
        }

        // v8: pockets are inventory slots, unequip properly first.
        if (_container.TryGetContainingContainer((item, null, null), out var slotContainer) && slotContainer.Owner == ent.Owner
            && _inventory.HasSlot(ent, slotContainer.ID))
        {
            if (!_inventory.TryUnequip(ent, ent, slotContainer.ID, silent: true, force: true))
                return false;
        }

        if (!_hands.TryPickup(ent, item, hand, checkActionBlocker: false, animate: false))
            return false;

        _hands.SetActiveHand(ent.Owner, hand);
        return true;
    }

    private static readonly string[] StowBagFirst = { "back", "belt" };
    private static readonly string[] StowBeltFirst = { "belt", "back" };

    private void StowOrDrop(EntityUid uid, EntityUid item)
    {
        // v8: shell boxes live in the webbing (РПС), everything else goes in the bag first.
        var order = MetaData(item).EntityPrototype?.ID.StartsWith("MagazineBoxShotgun") == true ? StowBeltFirst : StowBagFirst;
        foreach (var slot in order)
        {
            if (_inventory.TryGetSlotEntity(uid, slot, out var storage) && TryComp<StorageComponent>(storage, out var storageComp)
                && _storage.Insert(storage.Value, item, out _, user: uid, storageComp: storageComp, playSound: false))
                return;
        }

        _hands.TryDrop(uid, item);
    }

    /// <summary>
    /// Every item in his belt and backpack, including the contents of containers inside them (medkits).
    /// </summary>
    public IEnumerable<EntityUid> EnumerateStored(EntityUid uid)
    {
        // v8: pockets too (and what's inside a pocketed container), webbing first, bag last.
        foreach (var slot in StoredSlots)
        {
            if (!_inventory.TryGetSlotEntity(uid, slot, out var storage))
                continue;

            if (slot.StartsWith("pocket"))
                yield return storage.Value;

            foreach (var item in EnumerateStorageRecursive(storage.Value, 0))
                yield return item;
        }
    }

    private static readonly string[] StoredSlots = { "belt", "pocket1", "pocket2", "back" };

    private IEnumerable<EntityUid> EnumerateStorageRecursive(EntityUid storage, int depth)
    {
        if (depth > 2 || !TryComp<StorageComponent>(storage, out var comp))
            yield break;

        foreach (var item in comp.Container.ContainedEntities.ToList())
        {
            yield return item;
            foreach (var inner in EnumerateStorageRecursive(item, depth + 1))
                yield return inner;
        }
    }

    private bool TryFindStored(EntityUid uid, Func<EntityUid, bool> predicate, out EntityUid found)
    {
        foreach (var item in EnumerateStored(uid))
        {
            if (predicate(item))
            {
                found = item;
                return true;
            }
        }

        found = default;
        return false;
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

    private void CancelDoAfters(EntityUid uid)
    {
        if (!TryComp<DoAfterComponent>(uid, out var doAfter))
            return;

        foreach (var (id, da) in doAfter.DoAfters.ToList())
        {
            if (!da.Cancelled && !da.Completed)
                _doAfter.Cancel(uid, id, doAfter);
        }
    }

    private void FaceTowards(EntityUid uid, EntityUid target)
    {
        var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(uid);
        if (delta.LengthSquared() > 0.01f)
            _transform.SetWorldRotation(uid, delta.ToWorldAngle());
    }

    #endregion

    #region Lines

    private void SayRandom(Entity<GilbertAIComponent> ent, string category)
    {
        if (!ent.Comp.LineCounts.TryGetValue(category, out var count) || count <= 0)
            return;

        var index = _random.Next(1, count + 1);
        // Avoid repeating the exact same line twice in a row.
        if (count > 1 && ent.Comp.LastLine == $"{category}-{index}")
            index = index % count + 1;

        Say(ent, category, index);
    }

    /// <summary>
    /// Plays the recorded line and shows the matching chat text.
    /// </summary>
    public void Say(Entity<GilbertAIComponent> ent, string category, int index)
    {
        // Berserk: only screams, no words.
        if (InBerserk(ent))
            return;

        ent.Comp.LastLine = $"{category}-{index}";
        ent.Comp.SaidCounts[category] = ent.Comp.SaidCounts.GetValueOrDefault(category) + 1;
        _audio.PlayPvs(new SoundPathSpecifier($"{ent.Comp.AudioPath}/gilbert_{category}_{index}.ogg"), ent,
            AudioParams.Default.WithMaxDistance(14f));
        _chat.TrySendInGameICMessage(ent, Loc.GetString($"gilbert-line-{category}-{index}"), InGameICChatType.Speak,
            ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false, ignoreActionBlocker: true);
    }

    #endregion

    #region Test helpers

    /// <summary>Forces the AI into combat with a target. Used by tests and admin tooling.</summary>
    public void ForceCombat(EntityUid uid, EntityUid target)
    {
        if (TryComp<GilbertAIComponent>(uid, out var comp))
            EnterCombat((uid, comp), target, null);
    }

    #endregion
}
