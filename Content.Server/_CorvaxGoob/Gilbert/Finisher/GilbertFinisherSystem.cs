using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared._CorvaxGoob.Gilbert.Finisher;
using Content.Server.Decals;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Systems;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Buckle.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Maps;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Standing;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Gilbert.Finisher;

/// <summary>
/// Server side of the finisher cutscene: picks the direction, pulls the victim in front of the attacker, locks both,
/// walks them along the prototype's <c>move</c> tracks, plays the sounds and cleans everything up at the end.
/// Sprite-layer animation is done by the client (GilbertFinisherVisualsSystem) from the networked start time.
/// </summary>
public sealed class GilbertFinisherSystem : SharedGilbertFinisherSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly DecalSystem _decals = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly WoundSystem _wounds = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;

    public override void Initialize()
    {
        base.Initialize();
        // After the thresholds picked the state from the damage: a held victim can't get worse than HeldState.
        SubscribeLocalEvent<GilbertFinisherComponent, UpdateMobStateEvent>(OnUpdateMobState,
            after: new[] { typeof(MobThresholdSystem) });
    }

    private void OnUpdateMobState(Entity<GilbertFinisherComponent> ent, ref UpdateMobStateEvent args)
    {
        if (ent.Comp.HeldState is { } held && args.State > held)
            args.State = held;
    }

    public const string DefaultFinisher = "GilbertFinisherPhase1";

    /// <summary>The three phase-1 variants, in command order (1 = kick combo, 2 = shotgun barrel, 3 = ground and pound).</summary>
    public static readonly string[] Variants = { DefaultFinisher, "GilbertFinisherBarrel", "GilbertFinisherGroundPound" };

    /// <summary>"1".."3" or a finisher prototype id → prototype id; null when unknown.</summary>
    public string? ResolveVariant(string arg)
    {
        if (int.TryParse(arg, out var n))
            return n >= 1 && n <= Variants.Length ? Variants[n - 1] : null;
        return _proto.HasIndex<GilbertFinisherPrototype>(arg) ? arg : null;
    }

    /// <summary>Distance kept from the first wall in front (tiles).</summary>
    private const float WallMargin = 0.45f;

    public bool IsPlaying(EntityUid uid) => HasComp<GilbertFinisherComponent>(uid);

    /// <summary>Starts the cutscene. Returns false with a locale id in <paramref name="error"/> when it can't.</summary>
    public bool TryStart(EntityUid attacker, EntityUid victim, [NotNullWhen(false)] out string? error,
        string finisher = DefaultFinisher, bool keepDown = true)
    {
        error = null;
        if (!_proto.TryIndex<GilbertFinisherPrototype>(finisher, out var proto))
        {
            error = "cmd-gilbertfinisher-no-proto";
            return false;
        }

        if (attacker == victim)
        {
            error = "cmd-gilbertfinisher-same";
            return false;
        }

        if (!HasComp<HumanoidAppearanceComponent>(attacker) || !HasComp<HumanoidAppearanceComponent>(victim))
        {
            error = "cmd-gilbertfinisher-not-humanoid";
            return false;
        }

        if (IsPlaying(attacker) || IsPlaying(victim))
        {
            error = "cmd-gilbertfinisher-busy";
            return false;
        }

        if (TryComp<BuckleComponent>(attacker, out var b1) && b1.Buckled
            || TryComp<BuckleComponent>(victim, out var b2) && b2.Buckled)
        {
            error = "cmd-gilbertfinisher-buckled";
            return false;
        }

        var aXform = Transform(attacker);
        var vXform = Transform(victim);
        if (aXform.MapID == MapId.Nullspace || aXform.MapID != vXform.MapID)
        {
            error = "cmd-gilbertfinisher-other-map";
            return false;
        }

        var origin = _transform.GetWorldPosition(aXform);
        var toVictim = _transform.GetWorldPosition(vXform) - origin;
        var forward = PickForward(attacker, victim, aXform.MapID, origin, toVictim, proto.RequiredRoom, out var room);

        // Make sure both stand at the start (the victim may already be lying).
        _standing.Stand(victim, force: true);

        // Holster beat first (animation time -Intro..0) only when there is something in the attacker's hands.
        var intro = proto.Intro > 0f && proto.Attacker.HolsterHeldItems && _hands.EnumerateHeld(attacker).Any()
            ? proto.Intro
            : 0f;
        var start = _timing.CurTime + TimeSpan.FromSeconds(intro);
        var a = EnsureComp<GilbertFinisherComponent>(attacker);
        Setup(attacker, a, victim, GilbertFinisherRole.Attacker, proto, start, intro, forward, origin, room, keepDown);
        var v = EnsureComp<GilbertFinisherComponent>(victim);
        Setup(victim, v, attacker, GilbertFinisherRole.Victim, proto, start, intro, forward, origin, room, keepDown);

        // Face each other.
        var angle = DirectionAngle(forward);
        _transform.SetWorldRotation(attacker, angle);
        _transform.SetWorldRotation(victim, angle + Math.PI);

        Apply(attacker, a, proto, -intro);
        Apply(victim, v, proto, -intro);
        return true;
    }

    /// <summary>Stops the cutscene on <paramref name="uid"/> and its partner, restoring both.</summary>
    public void Stop(EntityUid uid)
    {
        if (!TryComp<GilbertFinisherComponent>(uid, out var comp))
            return;

        var partner = comp.Partner;
        Finish(uid, comp);
        if (partner is { } p && TryComp<GilbertFinisherComponent>(p, out var pComp))
            Finish(p, pComp);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<GilbertFinisherComponent>();
        var done = new List<EntityUid>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!_proto.TryIndex(comp.Finisher, out var proto))
            {
                done.Add(uid);
                continue;
            }

            // A participant got deleted / gibbed: end it for the other one too.
            if (comp.Partner is not { } partner || TerminatingOrDeleted(partner) || !HasComp<GilbertFinisherComponent>(partner))
            {
                done.Add(uid);
                continue;
            }

            // Real time -> animation time (frozen during the impact hit-stops; negative during the intro).
            var t = (float) (now - comp.StartTime).TotalSeconds;
            var anim = GilbertFinisherTimeline.AnimTime(proto, t);
            Apply(uid, comp, proto, anim);

            if (comp.Role == GilbertFinisherRole.Attacker)
                FireEvents(uid, comp, partner, proto, anim);

            if (t >= GilbertFinisherTimeline.TotalLength(proto))
                done.Add(uid);
        }

        foreach (var uid in done)
        {
            if (TryComp<GilbertFinisherComponent>(uid, out var comp))
                Finish(uid, comp);
        }
    }

    private void Setup(EntityUid uid, GilbertFinisherComponent comp, EntityUid partner, GilbertFinisherRole role,
        GilbertFinisherPrototype proto, TimeSpan start, float intro, Vector2 forward, Vector2 origin, float room, bool keepDown)
    {
        comp.Finisher = proto.ID;
        comp.Role = role;
        comp.Partner = partner;
        comp.StartTime = start;
        comp.Forward = forward;
        comp.Origin = origin;
        comp.MaxForward = MathF.Max(0f, room - WallMargin);
        comp.Intro = intro;
        comp.NextEvent = 0;
        while (comp.NextEvent < proto.Events.Count && proto.Events[comp.NextEvent].Time < -intro - 1e-4f)
            comp.NextEvent++; // skipped intro events
        comp.FlightScale = -1f;
        comp.KeepDown = keepDown;
        comp.HeldState = null;
        if (role == GilbertFinisherRole.Victim && proto.HoldAlive && TryComp<MobStateComponent>(uid, out var mob)
            && mob.CurrentState is MobState.Alive or MobState.Critical)
            comp.HeldState = mob.CurrentState;
        Dirty(uid, comp);

        if (TryComp<PhysicsComponent>(uid, out var body))
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);

        Blocker.UpdateCanMove(uid);
    }

    private void Apply(EntityUid uid, GilbertFinisherComponent comp, GilbertFinisherPrototype proto, float t)
    {
        var tracks = GilbertFinisherTimeline.Tracks(proto, comp.Role);
        if (tracks.Move.Count == 0)
            return;

        var pose = GilbertFinisherTimeline.Evaluate(tracks.Move, t);
        var side = GilbertFinisherTimeline.Side(comp.Forward);
        if (tracks.Flight is { } flight && t > flight.Start)
        {
            // Thrown: shorten the planned displacement (once, on take-off) so the landing spot is free floor.
            var p0 = GilbertFinisherTimeline.Evaluate(tracks.Move, flight.Start);
            if (comp.FlightScale < 0f)
                comp.FlightScale = PlanFlight(uid, comp, tracks, flight, p0, side);
            pose = new GilbertFinisherPose(
                p0.X + (pose.X - p0.X) * comp.FlightScale,
                p0.Y + (pose.Y - p0.Y) * comp.FlightScale,
                pose.Rot);
        }

        var along = MathF.Min(pose.X, comp.MaxForward);
        var pos = comp.Origin + comp.Forward * along + side * pose.Y;
        _transform.SetWorldPosition(uid, pos);

        if (TryComp<PhysicsComponent>(uid, out var body) && body.LinearVelocity != Vector2.Zero)
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
    }

    /// <summary>
    /// Largest fraction (1, 0.9, ... 0) of the flight displacement whose path is clear of walls / anchored blockers
    /// (plus <see cref="WallMargin"/>) and whose landing point is on a non-space tile.
    /// </summary>
    private float PlanFlight(EntityUid uid, GilbertFinisherComponent comp, GilbertFinisherActorTracks tracks,
        GilbertFinisherFlight flight, GilbertFinisherPose p0, Vector2 side)
    {
        var p1 = GilbertFinisherTimeline.Evaluate(tracks.Move, flight.End);
        var start = comp.Origin + comp.Forward * MathF.Min(p0.X, comp.MaxForward) + side * p0.Y;
        var d = comp.Forward * (p1.X - p0.X) + side * (p1.Y - p0.Y);
        var len = d.Length();
        if (len < 1e-3f)
            return 1f;

        var map = Transform(uid).MapID;
        var partner = comp.Partner ?? uid;
        var dir = d / len;
        var free = FreeDistance(uid, partner, map, start, dir, len + WallMargin);
        for (var i = 10; i > 0; i--)
        {
            var s = i / 10f;
            if (len * s + WallMargin > free + 1e-4f)
                continue;

            var land = start + d * s;
            if (!_mapManager.TryFindGridAt(map, land, out var gridUid, out var grid)
                || !_map.TryGetTileRef(gridUid, grid, land, out var tile)
                || _turf.IsSpace(tile))
                continue;

            return s;
        }

        return 0f;
    }

    private void FireEvents(EntityUid attacker, GilbertFinisherComponent comp, EntityUid victim,
        GilbertFinisherPrototype proto, float t)
    {
        while (comp.NextEvent < proto.Events.Count && proto.Events[comp.NextEvent].Time <= t)
        {
            var ev = proto.Events[comp.NextEvent++];
            var target = ev.Target == GilbertFinisherRole.Attacker ? attacker : victim;
            if (ev.Sound != null)
                _audio.PlayPvs(ev.Sound, target);

            if (ev.Decal != null)
                SpawnDecal(target, ev.Target, comp.Forward, proto, ev);

            foreach (var hit in ev.Damage)
                ApplyHit(attacker, target, hit);

            foreach (var part in ev.Amputate)
                Amputate(target, part);

            if (ev.Kill)
                Kill(attacker, victim);
        }
    }

    /// <summary>
    /// The kill beat: releases the held mob state, then tops the damage up to the death threshold (blunt, chest,
    /// through armour); if the body still isn't dead (part integrity caps), the dead state is forced.
    /// </summary>
    public void Kill(EntityUid attacker, EntityUid victim)
    {
        ReleaseHold(victim);
        if (!TryComp<MobStateComponent>(victim, out var mob) || mob.CurrentState == MobState.Dead)
            return;

        if (TryComp<DamageableComponent>(victim, out var damageable)
            && _thresholds.TryGetThresholdForState(victim, MobState.Dead, out var threshold))
        {
            var need = threshold.Value - damageable.TotalDamage + FixedPoint2.New(1);
            if (need > FixedPoint2.Zero)
            {
                var spec = new DamageSpecifier();
                spec.DamageDict["Blunt"] = need;
                _damageable.TryChangeDamage(victim, spec, ignoreResistances: true, interruptsDoAfters: false,
                    origin: attacker, targetPart: TargetBodyPart.Chest, canMiss: false);
            }
        }

        if (mob.CurrentState != MobState.Dead)
            _mobState.ChangeMobState(victim, MobState.Dead, mob, attacker);
    }

    private void ReleaseHold(EntityUid victim)
    {
        if (!TryComp<GilbertFinisherComponent>(victim, out var comp) || comp.HeldState == null)
            return;
        comp.HeldState = null;
        _mobState.UpdateMobState(victim);
    }

    /// <summary>
    /// One finisher blow: the whole specifier goes to one body part, can't miss and (by default) ignores resistances,
    /// which skips both the damage modifier sets and the armour relayed from the inventory (DamageModifyEvent).
    /// </summary>
    public void ApplyHit(EntityUid attacker, EntityUid target, GilbertFinisherHit hit)
    {
        var before = TryComp<DamageableComponent>(target, out var damageable) ? damageable.TotalDamage : FixedPoint2.Zero;
        _damageable.TryChangeDamage(target, hit.Damage, ignoreResistances: hit.IgnoreArmor, interruptsDoAfters: false,
            origin: attacker, targetPart: hit.Part, canMiss: false);

        // A part holds no more than its integrity (the head: 65), the rest of the budget would just vanish:
        // the overflow goes into the chest, so the blow always deals its full amount.
        if (damageable == null || hit.Part == TargetBodyPart.Chest)
            return;
        var total = hit.Damage.GetTotal();
        var dealt = damageable.TotalDamage - before;
        if (total <= FixedPoint2.Zero || dealt >= total - FixedPoint2.New(0.05))
            return;
        var rest = hit.Damage * ((total - dealt).Float() / total.Float());
        _damageable.TryChangeDamage(target, rest, ignoreResistances: hit.IgnoreArmor, interruptsDoAfters: false,
            origin: attacker, targetPart: TargetBodyPart.Chest, canMiss: false);
    }

    /// <summary>Tears a limb off (the part entity drops next to the body; its humanoid layers get hidden).</summary>
    public bool Amputate(EntityUid target, TargetBodyPart part)
    {
        foreach (var (id, bodyPart) in _body.GetBodyChildren(target))
        {
            if (_body.GetTargetBodyPart(bodyPart) != part || _body.GetParentPartOrNull(id) is not { } parent)
                continue;

            // The safe variant: no extra amputation damage on the chest (the prototype's damage is the whole budget),
            // the sound / blood come from the cutscene event.
            _wounds.AmputateWoundableSafely(parent, id);
            return true;
        }

        return false;
    }

    /// <summary>Leaves the event's floor decal (blood splatter) under the solved point, tinted with the target's blood.</summary>
    private void SpawnDecal(EntityUid target, GilbertFinisherRole role, Vector2 forward, GilbertFinisherPrototype proto,
        GilbertFinisherEvent ev)
    {
        var decal = ev.Decal!;
        var xform = Transform(target);
        if (xform.GridUid is not { } grid)
            return;

        var pose = GilbertFinisherTimeline.Solve(proto, role, ev.Time);
        var p = GilbertFinisherTimeline.Point(pose, decal.From, decal.At) / EyeManagerPpm;
        // Side view: forward-frame x runs along Forward, y is screen-up (world +Y) when the view is horizontal.
        var world = _transform.GetWorldPosition(xform) + forward * p.X;
        if (MathF.Abs(forward.X) > 0.5f)
            world += new Vector2(0, MathF.Max(p.Y, -0.45f));

        var color = decal.Color;
        if (TryComp<BloodstreamComponent>(target, out var blood)
            && _proto.TryIndex<ReagentPrototype>(blood.BloodReagent, out var reagent))
            color = reagent.SubstanceColor.WithAlpha(decal.Color.A);

        // Decal positions are their bottom-left corner.
        var coords = _transform.ToCoordinates(grid, new MapCoordinates(world - new Vector2(0.5f, 0.5f), xform.MapID));
        _decals.TryAddDecal(decal.Id, coords, out _, color, cleanable: true);
    }

    private const float EyeManagerPpm = 32f;

    private void Finish(EntityUid uid, GilbertFinisherComponent comp)
    {
        var role = comp.Role;
        var keepDown = comp.KeepDown;
        var held = comp.HeldState != null;
        comp.HeldState = null;
        RemComp<GilbertFinisherComponent>(uid);
        if (held && !TerminatingOrDeleted(uid))
            _mobState.UpdateMobState(uid);

        if (TerminatingOrDeleted(uid))
            return;

        if (TryComp<PhysicsComponent>(uid, out var body))
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);

        if (role == GilbertFinisherRole.Victim && keepDown)
            _standing.Down(uid, playSound: false, dropHeldItems: false, force: true);
    }

    /// <summary>
    /// Side views read best, so east/west are preferred: first the horizontal side the victim is on, then the other one,
    /// then the vertical ones. The first direction with enough free floor wins; otherwise the roomiest one.
    /// </summary>
    private Vector2 PickForward(EntityUid attacker, EntityUid victim, MapId map, Vector2 origin, Vector2 toVictim,
        float required, out float room)
    {
        var horizontal = new Vector2(toVictim.X >= 0 ? 1 : -1, 0);
        var vertical = new Vector2(0, toVictim.Y >= 0 ? 1 : -1);
        Span<Vector2> candidates = stackalloc Vector2[] { horizontal, -horizontal, vertical, -vertical };

        var best = horizontal;
        room = -1f;
        foreach (var dir in candidates)
        {
            var free = FreeDistance(attacker, victim, map, origin, dir, required);
            if (free >= required)
            {
                room = free;
                return dir;
            }

            if (free > room)
            {
                room = free;
                best = dir;
            }
        }

        return best;
    }

    private float FreeDistance(EntityUid attacker, EntityUid victim, MapId map, Vector2 origin, Vector2 dir, float max)
    {
        var ray = new CollisionRay(origin, dir, (int) (CollisionGroup.Impassable | CollisionGroup.HighImpassable | CollisionGroup.MidImpassable));
        var hits = _physics.IntersectRayWithPredicate(map, ray, max,
            e => e == attacker || e == victim || !Transform(e).Anchored, returnOnFirstHit: false);

        var free = max;
        foreach (var hit in hits)
            free = MathF.Min(free, hit.Distance);

        return free;
    }

    private static Angle DirectionAngle(Vector2 forward)
    {
        // Entity rotation 0 faces south; Vector2ToAngle already uses that convention (+90° = east).
        return forward.ToWorldAngle();
    }
}
