using System.Numerics;
using Content.Shared._CorvaxGoob.Redspace.Anomalies;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Client._CorvaxGoob.Redspace.Anomalies;

/// <summary>
/// Client-only redspace scenery around the local player: drifting dust and debris, flickering reality patches,
/// shadow silhouettes at the edge of view (more in the dark), discharge arcs between objects with an afterglow and
/// footprints that appear by themselves. Also shows private hallucinations sent by the server.
/// Everything is a local entity with a sprite; nothing is networked, no screen shaders or tint are used.
/// Only runs while the server says the player is exposed (RedspaceAffected.Visuals &gt; 0) and keeps every spawn
/// inside RedspaceAffected.SafeRadius, so nothing appears inside protected areas.
/// </summary>
public sealed class RedspaceAmbientVisualsSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;

    public const string DebrisProto = "RedspaceVisualDebris";
    public const string PatchProto = "RedspaceVisualPatch";
    public const string SilhouetteProto = "RedspaceVisualSilhouette";
    public const string ArcProto = "RedspaceVisualArc";
    public const string ArcGlowProto = "RedspaceVisualArcGlow";
    public const string FootprintProto = "RedspaceVisualFootprint";
    public const string HallucinationProto = "RedspaceHallucinationFigure";

    private const float MaxRange = 9f;

    private static readonly string[] DebrisStates = { "dust1", "dust2", "dust3", "debris1", "debris2", "debris3", "debris4" };
    private static readonly string[] PatchStates = { "wall", "chasm", "floor_wood", "floor_white", "lattice" };
    private static readonly string[] SilhouetteStates = { "stalker", "watcher" };

    private enum Kind : byte
    {
        Debris,
        Patch,
        Silhouette,
        Arc,
        Glow,
        Footprint,
        Hallucination,
    }

    private sealed class Visual
    {
        public EntityUid Uid;
        public Kind Kind;
        public float Age;
        public float Life;
        public Vector2 Velocity;
        public float Spin;
        public float Alpha = 1f;
    }

    private readonly List<Visual> _visuals = new();
    private readonly List<(MapCoordinates Pos, Angle Rot, bool Left, float At)> _pendingSteps = new();
    private readonly HashSet<EntityUid> _near = new();
    private readonly List<EntityUid> _candidates = new();

    private float _debrisTimer = 1f;
    private float _patchTimer = 6f;
    private float _silhouetteTimer = 10f;
    private float _arcTimer = 5f;
    private float _footTimer = 12f;
    private float _clock;

    /// <summary>How many visuals of each kind were spawned (for tests and admins).</summary>
    public int SpawnedDebris, SpawnedPatches, SpawnedSilhouettes, SpawnedArcs, SpawnedFootprints;

    /// <summary>Private hallucinations received from the server.</summary>
    public int ReceivedHallucinations;

    public int ActiveVisuals => _visuals.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RedspaceHallucinationEvent>(OnHallucination);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        ClearAll();
    }

    private void ClearAll()
    {
        foreach (var v in _visuals)
        {
            if (!TerminatingOrDeleted(v.Uid))
                QueueDel(v.Uid);
        }
        _visuals.Clear();
        _pendingSteps.Clear();
    }

    private void OnHallucination(RedspaceHallucinationEvent ev)
    {
        ReceivedHallucinations++;
        var coords = GetCoordinates(ev.Coordinates);
        var map = _transform.ToMapCoordinates(coords);
        if (map.MapId == MapId.Nullspace)
            return;

        var uid = Spawn(HallucinationProto, map);
        var state = ev.Kind == RedspaceHallucinationKind.Crawler ? "crawler" : "stalker";
        SetState(uid, state);
        Track(uid, Kind.Hallucination, Math.Max(1f, ev.Duration), Vector2.Zero, 0f, 0f);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        _clock += frameTime;

        var player = _player.LocalEntity;
        MapCoordinates? playerPos = null;
        if (player != null && !TerminatingOrDeleted(player.Value))
            playerPos = _transform.GetMapCoordinates(player.Value);

        UpdateVisuals(frameTime, playerPos);

        if (player == null
            || playerPos is not { } pos
            || pos.MapId == MapId.Nullspace
            || !TryComp<RedspaceAffectedComponent>(player.Value, out var affected)
            || affected.Visuals <= 0f)
        {
            _pendingSteps.Clear();
            return;
        }

        var range = Math.Min(affected.SafeRadius - 0.5f, MaxRange);
        if (range < 2f)
        {
            _pendingSteps.Clear();
            return;
        }

        var rate = affected.Visuals;
        UpdateSteps(pos.MapId);

        _debrisTimer -= frameTime * rate;
        if (_debrisTimer <= 0f)
        {
            _debrisTimer = _random.NextFloat(0.6f, 1.6f);
            SpawnDebris(pos, range);
        }

        _patchTimer -= frameTime * rate;
        if (_patchTimer <= 0f)
        {
            _patchTimer = _random.NextFloat(8f, 18f);
            SpawnPatch(pos, range);
        }

        // Darkness makes the shadows bolder: twice as often without a lit lamp nearby.
        _silhouetteTimer -= frameTime * rate * (IsDark(pos) ? 2f : 1f);
        if (_silhouetteTimer <= 0f)
        {
            _silhouetteTimer = _random.NextFloat(12f, 25f);
            SpawnSilhouette(pos, range);
        }

        _arcTimer -= frameTime * rate;
        if (_arcTimer <= 0f)
        {
            _arcTimer = _random.NextFloat(6f, 14f);
            SpawnArc(pos, range);
        }

        _footTimer -= frameTime * rate;
        if (_footTimer <= 0f)
        {
            _footTimer = _random.NextFloat(15f, 30f);
            StartFootprints(pos, range);
        }
    }

    /// <summary>Spawns one of every kind around <paramref name="center"/> right away. Returns how many spawned.</summary>
    public int SpawnAllAround(MapCoordinates center, float range)
    {
        var count = 0;
        count += SpawnDebris(center, range) ? 1 : 0;
        count += SpawnPatch(center, range) ? 1 : 0;
        count += SpawnSilhouette(center, range) ? 1 : 0;
        count += SpawnArc(center, range, force: true) ? 1 : 0;
        count += StartFootprints(center, range) ? 1 : 0;
        UpdateSteps(center.MapId, flush: true);
        return count;
    }

    private Vector2 RandomOffset(float min, float max)
    {
        return _random.NextAngle().ToVec() * _random.NextFloat(min, Math.Max(min, max));
    }

    private bool SpawnDebris(MapCoordinates pos, float range)
    {
        var at = pos.Offset(RandomOffset(1.5f, range));
        var uid = Spawn(DebrisProto, at);
        SetState(uid, DebrisStates[_random.Next(DebrisStates.Length)]);
        var vel = _random.NextAngle().ToVec() * _random.NextFloat(0.15f, 0.6f);
        Track(uid, Kind.Debris, _random.NextFloat(2.5f, 5f), vel, _random.NextFloat(-2f, 2f), 0f);
        SpawnedDebris++;
        return true;
    }

    private bool SpawnPatch(MapCoordinates pos, float range)
    {
        var off = RandomOffset(2f, Math.Min(range, 6f));
        var at = new MapCoordinates(new Vector2(MathF.Floor(pos.X + off.X) + 0.5f, MathF.Floor(pos.Y + off.Y) + 0.5f), pos.MapId);
        if ((at.Position - pos.Position).Length() > range)
            return false;

        var uid = Spawn(PatchProto, at);
        SetState(uid, PatchStates[_random.Next(PatchStates.Length)]);
        Track(uid, Kind.Patch, _random.NextFloat(1.5f, 3f), Vector2.Zero, 0f, 0f);
        SpawnedPatches++;
        return true;
    }

    private bool SpawnSilhouette(MapCoordinates pos, float range)
    {
        var min = Math.Min(6f, range - 0.5f);
        var at = pos.Offset(RandomOffset(Math.Max(2f, min), range));
        var uid = Spawn(SilhouetteProto, at);
        SetState(uid, SilhouetteStates[_random.Next(SilhouetteStates.Length)]);
        // Face the player.
        _transform.SetWorldRotation(uid, (pos.Position - at.Position).ToWorldAngle());
        Track(uid, Kind.Silhouette, _random.NextFloat(2.5f, 4.5f), Vector2.Zero, 0f, 0f);
        SpawnedSilhouettes++;
        return true;
    }

    private bool SpawnArc(MapCoordinates pos, float range, bool force = false)
    {
        Vector2 a, b;
        if (!TryFindArcPair(pos, Math.Min(range, 6f), out a, out b))
        {
            if (!force)
                return false;
            a = pos.Position + RandomOffset(1.5f, Math.Min(range, 3f));
            b = a + _random.NextAngle().ToVec() * 2.5f;
        }

        var diff = b - a;
        var length = diff.Length();
        var arc = Spawn(ArcProto, new MapCoordinates((a + b) / 2f, pos.MapId));
        _transform.SetWorldRotation(arc, new Angle(Math.Atan2(diff.Y, diff.X)));
        if (TryComp<SpriteComponent>(arc, out var sprite))
            _sprite.SetScale((arc, sprite), new Vector2(Math.Max(0.5f, length), 1f));
        Track(arc, Kind.Arc, 0.35f, Vector2.Zero, 0f, 0f);

        Track(Spawn(ArcGlowProto, new MapCoordinates(a, pos.MapId)), Kind.Glow, 1.4f, Vector2.Zero, 0f, 0f);
        Track(Spawn(ArcGlowProto, new MapCoordinates(b, pos.MapId)), Kind.Glow, 1.4f, Vector2.Zero, 0f, 0f);
        SpawnedArcs++;
        return true;
    }

    /// <summary>Two anchored objects 1.5-4 tiles apart near the player.</summary>
    private bool TryFindArcPair(MapCoordinates pos, float range, out Vector2 a, out Vector2 b)
    {
        a = b = default;
        _near.Clear();
        _candidates.Clear();
        _lookup.GetEntitiesInRange(pos.MapId, pos.Position, range, _near, LookupFlags.Static);
        foreach (var uid in _near)
        {
            if (Transform(uid).Anchored && HasComp<SpriteComponent>(uid))
                _candidates.Add(uid);
        }

        if (_candidates.Count < 2)
            return false;

        for (var i = 0; i < 8; i++)
        {
            var first = _candidates[_random.Next(_candidates.Count)];
            var second = _candidates[_random.Next(_candidates.Count)];
            if (first == second)
                continue;
            var pa = _transform.GetWorldPosition(first);
            var pb = _transform.GetWorldPosition(second);
            var d = (pb - pa).Length();
            if (d < 1.5f || d > 4f)
                continue;
            a = pa;
            b = pb;
            return true;
        }
        return false;
    }

    private bool StartFootprints(MapCoordinates pos, float range)
    {
        var start = pos.Position + RandomOffset(2f, Math.Min(range, 5f));
        var dir = _random.NextAngle();
        var step = dir.ToVec() * 0.55f;
        var side = new Vector2(-step.Y, step.X) * 0.3f;
        var count = _random.Next(5, 9);
        var placed = 0;
        for (var i = 0; i < count; i++)
        {
            var left = i % 2 == 0;
            var p = start + step * i + (left ? side : -side);
            if ((p - pos.Position).Length() > range)
                break;
            _pendingSteps.Add((new MapCoordinates(p, pos.MapId), dir, left, _clock + i * 0.45f));
            placed++;
        }

        if (placed == 0)
            return false;
        SpawnedFootprints++;
        return true;
    }

    private void UpdateSteps(MapId map, bool flush = false)
    {
        for (var i = _pendingSteps.Count - 1; i >= 0; i--)
        {
            var step = _pendingSteps[i];
            if (step.Pos.MapId != map)
            {
                _pendingSteps.RemoveAt(i);
                continue;
            }

            if (!flush && step.At > _clock)
                continue;

            _pendingSteps.RemoveAt(i);
            var uid = Spawn(FootprintProto, step.Pos);
            SetState(uid, step.Left ? "left" : "right");
            // The footprint sprite points north; world rotation turns it along the walk.
            _transform.SetWorldRotation(uid, step.Rot - Angle.FromDegrees(90));
            Track(uid, Kind.Footprint, 6f, Vector2.Zero, 0f, 0f);
        }
    }

    private bool IsDark(MapCoordinates pos)
    {
        foreach (var light in _lookup.GetEntitiesInRange<PointLightComponent>(pos, 5f))
        {
            if (light.Comp.Enabled && light.Comp.Energy > 0.3f)
                return false;
        }
        return true;
    }

    private void SetState(EntityUid uid, string state)
    {
        if (TryComp<SpriteComponent>(uid, out var sprite))
            _sprite.LayerSetRsiState((uid, sprite), 0, state);
    }

    private void Track(EntityUid uid, Kind kind, float life, Vector2 velocity, float spin, float alpha)
    {
        var v = new Visual { Uid = uid, Kind = kind, Life = life, Velocity = velocity, Spin = spin, Alpha = alpha };
        _visuals.Add(v);
        ApplyAlpha(v);
    }

    private void UpdateVisuals(float frameTime, MapCoordinates? playerPos)
    {
        for (var i = _visuals.Count - 1; i >= 0; i--)
        {
            var v = _visuals[i];
            v.Age += frameTime;
            if (TerminatingOrDeleted(v.Uid) || v.Age >= v.Life)
            {
                if (!TerminatingOrDeleted(v.Uid))
                    QueueDel(v.Uid);
                _visuals.RemoveAt(i);
                continue;
            }

            var t = v.Age / v.Life;
            switch (v.Kind)
            {
                case Kind.Debris:
                    _transform.SetWorldPosition(v.Uid, _transform.GetWorldPosition(v.Uid) + v.Velocity * frameTime);
                    _transform.SetWorldRotation(v.Uid, _transform.GetWorldRotation(v.Uid) + v.Spin * frameTime);
                    v.Alpha = Fade(t, 0.2f, 0.3f) * 0.8f;
                    break;
                case Kind.Patch:
                    // Flicker: mostly visible, sometimes gone for a frame or half-transparent.
                    var roll = _random.NextFloat();
                    v.Alpha = roll < 0.25f ? 0f : roll < 0.45f ? 0.45f : 0.9f;
                    break;
                case Kind.Silhouette:
                    v.Alpha = Fade(t, 0.3f, 0.3f) * 0.85f;
                    // Shadows don't like being approached.
                    if (playerPos is { } pp && (_transform.GetWorldPosition(v.Uid) - pp.Position).Length() < 3f)
                        v.Age = Math.Max(v.Age, v.Life * 0.85f);
                    break;
                case Kind.Arc:
                    v.Alpha = _random.Prob(0.8f) ? 1f : 0.3f;
                    break;
                case Kind.Glow:
                    v.Alpha = 1f - t;
                    break;
                case Kind.Footprint:
                    v.Alpha = Fade(t, 0.05f, 0.4f) * 0.85f;
                    break;
                case Kind.Hallucination:
                    v.Alpha = Fade(t, 0.15f, 0.15f) * 0.9f;
                    if (playerPos is { } target)
                    {
                        var here = _transform.GetWorldPosition(v.Uid);
                        var to = target.Position - here;
                        var dist = to.Length();
                        if (dist < 0.8f)
                        {
                            v.Age = v.Life;
                            break;
                        }
                        var dir = to / dist;
                        _transform.SetWorldPosition(v.Uid, here + dir * 2.2f * frameTime);
                        _transform.SetWorldRotation(v.Uid, dir.ToWorldAngle());
                    }
                    break;
            }

            ApplyAlpha(v);
        }
    }

    private static float Fade(float t, float fadeIn, float fadeOut)
    {
        if (t < fadeIn)
            return t / fadeIn;
        if (t > 1f - fadeOut)
            return Math.Max(0f, (1f - t) / fadeOut);
        return 1f;
    }

    private void ApplyAlpha(Visual v)
    {
        if (TryComp<SpriteComponent>(v.Uid, out var sprite))
            _sprite.SetColor((v.Uid, sprite), Color.White.WithAlpha(Math.Clamp(v.Alpha, 0f, 1f)));
    }
}
