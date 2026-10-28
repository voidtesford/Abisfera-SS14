using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Server.Decals;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Systems;
using Content.Shared._Shitmed.Damage;
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
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.ProcAnim;

/// <summary>
/// Server runtime of <see cref="ProcAnimScenePrototype"/> cutscenes: validates and locks the actors, picks the forward
/// direction, walks them along their <c>move</c> tracks, holds their mob state (holdAlive / holdCrit), fires the
/// timeline events (sounds, damage, amputation, kills, decals, popups) and cleans everything up at the end / on stop.
/// Sprite-layer animation and screen effects are done by the client (ProcAnimVisualsSystem) from the networked start time.
/// </summary>
public sealed partial class ProcAnimSystem : SharedProcAnimSystem
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
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;

    public override void Initialize()
    {
        base.Initialize();
        // After the thresholds picked the state from the damage: a held actor can't get worse than HeldState.
        SubscribeLocalEvent<ProcAnimComponent, UpdateMobStateEvent>(OnUpdateMobState,
            after: new[] { typeof(MobThresholdSystem) });
    }

    private void OnUpdateMobState(Entity<ProcAnimComponent> ent, ref UpdateMobStateEvent args)
    {
        if (ent.Comp.HeldState is { } held && args.State > held)
            args.State = held;
    }

    /// <summary>Distance kept from the first wall in front (tiles).</summary>
    private const float WallMargin = 0.45f;

    public bool IsPlaying(EntityUid uid) => HasComp<ProcAnimComponent>(uid);

    /// <summary>
    /// Starts <paramref name="scene"/> with <paramref name="actors"/> in its role order. Returns false with a locale id
    /// (<c>procanim-error-*</c>) in <paramref name="error"/> when it can't.
    /// </summary>
    /// <param name="keepDown">False: actors with knockDown are left standing at the end.</param>
    public bool TryPlay(string scene, IReadOnlyList<EntityUid> actors, [NotNullWhen(false)] out string? error,
        bool keepDown = true)
    {
        error = null;
        if (!_proto.TryIndex<ProcAnimScenePrototype>(scene, out var proto))
        {
            error = "procanim-error-no-proto";
            return false;
        }

        if (actors.Count != proto.Actors.Count || actors.Count == 0)
        {
            error = "procanim-error-actor-count";
            return false;
        }

        if (actors.Distinct().Count() != actors.Count)
        {
            error = "procanim-error-same";
            return false;
        }

        // The rig is a humanoid one (head / torso / two-segment limbs split from the humanoid layers).
        if (actors.Any(a => !HasComp<HumanoidAppearanceComponent>(a)))
        {
            error = "procanim-error-not-humanoid";
            return false;
        }

        if (actors.Any(IsPlaying))
        {
            error = "procanim-error-busy";
            return false;
        }

        if (actors.Any(a => TryComp<BuckleComponent>(a, out var buckle) && buckle.Buckled))
        {
            error = "procanim-error-buckled";
            return false;
        }

        var lead = Transform(actors[0]);
        var map = lead.MapID;
        if (map == MapId.Nullspace || actors.Any(a => Transform(a).MapID != map))
        {
            error = "procanim-error-other-map";
            return false;
        }

        // Real items: every required one must be there before anything changes.
        if (!PlanItems(proto, actors, out var items, out var bound, out var ops, out var heldAfter))
        {
            error = "procanim-error-missing-item";
            return false;
        }

        var origin = _transform.GetWorldPosition(lead);
        var toSecond = actors.Count > 1
            ? _transform.GetWorldPosition(Transform(actors[1])) - origin
            : _transform.GetWorldRotation(lead).ToWorldVec();
        // The scene is laid out in screen space: the axes are the screen axes of the lead's camera (grid / eye rotation),
        // so a side view stays a side view on rotated grids.
        var view = ScreenRotation(actors[0]);
        var forward = PickForward(actors, map, origin, toSecond, view, proto.RequiredRoom, out var room);

        // Make sure they stand at the start (e.g. the victim may already be lying).
        for (var i = 0; i < actors.Count; i++)
            if (proto.Actors[i].StandUp)
                _standing.Stand(actors[i], force: true);

        // Holster beat first (animation time -Intro..0) only when a holstering actor has something in its hands once the
        // stow pre-beat is done. The pre-beat (one reach per stowed / drawn item) comes before even that.
        var holster = false;
        var preBeat = 0f;
        for (var i = 0; i < actors.Count; i++)
        {
            holster |= proto.Actors[i].HolsterHeldItems && heldAfter[i];
            preBeat = MathF.Max(preBeat, ops[i].Count * (proto.Actors[i].StowHands ?? DefaultStow).Duration);
        }
        var intro = proto.Intro > 0f && holster ? proto.Intro : 0f;
        var start = _timing.CurTime + TimeSpan.FromSeconds(intro + preBeat);

        var comps = new ProcAnimComponent[actors.Count];
        for (var i = 0; i < actors.Count; i++)
        {
            comps[i] = EnsureComp<ProcAnimComponent>(actors[i]);
            Setup(actors[i], comps[i], i, actors, proto, start, intro, forward, view, origin, room, keepDown);
            comps[i].PreBeat = preBeat;
            SetupItems(actors[i], comps[i], items, bound[i], ops[i]);
            Dirty(actors[i], comps[i]);
        }

        // Face along the forward frame (facing -1: towards the first actor).
        var angle = DirectionAngle(forward);
        for (var i = 0; i < actors.Count; i++)
            _transform.SetWorldRotation(actors[i], proto.Actors[i].Facing < 0f ? angle + Math.PI : angle);

        for (var i = 0; i < actors.Count; i++)
        {
            Apply(actors[i], comps[i], proto, -intro);
            UpdateItems(actors[i], comps[i], proto, -intro - preBeat, -intro - preBeat);
        }
        return true;
    }

    /// <summary>Stops the scene <paramref name="uid"/> plays in, for every actor, restoring them (no final damage).</summary>
    public void Stop(EntityUid uid)
    {
        if (!TryComp<ProcAnimComponent>(uid, out var comp))
            return;

        var actors = comp.Actors.ToList();
        Finish(uid, comp);
        foreach (var other in actors)
            if (other != uid && TryComp<ProcAnimComponent>(other, out var otherComp))
                Finish(other, otherComp);
    }

    /// <summary>Stops every playing scene; returns the number of actors released.</summary>
    public int StopAll()
    {
        var all = new List<EntityUid>();
        var query = EntityQueryEnumerator<ProcAnimComponent>();
        while (query.MoveNext(out var uid, out _))
            all.Add(uid);
        foreach (var uid in all)
            if (TryComp<ProcAnimComponent>(uid, out var comp))
                Finish(uid, comp);
        return all.Count;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ProcAnimComponent>();
        var done = new List<(EntityUid Uid, bool Completed)>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!_proto.TryIndex(comp.Scene, out var proto) || comp.Actor < 0 || comp.Actor >= proto.Actors.Count)
            {
                done.Add((uid, false));
                continue;
            }

            // An actor got deleted / gibbed / released: end it for the others too.
            var lost = false;
            foreach (var other in comp.Actors)
                lost |= other != uid && (TerminatingOrDeleted(other) || !HasComp<ProcAnimComponent>(other));
            if (lost)
            {
                done.Add((uid, false));
                continue;
            }

            // Real time -> animation time (frozen during the impact hit-stops; negative during the intro).
            var t = (float) (now - comp.StartTime).TotalSeconds;
            var anim = ProcAnimTimeline.AnimTime(proto, t);
            Apply(uid, comp, proto, MathF.Max(anim, -comp.Intro));
            UpdateItems(uid, comp, proto, t, anim);
            if (!HasComp<ProcAnimComponent>(uid))
                continue;

            // The lead actor runs the event cursor for the whole scene.
            if (comp.Actor == 0)
                FireEvents(comp, proto, MathF.Max(anim, -comp.Intro));

            if (t >= ProcAnimTimeline.TotalLength(proto))
                done.Add((uid, true));
        }

        foreach (var (uid, completed) in done)
        {
            if (TryComp<ProcAnimComponent>(uid, out var comp))
                Finish(uid, comp, completed);
        }
    }

    private void Setup(EntityUid uid, ProcAnimComponent comp, int index, IReadOnlyList<EntityUid> actors,
        ProcAnimScenePrototype proto, TimeSpan start, float intro, Vector2 forward, Angle view, Vector2 origin, float room, bool keepDown)
    {
        var tr = proto.Actors[index];
        comp.Scene = proto.ID;
        comp.Actor = index;
        comp.Actors = actors.ToList();
        var partner = proto.DefaultTarget(index);
        comp.Partner = partner != index ? actors[partner] : null;
        comp.StartTime = start;
        comp.Forward = forward;
        comp.View = view;
        comp.Origin = origin;
        comp.MaxForward = MathF.Max(0f, room - WallMargin);
        comp.Intro = intro;
        comp.NextEvent = 0;
        while (comp.NextEvent < proto.Events.Count && proto.Events[comp.NextEvent].Time < -intro - 1e-4f)
            comp.NextEvent++; // skipped intro events
        comp.FlightScale = -1f;
        comp.KeepDown = keepDown;
        comp.HeldState = null;
        // holdCrit: an actor already in crit stays there (crit damage over time / the beats must not kill it before
        // the final blow); holdAlive: the current state is held, alive or crit.
        if (TryComp<MobStateComponent>(uid, out var mob)
            && (tr.HoldAlive && mob.CurrentState is MobState.Alive or MobState.Critical
                || tr.HoldCrit && mob.CurrentState == MobState.Critical))
            comp.HeldState = mob.CurrentState;
        Dirty(uid, comp);

        if (TryComp<PhysicsComponent>(uid, out var body))
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);

        Blocker.UpdateCanMove(uid);
    }

    private void Apply(EntityUid uid, ProcAnimComponent comp, ProcAnimScenePrototype proto, float t)
    {
        var tracks = ProcAnimTimeline.Tracks(proto, comp.Actor);
        if (tracks.Move.Count == 0)
            return;

        var pose = ProcAnimTimeline.Evaluate(tracks.Move, t);
        var side = ProcAnimTimeline.Side(comp.Forward, comp.View);
        if (tracks.Flight is { } flight && t > flight.Start)
        {
            // Thrown: shorten the planned displacement (once, on take-off) so the landing spot is free floor.
            var p0 = ProcAnimTimeline.Evaluate(tracks.Move, flight.Start);
            if (comp.FlightScale < 0f)
                comp.FlightScale = PlanFlight(uid, comp, tracks, flight, p0, side);
            pose = new ProcAnimPose(
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
    private float PlanFlight(EntityUid uid, ProcAnimComponent comp, ProcAnimActor tracks,
        ProcAnimFlight flight, ProcAnimPose p0, Vector2 side)
    {
        var p1 = ProcAnimTimeline.Evaluate(tracks.Move, flight.End);
        var start = comp.Origin + comp.Forward * MathF.Min(p0.X, comp.MaxForward) + side * p0.Y;
        var d = comp.Forward * (p1.X - p0.X) + side * (p1.Y - p0.Y);
        var len = d.Length();
        if (len < 1e-3f)
            return 1f;

        var map = Transform(uid).MapID;
        var dir = d / len;
        var free = FreeDistance(comp.Actors, map, start, dir, len + WallMargin);
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

    private void FireEvents(ProcAnimComponent comp, ProcAnimScenePrototype proto, float t)
    {
        var lead = comp.Actors[0];
        while (comp.NextEvent < proto.Events.Count && proto.Events[comp.NextEvent].Time <= t)
        {
            var ev = proto.Events[comp.NextEvent++];
            if (ev.TargetIndex < 0 || ev.TargetIndex >= comp.Actors.Count)
                continue;
            var target = comp.Actors[ev.TargetIndex];
            if (ev.Sound != null)
                _audio.PlayPvs(ev.Sound, target);

            if (ev.Decal != null)
                SpawnDecal(target, ev.TargetIndex, comp.Forward, comp.View, proto, ev);

            if (ev.Popup is { } popup)
                _popup.PopupEntity(Loc.GetString(popup.Text), target, popup.Type);

            foreach (var hit in ev.Damage)
                ApplyHit(lead, target, hit);

            foreach (var part in ev.Amputate)
                Amputate(target, part);

            if (ev.Kill)
                Kill(lead, target);

            var raised = new ProcAnimEventFiredEvent(proto.ID, comp.NextEvent - 1, ev, lead);
            RaiseLocalEvent(target, ref raised);
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

    /// <summary>
    /// <see cref="ProcAnimActor.FinalDamage"/> at the end of the cutscene: through armour and resistances, not aimed at
    /// a body part: split over every part like suffocation (a single random part would cap it at that part's
    /// integrity), then topped up part by part.
    /// </summary>
    public void ApplyFinalDamage(EntityUid victim, EntityUid? attacker, DamageSpecifier? finalDamage)
    {
        if (finalDamage is not { } damage || damage.Empty)
            return;
        if (!TryComp<DamageableComponent>(victim, out var damageable))
            return;

        var before = new Dictionary<string, FixedPoint2>(damageable.Damage.DamageDict);
        _damageable.TryChangeDamage(victim, damage, ignoreResistances: true, interruptsDoAfters: false,
            origin: attacker, targetPart: TargetBodyPart.All, ignoreBlockers: true, canMiss: false);

        // The split rounds per part and an already wrecked part caps its share: top the remainder up part by part.
        foreach (var part in FinalDamageTopUpParts)
        {
            if (TerminatingOrDeleted(victim))
                return;
            var rest = new DamageSpecifier();
            foreach (var (type, amount) in damage.DamageDict)
            {
                var dealt = damageable.Damage.DamageDict.GetValueOrDefault(type) - before.GetValueOrDefault(type);
                if (amount - dealt > FixedPoint2.Zero)
                    rest.DamageDict[type] = amount - dealt;
            }
            if (rest.Empty)
                return;
            _damageable.TryChangeDamage(victim, rest, ignoreResistances: true, interruptsDoAfters: false,
                origin: attacker, targetPart: part, ignoreBlockers: true, splitDamage: SplitDamageBehavior.None, canMiss: false);
        }
    }

    private static readonly TargetBodyPart[] FinalDamageTopUpParts =
    {
        TargetBodyPart.Chest, TargetBodyPart.Groin, TargetBodyPart.Head, TargetBodyPart.LeftArm, TargetBodyPart.RightArm,
        TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg, TargetBodyPart.LeftHand, TargetBodyPart.RightHand,
        TargetBodyPart.LeftFoot, TargetBodyPart.RightFoot,
    };

    private void ReleaseHold(EntityUid victim)
    {
        if (!TryComp<ProcAnimComponent>(victim, out var comp) || comp.HeldState == null)
            return;
        comp.HeldState = null;
        _mobState.UpdateMobState(victim);
    }

    /// <summary>
    /// One damage beat: the whole specifier goes to one body part, can't miss and (by default) ignores resistances,
    /// which skips both the damage modifier sets and the armour relayed from the inventory (DamageModifyEvent).
    /// </summary>
    public void ApplyHit(EntityUid attacker, EntityUid target, ProcAnimHit hit)
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
    private void SpawnDecal(EntityUid target, int actor, Vector2 forward, Angle view, ProcAnimScenePrototype proto,
        ProcAnimEvent ev)
    {
        var decal = ev.Decal!;
        var xform = Transform(target);
        if (xform.GridUid is not { } grid)
            return;

        var pose = ProcAnimTimeline.Solve(proto, actor, ev.Time);
        var p = ProcAnimTimeline.Point(pose, decal.From, decal.At) / EyeManagerPpm;
        // Side view: forward-frame x runs along Forward, y is screen-up when the view is horizontal.
        var world = _transform.GetWorldPosition(xform) + forward * p.X;
        var up = view.RotateVec(new Vector2(0, 1));
        if (MathF.Abs(Vector2.Dot(forward, up)) < 0.5f)
            world += up * MathF.Max(p.Y, -0.45f);

        var color = decal.Color;
        if (TryComp<BloodstreamComponent>(target, out var blood)
            && _proto.TryIndex<ReagentPrototype>(blood.BloodReagent, out var reagent))
            color = reagent.SubstanceColor.WithAlpha(decal.Color.A);

        // Decal positions are their bottom-left corner.
        var coords = _transform.ToCoordinates(grid, new MapCoordinates(world - new Vector2(0.5f, 0.5f), xform.MapID));
        _decals.TryAddDecal(decal.Id, coords, out _, color, cleanable: true);
    }

    private const float EyeManagerPpm = 32f;

    private void Finish(EntityUid uid, ProcAnimComponent comp, bool completed = false)
    {
        var index = comp.Actor;
        var keepDown = comp.KeepDown;
        var held = comp.HeldState != null;
        var scene = comp.Scene;
        EntityUid? lead = comp.Actors.Count > 0 && comp.Actors[0] != uid ? comp.Actors[0] : null;
        comp.HeldState = null;
        _proto.TryIndex<ProcAnimScenePrototype>(scene, out var proto);
        var tr = proto != null && index >= 0 && index < proto.Actors.Count ? proto.Actors[index] : null;
        RemComp<ProcAnimComponent>(uid);
        if (held && !TerminatingOrDeleted(uid))
            _mobState.UpdateMobState(uid);

        // Items back where they belong (the component is gone, so nothing blocks the hands any more).
        FinishItems(uid, comp, tr);

        // Played to the end: the final damage, after the last blow.
        if (completed && tr != null && !TerminatingOrDeleted(uid))
            ApplyFinalDamage(uid, lead, tr.FinalDamage);

        if (TerminatingOrDeleted(uid))
            return;

        if (TryComp<PhysicsComponent>(uid, out var body))
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);

        if (tr is { KnockDown: true } && keepDown)
            _standing.Down(uid, playSound: false, dropHeldItems: false, force: true);

        var ended = new ProcAnimEndedEvent(scene, index, completed);
        RaiseLocalEvent(uid, ref ended);
    }

    /// <summary>
    /// Side views read best, so east/west are preferred: first the horizontal side the second actor is on, then the other one,
    /// then the vertical ones. The first direction with enough free floor wins; otherwise the roomiest one.
    /// </summary>
    private Vector2 PickForward(IReadOnlyList<EntityUid> actors, MapId map, Vector2 origin, Vector2 toVictim,
        Angle view, float required, out float room)
    {
        var right = view.RotateVec(new Vector2(1, 0));
        var up = view.RotateVec(new Vector2(0, 1));
        var horizontal = Vector2.Dot(toVictim, right) >= 0 ? right : -right;
        var vertical = Vector2.Dot(toVictim, up) >= 0 ? up : -up;
        Span<Vector2> candidates = stackalloc Vector2[] { horizontal, -horizontal, vertical, -vertical };

        var best = horizontal;
        room = -1f;
        foreach (var dir in candidates)
        {
            var free = FreeDistance(actors, map, origin, dir, required);
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

    private float FreeDistance(IReadOnlyList<EntityUid> actors, MapId map, Vector2 origin, Vector2 dir, float max)
    {
        var ray = new CollisionRay(origin, dir, (int) (CollisionGroup.Impassable | CollisionGroup.HighImpassable | CollisionGroup.MidImpassable));
        var hits = _physics.IntersectRayWithPredicate(map, ray, max,
            e => actors.Contains(e) || !Transform(e).Anchored, returnOnFirstHit: false);

        var free = max;
        foreach (var hit in hits)
            free = MathF.Min(free, hit.Distance);

        return free;
    }

    /// <summary>
    /// World rotation of the screen frame <paramref name="uid"/>'s camera shows (screen axis = <c>rotation.RotateVec(axis)</c>
    /// in world space): the inverse of the eye rotation, i.e. the mover's parent grid angle (what <c>EyeLerpingSystem</c>
    /// follows), else the rotation of the grid / map it stands on.
    /// </summary>
    public Angle ScreenRotation(EntityUid uid)
    {
        if (TryComp<InputMoverComponent>(uid, out var mover) && Exists(mover.RelativeEntity))
            return _mover.GetParentGridAngle(mover);

        var xform = Transform(uid);
        return xform.GridUid is { } grid ? _transform.GetWorldRotation(grid)
            : xform.MapUid is { } mapUid ? _transform.GetWorldRotation(mapUid)
            : Angle.Zero;
    }

    private static Angle DirectionAngle(Vector2 forward)
    {
        // Entity rotation 0 faces south; Vector2ToAngle already uses that convention (+90° = east).
        return forward.ToWorldAngle();
    }
}
