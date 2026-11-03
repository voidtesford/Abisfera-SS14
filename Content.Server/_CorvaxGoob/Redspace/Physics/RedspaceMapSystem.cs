using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server.Electrocution;
using Content.Server.Light.Components;
using Content.Server.Light.EntitySystems;
using Content.Server.Medical;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Alert;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Gravity;
using Content.Shared.Inventory;
using Content.Shared.Jittering;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Radio.Components;
using Content.Shared.Throwing;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.Markdown.Value;
using Robust.Shared.Spawners;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Redspace.Physics;

/// <summary>
/// Runs redspace on maps with <see cref="RedspaceMapComponent"/>.
/// Performance: nothing runs per tick over the whole world. Once per <see cref="StepTime"/> it looks only at
/// mobs within <see cref="RedspaceSettings.ActiveRange"/> of living players on redspace maps; every
/// <see cref="FastTime"/> it moves echoes, wraps affected mobs and bends the (few) flying projectiles.
/// </summary>
public sealed class RedspaceMapSystem : EntitySystem
{
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly ElectrocutionSystem _electrocution = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PoweredLightSystem _lights = default!;
    [Dependency] private readonly RedspaceAffectedSystem _affected = default!;
    [Dependency] private readonly RedspaceSuppressionSystem _suppression = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedJitteringSystem _jitter = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly VomitSystem _vomit = default!;

    public const float StepTime = 1f;
    public const float FastTime = 0.1f;
    public const int HallucinationLines = 10;

    private static readonly ProtoId<DamageTypePrototype> Cellular = "Cellular";
    private static readonly ProtoId<DamageTypePrototype> Shock = "Shock";
    public static readonly EntProtoId EchoPrototype = "RedspaceEcho";
    public static readonly EntProtoId AnomalyPrototype = "RedspaceAnomalyPatch";

    private float _stepAccumulator;
    private float _fastAccumulator;

    /// <summary>Enabled redspace maps and their suppressors, refreshed every step.</summary>
    private readonly Dictionary<MapId, (Entity<RedspaceMapComponent> Map, List<RedspaceSuppressor> Suppressors)> _active = new();
    private readonly HashSet<Entity<MobStateComponent>> _mobs = new();
    private readonly HashSet<EntityUid> _affectedNow = new();
    private readonly List<EntityUid> _onMap = new();
    private readonly List<EntityUid> _toRemove = new();
    private readonly HashSet<Entity<PoweredLightComponent>> _lightSet = new();
    private readonly Dictionary<EntityUid, TimeSpan> _flickering = new();
    private readonly Dictionary<EntityUid, EntityUid> _ambient = new();
    private readonly Stack<List<RedspaceSuppressor>> _listPool = new();

    private static readonly Dictionary<string, FieldInfo> SettingFields = typeof(RedspaceSettings)
        .GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(f => f.GetCustomAttribute<DataFieldAttribute>() != null)
        .ToDictionary(f => f.GetCustomAttribute<DataFieldAttribute>()!.Tag ?? char.ToLowerInvariant(f.Name[0]) + f.Name[1..],
            f => f, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> SettingNames => SettingFields.Keys;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RedspaceMapComponent, ComponentStartup>(OnMapStartup);
        SubscribeLocalEvent<RedspaceMapComponent, ComponentShutdown>(OnMapShutdown);
        SubscribeLocalEvent<ThrownEvent>(OnThrown);
    }

    #region Settings

    private void OnMapStartup(Entity<RedspaceMapComponent> ent, ref ComponentStartup args)
    {
        ApplySettings(ent);
        var s = ent.Comp.Settings;
        ent.Comp.CurrentAngle = s.CurrentRandomDirection ? _random.NextFloat(0f, 360f) : s.CurrentDirection;
        ent.Comp.NextWave = Next(s.WaveIntervalMin, s.WaveIntervalMax);
        ent.Comp.NextWeightless = Next(s.WeightlessIntervalMin, s.WeightlessIntervalMax);
        ent.Comp.NextAnomaly = Next(s.AnomalyIntervalMin, s.AnomalyIntervalMax);
    }

