using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// v13: the Wolf Dance finisher's katana shadows, and the Claw Mastery skills "Wolf Judgment" (Волчий суд) and
/// "Wolf Gaze" (Волчий взгляд).
/// </summary>
public sealed partial class ProperKungFuSystem
{
    // ---- Shared helpers ----

    private static readonly EntProtoId TintZoneProto = "ProperKungFuTintZone";

    private void SendPose(EntityUid target, ProperKungFuPose pose, float duration, float height, float spins)
    {
        RaiseNetworkEvent(new ProperKungFuPoseAnimationEvent(GetNetEntity(target), pose, duration, height, spins),
            Filter.Pvs(target, entityManager: EntityManager));
    }

    /// <summary>
    /// A recoloured circle of the world (grey, crimson or black-and-white) for <paramref name="duration"/>.
    /// </summary>
    public EntityUid SpawnTintZone(MapCoordinates coords, ProperKungFuTintMode mode, float radius, TimeSpan duration,
        float fadeIn = 0.15f, float fadeOut = 0.2f)
    {
        var now = _timing.CurTime;
        var zone = Spawn(TintZoneProto, coords);
        var tint = EnsureComp<ProperKungFuTintZoneComponent>(zone);
        tint.Mode = mode;
        tint.Radius = radius;
        tint.Start = now;
        tint.End = now + duration;
        tint.FadeIn = fadeIn;
        tint.FadeOut = fadeOut;
        Dirty(zone, tint);
        EnsureComp<TimedDespawnComponent>(zone).Lifetime = (float) duration.TotalSeconds + 0.1f;
        return zone;
    }

    /// <summary>
    /// Ends every pull between or by <paramref name="a"/> and <paramref name="b"/> before one of them is moved by a
    /// skill: a pull joint left in place yanks the other body after every blink and slide.
    /// </summary>
    private void ReleasePulls(EntityUid a, EntityUid b)
    {
        foreach (var uid in new[] { a, b })
        {
            if (TryComp<PullableComponent>(uid, out var pullable) && pullable.BeingPulled)
                _pulling.TryStopPull(uid, pullable, ignoreGrab: true);
            if (TryComp<PullerComponent>(uid, out var puller) && puller.Pulling is { } pulled
                && TryComp<PullableComponent>(pulled, out var pulledComp))
                _pulling.TryStopPull(pulled, pulledComp, ignoreGrab: true);
        }
    }

    private Vector2 DirectionTo(EntityUid from, EntityUid to, Vector2 fallback)
    {
        var delta = _transform.GetWorldPosition(to) - _transform.GetWorldPosition(from);
        return delta.LengthSquared() > 0.0001f ? Vector2.Normalize(delta) : fallback;
    }

    /// <summary>
    /// Never a target of the ultimate skills: the user, their clones / shadows and any Pack Dog.
    /// </summary>
    private bool IsUltimateFriend(EntityUid user, EntityUid uid)
    {
        return uid == user
               || IsPackMember(user, uid)
               || TryComp<ProperKungFuComponent>(uid, out var other) && other.Pupil;
    }

    // ---- Wolf Dance finisher: katana shadows ----

    private static readonly EntProtoId KatanaCloneProto = "ProperKungFuKatanaClone";
    private static readonly EntProtoId KatanaBladeProto = "ProperKungFuKatanaBlade";
    private static readonly EntProtoId KatanaAfterimageProto = "ProperKungFuKatanaAfterimage";
    private static readonly EntProtoId KatanaSlashProto = "ProperKungFuKatanaSlash";
    private static readonly SoundSpecifier KatanaSlashSound = new SoundPathSpecifier("/Audio/Weapons/bladeslice.ogg", AudioParams.Default.WithVolume(4f));

    private const float KatanaRingRadius = 2.2f;
    public static readonly TimeSpan KatanaSpawnDelay = TimeSpan.FromSeconds(0.3);
    public static readonly TimeSpan KatanaDashDelay = TimeSpan.FromSeconds(0.9);
    public static readonly TimeSpan KatanaVanishDelay = TimeSpan.FromSeconds(1.3);
    public static readonly TimeSpan KatanaPhaseLength = TimeSpan.FromSeconds(2.4);

    private void StartKatanaClones(EntityUid user, EntityUid victim, MapId map, Vector2 center, Vector2 away, WolfDanceActionEvent args)
    {
        if (TryComp<ProperKungFuKatanaClonesComponent>(user, out var old))
            FinishKatanaClones(user, old);

        var now = _timing.CurTime;
        var comp = EnsureComp<ProperKungFuKatanaClonesComponent>(user);
        comp.Victim = victim;
        comp.Map = map;
        comp.Center = center;
        comp.Away = away;
        comp.Event = args;
        comp.Clones.Clear();
        comp.From.Clear();
        comp.Dashed = false;
        comp.Struck = false;
        comp.DashStart = now + KatanaDashDelay;
        comp.End = now + KatanaPhaseLength;

        _stun.TryStun(victim, KatanaPhaseLength + TimeSpan.FromSeconds(0.2), true);
        // Spinning in the air, slowly falling while the blades go through.
        SendPose(victim, ProperKungFuPose.SlowFall, (float) KatanaPhaseLength.TotalSeconds, 1.0f, 3f);
    }

