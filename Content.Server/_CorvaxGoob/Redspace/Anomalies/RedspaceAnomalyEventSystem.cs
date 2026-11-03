using System.Linq;
using System.Numerics;
using System.Text;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server._CorvaxGoob.Redspace.Physics;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.PowerCell;
using Content.Shared._CorvaxGoob.Redspace.Anomalies;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Atmos.Rotting;
using Content.Shared.Chat;
using Content.Shared.Chemistry.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Ghost;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Radio;
using Content.Shared.Speech;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Content.Shared.Inventory;

namespace Content.Server._CorvaxGoob.Redspace.Anomalies;

/// <summary>
/// Redspace anomalies: zone events (rift, time loop, doppelganger, blood rain, mirror corridor, whispers, gravity
/// knot) and body effects on unprotected players (mutations, private hallucinations, item decay).
/// Driven by <see cref="RedspaceMapSteppedEvent"/> (once a second per map, only for mobs near living players) plus a
/// fast tick for the moving parts. Suppressed areas and immune mobs are skipped everywhere.
/// </summary>
public sealed class RedspaceAnomalyEventSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly RedspaceMapSystem _redspace = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly PuddleSystem _puddle = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedRottingSystem _rotting = default!;
    [Dependency] private readonly BatterySystem _battery = default!;
    [Dependency] private readonly PowerCellSystem _powerCell = default!;
    [Dependency] private readonly RedspaceAnomalySharedSystem _shared = default!;

    public const string RiftPrototype = "RedspaceRift";
    public const string GravityKnotPrototype = "RedspaceGravityKnot";
    public const string BloodRainPrototype = "RedspaceBloodRain";
    public const string MirrorPrototype = "RedspaceMirrorCorridor";
    public const string DoppelgangerPrototype = "RedspaceDoppelganger";
    public const string TimeLoopMarkerPrototype = "RedspaceTimeLoopMarker";
    public const string RiftExitPrototype = "RedspaceRiftExit";

    private static readonly ProtoId<DamageTypePrototype> Blunt = "Blunt";
    private static readonly ProtoId<DamageTypePrototype> Slash = "Slash";
    private static readonly ProtoId<DamageTypePrototype> Cellular = "Cellular";
    private static readonly ProtoId<RadioChannelPrototype> CommonChannel = "Common";

    private static readonly SoundSpecifier RiftSound = new SoundPathSpecifier("/Audio/Effects/teleport_departure.ogg", AudioParams.Default.WithVolume(-4f));
    private static readonly SoundSpecifier RiftExitSound = new SoundPathSpecifier("/Audio/Effects/teleport_arrival.ogg", AudioParams.Default.WithVolume(-4f));
    private static readonly SoundSpecifier LoopSound = new SoundPathSpecifier("/Audio/Effects/pop.ogg", AudioParams.Default.WithPitchScale(0.6f));
    private static readonly SoundSpecifier HitSound = new SoundCollectionSpecifier("MetalThud", AudioParams.Default.WithVolume(-3f));
    private static readonly SoundSpecifier WhisperSound = new SoundCollectionSpecifier("RedspaceWhispers", AudioParams.Default.WithVolume(-6f));
    private static readonly SoundSpecifier Footsteps = new SoundCollectionSpecifier("FootstepFloor", AudioParams.Default.WithVolume(-2f).WithVariation(0.1f));
    private static readonly SoundSpecifier JamSound = new SoundPathSpecifier("/Audio/Weapons/Guns/Empty/empty.ogg");

    /// <summary>Fast tick for movement of pulled things, the doppelganger and the time loop.</summary>
    public const float FastTime = 0.1f;

    private const int SpeechBuffer = 40;
    private const float DoppelgangerHitRange = 1.3f;
    private const float RiftSwallowRange = 0.7f;
    private const float MaxPullSpeed = 7f;

    private readonly List<(EntityUid Source, string Name, string Message, TimeSpan Time)> _speech = new();
    private readonly HashSet<EntityUid> _near = new();
    private readonly List<EntityUid> _toDelete = new();
    private readonly List<Vector2> _players = new();
    private readonly List<RedspaceSuppressor> _scratch = new();
    private float _fastAccumulator;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceMapSteppedEvent>(OnMapStepped);
        SubscribeLocalEvent<EntitySpokeEvent>(OnSpoke);
        SubscribeLocalEvent<RedspaceAffectedComponent, ComponentRemove>(OnUnaffected);
        SubscribeLocalEvent<RedspaceMutationComponent, AccentGetEvent>(OnAccent);
        SubscribeLocalEvent<RedspaceDoppelgangerStateComponent, DamageChangedEvent>(OnDoppelgangerDamaged);
        SubscribeLocalEvent<RedspaceZoneEventComponent, ComponentShutdown>(OnZoneShutdown);
        SubscribeLocalEvent<RedspaceTimeLoopComponent, ComponentShutdown>(OnLoopShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _fastAccumulator += frameTime;
        if (_fastAccumulator < FastTime)
            return;

        var dt = _fastAccumulator;
        _fastAccumulator = 0f;
        StepEvents(dt);
    }

    #region Map step: scheduling and body effects

    private void OnMapStepped(RedspaceMapSteppedEvent ev)
    {
        var s = ev.Map.Comp.Settings;
        var mapState = EnsureComp<RedspaceAnomalyMapComponent>(ev.Map);

        if (s.AnomalyEventsEnabled && Tick(ref mapState.NextEvent, s.AnomalyEventIntervalMin, s.AnomalyEventIntervalMax, ev.Dt))
            TryRandomEvent(ev);

        foreach (var uid in ev.Exposed)
        {
            if (!TryComp<ActorComponent>(uid, out var actor) || !TryComp<RedspaceAffectedComponent>(uid, out var affected))
                continue;

            affected.Exposure += ev.Dt;
            StepMutations(uid, affected, s, ev.Dt);

            if (s.PrivateHallucinationsEnabled
                && Tick(ref affected.NextPrivateHallucination, s.PrivateHallucinationIntervalMin, s.PrivateHallucinationIntervalMax, ev.Dt))
                TrySendHallucination(uid, actor.PlayerSession, ev.MapId, ev.Suppressors);

            if (s.ItemDecayEnabled)
                ApplyItemDecay(uid, s, ev.Dt);
        }
    }

    private void TryRandomEvent(RedspaceMapSteppedEvent ev)
    {
        var s = ev.Map.Comp.Settings;
        var victims = ev.Exposed.Where(u => HasComp<ActorComponent>(u)).ToList();
        if (victims.Count == 0)
            return;

        var zones = 0;
        var query = EntityQueryEnumerator<RedspaceZoneEventComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var xform))
        {
            if (xform.MapID == ev.MapId)
                zones++;
        }

        var weights = new List<(RedspaceAnomalyEvent, float)>
        {
            (RedspaceAnomalyEvent.Rift, s.RiftWeight),
            (RedspaceAnomalyEvent.TimeLoop, s.TimeLoopWeight),
            (RedspaceAnomalyEvent.Doppelganger, s.DoppelgangerWeight),
            (RedspaceAnomalyEvent.BloodRain, s.BloodRainWeight),
            (RedspaceAnomalyEvent.MirrorCorridor, s.MirrorCorridorWeight),
            (RedspaceAnomalyEvent.Whispers, s.WhispersWeight),
            (RedspaceAnomalyEvent.GravityKnot, s.GravityKnotWeight),
        };

        if (zones >= s.AnomalyEventMax)
            weights.RemoveAll(w => IsZone(w.Item1));

        // A few tries: whispers may have nothing to replay, a loop may already be running.
        for (var i = 0; i < 3; i++)
        {
            var total = weights.Sum(w => Math.Max(0f, w.Item2));
            if (total <= 0f)
                return;

            var roll = _random.NextFloat() * total;
            var kind = weights[^1].Item1;
            foreach (var (k, w) in weights)
            {
                if ((roll -= Math.Max(0f, w)) <= 0f)
                {
                    kind = k;
                    break;
                }
            }

            if (TryTriggerEvent(kind, _random.Pick(victims), out _, out _))
                return;

            weights.RemoveAll(w => w.Item1 == kind);
        }
    }

    private static bool IsZone(RedspaceAnomalyEvent kind)
    {
        return kind is RedspaceAnomalyEvent.Rift or RedspaceAnomalyEvent.GravityKnot or RedspaceAnomalyEvent.BloodRain
            or RedspaceAnomalyEvent.MirrorCorridor or RedspaceAnomalyEvent.Doppelganger;
    }

    private void OnUnaffected(Entity<RedspaceAffectedComponent> ent, ref ComponentRemove args)
    {
        // Protected now (collar, beacon, gate), immune, or out of redspace: the body recovers at once.
        if (TerminatingOrDeleted(ent))
            return;

        RemCompDeferred<RedspaceMutationComponent>(ent);
        RemCompDeferred<RedspaceMirroredComponent>(ent);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Starts <paramref name="kind"/> on or next to <paramref name="target"/>. Fails (with a locale key in
    /// <paramref name="reason"/>) outside redspace, inside a suppressed area, for immune or dead targets.
    /// </summary>
    public bool TryTriggerEvent(RedspaceAnomalyEvent kind, EntityUid target, out EntityUid? spawned, out string reason)
    {
        spawned = null;
        reason = string.Empty;
        if (TerminatingOrDeleted(target))
        {
            reason = "redspace-event-fail-target";
            return false;
        }

        var xform = Transform(target);
        if (!_redspace.TryGetActive(xform.MapID, out var map, out var suppressors))
        {
            reason = "redspace-event-fail-not-redspace";
            return false;
        }

        if (_redspace.IsImmune(target) || HasComp<GhostComponent>(target))
        {
            reason = "redspace-event-fail-immune";
            return false;
        }

        if (TryComp<MobStateComponent>(target, out var mobState) && _mobState.IsDead(target, mobState))
        {
            reason = "redspace-event-fail-dead";
            return false;
        }

        var pos = _transform.GetWorldPosition(xform);
        if (RedspaceSuppressionSystem.IsSuppressed(suppressors, pos, out _))
        {
            reason = "redspace-event-fail-suppressed";
            return false;
        }

        var s = map.Comp.Settings;
        switch (kind)
        {
            case RedspaceAnomalyEvent.Rift:
                spawned = SpawnZone(RiftPrototype, kind, xform.MapID, PlaceNear(xform.MapID, pos, 2.5f, 4f, suppressors),
                    Next(s.RiftDurationMin, s.RiftDurationMax), s.RiftRadius);
                _audio.PlayPvs(RiftSound, spawned.Value);
                break;
            case RedspaceAnomalyEvent.GravityKnot:
                spawned = SpawnZone(GravityKnotPrototype, kind, xform.MapID, PlaceNear(xform.MapID, pos, 1.5f, 3f, suppressors),
                    s.GravityKnotDuration, s.GravityKnotRadius);
                break;
            case RedspaceAnomalyEvent.BloodRain:
                spawned = SpawnZone(BloodRainPrototype, kind, xform.MapID, PlaceNear(xform.MapID, pos, 0f, 1.5f, suppressors),
                    s.BloodRainDuration, s.BloodRainRadius);
                break;
            case RedspaceAnomalyEvent.MirrorCorridor:
            {
                var center = PlaceNear(xform.MapID, pos, 0f, 1f, suppressors);
                spawned = SpawnZone(MirrorPrototype, kind, xform.MapID, center, s.MirrorCorridorDuration,
                    MathF.Max(s.MirrorCorridorLength, s.MirrorCorridorWidth) / 2f);
                var zone = Comp<RedspaceZoneEventComponent>(spawned.Value);
                // Lies along X or along Y; the sprite is rotated with it.
                var vertical = _random.Prob(0.5f);
                _transform.SetWorldRotation(spawned.Value, vertical ? Angle.FromDegrees(90) : Angle.Zero);
                zone.HalfSize = new Vector2(s.MirrorCorridorLength, s.MirrorCorridorWidth) / 2f;
                break;
            }
            case RedspaceAnomalyEvent.TimeLoop:
                if (!StartTimeLoop(target, s, out reason))
                    return false;
                break;
            case RedspaceAnomalyEvent.Doppelganger:
                spawned = SpawnDoppelganger(target, xform.MapID, pos, s, suppressors);
                break;
            case RedspaceAnomalyEvent.Whispers:
                if (!TryWhisper(target, s, out reason))
                    return false;
                break;
            default:
                reason = "redspace-event-fail-type";
                return false;
        }

        if (spawned is { } zoneUid && IsZone(kind) && kind != RedspaceAnomalyEvent.Doppelganger)
            _popup.PopupEntity(Loc.GetString($"redspace-event-start-{kind.ToString().ToLowerInvariant()}"), target, target, PopupType.MediumCaution);

        return true;
    }

    /// <summary>Remembers a phrase for the whispers (normally fed by <see cref="EntitySpokeEvent"/>).</summary>
    public void RecordSpeech(EntityUid source, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        _speech.Add((source, Identity.Name(source, EntityManager), message, _timing.CurTime));
        if (_speech.Count > SpeechBuffer)
            _speech.RemoveAt(0);
    }

    /// <summary>Seconds until the next random event on the map (-1 when not rolled yet).</summary>
    public float NextEventIn(EntityUid mapUid)
    {
        return TryComp<RedspaceAnomalyMapComponent>(mapUid, out var comp) ? comp.NextEvent : -1f;
    }

    /// <summary>Running zone events on a map, for redspacestatus.</summary>
    public List<(RedspaceAnomalyEvent Kind, float Left)> ActiveEvents(MapId mapId)
    {
        var list = new List<(RedspaceAnomalyEvent, float)>();
        var query = EntityQueryEnumerator<RedspaceZoneEventComponent, TransformComponent>();
        while (query.MoveNext(out var zone, out var xform))
        {
            if (xform.MapID == mapId)
                list.Add((zone.Kind, zone.Left));
        }

        var dopp = EntityQueryEnumerator<RedspaceDoppelgangerStateComponent, TransformComponent>();
        while (dopp.MoveNext(out var state, out var xform))
        {
            if (xform.MapID == mapId)
                list.Add((RedspaceAnomalyEvent.Doppelganger, state.Left));
        }

        var loops = EntityQueryEnumerator<RedspaceTimeLoopComponent, TransformComponent>();
        while (loops.MoveNext(out var loop, out var xform))
        {
            if (xform.MapID == mapId)
                list.Add((RedspaceAnomalyEvent.TimeLoop, loop.Left));
        }

        return list;
    }

    #endregion

    #region Fast tick

    /// <summary>Advances every running event by <paramref name="dt"/> seconds. Public for tests.</summary>
    public void StepEvents(float dt)
    {
        _players.Clear();
        var actors = EntityQueryEnumerator<ActorComponent, MobStateComponent, TransformComponent>();
        while (actors.MoveNext(out var uid, out _, out var mobState, out var xform))
        {
            if (!_mobState.IsDead(uid, mobState))
                _players.Add(_transform.GetWorldPosition(xform));
        }

        _toDelete.Clear();
        var zones = EntityQueryEnumerator<RedspaceZoneEventComponent, TransformComponent>();
        while (zones.MoveNext(out var uid, out var zone, out var xform))
        {
            zone.Left -= dt;
            if (zone.Left <= 0f || !_redspace.TryGetActive(xform.MapID, out var map, out var suppressors))
            {
                _toDelete.Add(uid);
                continue;
            }

            var center = _transform.GetWorldPosition(xform);
            // Only near living players (same rule as the rest of redspace).
            if (!NearPlayer(center, map.Comp.Settings.ActiveRange + zone.Radius))
                continue;

            switch (zone.Kind)
            {
                case RedspaceAnomalyEvent.Rift:
                    StepRift((uid, zone), xform.MapID, center, map.Comp.Settings, suppressors, dt);
                    break;
                case RedspaceAnomalyEvent.GravityKnot:
                    StepKnot((uid, zone), xform.MapID, center, map.Comp.Settings, suppressors, dt);
                    break;
                case RedspaceAnomalyEvent.BloodRain:
                    StepRain((uid, zone), xform.MapID, center, map.Comp.Settings, suppressors, dt);
                    break;
                case RedspaceAnomalyEvent.MirrorCorridor:
                    StepMirror((uid, zone), xform, center, suppressors);
                    break;
            }
        }

        var dopps = EntityQueryEnumerator<RedspaceDoppelgangerStateComponent, TransformComponent>();
        while (dopps.MoveNext(out var uid, out var state, out var xform))
        {
            if (!StepDoppelganger((uid, state), xform, dt))
                _toDelete.Add(uid);
        }

        foreach (var uid in _toDelete)
        {
            if (HasComp<RedspaceDoppelgangerStateComponent>(uid))
                Vanish(uid);
            else
                QueueDel(uid);
        }

        var loops = EntityQueryEnumerator<RedspaceTimeLoopComponent>();
        _toDelete.Clear();
        while (loops.MoveNext(out var uid, out var loop))
        {
            if ((loop.Left -= dt) <= 0f)
                _toDelete.Add(uid);
        }

        foreach (var uid in _toDelete)
            FinishTimeLoop(uid);

        var jammed = EntityQueryEnumerator<RedspaceJammedComponent>();
        _toDelete.Clear();
        while (jammed.MoveNext(out var uid, out var jam))
        {
            if ((jam.Left -= dt) <= 0f)
                _toDelete.Add(uid);
        }

        foreach (var uid in _toDelete)
            RemComp<RedspaceJammedComponent>(uid);
    }

    private bool NearPlayer(Vector2 pos, float range)
    {
        foreach (var p in _players)
        {
            if ((p - pos).LengthSquared() <= range * range)
                return true;
        }

        return false;
    }

    /// <summary>Loose things and mobs in reach that redspace may move: not anchored, not in a container, not immune, not protected.</summary>
    private void GetMovable(MapId mapId, Vector2 center, float radius, List<RedspaceSuppressor> suppressors, EntityUid self,
        List<(EntityUid Uid, PhysicsComponent Body, Vector2 Pos)> result)
    {
        result.Clear();
        _near.Clear();
        _lookup.GetEntitiesInRange(mapId, center, radius, _near, LookupFlags.Dynamic | LookupFlags.Sundries);
        foreach (var uid in _near)
        {
            if (uid == self || !TryComp<PhysicsComponent>(uid, out var body) || body.BodyType == BodyType.Static)
                continue;

            var xform = Transform(uid);
            if (xform.Anchored || xform.ParentUid != xform.GridUid && xform.ParentUid != xform.MapUid)
                continue;

            if (_redspace.IsImmune(uid) || HasComp<GhostComponent>(uid) || HasComp<RedspaceZoneEventComponent>(uid)
                || HasComp<RedspaceDoppelgangerStateComponent>(uid))
                continue;

            var pos = _transform.GetWorldPosition(xform);
            if (RedspaceSuppressionSystem.IsSuppressed(suppressors, pos, out _))
                continue;

            result.Add((uid, body, pos));
        }
    }

    private readonly List<(EntityUid Uid, PhysicsComponent Body, Vector2 Pos)> _movable = new();

    private void StepRift(Entity<RedspaceZoneEventComponent> rift, MapId mapId, Vector2 center, RedspaceSettings s,
        List<RedspaceSuppressor> suppressors, float dt)
    {
        GetMovable(mapId, center, rift.Comp.Radius, suppressors, rift, _movable);
        foreach (var (uid, body, pos) in _movable)
        {
            var offset = center - pos;
            var dist = offset.Length();
            if (dist <= RiftSwallowRange)
            {
                Swallow(uid, mapId, center, s, suppressors);
                continue;
            }

            // Stronger the closer it is.
            var pull = s.RiftPull * (1.5f - 0.75f * dist / rift.Comp.Radius);
            var vel = body.LinearVelocity + offset / dist * pull * dt;
            if (vel.Length() > MaxPullSpeed)
                vel = Vector2.Normalize(vel) * MaxPullSpeed;

            _physics.SetLinearVelocity(uid, vel, body: body);
            _physics.WakeBody(uid, body: body);
        }
    }

    /// <summary>Whatever falls into a rift comes out at a random free spot far away, flung and (if alive) hurt.</summary>
    private void Swallow(EntityUid uid, MapId mapId, Vector2 center, RedspaceSettings s, List<RedspaceSuppressor> suppressors)
    {
        var target = center;
        for (var i = 0; i < 20; i++)
        {
            var candidate = center + _random.NextAngle().ToVec() * _random.NextFloat(s.RiftThrowMin, s.RiftThrowMax);
            if (!_redspace.IsFree(mapId, candidate) || RedspaceSuppressionSystem.IsSuppressed(suppressors, candidate, out _))
                continue;

            target = candidate;
            break;
        }

        if (target == center)
            target = center + _random.NextAngle().ToVec() * s.RiftThrowMin;

        _audio.PlayPvs(RiftSound, uid);
        _transform.SetMapCoordinates(uid, new MapCoordinates(target, mapId));
        if (TryComp<PhysicsComponent>(uid, out var body))
            _physics.SetLinearVelocity(uid, _random.NextAngle().ToVec() * 5f, body: body);

        Spawn(RiftExitPrototype, new MapCoordinates(target, mapId));
        _audio.PlayPvs(RiftExitSound, uid);

        if (HasComp<MobStateComponent>(uid))
        {
            var spec = new DamageSpecifier();
            spec.DamageDict[Blunt] = FixedPoint2.New(s.RiftDamage);
            _damage.TryChangeDamage(uid, spec, origin: null);
            _popup.PopupEntity(Loc.GetString("redspace-rift-swallowed"), uid, uid, PopupType.LargeCaution);
        }
    }

    private void StepKnot(Entity<RedspaceZoneEventComponent> knot, MapId mapId, Vector2 center, RedspaceSettings s,
        List<RedspaceSuppressor> suppressors, float dt)
    {
        GetMovable(mapId, center, knot.Comp.Radius, suppressors, knot, _movable);
        foreach (var (uid, body, pos) in _movable)
        {
            var offset = pos - center;
            var dist = offset.Length();
            if (dist < 0.2f)
                continue;

            var radial = offset / dist;
            var tangent = new Vector2(-radial.Y, radial.X);
            var vel = body.LinearVelocity + (tangent * s.GravityKnotOrbit - radial * s.GravityKnotInward) * dt;

            // Hard to walk out: most of the outward speed is eaten.
            var outward = Vector2.Dot(vel, radial);
            if (outward > 0f)
                vel -= radial * outward * MathF.Min(1f, 4f * dt);

            if (vel.Length() > MaxPullSpeed)
                vel = Vector2.Normalize(vel) * MaxPullSpeed;

            _physics.SetLinearVelocity(uid, vel, body: body);
            _physics.WakeBody(uid, body: body);
        }
    }

    private void StepRain(Entity<RedspaceZoneEventComponent> rain, MapId mapId, Vector2 center, RedspaceSettings s,
        List<RedspaceSuppressor> suppressors, float dt)
    {
        rain.Comp.Tick += dt;
        if (rain.Comp.Tick < 1f)
            return;

        var seconds = rain.Comp.Tick;
        rain.Comp.Tick = 0f;

        // A couple of drops become puddles each second.
        for (var i = 0; i < 2; i++)
        {
            var at = center + _random.NextAngle().ToVec() * _random.NextFloat(0f, rain.Comp.Radius);
            if (RedspaceSuppressionSystem.IsSuppressed(suppressors, at, out _))
                continue;

            // Puddles need a grid tile; drops over open space just vanish.
            if (!_mapManager.TryFindGridAt(mapId, at, out var gridUid, out _))
                continue;

            var coords = _transform.ToCoordinates(gridUid, new MapCoordinates(at, mapId));
            var blood = new Solution("Blood", FixedPoint2.New(4));
            _puddle.TrySpillAt(coords, blood, out _, sound: false);
        }

        _near.Clear();
        _lookup.GetEntitiesInRange(mapId, center, rain.Comp.Radius, _near, LookupFlags.Dynamic);
        foreach (var uid in _near)
        {
            if (!TryComp<MobStateComponent>(uid, out var mobState) || _mobState.IsDead(uid, mobState) || _redspace.IsImmune(uid))
                continue;

            var pos = _transform.GetWorldPosition(uid);
            if (RedspaceSuppressionSystem.IsSuppressed(suppressors, pos, out _))
                continue;

            var spec = new DamageSpecifier();
            spec.DamageDict[Cellular] = FixedPoint2.New(s.BloodRainDamage * seconds);
            _damage.TryChangeDamage(uid, spec, interruptsDoAfters: false, origin: null);

            if (TryComp<PhysicsComponent>(uid, out var body) && body.LinearVelocity.LengthSquared() > 0.25f
                && _random.Prob(Math.Clamp(s.BloodRainSlipChance * seconds, 0f, 1f))
                && _stun.TryKnockdown(uid, TimeSpan.FromSeconds(2), true))
            {
                _popup.PopupEntity(Loc.GetString("redspace-blood-rain-slip"), uid, uid, PopupType.SmallCaution);
            }
        }
    }

    private void StepMirror(Entity<RedspaceZoneEventComponent> mirror, TransformComponent xform, Vector2 center,
        List<RedspaceSuppressor> suppressors)
    {
        var rot = _transform.GetWorldRotation(xform);
        _near.Clear();
        _lookup.GetEntitiesInRange(xform.MapID, center, mirror.Comp.Radius + 0.5f, _near, LookupFlags.Dynamic);

        var inside = new HashSet<EntityUid>();
        foreach (var uid in _near)
        {
            if (!HasComp<MobStateComponent>(uid) || _redspace.IsImmune(uid))
                continue;

            var pos = _transform.GetWorldPosition(uid);
            if (RedspaceSuppressionSystem.IsSuppressed(suppressors, pos, out _))
                continue;

            var local = (-rot).RotateVec(pos - center);
            if (MathF.Abs(local.X) <= mirror.Comp.HalfSize.X && MathF.Abs(local.Y) <= mirror.Comp.HalfSize.Y)
                inside.Add(uid);
        }

        foreach (var uid in mirror.Comp.Members)
        {
            if (!inside.Contains(uid) && !Deleted(uid))
                RemComp<RedspaceMirroredComponent>(uid);
        }

        foreach (var uid in inside)
        {
            if (!mirror.Comp.Members.Contains(uid) && !HasComp<RedspaceMirroredComponent>(uid))
                _popup.PopupEntity(Loc.GetString("redspace-mirror-enter"), uid, uid, PopupType.SmallCaution);

            EnsureComp<RedspaceMirroredComponent>(uid);
        }

        mirror.Comp.Members.Clear();
        mirror.Comp.Members.UnionWith(inside);
    }

    private void OnZoneShutdown(Entity<RedspaceZoneEventComponent> ent, ref ComponentShutdown args)
    {
        foreach (var uid in ent.Comp.Members)
        {
            if (!TerminatingOrDeleted(uid))
                RemComp<RedspaceMirroredComponent>(uid);
        }

        ent.Comp.Members.Clear();
    }

    private EntityUid SpawnZone(string proto, RedspaceAnomalyEvent kind, MapId mapId, Vector2 pos, float duration, float radius)
    {
        var uid = Spawn(proto, new MapCoordinates(pos, mapId));
        var zone = EnsureComp<RedspaceZoneEventComponent>(uid);
        zone.Kind = kind;
        zone.Left = duration;
        zone.Duration = duration;
        zone.Radius = radius;
        return uid;
    }

    /// <summary>A spot <paramref name="min"/>–<paramref name="max"/> tiles from <paramref name="pos"/> that is free and not protected.</summary>
    private Vector2 PlaceNear(MapId mapId, Vector2 pos, float min, float max, List<RedspaceSuppressor> suppressors)
    {
        for (var i = 0; i < 12; i++)
        {
            var candidate = pos + _random.NextAngle().ToVec() * _random.NextFloat(min, max);
            if (_redspace.IsFree(mapId, candidate) && !RedspaceSuppressionSystem.IsSuppressed(suppressors, candidate, out _))
                return candidate;
        }

        return pos;
    }

    #endregion

    #region Time loop

    private bool StartTimeLoop(EntityUid target, RedspaceSettings s, out string reason)
    {
        reason = string.Empty;
        if (HasComp<RedspaceTimeLoopComponent>(target))
        {
            reason = "redspace-event-fail-busy";
            return false;
        }

        if (!HasComp<ActorComponent>(target) && !HasComp<MobStateComponent>(target))
        {
            reason = "redspace-event-fail-target";
            return false;
        }

        var xform = Transform(target);
        var loop = AddComp<RedspaceTimeLoopComponent>(target);
        loop.Left = s.TimeLoopDelay;
        loop.Origin = xform.Coordinates;
        if (TryComp<DamageableComponent>(target, out var damageable))
            loop.Snapshot = new DamageSpecifier(damageable.Damage);

        loop.Marker = Spawn(TimeLoopMarkerPrototype, loop.Origin);
        _popup.PopupEntity(Loc.GetString("redspace-event-start-timeloop"), target, target, PopupType.MediumCaution);
        _audio.PlayEntity(LoopSound, Filter.Entities(target), target, true);
        return true;
    }

    private void FinishTimeLoop(EntityUid uid)
    {
        if (!TryComp<RedspaceTimeLoopComponent>(uid, out var loop))
            return;

        var origin = loop.Origin;
        var snapshot = loop.Snapshot;
        RemComp<RedspaceTimeLoopComponent>(uid);

        var mapId = _transform.GetMapId(origin);
        var dead = TryComp<MobStateComponent>(uid, out var mobState) && _mobState.IsDead(uid, mobState);
        if (dead || _redspace.IsImmune(uid) || !_redspace.TryGetActive(Transform(uid).MapID, out _, out var suppressors)
            || Transform(uid).MapID != mapId
            || RedspaceSuppressionSystem.IsSuppressed(suppressors, _transform.GetWorldPosition(uid), out _)
            || !origin.IsValid(EntityManager))
        {
            if (!dead)
                _popup.PopupEntity(Loc.GetString("redspace-timeloop-broken"), uid, uid, PopupType.Small);
            return;
        }

        _transform.SetCoordinates(uid, origin);
        if (TryComp<PhysicsComponent>(uid, out var body))
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);

        if (snapshot != null && TryComp<DamageableComponent>(uid, out var damageable))
            _damage.SetDamage(uid, damageable, snapshot);

        _audio.PlayPvs(LoopSound, uid);
        _popup.PopupEntity(Loc.GetString("redspace-timeloop-snap"), uid, uid, PopupType.LargeCaution);
    }

    private void OnLoopShutdown(Entity<RedspaceTimeLoopComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Marker is { } marker && !TerminatingOrDeleted(marker))
            QueueDel(marker);
    }

    #endregion

    #region Doppelganger

    private EntityUid SpawnDoppelganger(EntityUid target, MapId mapId, Vector2 pos, RedspaceSettings s, List<RedspaceSuppressor> suppressors)
    {
        var at = PlaceNear(mapId, pos, 1.5f, 2.5f, suppressors);
        var uid = Spawn(DoppelgangerPrototype, new MapCoordinates(at, mapId));
        var visual = EnsureComp<RedspaceDoppelgangerComponent>(uid);
        visual.Source = target;
        Dirty(uid, visual);

        var state = EnsureComp<RedspaceDoppelgangerStateComponent>(uid);
        state.Target = target;
        state.Left = s.DoppelgangerLifetime;
        state.Start = at;
        state.Anchor = pos;
        state.HitCooldown = s.DoppelgangerHitCooldown;
        _popup.PopupEntity(Loc.GetString("redspace-event-start-doppelganger"), target, target, PopupType.MediumCaution);
        return uid;
    }

    /// <summary>Moves the doppelganger along the delayed track of its target and lets it hit; false = it should vanish.</summary>
    private bool StepDoppelganger(Entity<RedspaceDoppelgangerStateComponent> ent, TransformComponent xform, float dt)
    {
        var state = ent.Comp;
        if ((state.Left -= dt) <= 0f || TerminatingOrDeleted(state.Target))
            return false;

        if (!_redspace.TryGetActive(xform.MapID, out var map, out var suppressors))
            return false;

        var s = map.Comp.Settings;
        var targetXform = Transform(state.Target);
        if (targetXform.MapID != xform.MapID || _redspace.IsImmune(state.Target)
            || TryComp<MobStateComponent>(state.Target, out var mobState) && _mobState.IsDead(state.Target, mobState))
            return false;

        var (targetPos, targetRot) = _transform.GetWorldPositionRotation(targetXform);
        state.Clock += dt;
        state.Samples.Add((state.Clock, targetPos, targetRot));
        while (state.Samples.Count > 2 && state.Samples[1].Time <= state.Clock - s.DoppelgangerDelay)
            state.Samples.RemoveAt(0);

        var (time, delayedPos, delayedRot) = state.Samples[0];
        if (time > state.Clock - s.DoppelgangerDelay)
            (delayedPos, delayedRot) = (state.Anchor, targetRot);

        var newPos = state.Start + (delayedPos - state.Anchor);
        if (RedspaceSuppressionSystem.IsSuppressed(suppressors, newPos, out _))
            return false;

        _transform.SetWorldPositionRotation(ent, newPos, delayedRot);

        state.HitCooldown -= dt;
        if (state.HitCooldown <= 0f && (targetPos - newPos).Length() <= DoppelgangerHitRange
            && !RedspaceSuppressionSystem.IsSuppressed(suppressors, targetPos, out _))
        {
            state.HitCooldown = s.DoppelgangerHitCooldown;
            var spec = new DamageSpecifier();
            spec.DamageDict[Blunt] = FixedPoint2.New(s.DoppelgangerDamage);
            _damage.TryChangeDamage(state.Target, spec, origin: ent);
            _audio.PlayPvs(HitSound, state.Target);
            _popup.PopupEntity(Loc.GetString("redspace-doppelganger-hit"), state.Target, state.Target, PopupType.SmallCaution);
        }

        return true;
    }

    private void OnDoppelgangerDamaged(Entity<RedspaceDoppelgangerStateComponent> ent, ref DamageChangedEvent args)
    {
        if (args.DamageIncreased && !TerminatingOrDeleted(ent))
            Vanish(ent);
    }

    private void Vanish(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid))
            return;

        Spawn(RiftExitPrototype, Transform(uid).Coordinates);
        _popup.PopupCoordinates(Loc.GetString("redspace-doppelganger-vanish"), Transform(uid).Coordinates, PopupType.Small);
        QueueDel(uid);
    }

    #endregion

    #region Whispers

    private void OnSpoke(EntitySpokeEvent ev)
    {
        if (HasComp<ActorComponent>(ev.Source) && !HasComp<GhostComponent>(ev.Source))
            RecordSpeech(ev.Source, ev.Message);
    }

    private bool TryWhisper(EntityUid target, RedspaceSettings s, out string reason)
    {
        reason = string.Empty;
        if (!TryComp<ActorComponent>(target, out var actor))
        {
            reason = "redspace-event-fail-not-player";
            return false;
        }

        var now = _timing.CurTime;
        var lines = _speech.Where(l => l.Source != target && (now - l.Time).TotalSeconds <= s.WhispersMemory).ToList();
        if (lines.Count == 0)
        {
            reason = "redspace-event-fail-no-speech";
            return false;
        }

        var (source, name, message, _) = _random.Pick(lines);
        var escaped = FormattedMessage.EscapeText(message);
        string wrapped;
        if (_random.Prob(s.WhispersRadioChance) && _proto.TryIndex(CommonChannel, out var channel))
        {
            wrapped = Loc.GetString("redspace-whispers-radio",
                ("color", channel.Color.ToHex()), ("channel", $"\\[{channel.LocalizedName}\\]"), ("name", name), ("message", escaped));
            _chat.ChatMessageToOne(ChatChannel.Radio, message, wrapped, EntityUid.Invalid, false, actor.PlayerSession.Channel);
        }
        else
        {
            wrapped = Loc.GetString("redspace-whispers-local", ("name", name), ("message", escaped));
            _chat.ChatMessageToOne(ChatChannel.Local, message, wrapped, EntityUid.Invalid, false, actor.PlayerSession.Channel);
        }

        _audio.PlayEntity(WhisperSound, Filter.SinglePlayer(actor.PlayerSession), target, false);
        LastWhisper = (target, source, message);
        return true;
    }

    /// <summary>Last replayed phrase (target, original speaker, text), for tests and logs.</summary>
    public (EntityUid Target, EntityUid Source, string Message)? LastWhisper { get; private set; }

    #endregion

    #region Body effects

    private void StepMutations(EntityUid uid, RedspaceAffectedComponent affected, RedspaceSettings s, float dt)
    {
        if (TryComp<RedspaceMutationComponent>(uid, out var mut))
        {
            var changed = false;
            if (mut.Voice && (mut.VoiceLeft -= dt) <= 0f)
            {
                mut.Voice = false;
                changed = true;
            }

            if (mut.Slowdown < 1f && (mut.SlowLeft -= dt) <= 0f)
            {
                mut.Slowdown = 1f;
                changed = true;
                _shared.RefreshSpeed(uid);
            }

            if (mut.ExtraEye && (mut.EyeLeft -= dt) <= 0f)
            {
                mut.ExtraEye = false;
                changed = true;
            }

            if (!mut.Voice && mut.Slowdown >= 1f && !mut.ExtraEye)
            {
                RemComp<RedspaceMutationComponent>(uid);
                _popup.PopupEntity(Loc.GetString("redspace-mutation-gone"), uid, uid, PopupType.Small);
            }
            else if (changed)
                Dirty(uid, mut);
        }

        if (!s.MutationsEnabled || affected.Exposure < s.MutationExposure
            || !_random.Prob(Math.Clamp(s.MutationChancePerMinute * dt / 60f, 0f, 1f)))
            return;

        TryMutate(uid, s);
    }

    /// <summary>Gives a random mutation the mob does not have yet. Public for tests and the admin command.</summary>
    public bool TryMutate(EntityUid uid, RedspaceSettings s, int? which = null)
    {
        if (_redspace.IsImmune(uid))
            return false;

        var mut = EnsureComp<RedspaceMutationComponent>(uid);
        var free = new List<int>();
        if (!mut.Voice)
            free.Add(0);
        if (mut.Slowdown >= 1f)
            free.Add(1);
        if (!mut.ExtraEye)
            free.Add(2);

        if (free.Count == 0)
            return false;

        var pick = which is { } w && free.Contains(w) ? w : _random.Pick(free);
        switch (pick)
        {
            case 0:
                mut.Voice = true;
                mut.VoiceLeft = s.MutationDuration;
                _popup.PopupEntity(Loc.GetString("redspace-mutation-voice"), uid, uid, PopupType.MediumCaution);
                break;
            case 1:
                mut.Slowdown = s.MutationSlowdown;
                mut.SlowLeft = s.MutationDuration;
                _popup.PopupEntity(Loc.GetString("redspace-mutation-slow"), uid, uid, PopupType.MediumCaution);
                break;
            default:
                mut.ExtraEye = true;
                mut.EyeLeft = s.MutationDuration;
                _popup.PopupEntity(Loc.GetString("redspace-mutation-eye"), uid, uid, PopupType.MediumCaution);
                break;
        }

        Dirty(uid, mut);
        _shared.RefreshSpeed(uid);
        return true;
    }

    private void OnAccent(Entity<RedspaceMutationComponent> ent, ref AccentGetEvent args)
    {
        if (ent.Comp.Voice)
            args.Message = Distort(args.Message);
    }

    /// <summary>Stammers word starts and echoes word ends: «к-к-красный космос... осмос».</summary>
    public string Distort(string message)
    {
        var words = message.Split(' ');
        var sb = new StringBuilder(message.Length * 2);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (i > 0)
                sb.Append(' ');

            if (word.Length > 2 && char.IsLetter(word[0]) && _random.Prob(0.35f))
                sb.Append(word[0]).Append('-').Append(char.ToLowerInvariant(word[0])).Append('-');

            sb.Append(word);
            if (word.Length > 4 && _random.Prob(0.2f))
                sb.Append("... ").Append(word.Substring(word.Length - 3).ToLowerInvariant());
        }

        // Always audibly wrong, even for one short word.
        if (sb.Length == message.Length)
            sb.Append("...");

        return sb.ToString();
    }

    /// <summary>Shows a fake enemy and plays footsteps to this player only. Public for tests.</summary>
    public bool TrySendHallucination(EntityUid uid, ICommonSession session, MapId mapId, List<RedspaceSuppressor> suppressors)
    {
        var pos = _transform.GetWorldPosition(uid);
        for (var i = 0; i < 8; i++)
        {
            var at = pos + _random.NextAngle().ToVec() * _random.NextFloat(4f, 7f);
            if (!_redspace.IsFree(mapId, at) || RedspaceSuppressionSystem.IsSuppressed(suppressors, at, out _))
                continue;

            var coords = _transform.ToCoordinates(new MapCoordinates(at, mapId));
            var kind = _random.Prob(0.6f) ? RedspaceHallucinationKind.Stalker : RedspaceHallucinationKind.Crawler;
            RaiseNetworkEvent(new RedspaceHallucinationEvent(GetNetCoordinates(coords), kind, _random.NextFloat(3f, 5f)), session);
            _audio.PlayStatic(Footsteps, Filter.SinglePlayer(session), coords, false);
            return true;
        }

        return false;
    }

    /// <summary>Food rots faster, batteries drain, guns jam: for what the player holds and wears.</summary>
    public void ApplyItemDecay(EntityUid uid, RedspaceSettings s, float dt)
    {
        foreach (var item in _inventory.GetHandOrInventoryEntities(uid))
        {
            if (s.FoodRotMultiplier > 1f && HasComp<PerishableComponent>(item) && !HasComp<RottingComponent>(item)
                && _rotting.IsRotProgressing(item, null))
                _rotting.ReduceAccumulator(item, -TimeSpan.FromSeconds(dt * (s.FoodRotMultiplier - 1f)));

            if (s.BatteryDrainPerMinute > 0f)
            {
                if (TryComp<BatteryComponent>(item, out var battery))
                    _battery.UseCharge(item, battery.MaxCharge * s.BatteryDrainPerMinute * dt / 60f, battery);
                else if (_powerCell.TryGetBatteryFromSlot(item, out var cell, out var cellBattery))
                    _battery.UseCharge(cell.Value, cellBattery.MaxCharge * s.BatteryDrainPerMinute * dt / 60f, cellBattery);
            }

            if (s.GunJamChancePerMinute > 0f && HasComp<GunComponent>(item) && !HasComp<RedspaceJammedComponent>(item)
                && _random.Prob(Math.Clamp(s.GunJamChancePerMinute * dt / 60f, 0f, 1f)))
                Jam(item, uid, s.GunJamDuration);
        }
    }

    /// <summary>Jams a gun for <paramref name="seconds"/>.</summary>
    public void Jam(EntityUid gun, EntityUid? holder, float seconds)
    {
        var jam = EnsureComp<RedspaceJammedComponent>(gun);
        jam.Left = seconds;
        if (holder is { } h)
        {
            _popup.PopupEntity(Loc.GetString("redspace-gun-jam-start", ("gun", gun)), h, h, PopupType.SmallCaution);
            _audio.PlayEntity(JamSound, Filter.Entities(h), gun, false);
        }
    }

    #endregion

    private float Next(float min, float max)
    {
        return max > min ? _random.NextFloat(min, max) : min;
    }

    private bool Tick(ref float timer, float min, float max, float dt)
    {
        if (timer < 0f)
        {
            timer = Next(min, max);
            return false;
        }

        if ((timer -= dt) > 0f)
            return false;

        timer = Next(min, max);
        return true;
    }
}
