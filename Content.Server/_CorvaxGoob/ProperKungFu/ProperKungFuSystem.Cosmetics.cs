using System.Numerics;
using Content.Shared.Camera;
using Content.Shared.Damage;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// v13.1: short-lived white-red flourishes layered over the techniques' main effects (hit sparks, blood flecks,
/// slam dust, ground ripples, speed lines, wind swirls, choke puffs, pulse rings, the ultimate's mist and haze)
/// and a light camera kick for the user on the big finishers. Purely cosmetic: nothing here changes gameplay.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly SharedCameraRecoilSystem _cameraRecoil = default!;

    private static readonly EntProtoId HitSparkEffect = "ProperKungFuHitSpark";
    private static readonly EntProtoId BloodFleckEffect = "ProperKungFuBloodFleck";
    private static readonly EntProtoId ScratchSparksEffect = "ProperKungFuScratchSparks";
    private static readonly EntProtoId SlamDustEffect = "ProperKungFuSlamDust";
    private static readonly EntProtoId GroundRippleEffect = "ProperKungFuGroundRipple";
    private static readonly EntProtoId WindSwirlEffect = "ProperKungFuWindSwirl";
    private static readonly EntProtoId ChokeBreathEffect = "ProperKungFuChokeBreath";
    private static readonly EntProtoId PulseRingEffect = "ProperKungFuPulseRing";
    private static readonly EntProtoId DashSpeedLinesEffect = "ProperKungFuDashSpeedLines";
    private static readonly EntProtoId UltMistEffect = "ProperKungFuUltMist";
    private static readonly EntProtoId UltHazeEffect = "ProperKungFuUltHaze";

    /// <summary>A target sparks at most this often, so chunked damage doesn't stack a dozen bursts.</summary>
    private static readonly TimeSpan HitSparkInterval = TimeSpan.FromSeconds(0.08);

    private readonly Dictionary<EntityUid, TimeSpan> _lastHitSpark = new();

    /// <summary>
    /// A white-hot spark on the struck body, plus blood flecks flung away from the attacker on a cut or a stab.
    /// </summary>
    private void SpawnHitFlourish(EntityUid target, EntityUid? origin, DamageSpecifier damage)
    {
        if (TerminatingOrDeleted(target))
            return;

        var now = _timing.CurTime;
        if (_lastHitSpark.TryGetValue(target, out var last) && now - last < HitSparkInterval)
            return;

        if (_lastHitSpark.Count > 256)
            _lastHitSpark.Clear();
        _lastHitSpark[target] = now;

        var coords = _transform.GetMapCoordinates(target);
        if (coords.MapId == MapId.Nullspace)
            return;

        var jitter = new Vector2(_instinctRandom.NextFloat(-0.15f, 0.15f), _instinctRandom.NextFloat(-0.1f, 0.25f));
        var spark = Spawn(HitSparkEffect, coords.Offset(jitter));
        _transform.SetWorldRotation(spark, _instinctRandom.NextAngle());

        var cutting = damage.DamageDict.TryGetValue("Slash", out var slash) && slash > 0
                      || damage.DamageDict.TryGetValue("Piercing", out var pierce) && pierce > 0;
        if (!cutting)
            return;

        var away = origin is { } attacker && !TerminatingOrDeleted(attacker)
            ? DirectionTo(attacker, target, _transform.GetWorldRotation(target).ToWorldVec())
            : _instinctRandom.NextAngle().ToVec();
        var fleck = Spawn(BloodFleckEffect, coords);
        // The sprite is drawn flying east.
        _transform.SetWorldRotation(fleck, away.ToAngle());
    }

    /// <summary>Speed lines along a blink or a dash from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private void SpawnSpeedLines(MapId map, Vector2 from, Vector2 to)
    {
        var delta = to - from;
        if (map == MapId.Nullspace || delta.LengthSquared() < 0.25f)
            return;

        var lines = Spawn(DashSpeedLinesEffect, new MapCoordinates(Vector2.Lerp(from, to, 0.5f), map));
        _transform.SetWorldRotation(lines, delta.ToAngle());
    }

    /// <summary>Dust ring (and optionally a ground ripple) where a body hits the floor.</summary>
    private void SpawnSlamFlourish(MapCoordinates coords, bool ripple)
    {
        if (coords.MapId == MapId.Nullspace)
            return;

        Spawn(SlamDustEffect, coords);
        if (ripple)
            Spawn(GroundRippleEffect, coords);
    }

    private void SpawnScratchSparks(MapCoordinates coords)
    {
        if (coords.MapId == MapId.Nullspace)
            return;

        var sparks = Spawn(ScratchSparksEffect, coords);
        _transform.SetWorldRotation(sparks, _instinctRandom.NextAngle());
    }

    private void SpawnWindSwirl(EntityUid target)
    {
        if (!TerminatingOrDeleted(target))
            SpawnAttachedTo(WindSwirlEffect, new EntityCoordinates(target, Vector2.Zero));
    }

    private void SpawnChokeBreath(EntityUid target)
    {
        if (!TerminatingOrDeleted(target))
            SpawnAttachedTo(ChokeBreathEffect, new EntityCoordinates(target, new Vector2(0f, 0.3f)));
    }

    private void SpawnPulseRing(EntityUid user)
    {
        if (!TerminatingOrDeleted(user))
            SpawnAttachedTo(PulseRingEffect, new EntityCoordinates(user, Vector2.Zero));
    }

    /// <summary>Ultimate activation: a red mist plume around the user and a faint red haze that follows them a while.</summary>
    private void SpawnUltimateMist(EntityUid user)
    {
        if (TerminatingOrDeleted(user))
            return;

        Spawn(UltMistEffect, _transform.GetMapCoordinates(user));
        SpawnAttachedTo(UltHazeEffect, new EntityCoordinates(user, Vector2.Zero));
    }

    /// <summary>A light camera kick for the user (the victims' screens are left alone) on a big finisher.</summary>
    private void KickFinisherCamera(EntityUid user, Vector2 direction, float strength = 0.35f)
    {
        if (TerminatingOrDeleted(user) || !HasComp<CameraRecoilComponent>(user))
            return;

        var dir = direction.LengthSquared() > 0.0001f ? Vector2.Normalize(direction) : _instinctRandom.NextAngle().ToVec();
        _cameraRecoil.KickCamera(user, dir * strength);
    }
}
