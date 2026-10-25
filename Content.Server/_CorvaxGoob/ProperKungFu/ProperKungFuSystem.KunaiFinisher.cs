using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server.Body.Components;
using Content.Server.Damage.Systems;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Kunai finishers (master only, never on a dead target, at 80+ damage before the hit, +25 heal):
/// a melee kunai hit kicks the victim away, a kunai cuts its neck (all blood out) and a second kunai carries the
/// user in for the one-inch death punch; a thrown kunai hit calls ten crimson doubles and a ten-kunai volley.
/// Kunai thrown by their summoner also fly twice as fast.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly SharedBloodstreamSystem _bloodstream = default!;

    public const float KunaiFinisherThreshold = ProperKungFuInfo.KunaiFinisherThreshold;
    public const float KunaiThrowSpeedMultiplier = 2f;

    private static readonly EntProtoId FlyingKunaiProto = "ProperKungFuFlyingKunai";
    private static readonly EntProtoId KunaiKickTrail = "ProperKungFuKunaiKickTrail";
    private static readonly EntProtoId KunaiNeckSlash = "ProperKungFuKunaiNeckSlash";
    private static readonly EntProtoId KunaiPierce = "ProperKungFuKunaiPierce";
    private static readonly EntProtoId DeathPunchKanji = "ProperKungFuOneInchKanji";
    private static readonly EntProtoId DeathPunchImpact = "ProperKungFuPalmFlash";
    private static readonly EntProtoId KunaiDouble = "ProperKungFuKunaiDouble";

    private static readonly SoundSpecifier KunaiKickSound = new SoundCollectionSpecifier("kick");
    private static readonly SoundSpecifier KunaiThrowSound = new SoundPathSpecifier("/Audio/Weapons/punchmiss.ogg", AudioParams.Default.WithVolume(-4f));
    private static readonly SoundSpecifier KunaiPierceSound = new SoundPathSpecifier("/Audio/Weapons/bladeslice.ogg", AudioParams.Default.WithVolume(-3f));
    private static readonly SoundSpecifier DeathPunchSound = new SoundPathSpecifier("/Audio/Effects/explosion_small1.ogg", AudioParams.Default.WithVolume(2f));

    private const float KunaiKickDistance = 3f;
    private static readonly TimeSpan KunaiKickDuration = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan KunaiMeleeVictimStun = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KunaiNeckThrowAt = TimeSpan.FromSeconds(0.45);
    private static readonly TimeSpan KunaiPunchThrowAt = TimeSpan.FromSeconds(1.0);

    private const float DeathPunchKnockback = 20f;
    private static readonly TimeSpan DeathPunchKnockbackDuration = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan DeathPunchBreathBlock = TimeSpan.FromMinutes(2);
    private const float DeathPunchOrganFraction = 0.6f;
    private const float DeathPunchKanjiDistance = 1.1f;
    private static readonly DamageSpecifier DeathPunchDamage = new() { DamageDict = { ["Blunt"] = 90 } };
    private static readonly HashSet<string> DeathPunchOrganSlots = new() { "heart", "lungs", "lung", "stomach", "liver", "kidneys" };

    private const int KunaiDoubleCount = 10;
    private const float KunaiDoubleRadius = 3f;
    private static readonly TimeSpan KunaiDoubleInterval = TimeSpan.FromSeconds(0.1);
    private const int KunaiVolleyCount = 10;
    private static readonly TimeSpan KunaiVolleyInterval = TimeSpan.FromSeconds(0.06);
    private const float KunaiVolleyDamage = 15f;
    private const float KunaiBackflipDistance = 1f;
    private static readonly TimeSpan KunaiBackflipDuration = TimeSpan.FromSeconds(0.35);
    private static readonly TimeSpan KunaiThrownVictimStun = TimeSpan.FromSeconds(3);

    private const float FlyingKunaiFastSpeed = 25f;
    private const float FlyingKunaiArrive = 0.3f;
    private static readonly Angle FlyingKunaiSpriteOffset = Angle.FromDegrees(225);

    private void InitializeKunaiFinishers()
    {
        SubscribeLocalEvent<ProperKungFuComponent, BeforeThrowEvent>(OnKungFuBeforeThrow);
        SubscribeLocalEvent<ProperKungFuKunaiComponent, MeleeHitEvent>(OnKunaiMeleeHit);
        SubscribeLocalEvent<ProperKungFuKunaiComponent, ThrowDoHitEvent>(OnKunaiThrowHit,
            before: new[] { typeof(DamageOtherOnHitSystem) });
    }

    private void OnKungFuBeforeThrow(Entity<ProperKungFuComponent> ent, ref BeforeThrowEvent args)
    {
        if (TryComp<ProperKungFuKunaiComponent>(args.ItemUid, out var kunai) && kunai.Summoner == ent.Owner)
            args.ThrowSpeed *= KunaiThrowSpeedMultiplier;
    }

    private bool CanStartKunaiFinisher(EntityUid user, EntityUid target, float preDamage)
    {
        return TryComp<ProperKungFuComponent>(user, out var kungFu)
               && kungFu.HasPassives
               && !HasComp<ProperKungFuCloneComponent>(user)
               && target != user
               && HasComp<MobStateComponent>(target)
               && !_mobState.IsDead(target)
               && preDamage >= KunaiFinisherThreshold
               && !HasComp<ProperKungFuKunaiFinisherComponent>(user);
    }

    private float GetTotalDamage(EntityUid uid)
    {
        return TryComp<DamageableComponent>(uid, out var damageable) ? damageable.TotalDamage.Float() : 0f;
    }

    private void OnKunaiMeleeHit(Entity<ProperKungFuKunaiComponent> ent, ref MeleeHitEvent args)
    {
        if (!args.IsHit || args.Handled || args.HitEntities.Count == 0 || args.User != ent.Comp.Summoner)
            return;

        var user = args.User;
        foreach (var hit in args.HitEntities)
        {
            MarkBloodHarvest(user, hit);
        }

        var target = args.HitEntities[0];
        if (!CanStartKunaiFinisher(user, target, GetTotalDamage(target)))
            return;

        // The kunai strike itself becomes the finisher's opening kick.
        args.Handled = true;
        StartKunaiFinisher(user, target, KunaiFinisherKind.Melee);
    }

    private void OnKunaiThrowHit(Entity<ProperKungFuKunaiComponent> ent, ref ThrowDoHitEvent args)
    {
        if (ent.Comp.ThrowOrigin == null || ent.Comp.Summoner is not { } user)
            return;

        MarkBloodHarvest(user, args.Target);

        // Ordered before the throw damage: this is still the damage from before the hit.
        if (!CanStartKunaiFinisher(user, args.Target, GetTotalDamage(args.Target)))
            return;

        // No pull to the kunai: the user flips back instead.
        ent.Comp.ThrowOrigin = null;
        StartKunaiFinisher(user, args.Target, KunaiFinisherKind.Thrown);
    }

    private void StartKunaiFinisher(EntityUid user, EntityUid victim, KunaiFinisherKind kind)
    {
        var now = _timing.CurTime;
        var finisher = EnsureComp<ProperKungFuKunaiFinisherComponent>(user);
        finisher.Kind = kind;
        finisher.Victim = victim;
        finisher.Map = _transform.GetMapId(user);
        finisher.Phase = 0;
        finisher.Count = 0;
        finisher.NextStep = now;
        finisher.EndBy = now + TimeSpan.FromSeconds(6);

        var delta = _transform.GetWorldPosition(victim) - _transform.GetWorldPosition(user);
        finisher.Direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(user).ToWorldVec();
        _transform.SetWorldRotation(user, finisher.Direction.ToWorldAngle());

        _stun.TryStun(victim, kind == KunaiFinisherKind.Melee ? KunaiMeleeVictimStun : KunaiThrownVictimStun, true);

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-finisher-user", ("target", victim)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-finisher-target", ("user", user)), victim, victim, PopupType.LargeCaution);

        var hitEv = new ProperKungFuSpecialHitEvent(victim, KunaiMove, false);
        RaiseLocalEvent(user, ref hitEv);
    }

    private const string KunaiMove = "Kunai";

    private void UpdateKunaiFinishers(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuKunaiFinisherComponent>();
        while (query.MoveNext(out var user, out var finisher))
        {
            var victim = finisher.Victim;
            if (now >= finisher.EndBy
                || TerminatingOrDeleted(victim)
                || _transform.GetMapId(victim) != finisher.Map
                || _transform.GetMapId(user) != finisher.Map
                || _container.IsEntityInContainer(user))
            {
                RemCompDeferred(user, finisher);
                continue;
            }

            if (now < finisher.NextStep)
                continue;

            if (finisher.Kind == KunaiFinisherKind.Melee)
                UpdateKunaiMeleeFinisher(user, finisher, now);
            else
                UpdateKunaiThrownFinisher(user, finisher, now);
        }

        UpdateFlyingKunai(now);

        var breaths = EntityQueryEnumerator<ProperKungFuDeathPunchBreathComponent>();
        while (breaths.MoveNext(out var uid, out var breath))
        {
            // Cleared on death (and once its time is up).
            if (now < breath.ExpiresAt && !_mobState.IsDead(uid))
                continue;

            RemCompDeferred<KravMagaBlockedBreathingComponent>(uid);
            RemCompDeferred(uid, breath);
        }
    }

    private void UpdateKunaiMeleeFinisher(EntityUid user, ProperKungFuKunaiFinisherComponent finisher, TimeSpan now)
    {
        var victim = finisher.Victim;
        switch (finisher.Phase)
        {
            case 0:
            {
                // Horizontal kick: the victim flies 3 tiles back behind a claw trail.
                var trail = Spawn(KunaiKickTrail, _transform.GetMapCoordinates(victim));
                _transform.SetWorldRotation(trail, finisher.Direction.ToWorldAngle());
                Spawn(PalmFlashEffect, _transform.GetMapCoordinates(victim));
                StartKnockback(victim, finisher.Direction, KunaiKickDistance, KunaiKickDuration, dust: true);
                _audio.PlayPvs(KunaiKickSound, victim);

                finisher.Phase = 1;
                finisher.NextStep = now + KunaiNeckThrowAt;
                return;
            }
            case 1:
                LaunchFlyingKunai(user, finisher.Map, _transform.GetWorldPosition(user), victim, FlyingKunaiKind.Neck, 1f, FlyingKunaiFastSpeed);
                finisher.Phase = 2;
                finisher.NextStep = now + (KunaiPunchThrowAt - KunaiNeckThrowAt);
                return;
            case 2:
                LaunchFlyingKunai(user, finisher.Map, _transform.GetWorldPosition(user), victim, FlyingKunaiKind.Punch, 1f, FlyingKunaiFastSpeed);
                // The punch kunai ends the sequence on arrival; this is only a fallback.
                finisher.Phase = 3;
                finisher.NextStep = now + TimeSpan.FromSeconds(2);
                return;
            default:
                RemCompDeferred(user, finisher);
                return;
        }
    }

    private void UpdateKunaiThrownFinisher(EntityUid user, ProperKungFuKunaiFinisherComponent finisher, TimeSpan now)
    {
        var victim = finisher.Victim;
        switch (finisher.Phase)
        {
            case 0:
            {
                // The user backflips one tile away.
                StartKnockback(user, -finisher.Direction, KunaiBackflipDistance, KunaiBackflipDuration, dust: false);
                RaiseNetworkEvent(new ProperKungFuBackflipEvent(GetNetEntity(user), (float) KunaiBackflipDuration.TotalSeconds, -finisher.Direction),
                    Filter.Pvs(user, entityManager: EntityManager));

                finisher.Phase = 1;
                finisher.Count = 0;
                finisher.NextStep = now + TimeSpan.FromSeconds(0.1);
                return;
            }
            case 1:
            {
                // Ten crimson doubles around the victim, each throws one fast kunai and fades away.
                var victimPos = _transform.GetWorldPosition(victim);
                var angle = finisher.Direction.ToAngle() + Angle.FromDegrees(180 + 360.0 / KunaiDoubleCount * finisher.Count);
                var dir = angle.ToVec();
                var spot = victimPos + dir * KunaiDoubleRadius;
                if (IsPathBlocked(victim, finisher.Map, victimPos, spot, out var free))
                    spot = free;

                var ghost = SpawnAfterimage(user, finisher.Map, spot, KunaiDouble);
                _transform.SetWorldRotation(ghost, (-dir).ToWorldAngle());
                LaunchFlyingKunai(user, finisher.Map, spot, victim, FlyingKunaiKind.Pierce, 1f, FlyingKunaiFastSpeed);

                finisher.Count++;
                finisher.NextStep = now + KunaiDoubleInterval;
                if (finisher.Count >= KunaiDoubleCount)
                {
                    finisher.Phase = 2;
                    finisher.Count = 0;
                    finisher.NextStep = now + TimeSpan.FromSeconds(0.25);
                }

                return;
            }
            case 2:
            {
                // The volley: ten kunai from behind the user at normal damage.
                var userPos = _transform.GetWorldPosition(user);
                var victimPos = _transform.GetWorldPosition(victim);
                var aim = victimPos - userPos;
                var forward = aim.LengthSquared() > 0.0001f ? Vector2.Normalize(aim) : finisher.Direction;
                var side = new Vector2(-forward.Y, forward.X);
                var from = userPos - forward * 0.7f + side * _instinctRandom.NextFloat(-0.6f, 0.6f);
                if (IsPathBlocked(user, finisher.Map, userPos, from, out var free))
                    from = free;

                LaunchFlyingKunai(user, finisher.Map, from, victim, FlyingKunaiKind.Pierce, KunaiVolleyDamage, FlyingKunaiFastSpeed);

                finisher.Count++;
                finisher.NextStep = now + KunaiVolleyInterval;
                if (finisher.Count >= KunaiVolleyCount)
                {
                    HealFinisherUser(user);
                    RemCompDeferred(user, finisher);
                }

                return;
            }
            default:
                RemCompDeferred(user, finisher);
                return;
        }
    }

    private void LaunchFlyingKunai(EntityUid user, MapId map, Vector2 from, EntityUid target, FlyingKunaiKind kind, float damage, float speed)
    {
        var kunai = Spawn(FlyingKunaiProto, new MapCoordinates(from, map));
        var comp = EnsureComp<ProperKungFuFlyingKunaiComponent>(kunai);
        comp.User = user;
        comp.Target = target;
        comp.Kind = kind;
        comp.Damage = damage;
        comp.Speed = speed;
        comp.ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(2);

        var delta = _transform.GetWorldPosition(target) - from;
        if (delta.LengthSquared() > 0.0001f)
            _transform.SetWorldRotation(kunai, delta.ToWorldAngle() + FlyingKunaiSpriteOffset);

        _audio.PlayPvs(KunaiThrowSound, kunai);
    }

    private void UpdateFlyingKunai(TimeSpan now)
    {
        var frame = (float) _timing.TickPeriod.TotalSeconds;
        var query = EntityQueryEnumerator<ProperKungFuFlyingKunaiComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var kunai, out var xform))
        {
            if (now >= kunai.ExpiresAt
                || TerminatingOrDeleted(kunai.Target)
                || Transform(kunai.Target).MapID != xform.MapID)
            {
                QueueDel(uid);
                continue;
            }

            var pos = _transform.GetWorldPosition(xform);
            var targetPos = _transform.GetWorldPosition(kunai.Target);
            var delta = targetPos - pos;
            var distance = delta.Length();
            var step = kunai.Speed * frame;
            if (distance > FlyingKunaiArrive + step)
            {
                var dir = delta / distance;
                _transform.SetWorldPosition(uid, pos + dir * step);
                _transform.SetWorldRotation(uid, dir.ToWorldAngle() + FlyingKunaiSpriteOffset);
                continue;
            }

            QueueDel(uid);
            OnFlyingKunaiArrived(kunai);
        }
    }

    private void OnFlyingKunaiArrived(ProperKungFuFlyingKunaiComponent kunai)
    {
        var user = kunai.User;
        var victim = kunai.Target;
        var damage = new DamageSpecifier { DamageDict = { ["Slash"] = kunai.Damage } };
        var userValid = !TerminatingOrDeleted(user);

        switch (kunai.Kind)
        {
            case FlyingKunaiKind.Neck:
            {
                SpawnAttachedTo(KunaiNeckSlash, new EntityCoordinates(victim, new Vector2(0f, 0.25f)));
                DealKungFuDamage(victim, damage, origin: userValid ? user : null, targetPart: TargetBodyPart.Head, canMiss: false);
                _audio.PlayPvs(KunaiPierceSound, victim);

                // All of the victim's blood comes out.
                // (splitting more than is there takes everything, and it spills as a puddle)
                if (TryComp<BloodstreamComponent>(victim, out var blood))
                    _bloodstream.TryModifyBloodLevel((victim, blood), -(blood.BloodMaxVolume * 4));

                _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-finisher-neck", ("target", victim)), victim, PopupType.MediumCaution);
                return;
            }
            case FlyingKunaiKind.Punch:
            {
                SpawnKunaiPierce(victim);
                DealKungFuDamage(victim, damage, origin: userValid ? user : null, targetPart: SkillTargetPart, canMiss: false);
                if (userValid && TryComp<ProperKungFuKunaiFinisherComponent>(user, out var finisher) && finisher.Victim == victim)
                {
                    DoDeathPunch(user, victim, finisher);
                    RemCompDeferred(user, finisher);
                }

                return;
            }
            default:
            {
                SpawnKunaiPierce(victim);
                if (kunai.Damage > 10f)
                    DealChunkedDamage(user, victim, damage, SkillTargetPart);
                else
                    DealKungFuDamage(victim, damage, origin: userValid ? user : null, targetPart: SkillTargetPart, canMiss: false);
                _audio.PlayPvs(KunaiPierceSound, victim);
                return;
            }
        }
    }

    private void SpawnKunaiPierce(EntityUid victim)
    {
        var offset = new Vector2(_instinctRandom.NextFloat(-0.2f, 0.2f), _instinctRandom.NextFloat(-0.15f, 0.25f));
        var fx = SpawnAttachedTo(KunaiPierce, new EntityCoordinates(victim, offset));
        _transform.SetWorldRotation(fx, _instinctRandom.NextAngle());
    }

    /// <summary>
    /// 一寸死拳: the user appears next to the victim and lands a one-inch punch. 90 Blunt to the chest, heart, lungs,
    /// stomach, kidneys and liver lose 60% of their integrity, 20 tiles of knockback and no breathing for 2 minutes.
    /// </summary>
    private void DoDeathPunch(EntityUid user, EntityUid victim, ProperKungFuKunaiFinisherComponent finisher)
    {
        var map = finisher.Map;
        var userPos = _transform.GetWorldPosition(user);
        var victimPos = _transform.GetWorldPosition(victim);
        var delta = victimPos - userPos;
        var dir = delta.LengthSquared() > 0.0001f ? Vector2.Normalize(delta) : finisher.Direction;

        // Teleport right in front of the victim, a red silhouette left behind.
        var spot = victimPos - dir * 0.7f;
        if (IsPathBlocked(user, map, victimPos, spot, out var free))
            spot = free;

        SpawnAfterimage(user, map, userPos, DashAfterimageEffect);
        RemComp<ProperKungFuSlidingComponent>(user);
        _physics.SetLinearVelocity(user, Vector2.Zero);
        _transform.SetWorldPosition(user, spot);
        _transform.SetWorldRotation(user, dir.ToWorldAngle());

        // The kanji glow behind the victim.
        var kanjiPos = victimPos + dir * DeathPunchKanjiDistance;
        if (IsPathBlocked(victim, map, victimPos, kanjiPos, out var kanjiFree))
            kanjiPos = kanjiFree;
        Spawn(DeathPunchKanji, new MapCoordinates(kanjiPos, map));
        Spawn(DeathPunchImpact, new MapCoordinates(victimPos, map));

        DealChunkedDamage(user, victim, DeathPunchDamage, TargetBodyPart.Chest);
        foreach (var part in new[] { TargetBodyPart.Chest, TargetBodyPart.Groin })
        {
            InflictPartTrauma(victim, part, "Blunt",
                (organ, organComp) => IsDeathPunchOrgan(organ, organComp) ? (FixedPoint2?) (organComp.IntegrityCap * DeathPunchOrganFraction) : null,
                breakBones: false);
        }

        if (!TerminatingOrDeleted(victim) && !_mobState.IsDead(victim))
        {
            BlockBreathing(victim, DeathPunchBreathBlock);
            if (HasComp<KravMagaBlockedBreathingComponent>(victim))
                EnsureComp<ProperKungFuDeathPunchBreathComponent>(victim).ExpiresAt = _timing.CurTime + DeathPunchBreathBlock;
        }

        RemComp<ProperKungFuSlidingComponent>(victim);
        StartKnockback(victim, dir, DeathPunchKnockback, DeathPunchKnockbackDuration, dust: true);

        HealFinisherUser(user);
        _audio.PlayPvs(DeathPunchSound, victim);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-death-punch-user", ("target", victim)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-death-punch-target", ("user", user)), victim, victim, PopupType.LargeCaution);
    }

    private bool IsDeathPunchOrgan(EntityUid organ, Content.Shared.Body.Organ.OrganComponent organComp)
    {
        return HasComp<HeartComponent>(organ)
               || HasComp<LungComponent>(organ)
               || HasComp<StomachComponent>(organ)
               || HasComp<LiverComponent>(organ)
               || DeathPunchOrganSlots.Contains(organComp.SlotId);
    }
}