    private void OnMapShutdown(Entity<RedspaceMapComponent> ent, ref ComponentShutdown args)
    {
        foreach (var anomaly in ent.Comp.Anomalies)
            QueueDel(anomaly);
        ent.Comp.Anomalies.Clear();
    }

    /// <summary>Rebuilds the effective settings: a copy of the preset with <see cref="RedspaceMapComponent.Overrides"/> applied.</summary>
    public void ApplySettings(Entity<RedspaceMapComponent> ent)
    {
        var settings = _proto.TryIndex(ent.Comp.Preset, out var preset)
            ? _serialization.CreateCopy(preset.Settings, notNullableOverride: true)
            : new RedspaceSettings();

        foreach (var (key, value) in ent.Comp.Overrides)
        {
            if (!TrySetField(settings, key, value, out var error))
                Log.Error($"Bad redspace override '{key}: {value}' on {ToPrettyString(ent)}: {error}");
        }

        ent.Comp.Settings = settings;
    }

    /// <summary>Sets one settings field from text, as YAML would read it.</summary>
    public bool TrySetField(RedspaceSettings settings, string key, string value, out string? error)
    {
        error = null;
        if (!SettingFields.TryGetValue(key, out var field))
        {
            error = "unknown field";
            return false;
        }

        try
        {
            field.SetValue(settings, _serialization.Read(field.FieldType, new ValueDataNode(value), notNullableOverride: true));
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>Makes <paramref name="mapUid"/> redspace with a preset, or switches it off when <paramref name="preset"/> is null.</summary>
    public void SetRedspace(EntityUid mapUid, string? preset, Dictionary<string, string>? overrides = null)
    {
        if (preset == null)
        {
            RemComp<RedspaceMapComponent>(mapUid);
            return;
        }

        RemComp<RedspaceMapComponent>(mapUid);
        var comp = Factory.GetComponent<RedspaceMapComponent>();
        comp.Preset = preset;
        if (overrides != null)
            comp.Overrides = new Dictionary<string, string>(overrides);
        AddComp(mapUid, comp, true);
    }

    #endregion

    #region Suppression queries

    /// <summary>Whether <paramref name="coords"/> is safe from redspace (not a redspace map, disabled, or suppressed).</summary>
    public bool IsSuppressed(MapCoordinates coords, out EntityUid? by)
    {
        by = null;
        if (!_map.TryGetMap(coords.MapId, out var mapUid) || !TryComp<RedspaceMapComponent>(mapUid, out var comp) || !comp.Enabled)
            return true;

        var list = new List<RedspaceSuppressor>();
        _suppression.GetSuppressors(coords.MapId, comp.Settings.GateSuppressionRadius, list);
        return RedspaceSuppressionSystem.IsSuppressed(list, coords.Position, out by);
    }

    /// <summary>Whether redspace acts on things at <paramref name="coords"/> at all.</summary>
    public bool IsRedspace(MapCoordinates coords)
    {
        return _map.TryGetMap(coords.MapId, out var mapUid) && TryComp<RedspaceMapComponent>(mapUid, out var comp) && comp.Enabled;
    }

    #endregion

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _fastAccumulator += frameTime;
        if (_fastAccumulator >= FastTime)
        {
            FastStep(_fastAccumulator);
            _fastAccumulator = 0f;
        }

        _stepAccumulator += frameTime;
        if (_stepAccumulator >= StepTime)
        {
            Step(_stepAccumulator);
            _stepAccumulator = 0f;
        }
    }

    #region Step

    /// <summary>One full redspace step of <paramref name="dt"/> seconds. Public so tests can fast-forward.</summary>
    public void Step(float dt)
    {
        foreach (var (_, entry) in _active)
            _listPool.Push(entry.Suppressors);
        _active.Clear();
        _affectedNow.Clear();

        _suppression.UpdateBeacons(dt);

        var maps = EntityQueryEnumerator<RedspaceMapComponent, MapComponent>();
        while (maps.MoveNext(out var mapUid, out var redspace, out var mapComp))
        {
            if (!redspace.Enabled)
                continue;

            var list = _listPool.Count > 0 ? _listPool.Pop() : new List<RedspaceSuppressor>();
            _suppression.GetSuppressors(mapComp.MapId, redspace.Settings.GateSuppressionRadius, list);
            _active[mapComp.MapId] = ((mapUid, redspace), list);

            _suppression.UpdateCollars(mapComp.MapId, list, dt);
            // Collars may have just run dry.
            _suppression.GetSuppressors(mapComp.MapId, redspace.Settings.GateSuppressionRadius, list);

            StepMap((mapUid, redspace), mapComp.MapId, list, dt);
        }

        // Everything that is no longer affected (moved away, got suppressed, map turned off).
        _toRemove.Clear();
        var affected = EntityQueryEnumerator<RedspaceAffectedComponent>();
        while (affected.MoveNext(out var uid, out _))
        {
            if (!_affectedNow.Contains(uid))
                _toRemove.Add(uid);
        }

        foreach (var uid in _toRemove)
            Unaffect(uid);

        StepSlides();
        StepLights();
    }

    private void StepMap(Entity<RedspaceMapComponent> map, MapId mapId, List<RedspaceSuppressor> suppressors, float dt)
    {
        var s = map.Comp.Settings;

        // Map-wide timers.
        map.Comp.CurrentAngle = (map.Comp.CurrentAngle + s.CurrentTurnRate * dt / 60f) % 360f;
        var wave = false;
        if (s.WavesEnabled && (map.Comp.NextWave -= dt) <= 0f)
        {
            map.Comp.NextWave = Next(s.WaveIntervalMin, s.WaveIntervalMax);
            wave = true;
        }

        var weightlessStarted = false;
        if (map.Comp.WeightlessLeft > 0f)
            map.Comp.WeightlessLeft -= dt;
        if (s.WeightlessEnabled && (map.Comp.NextWeightless -= dt) <= 0f)
        {
            map.Comp.NextWeightless = Next(s.WeightlessIntervalMin, s.WeightlessIntervalMax);
            map.Comp.WeightlessLeft = s.WeightlessDuration;
            weightlessStarted = true;
        }

        if (!s.WeightlessEnabled)
            map.Comp.WeightlessLeft = 0f;

        // Who is near a living player.
        _onMap.Clear();
        var players = EntityQueryEnumerator<ActorComponent, MobStateComponent, TransformComponent>();
        while (players.MoveNext(out var player, out _, out var mobState, out var xform))
        {
            if (xform.MapID != mapId || _mobState.IsDead(player, mobState))
                continue;

            _mobs.Clear();
            _lookup.GetEntitiesInRange(xform.Coordinates, s.ActiveRange, _mobs);
            foreach (var mob in _mobs)
            {
                if (!_mobState.IsDead(mob, mob.Comp) && _affectedNow.Add(mob))
                    _onMap.Add(mob);
            }

            if (_affectedNow.Add(player))
                _onMap.Add(player);
        }

        PruneAnomalies(map.Comp, mapId);
        var waveDir = new Angle(_random.NextFloat(0f, MathF.Tau)).ToVec();

        foreach (var uid in _onMap)
        {
            var pos = _transform.GetWorldPosition(uid);
            if (RedspaceSuppressionSystem.IsSuppressed(suppressors, pos, out _))
            {
                _affectedNow.Remove(uid);
                continue;
            }

            var comp = Affect(uid, map, pos);
            var isPlayer = HasComp<ActorComponent>(uid);

            if (wave && TryComp<PhysicsComponent>(uid, out var body))
            {
                _physics.SetLinearVelocity(uid, body.LinearVelocity + waveDir * s.WaveStrength, body: body);
                if (isPlayer)
                {
                    _audio.PlayGlobal(s.WaveSound, uid);
                    _popup.PopupEntity(Loc.GetString("redspace-wave"), uid, uid, PopupType.SmallCaution);
                }
            }

            if (weightlessStarted && isPlayer)
                _popup.PopupEntity(Loc.GetString("redspace-weightless"), uid, uid, PopupType.SmallCaution);

            StepOrganism(uid, comp, s, dt);

            if (s.BlinkEnabled && _random.Prob(Math.Clamp(s.BlinkChancePerMinute * dt / 60f, 0f, 1f)))
                Blink(uid, mapId, pos, s, suppressors);

            if (isPlayer)
                StepPlayer(uid, comp, map, mapId, pos, suppressors, dt);
        }

        if (s.AnomaliesEnabled && (map.Comp.NextAnomaly -= dt) <= 0f)
        {
            map.Comp.NextAnomaly = Next(s.AnomalyIntervalMin, s.AnomalyIntervalMax);
            SpawnAnomaly(map, mapId, suppressors);
        }
    }

    /// <summary>Gives a mob the redspace marker and keeps its values up to date (dirtied only on change).</summary>
    private RedspaceAffectedComponent Affect(EntityUid uid, Entity<RedspaceMapComponent> map, Vector2 pos)
    {
        var s = map.Comp.Settings;
        var comp = EnsureComp<RedspaceAffectedComponent>(uid);

        var friction = s.InertiaEnabled ? s.MobFriction : 1f;
        var accel = s.InertiaEnabled ? s.MobAcceleration : 1f;
        var speed = 1f;
        if (s.AnomaliesEnabled)
        {
            foreach (var anomaly in map.Comp.Anomalies)
            {
                if (!TryComp<RedspaceAnomalyPatchComponent>(anomaly, out var patch))
                    continue;

                if ((_transform.GetWorldPosition(anomaly) - pos).LengthSquared() <= patch.Radius * patch.Radius)
                {
                    speed = patch.Speed;
                    break;
                }
            }
        }

        var drift = s.CurrentEnabled
            ? Angle.FromDegrees(map.Comp.CurrentAngle).ToVec() * s.CurrentStrength
            : Vector2.Zero;
        // Round so the slowly turning current doesn't dirty the component every second.
        drift = new Vector2(MathF.Round(drift.X, 1), MathF.Round(drift.Y, 1));
        var weightless = s.WeightlessEnabled && map.Comp.WeightlessLeft > 0f;
        var doAfter = s.DoAfterMultiplier;

        var refresh = !MathHelper.CloseTo(comp.Friction, friction) || !MathHelper.CloseTo(comp.Acceleration, accel)
                      || !MathHelper.CloseTo(comp.Speed, speed);
        var changed = refresh || comp.Drift != drift || comp.Weightless != weightless || !MathHelper.CloseTo(comp.DoAfter, doAfter);

        if (!changed)
            return comp;

        var weightlessChanged = comp.Weightless != weightless;
        comp.Friction = friction;
        comp.Acceleration = accel;
        comp.Speed = speed;
        comp.Drift = drift;
        comp.Weightless = weightless;
        comp.DoAfter = doAfter;
        Dirty(uid, comp);

        if (refresh)
            _affected.Refresh(uid);

        if (weightlessChanged && HasComp<AlertsComponent>(uid))
        {
            if (weightless)
                _alerts.ShowAlert(uid, SharedGravitySystem.WeightlessAlert);
            else
                _alerts.ClearAlert(uid, SharedGravitySystem.WeightlessAlert);
        }

        return comp;
    }

    private void Unaffect(EntityUid uid)
    {
        if (_ambient.Remove(uid, out var audio))
            _audio.Stop(audio);

        if (TryComp<RedspaceAffectedComponent>(uid, out var comp) && comp.Weightless && HasComp<AlertsComponent>(uid))
            _alerts.ClearAlert(uid, SharedGravitySystem.WeightlessAlert);

        RemComp<RedspaceAffectedComponent>(uid);
    }

    #endregion

    #region Organism

    /// <summary>Damage per second an unprotected mob takes, for tests and docs.</summary>
    public DamageSpecifier DamagePerSecond(RedspaceSettings s)
    {
        var spec = new DamageSpecifier();
        if (s.CellularPerSecond > 0f)
            spec.DamageDict[Cellular] = FixedPoint2.New(s.CellularPerSecond);
        if (s.ShockPerSecond > 0f)
            spec.DamageDict[Shock] = FixedPoint2.New(s.ShockPerSecond);
        return spec;
    }

    private void StepOrganism(EntityUid uid, RedspaceAffectedComponent comp, RedspaceSettings s, float dt)
    {
        if (!s.OrganismEnabled)
            return;

        comp.DamageAccumulator += dt;
        var interval = Math.Max(0.1f, s.DamageInterval);
        while (comp.DamageAccumulator >= interval)
        {
            comp.DamageAccumulator -= interval;
            _damage.TryChangeDamage(uid, DamagePerSecond(s) * interval, interruptsDoAfters: false, origin: null);

            if (_random.Prob(Math.Clamp(s.JitterChance, 0f, 1f)))
                _jitter.DoJitter(uid, TimeSpan.FromSeconds(3), true, 6f, 6f);
        }

        if (s.VomitChancePerMinute > 0f && _random.Prob(Math.Clamp(s.VomitChancePerMinute * dt / 60f, 0f, 1f)))
            _vomit.Vomit(uid, -10f, -10f);
    }

    #endregion

    #region Player-centred effects

    private void StepPlayer(EntityUid uid, RedspaceAffectedComponent comp, Entity<RedspaceMapComponent> map, MapId mapId,
        Vector2 pos, List<RedspaceSuppressor> suppressors, float dt)
    {
        var s = map.Comp.Settings;

        if (s.AmbientEnabled && s.AmbientSound != null && !_ambient.ContainsKey(uid))
        {
            var audio = _audio.PlayGlobal(s.AmbientSound, uid);
            if (audio != null)
                _ambient[uid] = audio.Value.Entity;
        }
        else if (!s.AmbientEnabled && _ambient.Remove(uid, out var old))
        {
            _audio.Stop(old);
        }

        if (s.HallucinationsEnabled && Tick(ref comp.NextHallucination, s.HallucinationIntervalMin, s.HallucinationIntervalMax, dt))
            Hallucinate(uid, mapId, pos, s);

        if (s.EchoEnabled && Tick(ref comp.NextEcho, s.EchoIntervalMin, s.EchoIntervalMax, dt))
            SpawnEcho(uid, s);

        if (s.DischargesEnabled && Tick(ref comp.NextDischarge, s.DischargeIntervalMin, s.DischargeIntervalMax, dt))
            Discharge(mapId, pos, s, suppressors);

        if (s.LightFlickerEnabled && Tick(ref comp.NextFlicker, s.LightFlickerIntervalMin, s.LightFlickerIntervalMax, dt))
            FlickerLights(uid, s, suppressors);

        if (s.RadioStaticEnabled && Tick(ref comp.NextStatic, s.RadioStaticIntervalMin, s.RadioStaticIntervalMax, dt))
            RadioStatic(uid, s);
    }

    private void Hallucinate(EntityUid uid, MapId mapId, Vector2 pos, RedspaceSettings s)
    {
        if (_random.Prob(0.6f))
        {
            var line = Loc.GetString($"redspace-hallucination-{_random.Next(1, HallucinationLines + 1)}");
            _popup.PopupEntity(line, uid, uid, PopupType.Small);
        }

        if (s.HallucinationSound != null)
        {
            var offset = _random.NextAngle().ToVec() * _random.NextFloat(2f, 5f);
            var coords = _transform.ToCoordinates(new MapCoordinates(pos + offset, mapId));
            _audio.PlayStatic(s.HallucinationSound, uid, coords);
        }
    }

    private void Discharge(MapId mapId, Vector2 center, RedspaceSettings s, List<RedspaceSuppressor> suppressors)
    {
        var pos = center + _random.NextAngle().ToVec() * _random.NextFloat(1f, Math.Max(1f, s.DischargeRange));
        if (RedspaceSuppressionSystem.IsSuppressed(suppressors, pos, out _))
            return;

        var fx = Spawn(s.DischargeEffect, new MapCoordinates(pos, mapId));
        if (!_random.Prob(Math.Clamp(s.DischargeShockChance, 0f, 1f)))
            return;

        _mobs.Clear();
        _lookup.GetEntitiesInRange(new MapCoordinates(pos, mapId), s.DischargeShockRadius, _mobs);
        foreach (var mob in _mobs)
        {
            if (RedspaceSuppressionSystem.IsSuppressed(suppressors, _transform.GetWorldPosition(mob), out _))
                continue;

            _electrocution.TryDoElectrocution(mob, fx, s.DischargeShockDamage, TimeSpan.FromSeconds(s.DischargeStun), true);
        }
    }

    private void FlickerLights(EntityUid uid, RedspaceSettings s, List<RedspaceSuppressor> suppressors)
    {
        _lightSet.Clear();
        _lookup.GetEntitiesInRange(Transform(uid).Coordinates, s.LightFlickerRadius, _lightSet, LookupFlags.Static | LookupFlags.Sundries);
        if (_lightSet.Count == 0)
            return;

        var end = _timing.CurTime + TimeSpan.FromSeconds(s.LightFlickerDuration);
        var count = 0;
        foreach (var light in _lightSet.OrderBy(_ => _random.Next()))
        {
            if (count++ >= 3)
                break;

            if (_flickering.ContainsKey(light) ||
                RedspaceSuppressionSystem.IsSuppressed(suppressors, _transform.GetWorldPosition(light), out _))
                continue;

            _lights.ToggleBlinkingLight(light, light.Comp, true);
            _flickering[light] = end;
        }
    }

    private void StepLights()
    {
        if (_flickering.Count == 0)
            return;

        var now = _timing.CurTime;
        _toRemove.Clear();
        foreach (var (light, end) in _flickering)
        {
            if (end <= now || TerminatingOrDeleted(light))
                _toRemove.Add(light);
        }

        foreach (var light in _toRemove)
        {
            _flickering.Remove(light);
            if (TryComp<PoweredLightComponent>(light, out var comp))
                _lights.ToggleBlinkingLight(light, comp, false);
        }
    }

    private void RadioStatic(EntityUid uid, RedspaceSettings s)
    {
        if (!_inventory.TryGetSlotEntity(uid, "ears", out var ears) || !HasComp<HeadsetComponent>(ears))
            return;

        _popup.PopupEntity(Loc.GetString("redspace-radio-static"), uid, uid, PopupType.Small);
        _audio.PlayGlobal(s.RadioStaticSound, uid);
    }

    #endregion

    #region Space

    private void Blink(EntityUid uid, MapId mapId, Vector2 pos, RedspaceSettings s, List<RedspaceSuppressor> suppressors)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var dist = _random.Next(Math.Max(1, s.BlinkMinDistance), Math.Max(s.BlinkMinDistance, s.BlinkMaxDistance) + 1);
            var dir = _random.Next(8);
            var offset = new Angle(dir * MathF.PI / 4f).ToVec() * dist;
            offset = new Vector2(MathF.Round(offset.X), MathF.Round(offset.Y));
            var target = pos + offset;

            if (RedspaceSuppressionSystem.IsSuppressed(suppressors, target, out _) || !IsFree(mapId, target))
                continue;

            var start = new MapCoordinates(pos, mapId);
            var end = new MapCoordinates(target, mapId);
            Spawn(s.DischargeEffect, start);
            _transform.SetMapCoordinates(uid, end);
            Spawn(s.DischargeEffect, end);
            _audio.PlayPvs(s.BlinkSound, uid);
            return;
        }
    }

    /// <summary>Open floor (or plain space on a gridless map) that nothing solid blocks.</summary>
    private bool IsFree(MapId mapId, Vector2 pos)
    {
        if (!_mapManager.TryFindGridAt(mapId, pos, out var gridUid, out var grid))
            return true;

        if (!_map.TryGetTileRef(gridUid, grid, _map.WorldToTile(gridUid, grid, pos), out var tile) || _turf.IsSpace(tile))
            return false;

        return !_turf.IsTileBlocked(tile, CollisionGroup.Impassable | CollisionGroup.MidImpassable);
    }

    private void SpawnAnomaly(Entity<RedspaceMapComponent> map, MapId mapId, List<RedspaceSuppressor> suppressors)
    {
        var s = map.Comp.Settings;
        if (map.Comp.Anomalies.Count >= s.AnomalyMax)
            return;

        // Next to a random affected player on this map.
        EntityUid? anchor = null;
        var seen = 0;
        foreach (var uid in _onMap)
        {
            if (!_affectedNow.Contains(uid) || !HasComp<ActorComponent>(uid))
                continue;

            if (_random.Next(++seen) == 0)
                anchor = uid;
        }

        if (anchor == null)
            return;

        var pos = _transform.GetWorldPosition(anchor.Value) + _random.NextAngle().ToVec() * _random.NextFloat(3f, 9f);
        if (RedspaceSuppressionSystem.IsSuppressed(suppressors, pos, out _))
            return;

        var patch = Spawn(AnomalyPrototype, new MapCoordinates(pos, mapId));
        var comp = EnsureComp<RedspaceAnomalyPatchComponent>(patch);
        comp.Radius = s.AnomalyRadius;
        comp.Speed = s.AnomalySpeed;
        EnsureComp<TimedDespawnComponent>(patch).Lifetime = s.AnomalyLifetime;
        map.Comp.Anomalies.Add(patch);
    }

    private void PruneAnomalies(RedspaceMapComponent comp, MapId mapId)
    {
        comp.Anomalies.RemoveAll(uid => TerminatingOrDeleted(uid) || Transform(uid).MapID != mapId);

        // Patches placed by hand or by a mapper count too.
        var query = EntityQueryEnumerator<RedspaceAnomalyPatchComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapID == mapId && !comp.Anomalies.Contains(uid))
                comp.Anomalies.Add(uid);
        }
    }

    #endregion

    #region Fast step: echoes, wrap-around, projectiles

    private void FastStep(float dt)
    {
        if (_active.Count == 0)
        {
            CleanupEchoes();
            return;
        }

        StepEchoes();

        foreach (var (mapId, entry) in _active)
        {
            var s = entry.Map.Comp.Settings;
            if (!s.WrapEnabled)
                continue;

            var size = s.WrapMax - s.WrapMin;
            if (size.X <= 0f || size.Y <= 0f)
                continue;

            var affected = EntityQueryEnumerator<RedspaceAffectedComponent, TransformComponent>();
            while (affected.MoveNext(out var uid, out _, out var xform))
            {
                if (xform.MapID != mapId)
                    continue;

                var pos = _transform.GetWorldPosition(xform);
                var wrapped = pos;
                if (pos.X < s.WrapMin.X) wrapped.X += size.X;
                else if (pos.X > s.WrapMax.X) wrapped.X -= size.X;
                if (pos.Y < s.WrapMin.Y) wrapped.Y += size.Y;
                else if (pos.Y > s.WrapMax.Y) wrapped.Y -= size.Y;

                if (wrapped != pos && !RedspaceSuppressionSystem.IsSuppressed(entry.Suppressors, wrapped, out _))
                    _transform.SetMapCoordinates(uid, new MapCoordinates(wrapped, mapId));
            }
        }

        var projectiles = EntityQueryEnumerator<ProjectileComponent, PhysicsComponent, TransformComponent>();
        while (projectiles.MoveNext(out var uid, out _, out var body, out var xform))
        {
            if (!_active.TryGetValue(xform.MapID, out var entry))
                continue;

            var s = entry.Map.Comp.Settings;
            if (!s.InertiaEnabled || s.ProjectileDrift <= 0f || body.LinearVelocity.LengthSquared() < 1f)
                continue;

            if (RedspaceSuppressionSystem.IsSuppressed(entry.Suppressors, _transform.GetWorldPosition(xform), out _))
                continue;

            // Curve sideways, always to the left of flight so the bend is visible whatever the direction.
            var dir = body.LinearVelocity;
            var side = new Vector2(-dir.Y, dir.X) / dir.Length();
            _physics.SetLinearVelocity(uid, dir + side * s.ProjectileDrift * dt, body: body);
        }
    }

    private void SpawnEcho(EntityUid source, RedspaceSettings s)
    {
        if (HasComp<RedspaceTrailComponent>(source))
            return;

        var xform = Transform(source);
        var echo = Spawn(EchoPrototype, _transform.GetMapCoordinates(source, xform), rotation: _transform.GetWorldRotation(xform));
        var visual = EnsureComp<RedspaceEchoComponent>(echo);
        visual.Source = source;
        Dirty(echo, visual);

        var follow = EnsureComp<RedspaceEchoFollowComponent>(echo);
        follow.Source = source;
        follow.Delay = TimeSpan.FromSeconds(s.EchoDelay);
        follow.End = _timing.CurTime + TimeSpan.FromSeconds(s.EchoDelay + s.EchoDuration);
        EnsureComp<RedspaceTrailComponent>(source);
    }

    private void StepEchoes()
    {
        var now = _timing.CurTime;

        var trails = EntityQueryEnumerator<RedspaceTrailComponent, TransformComponent>();
        while (trails.MoveNext(out _, out var trail, out var xform))
        {
            var (pos, rot) = _transform.GetWorldPositionRotation(xform);
            trail.Samples.Enqueue((now, pos, rot));
        }

        var echoes = EntityQueryEnumerator<RedspaceEchoFollowComponent>();
        while (echoes.MoveNext(out var uid, out var follow))
        {
            if (now >= follow.End || !TryComp<RedspaceTrailComponent>(follow.Source, out var trail)
                || Transform(follow.Source).MapID != Transform(uid).MapID)
            {
                RemComp<RedspaceTrailComponent>(follow.Source);
                QueueDel(uid);
                continue;
            }

            var target = now - follow.Delay;
            (TimeSpan Time, Vector2 Position, Angle Rotation)? sample = null;
            while (trail.Samples.Count > 0 && trail.Samples.Peek().Time <= target)
                sample = trail.Samples.Dequeue();

            if (sample != null)
                _transform.SetWorldPositionRotation(uid, sample.Value.Position, sample.Value.Rotation);
        }
    }

    private void CleanupEchoes()
    {
        var echoes = EntityQueryEnumerator<RedspaceEchoFollowComponent>();
        while (echoes.MoveNext(out var uid, out var follow))
        {
            RemComp<RedspaceTrailComponent>(follow.Source);
            QueueDel(uid);
        }
    }

    #endregion

    #region Thrown items

    private void OnThrown(ref ThrownEvent args)
    {
        var thrown = args.Thrown;
        if (!_active.TryGetValue(Transform(thrown).MapID, out var entry))
            return;

        var s = entry.Map.Comp.Settings;
        if (!s.InertiaEnabled || RedspaceSuppressionSystem.IsSuppressed(entry.Suppressors, _transform.GetWorldPosition(thrown), out _))
            return;

        var slide = EnsureComp<RedspaceSlideComponent>(thrown);
        slide.Friction = s.ThrownFriction;
        Dirty(thrown, slide);
    }

    private void StepSlides()
    {
        _toRemove.Clear();
        var query = EntityQueryEnumerator<RedspaceSlideComponent, PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var body, out var xform))
        {
            if (body.LinearVelocity.LengthSquared() < 0.01f || !_active.ContainsKey(xform.MapID) || xform.ParentUid != xform.GridUid && xform.ParentUid != xform.MapUid)
                _toRemove.Add(uid);
        }

        foreach (var uid in _toRemove)
            RemComp<RedspaceSlideComponent>(uid);
    }

    #endregion

    #region Helpers

    private float Next(float min, float max)
    {
        return max <= min ? Math.Max(min, 0.1f) : _random.NextFloat(min, max);
    }

    /// <summary>Counts a per-entity timer down; true when it fires (and it is re-armed).</summary>
    private bool Tick(ref float timer, float min, float max, float dt)
    {
        if (timer < 0f)
        {
            timer = Next(min, max);
            return false;
        }

        timer -= dt;
        if (timer > 0f)
            return false;

        timer = Next(min, max);
        return true;
    }

    #endregion
}
