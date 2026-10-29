using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server._CorvaxGoob.ProcAnim;
using Content.Server.Popups;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Jittering;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// v13: Gilbert's three rage abilities (see <see cref="GilbertRageAbilitiesComponent"/>) and the rage finisher hook.
/// Only while his berserk phase is <see cref="GilbertBerserkPhase.Rage"/>. Each ability is a small state machine
/// advanced in <see cref="Update"/>; while one runs <see cref="IsBusy"/> is true and his AI stands still.
/// </summary>
public sealed class GilbertRageAbilitiesSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly GilbertFinisherSystem _finisher = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly ProcAnimSystem _procAnim = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedJitteringSystem _jitter = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    public const string RageFinisher = GilbertFinisherSystem.RageFinisher;

    private static readonly EntProtoId Aura = "GilbertRageAura";
    private static readonly EntProtoId ConeSpark = "GilbertConeSpark";
    private static readonly EntProtoId ConeFlash = "GilbertConeFlash";
    private static readonly EntProtoId BoltSegment = "GilbertBoltSegment";
    private static readonly EntProtoId BoltCrawl = "GilbertBoltCrawl";
    private static readonly EntProtoId FloorThread = "GilbertLevitateFloorThread";
    private static readonly EntProtoId RiseThread = "GilbertLevitateRise";
    private static readonly EntProtoId BlinkBurst = "GilbertBlinkBurst";
    private static readonly EntProtoId SpawnArc = "GilbertSpawnArc";
    private static readonly EntProtoId DashTrail = "GilbertDashTrail";
    private static readonly EntProtoId DashBurst = "GilbertDashBurst";
    private static readonly TargetBodyPart[] BoltParts =
    {
        TargetBodyPart.Head, TargetBodyPart.Chest, TargetBodyPart.Chest, TargetBodyPart.Groin,
        TargetBodyPart.LeftArm, TargetBodyPart.RightArm, TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg,
    };

    private static readonly EntProtoId Explosion = "GilbertBerserkExplosion";
    private static readonly EntProtoId Stream = "GilbertBerserkStream";

    /// <summary>Rays the cone is traced with (walls stop each separately).</summary>
    public const int ConeRayCount = 15;

    /// <summary>Height (tiles) of his body above his origin while levitating: where the bolts come from.</summary>
    private const float BoltSourceHeight = 0.7f;

    private static readonly SoundSpecifier WindupSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningshock.ogg");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertRageAbilitiesComponent, ProcAnimEventFiredEvent>(OnSceneEvent);
        SubscribeLocalEvent<GilbertLeapThrowComponent, ProcAnimEndedEvent>(OnLeapSceneEnded);
        SubscribeLocalEvent<GilbertRageAbilitiesComponent, GilbertBerserkPhaseChangedEvent>(OnPhaseChanged);
        SubscribeLocalEvent<GilbertRageAbilitiesComponent, ComponentShutdown>(OnShutdown);
    }

    #region Common

    public bool InRage(EntityUid uid)
    {
        return TryComp<GilbertBerserkComponent>(uid, out var berserk) && berserk.Phase == GilbertBerserkPhase.Rage;
    }

    /// <summary>One of the abilities is running (wind-up, cone wave, levitation): he holds still.</summary>
    public bool IsBusy(EntityUid uid, GilbertRageAbilitiesComponent? comp = null)
    {
        return Resolve(uid, ref comp, false) && (comp.LeapTarget != null || comp.ConeActive || comp.Levitating);
    }

    private bool CanStart(EntityUid uid, GilbertRageAbilitiesComponent comp, bool ignoreCooldowns)
    {
        if (!InRage(uid) || IsBusy(uid, comp) || _procAnim.IsPlaying(uid) || _mobState.IsIncapacitated(uid))
            return false;
        return ignoreCooldowns || _timing.CurTime >= comp.NextAny;
    }

    private void SpawnFx(EntProtoId proto, MapCoordinates coords, Angle? rotation = null)
    {
        var fx = Spawn(proto.Id, coords);
        if (rotation != null)
            _transform.SetWorldRotation(fx, rotation.Value);
    }

    private void SpawnOn(EntProtoId proto, EntityUid on, Vector2 offset)
    {
        SpawnAttachedTo(proto, new EntityCoordinates(on, offset));
    }

    private void Halt(EntityUid uid)
    {
        if (TryComp<PhysicsComponent>(uid, out var body))
            _physics.SetLinearVelocity(uid, Vector2.Zero, body: body);
    }

    private void Face(EntityUid uid, Vector2 dir)
    {
        if (dir.LengthSquared() > 0.0001f)
            _transform.SetWorldRotation(uid, dir.ToWorldAngle());
    }

    /// <summary>Free distance from <paramref name="from"/> along <paramref name="dir"/> up to <paramref name="max"/>.</summary>
    private float FreeDistance(MapId map, Vector2 from, Vector2 dir, float max, EntityUid ignore, EntityUid? alsoIgnore = null)
    {
        var ray = new CollisionRay(from, dir, (int) CollisionGroup.Impassable);
        foreach (var hit in _physics.IntersectRay(map, ray, max, ignore))
        {
            if (hit.HitEntity == alsoIgnore || HasComp<MobStateComponent>(hit.HitEntity))
                continue;
            return MathF.Max(0f, hit.Distance);
        }

        return max;
    }

    private void Shock(EntityUid target, EntityUid gilbert, FixedPoint2 amount, TargetBodyPart part = TargetBodyPart.Chest)
    {
        var spec = new DamageSpecifier();
        spec.DamageDict["Shock"] = amount;
        _damageable.TryChangeDamage(target, spec, origin: gilbert, targetPart: part, canMiss: false);
    }

    private void CancelAll(EntityUid uid, GilbertRageAbilitiesComponent comp)
    {
        comp.LeapTarget = null;
        comp.ConeActive = false;
        comp.ConeFired = false;
        if (comp.Levitating)
            EndLevitation(uid, comp);
    }

    private void OnPhaseChanged(Entity<GilbertRageAbilitiesComponent> ent, ref GilbertBerserkPhaseChangedEvent args)
    {
        if (!InRage(ent))
            CancelAll(ent, ent.Comp);
    }

    private void OnShutdown(Entity<GilbertRageAbilitiesComponent> ent, ref ComponentShutdown args)
    {
        RemComp<GilbertLevitatingComponent>(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;

        var query = EntityQueryEnumerator<GilbertRageAbilitiesComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (!IsBusy(uid, comp))
                continue;

            if (!InRage(uid) || _mobState.IsIncapacitated(uid) || _procAnim.IsPlaying(uid))
            {
                CancelAll(uid, comp);
                continue;
            }

            if (comp.LeapTarget != null)
                UpdateLeap((uid, comp), xform, now);
            if (comp.ConeActive)
                UpdateCone((uid, comp), xform, now, frameTime);
            if (comp.Levitating)
                UpdateLevitation((uid, comp), xform, now);
        }

        var shocked = EntityQueryEnumerator<GilbertShockedComponent>();
        while (shocked.MoveNext(out var uid, out var comp))
        {
            if (now >= comp.End || TerminatingOrDeleted(uid))
            {
                RemCompDeferred<GilbertShockedComponent>(uid);
                continue;
            }

            if (now < comp.NextFx)
                continue;
            comp.NextFx = now + TimeSpan.FromSeconds(0.1);
            SpawnOn(Aura, uid, _random.NextVector2(0.25f));
            if (_random.Prob(0.5f))
                SpawnOn(BoltCrawl, uid, _random.NextVector2(0.3f));
        }
    }

    #endregion

    #region Leap

    public bool CanLeap(EntityUid uid, GilbertRageAbilitiesComponent comp, bool ignoreCooldowns = false)
    {
        return CanStart(uid, comp, ignoreCooldowns) && (ignoreCooldowns || _timing.CurTime >= comp.NextLeap);
    }

    /// <summary>Starts the leap at <paramref name="target"/>: wind-up now, the jump after <see cref="GilbertRageAbilitiesComponent.LeapWindup"/>.</summary>
    public bool TryLeap(Entity<GilbertRageAbilitiesComponent?> ent, EntityUid target, bool ignoreCooldowns = false)
    {
        if (!Resolve(ent, ref ent.Comp, false) || !CanLeap(ent, ent.Comp, ignoreCooldowns) || target == ent.Owner)
            return false;

        var from = _transform.GetMapCoordinates(ent);
        var to = _transform.GetMapCoordinates(target);
        if (from.MapId != to.MapId || (to.Position - from.Position).Length() > ent.Comp.LeapRange + 1f)
            return false;

        var now = _timing.CurTime;
        var comp = ent.Comp;
        comp.LeapTarget = target;
        comp.LeapAt = now + comp.LeapWindup;
        comp.NextLeapFx = now;
        comp.NextLeap = now + comp.LeapCooldown;
        comp.NextAny = now + comp.GlobalGap;
        comp.Leaps++;
        Halt(ent);
        Face(ent, to.Position - from.Position);
        _audio.PlayPvs(WindupSound, ent, AudioParams.Default.WithVolume(-2f));
        _popup.PopupEntity(Loc.GetString("gilbert-leap-windup"), ent, PopupType.LargeCaution);
        return true;
    }

    private void UpdateLeap(Entity<GilbertRageAbilitiesComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.LeapTarget is not { } target || TerminatingOrDeleted(target))
        {
            comp.LeapTarget = null;
            return;
        }

        Halt(ent);
        if (now < comp.LeapAt)
        {
            // Crouched, discharges running over him and red streams rising.
            if (now >= comp.NextLeapFx)
            {
                comp.NextLeapFx = now + TimeSpan.FromSeconds(0.08);
                SpawnOn(Aura, ent, _random.NextVector2(0.3f));
                if (_random.Prob(0.4f))
                    SpawnOn(Stream, ent, Vector2.Zero);
            }

            Face(ent, _transform.GetWorldPosition(target) - _transform.GetWorldPosition(xform));
            return;
        }

        comp.LeapTarget = null;
        Leap(ent, xform, target);
    }

    private void Leap(Entity<GilbertRageAbilitiesComponent> ent, TransformComponent xform, EntityUid target)
    {
        var comp = ent.Comp;
        var from = _transform.GetMapCoordinates(ent, xform);
        var to = _transform.GetMapCoordinates(target);
        if (from.MapId != to.MapId)
            return;

        var delta = to.Position - from.Position;
        var dist = delta.Length();
        if (dist < 0.05f)
            return;

        var dir = delta / dist;
        // He lands just short of the victim (a fraction further than the dash: he means to hit them).
        var length = MathF.Min(comp.LeapRange + 1f, MathF.Max(0f, dist - 0.75f));
        length = MathF.Min(length, MathF.Max(0f, FreeDistance(from.MapId, from.Position, dir, length + 0.5f, ent, target) - 0.45f));

        SpawnFx(DashBurst, from);
        var angle = dir.ToWorldAngle();
        for (var d = 0.25f; d < length; d += 0.3f)
            SpawnFx(DashTrail, new MapCoordinates(from.Position + dir * d, from.MapId), angle);

        var land = from.Position + dir * length;
        _transform.SetWorldPosition((ent, xform), land);
        _transform.SetWorldRotation(ent, angle);
        SpawnFx(Explosion, new MapCoordinates(land, from.MapId));
        _audio.PlayPvs(comp.LeapSound, ent);
        _audio.PlayPvs(comp.LandSound, ent, AudioParams.Default.WithVolume(-3f));

        if ((to.Position - land).Length() > comp.LeapHitRange
            || !_interaction.InRangeUnobstructed(ent.Owner, target, comp.LeapHitRange + 0.2f)
            || !HasComp<MobStateComponent>(target) || _mobState.IsDead(target))
            return;

        comp.LeapHits++;
        _stun.TryKnockdown(target, comp.LeapKnockdown, true);
        _popup.PopupEntity(Loc.GetString("gilbert-leap-hit", ("target", Identity.Entity(target, EntityManager))),
            target, PopupType.LargeCaution);

        if (_procAnim.TryPlay(comp.LeapScene.Id, new[] { ent.Owner, target }, out _))
            return;

        // No room for the scene (or the victim is busy in another one): the same blows, the throw at once.
        Shock(target, ent, 10, TargetBodyPart.Chest);
        Shock(target, ent, 10, TargetBodyPart.Head);
        var blunt = new DamageSpecifier();
        blunt.DamageDict["Blunt"] = 10;
        _damageable.TryChangeDamage(target, blunt, origin: ent, targetPart: TargetBodyPart.Chest, canMiss: false);
        ThrowVictim(ent, target, dir);
    }

    private void ThrowVictim(Entity<GilbertRageAbilitiesComponent> ent, EntityUid victim, Vector2 dir)
    {
        if (TerminatingOrDeleted(victim) || dir.LengthSquared() < 0.0001f)
            return;
        _throwing.TryThrow(victim, Vector2.Normalize(dir) * ent.Comp.LeapThrowDistance, ent.Comp.LeapThrowSpeed, ent,
            pushbackRatio: 0f, recoil: false);
        _stun.TryKnockdown(victim, ent.Comp.LeapKnockdown, true);
        ent.Comp.LeapThrows++;
        ent.Comp.PunchGraceUntil = _timing.CurTime + TimeSpan.FromSeconds(0.8);
    }

    private void OnSceneEvent(Entity<GilbertRageAbilitiesComponent> ent, ref ProcAnimEventFiredEvent args)
    {
        switch (args.Event.Beat)
        {
            // Leap scene: he lets go of the arms - the real throw follows the scene's own flight.
            case "release" when TryComp<ProcAnimComponent>(ent, out var anim) && anim.Partner is { } victim:
            {
                var dir = _transform.GetWorldPosition(victim) - _transform.GetWorldPosition(ent);
                if (dir.LengthSquared() < 0.01f)
                    dir = anim.Forward;
                var pending = EnsureComp<GilbertLeapThrowComponent>(victim);
                pending.Gilbert = ent;
                pending.Dir = Vector2.Normalize(dir);
                break;
            }
            // Rage finisher: he vanishes and reappears behind the victim - his spawn burst, faster and paler.
            case "vanish":
            case "appear":
            {
                var at = _transform.GetMapCoordinates(ent);
                SpawnFx(BlinkBurst, at);
                for (var i = 0; i < 4; i++)
                    SpawnFx(SpawnArc, new MapCoordinates(at.Position + _random.NextVector2(0.2f, 0.6f), at.MapId),
                        _random.NextAngle());
                _audio.PlayPvs(WindupSound, ent, AudioParams.Default.WithVolume(-4f).WithPitchScale(1.4f));
                break;
            }
        }
    }

    private void OnLeapSceneEnded(Entity<GilbertLeapThrowComponent> ent, ref ProcAnimEndedEvent args)
    {
        var pending = ent.Comp;
        RemComp<GilbertLeapThrowComponent>(ent);
        if (!args.Completed || !TryComp<GilbertRageAbilitiesComponent>(pending.Gilbert, out var comp))
            return;
        ThrowVictim((pending.Gilbert, comp), ent, pending.Dir);
    }

    #endregion

    #region Cone

    public bool CanCone(EntityUid uid, GilbertRageAbilitiesComponent comp, bool ignoreCooldowns = false)
    {
        return CanStart(uid, comp, ignoreCooldowns) && (ignoreCooldowns || _timing.CurTime >= comp.NextCone);
    }

    /// <summary>Starts the red discharge cone towards <paramref name="target"/> (or straight ahead with null).</summary>
    public bool TryCone(Entity<GilbertRageAbilitiesComponent?> ent, EntityUid? target, bool ignoreCooldowns = false)
    {
        if (!Resolve(ent, ref ent.Comp, false) || !CanCone(ent, ent.Comp, ignoreCooldowns))
            return false;

        var comp = ent.Comp;
        var now = _timing.CurTime;
        var myPos = _transform.GetWorldPosition(ent);
        var dir = target is { } t && !TerminatingOrDeleted(t)
            ? _transform.GetWorldPosition(t) - myPos
            : _transform.GetWorldRotation(ent).ToWorldVec();
        if (dir.LengthSquared() < 0.0001f)
            dir = new Vector2(1, 0);

        comp.ConeActive = true;
        comp.ConeFired = false;
        comp.ConeTarget = target;
        comp.ConeDir = Vector2.Normalize(dir);
        comp.ConeAt = now + comp.ConeWindup;
        comp.NextConeFx = now;
        comp.NextCone = now + comp.ConeCooldown;
        comp.NextAny = now + comp.GlobalGap;
        comp.ConeHit.Clear();
        comp.Cones++;
        Halt(ent);
        Face(ent, comp.ConeDir);
        _audio.PlayPvs(WindupSound, ent);
        return true;
    }

    private void UpdateCone(Entity<GilbertRageAbilitiesComponent> ent, TransformComponent xform, TimeSpan now, float frameTime)
    {
        var comp = ent.Comp;
        Halt(ent);
        if (!comp.ConeFired)
        {
            // Discharges all over him.
            if (now >= comp.NextConeFx)
            {
                comp.NextConeFx = now + TimeSpan.FromSeconds(0.06);
                SpawnOn(Aura, ent, _random.NextVector2(0.35f));
                SpawnOn(BoltCrawl, ent, _random.NextVector2(0.3f));
            }

            if (comp.ConeTarget is { } aim && !TerminatingOrDeleted(aim))
            {
                var d = _transform.GetWorldPosition(aim) - _transform.GetWorldPosition(xform);
                if (d.LengthSquared() > 0.0001f)
                    comp.ConeDir = Vector2.Normalize(d);
                Face(ent, comp.ConeDir);
            }

            if (now < comp.ConeAt)
                return;
            FireCone(ent, xform);
        }

        comp.ConeFront += comp.ConeSpeed * frameTime;
        SpreadCone(ent, xform);
        var longest = 0f;
        foreach (var r in comp.ConeRays)
            longest = MathF.Max(longest, r);
        if (comp.ConeFront >= MathF.Min(comp.ConeRange, longest + 0.6f))
        {
            comp.ConeActive = false;
            comp.ConeFired = false;
        }
    }

    private void FireCone(Entity<GilbertRageAbilitiesComponent> ent, TransformComponent xform)
    {
        var comp = ent.Comp;
        comp.ConeFired = true;
        comp.ConeOrigin = _transform.GetWorldPosition(xform);
        comp.ConeFront = 0.4f;
        comp.ConeRays = new float[ConeRayCount];
        for (var i = 0; i < ConeRayCount; i++)
        {
            var dir = ConeRayDir(comp, i);
            comp.ConeRays[i] = FreeDistance(xform.MapID, comp.ConeOrigin, dir, comp.ConeRange, ent);
        }

        _audio.PlayPvs(comp.ConeSound, ent);
        SpawnFx(Explosion, new MapCoordinates(comp.ConeOrigin + comp.ConeDir * 0.5f, xform.MapID));
    }

    private static Vector2 ConeRayDir(GilbertRageAbilitiesComponent comp, int i)
    {
        var half = comp.ConeHalfAngle * MathF.PI / 180f;
        var a = -half + 2f * half * i / (ConeRayCount - 1);
        return new Angle(a).RotateVec(comp.ConeDir);
    }

    /// <summary>Free distance of the cone at <paramref name="offset"/> radians off its axis (nearest traced ray).</summary>
    private static float ConeReach(GilbertRageAbilitiesComponent comp, float offset)
    {
        if (comp.ConeRays.Length == 0)
            return comp.ConeRange;
        var half = comp.ConeHalfAngle * MathF.PI / 180f;
        var i = (int) MathF.Round((offset + half) / (2f * half) * (ConeRayCount - 1));
        return comp.ConeRays[Math.Clamp(i, 0, comp.ConeRays.Length - 1)];
    }

    /// <summary>Is <paramref name="pos"/> inside the fired cone (axis, angle, walls)?</summary>
    public static bool InCone(GilbertRageAbilitiesComponent comp, Vector2 pos, float reach)
    {
        var d = pos - comp.ConeOrigin;
        var len = d.Length();
        if (len < 0.01f)
            return true;
        if (len > reach)
            return false;
        var offset = (float) Angle.ShortestDistance(new Angle(comp.ConeDir), new Angle(d)).Theta;
        var half = comp.ConeHalfAngle * MathF.PI / 180f;
        // A body is about half a tile wide: let the edge graze it.
        var slack = MathF.Atan2(0.35f, MathF.Max(len, 0.5f));
        if (MathF.Abs(offset) > half + slack)
            return false;
        return len <= ConeReach(comp, Math.Clamp(offset, -half, half)) + 0.5f;
    }

    private void SpreadCone(Entity<GilbertRageAbilitiesComponent> ent, TransformComponent xform)
    {
        var comp = ent.Comp;
        var front = comp.ConeFront;
        var half = comp.ConeHalfAngle * MathF.PI / 180f;
        var map = xform.MapID;

        // Sparks along the front, about one per tile of arc, where the walls haven't stopped it yet.
        var count = Math.Max(2, (int) MathF.Ceiling(front * 2f * half / 0.9f));
        for (var i = 0; i < count; i++)
        {
            var off = -half + 2f * half * (i + _random.NextFloat(0.2f, 0.8f)) / count;
            if (front > ConeReach(comp, off) + 0.3f)
                continue;
            var r = front + _random.NextFloat(-0.35f, 0.15f);
            var p = comp.ConeOrigin + new Angle(off).RotateVec(comp.ConeDir) * r;
            SpawnFx(ConeSpark, new MapCoordinates(p, map), _random.NextAngle());
        }

        if (front <= ConeReach(comp, 0f) + 0.3f)
            SpawnFx(ConeFlash, new MapCoordinates(comp.ConeOrigin + comp.ConeDir * front, map));

        foreach (var (mob, _) in _lookup.GetEntitiesInRange<MobStateComponent>(new MapCoordinates(comp.ConeOrigin, map),
                     front + 0.5f))
        {
            if (mob == ent.Owner || comp.ConeHit.Contains(mob) || _mobState.IsDead(mob))
                continue;
            var pos = _transform.GetWorldPosition(mob);
            if (!InCone(comp, pos, front + 0.5f))
                continue;
            if (!_interaction.InRangeUnobstructed(ent.Owner, mob, (pos - comp.ConeOrigin).Length() + 0.6f))
                continue;

            comp.ConeHit.Add(mob);
            ConeHit(ent, mob);
        }
    }

    private void ConeHit(Entity<GilbertRageAbilitiesComponent> ent, EntityUid victim)
    {
        var comp = ent.Comp;
        Shock(victim, ent, comp.ConeShock);
        _stun.TryParalyze(victim, comp.ConeStun, true);
        _jitter.DoJitter(victim, comp.ConeStun, true, 14f, 7f, true);
        var shocked = EnsureComp<GilbertShockedComponent>(victim);
        shocked.End = _timing.CurTime + comp.ConeStun;
        shocked.NextFx = _timing.CurTime;
        _audio.PlayPvs(comp.BoltSound, victim, AudioParams.Default.WithVolume(-4f));
    }

    #endregion

    #region Levitation

    public bool CanLevitate(EntityUid uid, GilbertRageAbilitiesComponent comp, bool ignoreCooldowns = false)
    {
        return CanStart(uid, comp, ignoreCooldowns) && (ignoreCooldowns || _timing.CurTime >= comp.NextLevitate);
    }

    public bool TryLevitate(Entity<GilbertRageAbilitiesComponent?> ent, bool ignoreCooldowns = false)
    {
        if (!Resolve(ent, ref ent.Comp, false) || !CanLevitate(ent, ent.Comp, ignoreCooldowns))
            return false;

        var comp = ent.Comp;
        var now = _timing.CurTime;
        comp.Levitating = true;
        comp.LevitateStart = now;
        comp.StrikesDone = 0;
        comp.NextLevitateFx = now;
        comp.NextLevitate = now + comp.LevitateCooldown;
        comp.NextAny = now + comp.GlobalGap;
        comp.Levitations++;
        Halt(ent);

        var lev = EnsureComp<GilbertLevitatingComponent>(ent);
        lev.Start = now;
        lev.End = now + comp.LevitateRise + comp.LevitateDuration + TimeSpan.FromSeconds(0.3);
        lev.Rise = (float) comp.LevitateRise.TotalSeconds;
        Dirty(ent, lev);

        _audio.PlayPvs(WindupSound, ent);
        _popup.PopupEntity(Loc.GetString("gilbert-levitate-start"), ent, PopupType.LargeCaution);
        return true;
    }

    /// <summary>When strike <paramref name="i"/> (0-based) falls: evenly over the duration after the rise.</summary>
    public static TimeSpan StrikeTime(GilbertRageAbilitiesComponent comp, int i)
    {
        var n = Math.Max(1, comp.LevitateStrikes);
        return comp.LevitateStart + comp.LevitateRise + comp.LevitateDuration * ((i + 0.5) / n);
    }

    private void UpdateLevitation(Entity<GilbertRageAbilitiesComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        Halt(ent);

        // Red threads running out over the floor from under him, and up into him (what he hangs on).
        if (now >= comp.NextLevitateFx)
        {
            comp.NextLevitateFx = now + TimeSpan.FromSeconds(0.09);
            var pos = _transform.GetMapCoordinates(ent, xform);
            for (var i = 0; i < 2; i++)
                SpawnFx(FloorThread, pos, _random.NextAngle());
            SpawnOn(RiseThread, ent, new Vector2(_random.NextFloat(-0.15f, 0.15f), 0f));
            if (_random.Prob(0.35f))
                SpawnOn(Aura, ent, new Vector2(0f, 0.45f) + _random.NextVector2(0.25f));
        }

        while (comp.StrikesDone < comp.LevitateStrikes && now >= StrikeTime(comp, comp.StrikesDone))
        {
            Strike(ent, xform);
            comp.StrikesDone++;
        }

        if (now >= comp.LevitateStart + comp.LevitateRise + comp.LevitateDuration && comp.StrikesDone >= comp.LevitateStrikes)
            EndLevitation(ent, comp);
    }

    private void EndLevitation(EntityUid uid, GilbertRageAbilitiesComponent comp)
    {
        comp.Levitating = false;
        RemComp<GilbertLevitatingComponent>(uid);
        if (!TerminatingOrDeleted(uid))
            SpawnFx(DashBurst, _transform.GetMapCoordinates(uid));
    }

    /// <summary>One thin bolt: at a living thing in reach (mostly), else at a random spot on the floor.</summary>
    private void Strike(Entity<GilbertRageAbilitiesComponent> ent, TransformComponent xform)
    {
        var comp = ent.Comp;
        var map = xform.MapID;
        var origin = _transform.GetWorldPosition(xform);
        var source = origin + new Vector2(0f, BoltSourceHeight);

        var living = new List<EntityUid>();
        foreach (var (mob, _) in _lookup.GetEntitiesInRange<MobStateComponent>(new MapCoordinates(origin, map), comp.LevitateRange))
        {
            if (mob == ent.Owner || _mobState.IsDead(mob))
                continue;
            if (!_interaction.InRangeUnobstructed(ent.Owner, mob, comp.LevitateRange + 0.5f))
                continue;
            living.Add(mob);
        }

        Vector2 end;
        EntityUid? hit = null;
        if (living.Count > 0 && _random.Prob(comp.LevitateLivingChance))
        {
            hit = _random.Pick(living);
            end = _transform.GetWorldPosition(hit.Value) + _random.NextVector2(0.15f);
        }
        else
        {
            var dir = _random.NextAngle().ToVec();
            var dist = _random.NextFloat(1.5f, comp.LevitateRange);
            dist = MathF.Min(dist, FreeDistance(map, origin, dir, dist, ent) - 0.2f);
            end = origin + dir * MathF.Max(0.8f, dist);
        }

        DrawBolt(map, source, end);

        if (hit is { } target)
        {
            // Wherever it lands on the body (it crawls over that spot).
            Shock(target, ent, comp.LevitateShock, _random.Pick(BoltParts));
            comp.LevitateLivingHits++;
            SpawnOn(BoltCrawl, target, _random.NextVector2(0.2f));
            SpawnOn(Aura, target, Vector2.Zero);
        }
        else
        {
            SpawnFx(ConeSpark, new MapCoordinates(end, map), _random.NextAngle());
            SpawnFx(BoltCrawl, new MapCoordinates(end, map));
        }

        if (comp.StrikesDone % 3 == 0)
            _audio.PlayPvs(comp.BoltSound, hit ?? ent.Owner, AudioParams.Default.WithVolume(-6f).WithVariation(0.15f));
    }

    /// <summary>A jagged thin bolt from <paramref name="from"/> to <paramref name="to"/> out of 1-tile segments.</summary>
    private void DrawBolt(MapId map, Vector2 from, Vector2 to)
    {
        var delta = to - from;
        var len = delta.Length();
        if (len < 0.05f)
            return;

        var n = Math.Max(1, (int) MathF.Ceiling(len / 0.9f));
        var normal = new Vector2(-delta.Y, delta.X) / len;
        var prev = from;
        for (var i = 1; i <= n; i++)
        {
            var next = i == n
                ? to
                : from + delta * ((float) i / n) + normal * _random.NextFloat(-0.25f, 0.25f);
            var seg = next - prev;
            SpawnFx(BoltSegment, new MapCoordinates((prev + next) / 2f, map), new Angle(seg));
            prev = next;
        }
    }

    #endregion

    #region Finisher

    /// <summary>The rage finisher (palm strike, teleport behind, torn in half) on <paramref name="victim"/>.</summary>
    public bool TryRageFinisher(EntityUid gilbert, EntityUid victim, out string? error)
    {
        error = null;
        if (TryComp<GilbertRageAbilitiesComponent>(gilbert, out var comp))
            CancelAll(gilbert, comp);
        if (_finisher.TryStart(gilbert, victim, out var err, RageFinisher))
            return true;
        error = err;
        return false;
    }

    #endregion
}