    private void UpdateKatanaClones(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuKatanaClonesComponent>();
        while (query.MoveNext(out var user, out var comp))
        {
            var victim = comp.Victim;
            if (comp.Event is not { } args
                || TerminatingOrDeleted(victim)
                || _transform.GetMapId(victim) != comp.Map)
            {
                ClearKatanaClones(comp);
                RemCompDeferred(user, comp);
                continue;
            }

            if (now >= comp.End)
            {
                FinishKatanaClones(user, comp);
                continue;
            }

            // Held in the air.
            _transform.SetWorldPosition(victim, comp.Center);
            _physics.SetLinearVelocity(victim, Vector2.Zero);

            var spawnAt = comp.DashStart - (KatanaDashDelay - KatanaSpawnDelay);
            if (comp.Clones.Count == 0 && !comp.Dashed && now >= spawnAt)
                SpawnKatanaRing(user, comp, args);

            if (!comp.Dashed && now >= comp.DashStart)
                DoKatanaDash(user, comp, args);

            if (comp.Dashed && !comp.Struck && now >= comp.DashStart + (KatanaVanishDelay - KatanaDashDelay))
            {
                comp.Struck = true;
                ClearKatanaClones(comp);
            }
        }
    }

    private void SpawnKatanaRing(EntityUid user, ProperKungFuKatanaClonesComponent comp, WolfDanceActionEvent args)
    {
        var count = Math.Max(1, args.FinisherCloneCount);
        for (var i = 0; i < count; i++)
        {
            var dir = (comp.Away.ToAngle() + Angle.FromDegrees(360.0 / count * i + 30)).ToVec();
            var spot = comp.Center + dir * KatanaRingRadius;
            if (IsPathBlocked(comp.Victim, comp.Map, comp.Center, spot, out var free))
                spot = free;

            comp.From.Add(spot);
            comp.Clones.Add(SpawnKatanaClone(user, comp.Map, spot, -dir));
            Spawn(KunaiDoubleVanish, new MapCoordinates(spot, comp.Map));
        }
    }

    private EntityUid SpawnKatanaClone(EntityUid user, MapId map, Vector2 pos, Vector2 facing)
    {
        var clone = SpawnAfterimage(user, map, pos, KatanaCloneProto);
        _transform.SetWorldRotation(clone, facing.ToWorldAngle());
        var blade = SpawnAttachedTo(KatanaBladeProto, new EntityCoordinates(clone, facing * 0.35f));
        _transform.SetWorldRotation(blade, facing.ToWorldAngle() + Angle.FromDegrees(45));
        return clone;
    }

    /// <summary>
    /// All shadows dash through the victim together: each one deals FinisherFinalDamage / count to each clone part.
    /// </summary>
    private void DoKatanaDash(EntityUid user, ProperKungFuKatanaClonesComponent comp, WolfDanceActionEvent args)
    {
        comp.Dashed = true;
        foreach (var clone in comp.Clones)
        {
            QueueDel(clone);
        }

        comp.Clones.Clear();
        var count = Math.Max(1, args.FinisherCloneCount);
        var share = args.FinisherFinalDamage / count;
        foreach (var from in comp.From)
        {
            var through = comp.Center - from;
            var dir = through.LengthSquared() > 0.0001f ? Vector2.Normalize(through) : comp.Away;
            var to = comp.Center + dir * KatanaRingRadius;
            if (IsPathBlocked(comp.Victim, comp.Map, comp.Center, to, out var free))
                to = free;

            for (var step = 1; step <= 4; step++)
            {
                var ghost = SpawnAfterimage(user, comp.Map, Vector2.Lerp(from, to, step / 5f), KatanaAfterimageProto);
                _transform.SetWorldRotation(ghost, dir.ToWorldAngle());
            }

            comp.Clones.Add(SpawnKatanaClone(user, comp.Map, to, dir));
            var slash = Spawn(KatanaSlashProto, new MapCoordinates(comp.Center, comp.Map));
            _transform.SetWorldRotation(slash, dir.ToWorldAngle());

            foreach (var part in args.FinisherCloneParts)
            {
                DealChunkedDamage(user, comp.Victim, share, part);
            }
        }

        if (args.ShockwaveEffect is { } shockwave)
            Spawn(shockwave, new MapCoordinates(comp.Center, comp.Map));

        _audio.PlayPvs(KatanaSlashSound, comp.Victim);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-katana-user", ("target", comp.Victim)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-katana-target"), comp.Victim, comp.Victim, PopupType.LargeCaution);
    }

    private void ClearKatanaClones(ProperKungFuKatanaClonesComponent comp)
    {
        foreach (var clone in comp.Clones)
        {
            if (TerminatingOrDeleted(clone))
                continue;

            Spawn(KunaiDoubleVanish, _transform.GetMapCoordinates(clone));
            QueueDel(clone);
        }

        comp.Clones.Clear();
    }

    /// <summary>
    /// End of the katana phase (or cut short): any blow not dealt yet still lands, then the victim is thrown back.
    /// </summary>
    private void FinishKatanaClones(EntityUid user, ProperKungFuKatanaClonesComponent comp)
    {
        if (comp.Event is { } args && !TerminatingOrDeleted(comp.Victim))
        {
            if (!comp.Dashed)
                DoKatanaDash(user, comp, args);

            FinishDanceKnockback(comp.Victim, comp.Away, args);
        }

        ClearKatanaClones(comp);
        RemComp(user, comp);
    }

    // ---- Wolf Judgment ----

    public const int JudgmentFlurryHits = 10;
    public const float JudgmentFlurryDamage = 4f;
    public const int JudgmentBarrageHits = 12;
    public const float JudgmentBarrageDamage = 2.5f;
    public const float JudgmentPalmDamage = 50f;
    public const float JudgmentKnockback = 20f;
    public static readonly TimeSpan JudgmentCutDelay = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan JudgmentFlurryInterval = TimeSpan.FromSeconds(0.12);
    private static readonly TimeSpan JudgmentBarrageInterval = TimeSpan.FromSeconds(0.08);
    private static readonly TimeSpan JudgmentKnockbackTime = TimeSpan.FromSeconds(1.4);

    private static readonly EntProtoId JudgmentClaw = "ProperKungFuJudgmentClaw";
    private static readonly EntProtoId JudgmentGoldClaw = "ProperKungFuJudgmentGoldClaw";
    private static readonly EntProtoId JudgmentRedDouble = "ProperKungFuDashAfterimage";
    private static readonly EntProtoId JudgmentWhiteDouble = "ProperKungFuJudgmentWhiteDouble";
    private static readonly EntProtoId JudgmentWolfKanji = "ProperKungFuWolfKanji";
    private static readonly EntProtoId JudgmentEyeFlash = "ProperKungFuJudgmentEyeFlash";
    private static readonly SoundSpecifier JudgmentHitSound = new SoundPathSpecifier("/Audio/Weapons/genhit1.ogg", AudioParams.Default.WithVolume(-2f));
    private static readonly SoundSpecifier JudgmentPalmSound = new SoundPathSpecifier("/Audio/Effects/explosion_small1.ogg", AudioParams.Default.WithVolume(2f));

    private void InitializeWolfJudgment()
    {
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuWolfJudgmentActionEvent>(OnWolfJudgment);
    }

    private void OnWolfJudgment(Entity<ProperKungFuComponent> ent, ref ProperKungFuWolfJudgmentActionEvent args)
    {
        if (args.Handled)
            return;

        var user = args.Performer;
        if (!ent.Comp.UltimateActive
            || _mobState.IsIncapacitated(user)
            || HasComp<ProperKungFuJudgmentComponent>(user))
            return;

        var userCoords = _transform.GetMapCoordinates(user);
        var target = _transform.ToMapCoordinates(args.Target);
        if (target.MapId != userCoords.MapId)
            return;

        args.Handled = true;
        var delta = target.Position - userCoords.Position;
        var dir = delta.LengthSquared() > 0.0001f ? Vector2.Normalize(delta) : _transform.GetWorldRotation(user).ToWorldVec();
        _transform.SetWorldRotation(user, dir.ToWorldAngle());

        var now = _timing.CurTime;
        var comp = EnsureComp<ProperKungFuJudgmentComponent>(user);
        comp.Event = args;
        comp.Map = userCoords.MapId;
        comp.Direction = dir;
        comp.Origin = userCoords.Position;
        comp.Stage = 0;
        comp.Count = 0;
        comp.NextStep = now + JudgmentCutDelay;
        comp.EndBy = now + TimeSpan.FromSeconds(12);
        comp.Hit.Clear();
        comp.Full = false;

        SendPose(user, ProperKungFuPose.Jump, 0.5f, 0.6f, 0f);
        _audio.PlayPvs(args.JumpSound, user);
    }

    /// <summary>
    /// Everyone standing in the 3x6 rectangle in front of <paramref name="origin"/> along <paramref name="dir"/>.
    /// </summary>
    public List<EntityUid> GetJudgmentTargets(EntityUid user, MapId map, Vector2 origin, Vector2 dir, float length, float width)
    {
        var result = new List<EntityUid>();
        var center = origin + dir * (length / 2f);
        var radius = MathF.Sqrt(length * length / 4f + width * width / 4f) + 0.5f;
        foreach (var uid in _lookup.GetEntitiesInRange<MobStateComponent>(new MapCoordinates(center, map), radius))
        {
            if (IsUltimateFriend(user, uid) || _mobState.IsDead(uid) || _container.IsEntityInContainer(uid))
                continue;

            var rel = _transform.GetWorldPosition(uid) - origin;
            var along = Vector2.Dot(rel, dir);
            var side = MathF.Abs(rel.X * dir.Y - rel.Y * dir.X);
            if (along < -0.3f || along > length + 0.3f || side > width / 2f + 0.3f)
                continue;

            result.Add(uid);
        }

        return result;
    }

    private void UpdateJudgments(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuJudgmentComponent>();
        while (query.MoveNext(out var user, out var comp))
        {
            if (comp.Event is not { } args
                || now >= comp.EndBy
                || _transform.GetMapId(user) != comp.Map
                || _mobState.IsIncapacitated(user))
            {
                EndJudgment(user);
                continue;
            }

            if (comp.Stage >= 2)
            {
                if (TerminatingOrDeleted(comp.Victim) || _transform.GetMapId(comp.Victim) != comp.Map)
                {
                    EndJudgment(user);
                    continue;
                }

                // v13.1: only from the flurry on, once Center is the victim's real spot. Holding during stage 2
                // (before Center was set) pinned the victim to (0, 0) of the map, far from the cast.
                if (comp.Stage is >= 3 and <= 4)
                {
                    // Held in the air during the flurry and the barrage.
                    _transform.SetWorldPosition(comp.Victim, comp.Center);
                    _physics.SetLinearVelocity(comp.Victim, Vector2.Zero);
                }
            }

            if (now < comp.NextStep)
                continue;

            UpdateJudgmentStage(user, comp, args, now);
        }
    }

    private void UpdateJudgmentStage(EntityUid user, ProperKungFuJudgmentComponent comp, ProperKungFuWolfJudgmentActionEvent args, TimeSpan now)
    {
        var map = comp.Map;
        switch (comp.Stage)
        {
            case 0:
            case 1:
            {
                // The two cuts of the big X.
                if (comp.Stage == 0)
                {
                    comp.Hit = GetJudgmentTargets(user, map, comp.Origin, comp.Direction, args.AreaLength, args.AreaWidth);
                    _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-judgment-user"), user, user, PopupType.Large);
                }

                var areaCenter = comp.Origin + comp.Direction * (args.AreaLength / 2f);
                var claw = Spawn(JudgmentClaw, new MapCoordinates(areaCenter, map));
                _transform.SetWorldRotation(claw, comp.Direction.ToWorldAngle() + Angle.FromDegrees(comp.Stage == 0 ? 45 : -45));
                _audio.PlayPvs(args.CutSound, claw);

                foreach (var victim in comp.Hit)
                {
                    if (TerminatingOrDeleted(victim))
                        continue;

                    DealChunkedDamage(user, victim, args.CutDamage, SkillTargetPart);
                    SpawnWeakSkillFlashes(victim);
                    if (comp.Stage == 1)
                    {
                        _stun.TryKnockdown(victim, args.KnockdownTime, true);
                        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-judgment-target"), victim, victim, PopupType.LargeCaution);
                    }
                }

                if (comp.Stage == 0)
                {
                    comp.Stage = 1;
                    comp.NextStep = now + JudgmentCutDelay;
                    return;
                }

                var kungFu = CompOrNull<ProperKungFuComponent>(user);
                var victims = comp.Hit.Where(v => !TerminatingOrDeleted(v) && !_mobState.IsDead(v)).ToList();
                if (kungFu == null || kungFu.UltimateJudgmentUsed || victims.Count == 0)
                {
                    if (kungFu is { UltimateJudgmentUsed: true } && victims.Count > 0)
                        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-judgment-trimmed-user"), user, user, PopupType.Small);
                    EndJudgment(user);
                    return;
                }

                // The full version, once per ultimate mode.
                kungFu.UltimateJudgmentUsed = true;
                var userPos = _transform.GetWorldPosition(user);
                comp.Victim = victims.MinBy(v => (_transform.GetWorldPosition(v) - userPos).LengthSquared());
                comp.Center = _transform.GetWorldPosition(comp.Victim);
                ReleasePulls(user, comp.Victim);
                comp.Full = true;
                comp.Stage = 2;
                comp.NextStep = now + TimeSpan.FromSeconds(0.15);
                return;
            }
            case 2:
            {
                // Dash in, the world turns crimson, the victim is thrown up for the flurry.
                var victim = comp.Victim;
                comp.Center = _transform.GetWorldPosition(victim);
                ReleasePulls(user, victim);
                var from = _transform.GetWorldPosition(user);
                var dir = DirectionTo(user, victim, comp.Direction);
                comp.Direction = dir;
                var spot = comp.Center - dir * 0.8f;
                if (IsPathBlocked(user, map, comp.Center, spot, out var free))
                    spot = free;

                for (var i = 0; i < 4; i++)
                {
                    SpawnAfterimage(user, map, Vector2.Lerp(from, spot, i / 4f), JudgmentRedDouble);
                }

                SpawnSpeedLines(map, from, spot);

                RemComp<ProperKungFuSlidingComponent>(user);
                RemComp<ProperKungFuSlidingComponent>(victim);
                _transform.SetWorldPosition(user, spot);
                _transform.SetWorldRotation(user, dir.ToWorldAngle());

                comp.Tint = SpawnTintZone(new MapCoordinates(comp.Center, map), ProperKungFuTintMode.Crimson, 7f, TimeSpan.FromSeconds(1.6));
                _stun.TryStun(victim, TimeSpan.FromSeconds(4), true);
                SendPose(victim, ProperKungFuPose.Launch, 2.6f, 0.6f, 4f);
                Spawn(PalmFlashEffect, new MapCoordinates(comp.Center, map));
                _audio.PlayPvs(JudgmentHitSound, victim);

                _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-judgment-combo-user", ("target", victim)), user, user, PopupType.Large);
                _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-judgment-combo-target", ("user", user)), victim, victim, PopupType.LargeCaution);

                comp.Stage = 3;
                comp.Count = 0;
                comp.NextStep = now + JudgmentFlurryInterval;
                return;
            }
            case 3:
            {
                // Red flurry: the user blinks between the points of a star, a crimson triple claw every hit.
                var victim = comp.Victim;
                var angle = comp.Direction.ToAngle() + Angle.FromDegrees(180 + 144 * comp.Count);
                var spot = comp.Center + angle.ToVec() * 1.2f;
                if (IsPathBlocked(victim, map, comp.Center, spot, out var free))
                    spot = free;

                SpawnAfterimage(user, map, _transform.GetWorldPosition(user), JudgmentRedDouble);
                _transform.SetWorldPosition(user, spot);
                _transform.SetWorldRotation(user, (comp.Center - spot).ToWorldAngle());

                var gold = Spawn(JudgmentGoldClaw, new MapCoordinates(comp.Center, map));
                _transform.SetWorldRotation(gold, _instinctRandom.NextAngle());
                DealChunkedDamage(user, victim, Slash(JudgmentFlurryDamage), SkillTargetPart);
                _audio.PlayPvs(JudgmentHitSound, victim);

                comp.Count++;
                comp.NextStep = now + JudgmentFlurryInterval;
                if (comp.Count < JudgmentFlurryHits)
                    return;

                if (comp.Tint is { } tint && !TerminatingOrDeleted(tint))
                    QueueDel(tint);
                comp.Tint = SpawnTintZone(new MapCoordinates(comp.Center, map), ProperKungFuTintMode.Noir, 7f, TimeSpan.FromSeconds(1.3), fadeIn: 0.05f);
                comp.Stage = 4;
                comp.Count = 0;
                return;
            }
            case 4:
            {
                // Black-and-white barrage from every side.
                var victim = comp.Victim;
                var angle = comp.Direction.ToAngle() + Angle.FromDegrees(360.0 / JudgmentBarrageHits * comp.Count + _instinctRandom.NextFloat(-10f, 10f));
                var spot = comp.Center + angle.ToVec() * 1.0f;
                var ghost = SpawnAfterimage(user, map, spot, JudgmentWhiteDouble);
                _transform.SetWorldRotation(ghost, (comp.Center - spot).ToWorldAngle());
                DealChunkedDamage(user, victim, Slash(JudgmentBarrageDamage), SkillTargetPart);
                if (comp.Count % 3 == 0)
                    _audio.PlayPvs(JudgmentHitSound, victim);

                comp.Count++;
                comp.NextStep = now + JudgmentBarrageInterval;
                if (comp.Count < JudgmentBarrageHits)
                    return;

                comp.Stage = 5;
                comp.NextStep = now + TimeSpan.FromSeconds(0.3);
                return;
            }
            case 5:
            {
                // Colours back: the double palm into the chest, a shockwave ring and the launch.
                var victim = comp.Victim;
                if (comp.Tint is { } tint && !TerminatingOrDeleted(tint))
                    QueueDel(tint);
                comp.Tint = null;

                var dir = comp.Direction;
                var spot = comp.Center - dir * 0.7f;
                if (IsPathBlocked(user, map, comp.Center, spot, out var free))
                    spot = free;
                _transform.SetWorldPosition(user, spot);
                _transform.SetWorldRotation(user, dir.ToWorldAngle());

                Spawn(PalmFlashEffect, new MapCoordinates(comp.Center, map));
                Spawn(DeathPunchRing, new MapCoordinates(comp.Center, map));
                Spawn(TimeStopShockwave, new MapCoordinates(comp.Center, map));
                DealChunkedDamage(user, victim, Slash(JudgmentPalmDamage), SkillTargetPart);

                StartKnockback(victim, dir, JudgmentKnockback, JudgmentKnockbackTime, dust: true);
                KickFinisherCamera(user, dir, 0.5f);
                SendPose(victim, ProperKungFuPose.Launch, (float) JudgmentKnockbackTime.TotalSeconds, 0.5f, 3f);
                _stun.TryKnockdown(victim, JudgmentKnockbackTime + args.KnockdownTime, true);
                _audio.PlayPvs(JudgmentPalmSound, victim);

                comp.Stage = 6;
                comp.NextStep = now + TimeSpan.FromSeconds(0.4);
                return;
            }
            default:
            {
                // Aftermath: 狼 burns where it happened, the user slides back into a crouch, eyes flashing red.
                Spawn(JudgmentWolfKanji, new MapCoordinates(comp.Center + new Vector2(0f, 0.5f), map));
                StartKnockback(user, -comp.Direction, 1.5f, TimeSpan.FromSeconds(0.35), dust: true);
                SendPose(user, ProperKungFuPose.Crouch, 1.2f, 0.25f, 0f);
                SpawnAttachedTo(JudgmentEyeFlash, new EntityCoordinates(user, new Vector2(0f, 0.3f)));
                EndJudgment(user);
                return;
            }
        }
    }

    private static DamageSpecifier Slash(float amount)
    {
        return new DamageSpecifier { DamageDict = { ["Slash"] = FixedPoint2.New(amount) } };
    }

    private void EndJudgment(EntityUid user)
    {
        if (!TryComp<ProperKungFuJudgmentComponent>(user, out var comp))
            return;

        if (comp.Tint is { } tint && !TerminatingOrDeleted(tint))
            QueueDel(tint);

        RemCompDeferred(user, comp);
    }

    // ---- Wolf Gaze ----

    private static readonly EntProtoId GazeFlashProto = "ProperKungFuGazeFlash";
    private static readonly EntProtoId GazeStrikeProto = "ProperKungFuGazeStrike";
    private static readonly EntProtoId GazeEyeProto = "ProperKungFuGazeEye";
    private static readonly EntProtoId GazeAuraProto = "ProperKungFuInstinctAura";
    private static readonly TimeSpan GazeBeatInterval = TimeSpan.FromSeconds(0.75);

    private void InitializeWolfGaze()
    {
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuWolfGazeActionEvent>(OnWolfGaze);
        SubscribeLocalEvent<ProperKungFuGazeStanceComponent, BeforeDamageChangedEvent>(OnGazeStanceDamage);
        SubscribeLocalEvent<ProperKungFuGazeStanceComponent, ComponentShutdown>(OnGazeStanceShutdown);
        SubscribeLocalEvent<ProperKungFuGazeComponent, BeforeDamageChangedEvent>(OnGazeDamage);
    }

    private void OnWolfGaze(Entity<ProperKungFuComponent> ent, ref ProperKungFuWolfGazeActionEvent args)
    {
        if (args.Handled)
            return;

        var user = args.Performer;
        if (!ent.Comp.UltimateActive
            || _mobState.IsIncapacitated(user)
            || HasComp<ProperKungFuGazeComponent>(user))
            return;

        if (ent.Comp.UltimateGazeUsed)
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-gaze-used"), user, user, PopupType.SmallCaution);
            return;
        }

        if (HasComp<ProperKungFuGazeStanceComponent>(user))
            return;

        // The one use per ultimate is spent only by a successful trigger (see OnGazeStanceDamage).
        args.Handled = true;

        var stance = EnsureComp<ProperKungFuGazeStanceComponent>(user);
        stance.Event = args;
        stance.Action = args.Action.Owner;
        stance.EndTime = _timing.CurTime + args.Duration;
        stance.Attacker = null;
        if (stance.Aura is not { } aura || TerminatingOrDeleted(aura))
            stance.Aura = SpawnAttachedTo(GazeAuraProto, new EntityCoordinates(user, Vector2.Zero));

        _audio.PlayPvs(args.HeartbeatSound, user);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-gaze-user"), user, user, PopupType.Medium);
    }

    /// <summary>
    /// Whether <paramref name="attacker"/> can set off the stance: a living mob within range, not one of the user's own.
    /// </summary>
    public bool CanTriggerGaze(EntityUid user, EntityUid attacker, float range)
    {
        if (attacker == user
            || TerminatingOrDeleted(attacker)
            || !HasComp<MobStateComponent>(attacker)
            || IsUltimateFriend(user, attacker))
            return false;

        var a = _transform.GetMapCoordinates(user);
        var b = _transform.GetMapCoordinates(attacker);
        return a.MapId == b.MapId && (a.Position - b.Position).Length() <= range + 0.01f;
    }

    private void OnGazeStanceDamage(Entity<ProperKungFuGazeStanceComponent> ent, ref BeforeDamageChangedEvent args)
    {
        if (args.Cancelled
            || args.Origin is not { } attacker
            || ent.Comp.Event is not { } gaze
            || !args.Damage.AnyPositive())
            return;

        if (ent.Comp.Attacker == attacker)
        {
            args.Cancelled = true;
            return;
        }

        if (ent.Comp.Attacker != null
            || _timing.CurTime >= ent.Comp.EndTime
            || _mobState.IsIncapacitated(ent)
            || !CanTriggerGaze(ent, attacker, gaze.TriggerRange))
            return;

        // The attack is caught; the cutscene starts on the next update. This spends the one use per ultimate.
        args.Cancelled = true;
        ent.Comp.Attacker = attacker;
        if (TryComp<ProperKungFuComponent>(ent, out var kungFu))
            kungFu.UltimateGazeUsed = true;
    }

    private void OnGazeDamage(Entity<ProperKungFuGazeComponent> ent, ref BeforeDamageChangedEvent args)
    {
        if (args.Origin == ent.Comp.Attacker && args.Damage.AnyPositive())
            args.Cancelled = true;
    }

    private void OnGazeStanceShutdown(Entity<ProperKungFuGazeStanceComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Aura is { } aura && !TerminatingOrDeleted(aura))
            QueueDel(aura);
        ent.Comp.Aura = null;
    }

    private void EndGazeStance(EntityUid user)
    {
        RemCompDeferred<ProperKungFuGazeStanceComponent>(user);
    }

    private void UpdateWolfGaze(TimeSpan now)
    {
        var stances = EntityQueryEnumerator<ProperKungFuGazeStanceComponent>();
        while (stances.MoveNext(out var user, out var stance))
        {
            if (stance.Attacker is { } attacker && stance.Event is { } gaze)
            {
                RemCompDeferred(user, stance);
                if (!TerminatingOrDeleted(attacker) && !_mobState.IsIncapacitated(user))
                    StartGazeCutscene(user, attacker, gaze, now);
                continue;
            }

            if (now < stance.EndTime && !_mobState.IsIncapacitated(user))
                continue;

            RemCompDeferred(user, stance);
            // Nobody attacked: the use is not spent, the action just goes on the long cooldown.
            if (stance.Action is { } action && !TerminatingOrDeleted(action) && stance.Event is { } missed)
                _actions.SetCooldown(action, missed.MissCooldown);
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-gaze-fade"), user, user, PopupType.Small);
        }

        var scenes = EntityQueryEnumerator<ProperKungFuGazeComponent>();
        while (scenes.MoveNext(out var user, out var scene))
        {
            var attacker = scene.Attacker;
            if (scene.Event is not { } args
                || TerminatingOrDeleted(attacker)
                || _transform.GetMapId(attacker) != _transform.GetMapId(user))
            {
                EndGazeCutscene(user, scene);
                continue;
            }

            if (scene.Stage == 0)
            {
                if (now >= scene.NextBeat)
                {
                    scene.NextBeat = now + GazeBeatInterval;
                    _audio.PlayPvs(args.HeartbeatSound, user);
                }

                if (now < scene.ThrustAt)
                    continue;

                DoGazeThrust(user, scene, args, now);
                continue;
            }

            if (now < scene.ReleaseAt)
                continue;

            // Time moves again: the attacker is launched.
            EndGazeCutscene(user, scene);
            StartKnockback(attacker, scene.Direction, args.KnockbackDistance, TimeSpan.FromSeconds(0.9), dust: true);
            SendPose(attacker, ProperKungFuPose.Launch, 0.9f, 0.5f, 2f);
            _stun.TryKnockdown(attacker, TimeSpan.FromSeconds(0.9) + args.KnockdownTime, true);
        }
    }

    private void StartGazeCutscene(EntityUid user, EntityUid attacker, ProperKungFuWolfGazeActionEvent args, TimeSpan now)
    {
        var map = _transform.GetMapId(user);
        var dir = DirectionTo(user, attacker, _transform.GetWorldRotation(user).ToWorldVec());
        var userPos = _transform.GetWorldPosition(user);
        var attackerPos = _transform.GetWorldPosition(attacker);
        ReleasePulls(user, attacker);

        // Out of arm's reach (thrown or shot): one blink in to catch them.
        if ((attackerPos - userPos).Length() > 1.0f)
        {
            var spot = attackerPos - dir * 0.8f;
            if (IsPathBlocked(user, map, attackerPos, spot, out var free))
                spot = free;
            for (var i = 0; i < 3; i++)
            {
                SpawnAfterimage(user, map, Vector2.Lerp(userPos, spot, i / 3f), DashAfterimageEffect);
            }

            SpawnSpeedLines(map, userPos, spot);
            _transform.SetWorldPosition(user, spot);
            userPos = spot;
        }

        RemComp<ProperKungFuSlidingComponent>(user);
        RemComp<ProperKungFuSlidingComponent>(attacker);
        _transform.SetWorldRotation(user, dir.ToWorldAngle());
        _transform.SetWorldRotation(attacker, (-dir).ToWorldAngle());

        var hold = args.TensionTime + args.FreezeFrameTime + 1f;
        Freeze(attacker, hold, null);
        Freeze(user, hold, null);

        var mid = new MapCoordinates((userPos + attackerPos) / 2f, map);
        var flash = Spawn(GazeFlashProto, mid);
        _transform.SetWorldRotation(flash, dir.ToWorldAngle());
        _audio.PlayPvs(args.CatchSound, user);

        var scene = EnsureComp<ProperKungFuGazeComponent>(user);
        scene.Event = args;
        scene.Attacker = attacker;
        scene.Stage = 0;
        scene.Direction = dir;
        scene.ThrustAt = now + TimeSpan.FromSeconds(args.TensionTime);
        scene.NextBeat = now + TimeSpan.FromSeconds(0.3);
        scene.Eye = SpawnAttachedTo(GazeEyeProto, new EntityCoordinates(user, new Vector2(0f, 0.35f)));

        var total = args.TensionTime + args.FreezeFrameTime;
        SendGazeCinematic(user, new ProperKungFuGazeCinematicEvent(total, args.TensionTime, false));
        SendGazeCinematic(attacker, new ProperKungFuGazeCinematicEvent(total, args.TensionTime, true));

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-gaze-catch-user", ("target", attacker)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-gaze-catch-target", ("user", user)), attacker, attacker, PopupType.LargeCaution);
    }

    private void SendGazeCinematic(EntityUid uid, ProperKungFuGazeCinematicEvent ev)
    {
        if (TryComp<ActorComponent>(uid, out var actor))
            RaiseNetworkEvent(ev, actor.PlayerSession);
    }

    private void DoGazeThrust(EntityUid user, ProperKungFuGazeComponent scene, ProperKungFuWolfGazeActionEvent args, TimeSpan now)
    {
        var attacker = scene.Attacker;
        var coords = _transform.GetMapCoordinates(attacker);
        var strike = Spawn(GazeStrikeProto, coords);
        _transform.SetWorldRotation(strike, scene.Direction.ToWorldAngle());
        Spawn(PalmFlashEffect, coords);

        // 150 Blunt into the chest; no finisher heal.
        DealChunkedDamage(user, attacker, args.Damage, TargetBodyPart.Chest);

        // Freeze-frame: the world goes grey around them, one loud heartbeat.
        var mid = (_transform.GetWorldPosition(user) + coords.Position) / 2f;
        SpawnTintZone(new MapCoordinates(mid, coords.MapId), ProperKungFuTintMode.TimeStop, 5f,
            TimeSpan.FromSeconds(args.FreezeFrameTime), fadeIn: 0.03f, fadeOut: 0.15f);
        _audio.PlayPvs(args.ThrustSound, attacker);
        _audio.PlayPvs(args.HeartbeatSound, user, AudioParams.Default.WithVolume(8f));

        if (scene.Eye is { } eye && !TerminatingOrDeleted(eye))
            QueueDel(eye);
        scene.Eye = null;

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-gaze-strike-user", ("target", attacker)), user, user, PopupType.Large);
        scene.Stage = 1;
        scene.ReleaseAt = now + TimeSpan.FromSeconds(args.FreezeFrameTime);
    }

    private void EndGazeCutscene(EntityUid user, ProperKungFuGazeComponent scene)
    {
        Unfreeze(user);
        if (!TerminatingOrDeleted(scene.Attacker))
            Unfreeze(scene.Attacker);
        if (scene.Eye is { } eye && !TerminatingOrDeleted(eye))
            QueueDel(eye);
        RemComp(user, scene);
    }
}
