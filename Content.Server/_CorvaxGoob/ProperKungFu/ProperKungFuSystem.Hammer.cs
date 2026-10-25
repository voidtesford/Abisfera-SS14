using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Common.Weapons;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared.Body.Components;
using Content.Shared.Damage;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Part;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee;
using Content.Shared.Damage.Components;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Hammer" (Молот): a sweeping downward claw strike into the head.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly Content.Shared.StatusEffectNew.StatusEffectsSystem _statusNew = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;

    private const string HammerMove = "Hammer";

    private void OnHammer(Entity<ProperKungFuComponent> ent, ref HammerActionEvent args)
    {
        if (args.Handled)
            return;

        var user = args.Performer;
        var target = args.Target;

        if (user == target
            || !HasComp<MobStateComponent>(target)
            || HasComp<WolfDancingComponent>(user)
            || HasComp<ClawWhirlwindedComponent>(target)
            || HasComp<ClawWhirlwindedComponent>(user))
            return;

        if (!TryComp<MartialArtsKnowledgeComponent>(user, out var knowledge)
            || knowledge.MartialArtsForm != MartialArtsForms.ProperKungFu
            || knowledge.Blocked)
            return;

        var userCoords = _transform.GetMapCoordinates(user);
        var targetCoords = _transform.GetMapCoordinates(target);
        if (userCoords.MapId != targetCoords.MapId)
            return;

        // Same reach as the user's fists; out of reach nothing happens and the cooldown isn't spent.
        if (!TryComp<MeleeWeaponComponent>(user, out var melee))
            return;

        var rangeEv = new GetLightAttackRangeEvent(target, user, melee.Range);
        RaiseLocalEvent(user, ref rangeEv);
        if (!_melee.InRange(user, target, rangeEv.Range, null))
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-hammer-too-far"), user, user, PopupType.SmallCaution);
            return;
        }

        args.Handled = true;

        var delta = targetCoords.Position - userCoords.Position;
        var direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(user).ToWorldVec();
        _transform.SetWorldRotation(user, direction.ToWorldAngle());

        // Priority: Wolf Grip synergy > Wolf Dance mark > finisher > lying > base hit. The skull is never broken.
        // The finisher looks at the damage from before the hit.
        var preDamage = TryComp<DamageableComponent>(target, out var damageable) ? damageable.TotalDamage.Float() : 0f;

        if (TryConsumeWolfGripMark(user, target))
        {
            // From the grip the claws sweep up instead of down.
            SpawnHammerEffect(args.GripImpactEffect, targetCoords);
            DoHammerGripSynergy(user, target, direction, args);
            FinishHammer(user, target, "proper-kung-fu-hammer-grip-user", "proper-kung-fu-hammer-grip-target", args.HitSound);
            return;
        }

        // Wolf Dance variant: wins over the lying branch, since the dance itself knocks the target down.
        if (TryConsumeWolfDanceMark(user, target))
        {
            SpawnHammerEffect(args.ImpactEffect, targetCoords);
            DoHammerDanceVariant(user, target, direction, args);
            FinishHammer(user, target, "proper-kung-fu-hammer-dance-user", "proper-kung-fu-hammer-dance-target", args.HitSound);
            return;
        }

        if (args.FinisherThreshold > 0f
            && !IsWolfPackClone(user)
            && preDamage >= args.FinisherThreshold
            && !_mobState.IsDead(target)
            && !HasComp<ProperKungFuHammerFinisherComponent>(user))
        {
            StartHammerFinisher(user, target, direction, args);
            return;
        }

        if (_standing.IsDown(target) || HasComp<KnockedDownComponent>(target))
        {
            SpawnHammerEffect(args.ImpactEffect, targetCoords);
            RemComp<ProperKungFuWolfGripMarkComponent>(target);
            DealHammerHeadDamage(user, target, args.LyingDamage);
            InflictHammerTrauma(target, args);
            // Pinned and dazed: knocked down, stunned and disoriented for 10 s.
            _stun.TryParalyze(target, args.LyingStunTime, true);
            ApplyDisorientation(target, args.LyingDisorientEffect, args.LyingStunTime);
            FinishHammer(user, target, "proper-kung-fu-hammer-lying-user", "proper-kung-fu-hammer-lying-target", args.HitSound);
            return;
        }

        SpawnHammerEffect(args.ImpactEffect, targetCoords);
        DealHammerHeadDamage(user, target, args.Damage);
        InflictHammerTrauma(target, args);
        _stun.TryKnockdown(target, args.KnockdownTime, true);
        FinishHammer(user, target, "proper-kung-fu-hammer-user", "proper-kung-fu-hammer-target", args.HitSound);
    }

    private void SpawnHammerEffect(Robust.Shared.Prototypes.EntProtoId? effect, Robust.Shared.Map.MapCoordinates coords)
    {
        if (effect is { } proto)
            Spawn(proto, coords);
    }

    private void FinishHammer(EntityUid user, EntityUid target, string userLoc, string targetLoc, Robust.Shared.Audio.SoundSpecifier? sound)
    {
        _audio.PlayPvs(sound, target);
        SpawnSkillFlashes(target);
        _popup.PopupEntity(Loc.GetString(userLoc, ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString(targetLoc, ("user", user)), target, target, PopupType.LargeCaution);

        // +5 ult charge.
        var hitEv = new ProperKungFuSpecialHitEvent(target, HammerMove, false);
        RaiseLocalEvent(user, ref hitEv);
    }

    /// <summary>
    /// Hammer head damage, dealt in small hits: Shitmed only rolls a random dismemberment (and bone/organ traumas)
    /// when a wound grows by more than 10 at once, so the head can't randomly come off from the Hammer.
    /// </summary>
    private void DealHammerHeadDamage(EntityUid user, EntityUid target, DamageSpecifier damage)
    {
        DealChunkedDamage(user, target, damage, TargetBodyPart.Head);
    }

    /// <summary>
    /// Eyes and brain lose part of their integrity; the skull is never broken.
    /// </summary>
    private void InflictHammerTrauma(EntityUid target, HammerActionEvent args)
    {
        var eyes = args.EyeDamageFraction;
        var brain = args.BrainDamageFraction;
        InflictPartTrauma(target, TargetBodyPart.Head, "Blunt",
            (organ, organComp) =>
            {
                if (HasComp<EyesComponent>(organ))
                    return organComp.IntegrityCap * eyes;
                if (HasComp<BrainComponent>(organ))
                    return organComp.IntegrityCap * brain;
                return null;
            },
            breakBones: false);
    }

    /// <summary>
    /// Hammer on a Wolf-Grip-marked target: let go, 20 Blunt to the head (no knockdown), the user backflips away
    /// and for a moment a Wolf Dance on this target is an instant straight dash.
    /// </summary>
    private void DoHammerGripSynergy(EntityUid user, EntityUid target, Vector2 direction, HammerActionEvent args)
    {
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);

        DealHammerHeadDamage(user, target, args.GripDamage);
        InflictHammerTrauma(target, args);

        // Backflip: the user flies back away from the target (walls stop it).
        StartKnockback(user, -direction, args.BackflipDistance, args.BackflipDuration, dust: false);
        RaiseNetworkEvent(new ProperKungFuBackflipEvent(GetNetEntity(user), (float) args.BackflipDuration.TotalSeconds, -direction),
            Filter.Pvs(user, entityManager: EntityManager));

        var mark = EnsureComp<ProperKungFuHammerMarkComponent>(target);
        mark.Marker = user;
        mark.ExpiresAt = _timing.CurTime + args.DanceWindow;
    }

    private bool TryConsumeHammerMark(EntityUid user, EntityUid target)
    {
        if (!TryComp<ProperKungFuHammerMarkComponent>(target, out var mark)
            || mark.Marker != user
            || _timing.CurTime >= mark.ExpiresAt)
            return false;

        RemComp(target, mark);
        return true;
    }

    private bool TryConsumeWolfDanceMark(EntityUid user, EntityUid target)
    {
        if (!TryComp<ProperKungFuWolfDanceMarkComponent>(target, out var mark))
            return false;

        var valid = mark.Marker == user && _timing.CurTime < mark.ExpiresAt;
        if (valid)
            RemComp(target, mark);
        return valid;
    }

    /// <summary>
    /// Hammer on a Wolf-Dance-marked target: 20 Blunt to the body, an exact 5 tile knockback, landing knocked down.
    /// No eye/brain trauma and no sleep.
    /// </summary>
    private void DoHammerDanceVariant(EntityUid user, EntityUid target, Vector2 direction, HammerActionEvent args)
    {
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);

        DealKungFuDamage(target, args.DanceDamage, origin: user, targetPart: SkillTargetPart, canMiss: false);

        var slide = TimeSpan.FromSeconds(args.DanceKnockbackDistance / MathF.Max(0.1f, args.DanceKnockbackSpeed));
        StartKnockback(target, direction, args.DanceKnockbackDistance, slide, dust: true);

        // Stays down as from the dance (a dance knockdown never has more left than this), lands on the floor.
        _stun.TryKnockdown(target, slide + args.DanceKnockdownTime, true);
    }

    private void UpdateHammerMarks(TimeSpan now)
    {
        var danceMarks = EntityQueryEnumerator<ProperKungFuWolfDanceMarkComponent>();
        while (danceMarks.MoveNext(out var uid, out var mark))
        {
            if (now >= mark.ExpiresAt)
                RemCompDeferred(uid, mark);
        }

        var marks = EntityQueryEnumerator<ProperKungFuHammerMarkComponent>();
        while (marks.MoveNext(out var uid, out var mark))
        {
            if (now >= mark.ExpiresAt)
                RemCompDeferred(uid, mark);
        }
    }

    /// <summary>
    /// Hammer finisher, step 1: the normal downward strike for 1 damage, then the victim is locked for the rest.
    /// </summary>
    private void StartHammerFinisher(EntityUid user, EntityUid target, Vector2 direction, HammerActionEvent args)
    {
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);
        RemComp<ProperKungFuSlidingComponent>(target);
        RemComp<ProperKungFuWolfGripMarkComponent>(target);

        var finisher = EnsureComp<ProperKungFuHammerFinisherComponent>(user);
        finisher.Victim = target;
        finisher.Map = _transform.GetMapId(user);
        finisher.Direction = direction;
        finisher.StartTime = _timing.CurTime;
        finisher.Step = 1;
        finisher.Event = args;

        // Can't move or act until the spinning strike sends it flying (then it's knocked down).
        _stun.TryStun(target, args.FinisherSpinDelay + TimeSpan.FromSeconds(0.2), true);

        SpawnHammerEffect(args.ImpactEffect, _transform.GetMapCoordinates(target));
        DealHammerHeadDamage(user, target, args.FinisherFirstDamage);
        _audio.PlayPvs(args.HitSound, target);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-hammer-finisher-user", ("target", target)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-hammer-finisher-target", ("user", user)), target, target, PopupType.LargeCaution);
    }

    private void UpdateHammerFinishers(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuHammerFinisherComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var finisher, out var xform))
        {
            var victim = finisher.Victim;
            var args = finisher.Event;
            if (args == null
                || TerminatingOrDeleted(victim)
                || xform.MapID != finisher.Map
                || _transform.GetMapId(victim) != finisher.Map
                || _container.IsEntityInContainer(uid)
                || _container.IsEntityInContainer(victim)
                || _mobState.IsIncapacitated(uid))
            {
                RemCompDeferred(uid, finisher);
                if (!TerminatingOrDeleted(victim))
                    _stun.TryKnockdown(victim, TimeSpan.FromSeconds(1), true);
                continue;
            }

            // Nobody drags the victim away mid-sequence.
            if (TryComp<PullableComponent>(victim, out var pullable) && pullable.BeingPulled)
                _pulling.TryStopPull(victim, pullable, uid, true);

            var elapsed = now - finisher.StartTime;
            var dir = finisher.Direction;

            if (finisher.Step == 1 && elapsed >= args.FinisherStraightDelay)
            {
                // Step 2: straight strike, horizontal claws, the victim slides back 2 tiles.
                finisher.Step = 2;
                SpawnRotatedEffect(args.FinisherHorizontalEffect, _transform.GetMapCoordinates(victim), dir.ToWorldAngle());
                DealChunkedDamage(uid, victim, args.FinisherStraightDamage, SkillTargetPart);
                _audio.PlayPvs(args.HitSound, victim);
                var slide = TimeSpan.FromSeconds(args.FinisherStraightKnockback / MathF.Max(0.1f, args.FinisherKnockbackSpeed));
                StartKnockback(victim, dir, args.FinisherStraightKnockback, slide, dust: true);
                continue;
            }

            if (finisher.Step == 2 && elapsed >= args.FinisherFlipDelay)
            {
                // Step 3: a front flip up to the victim.
                finisher.Step = 3;
                var toVictim = _transform.GetWorldPosition(victim) - _transform.GetWorldPosition(uid);
                var gap = MathF.Max(0f, toVictim.Length() - 1f);
                var flipDir = toVictim.LengthSquared() > 0.0001f ? Vector2.Normalize(toVictim) : dir;
                finisher.Direction = flipDir;
                _transform.SetWorldRotation(uid, flipDir.ToWorldAngle());
                RemComp<ProperKungFuSlidingComponent>(uid);
                if (gap > 0.01f)
                    StartKnockback(uid, flipDir, gap, args.FinisherFlipDuration, dust: false);
                RaiseNetworkEvent(new ProperKungFuBackflipEvent(GetNetEntity(uid), (float) args.FinisherFlipDuration.TotalSeconds, flipDir, front: true),
                    Filter.Pvs(uid, entityManager: EntityManager));
                continue;
            }

            if (finisher.Step == 3 && elapsed >= args.FinisherFlipDelay + args.FinisherFlipDuration)
            {
                // Landing strike: the normal downward claws into the torso.
                finisher.Step = 4;
                SpawnHammerEffect(args.ImpactEffect, _transform.GetMapCoordinates(victim));
                DealChunkedDamage(uid, victim, args.FinisherFlipDamage, SkillTargetPart);
                _audio.PlayPvs(args.HitSound, victim);
                continue;
            }

            if (finisher.Step == 4 && elapsed >= args.FinisherSpinDelay)
            {
                finisher.Step = 5;
                var landing = DoHammerFinisherSpin(uid, victim, finisher, args);
                finisher.NextAt = now + landing + args.FinisherDashPause;
                continue;
            }

            if (finisher.Step == 5 && now >= finisher.NextAt)
            {
                // Step 6: dash at the victim wherever it landed after the fling.
                finisher.Step = 6;
                var toVictim = _transform.GetWorldPosition(victim) - _transform.GetWorldPosition(uid);
                var gap = MathF.Max(0f, toVictim.Length() - 1f);
                var dashDir = toVictim.LengthSquared() > 0.0001f ? Vector2.Normalize(toVictim) : finisher.Direction;
                finisher.Direction = dashDir;
                var dash = TimeSpan.FromSeconds(MathF.Max(0.05f, gap / MathF.Max(0.1f, args.FinisherDashSpeed)));
                finisher.NextAt = now + dash;
                _transform.SetWorldRotation(uid, dashDir.ToWorldAngle());
                RemComp<ProperKungFuSlidingComponent>(uid);
                if (gap > 0.01f)
                    StartKnockback(uid, dashDir, gap, dash, dust: true);
                // The victim stays down until the cross lands.
                _stun.TryStun(victim, dash + TimeSpan.FromSeconds(0.3), true);
                continue;
            }

            if (finisher.Step == 6 && now >= finisher.NextAt)
            {
                RemCompDeferred(uid, finisher);
                DoHammerFinisherCross(uid, victim, args);
            }
        }
    }

    /// <summary>
    /// Last hit of the Hammer finisher: a cross of claws into the torso of the landed victim, ignoring armor.
    /// </summary>
    private void DoHammerFinisherCross(EntityUid user, EntityUid victim, HammerActionEvent args)
    {
        RemComp<ProperKungFuSlidingComponent>(user);
        DealKungFuDamage(victim, args.FinisherCrossDamage, origin: user, targetPart: SkillTargetPart, canMiss: false,
            ignoreResistances: true);
        if (args.FinisherCrossEffect is { } cross)
            Spawn(cross, _transform.GetMapCoordinates(victim));
        _audio.PlayPvs(args.FinisherCrossSound, victim);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-hammer-finisher-cross-user", ("target", victim)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-hammer-finisher-cross-target", ("user", user)), victim, victim, PopupType.LargeCaution);
    }

    /// <summary>
    /// Step 4: the user spins, diagonal claws across the head (50 Blunt), the victim spins sideways through the air
    /// and lands knocked down 5 tiles from the user.
    /// </summary>
    /// <returns>How long the victim flies before landing.</returns>
    private TimeSpan DoHammerFinisherSpin(EntityUid user, EntityUid victim, ProperKungFuHammerFinisherComponent finisher, HammerActionEvent args)
    {
        RaiseNetworkEvent(new ProperKungFuFlowSpinEvent(GetNetEntity(user), (float) args.FinisherSpinDuration.TotalSeconds),
            Filter.Pvs(user, entityManager: EntityManager));

        var victimCoords = _transform.GetMapCoordinates(victim);
        SpawnRotatedEffect(args.FinisherDiagonalEffect,
            new Robust.Shared.Map.MapCoordinates(victimCoords.Position + new Vector2(0f, 0.35f), victimCoords.MapId),
            Angle.FromDegrees(45));
        DealHammerHeadDamage(user, victim, args.FinisherSpinDamage);

        var away = _transform.GetWorldPosition(victim) - _transform.GetWorldPosition(user);
        var current = away.Length();
        away = current > 0.01f ? away / current : finisher.Direction;
        var distance = MathF.Max(0f, args.FinisherFinalDistance - current);
        var slide = TimeSpan.FromSeconds(MathF.Max(0.2f, distance / MathF.Max(0.1f, args.FinisherKnockbackSpeed)));
        RemComp<ProperKungFuSlidingComponent>(victim);
        if (distance > 0.01f)
            StartKnockback(victim, away, distance, slide, dust: true);
        RaiseNetworkEvent(new WolfDanceTossAnimationEvent(GetNetEntity(victim), (float) slide.TotalSeconds, args.FinisherTossHeight, args.FinisherTossSpins, away),
            Filter.Pvs(victim, entityManager: EntityManager));
        _stun.TryKnockdown(victim, slide + args.FinisherKnockdownTime, true);

        _audio.PlayPvs(args.FinisherFinalSound, victim);
        HealFinisherUser(user);
        FinishHammer(user, victim, "proper-kung-fu-hammer-finisher-final-user", "proper-kung-fu-hammer-finisher-final-target", args.HitSound);
        return slide;
    }

    private void SpawnRotatedEffect(Robust.Shared.Prototypes.EntProtoId? effect, Robust.Shared.Map.MapCoordinates coords, Angle rotation)
    {
        if (effect is not { } proto)
            return;

        var uid = Spawn(proto, coords);
        _transform.SetWorldRotation(uid, rotation);
    }

    /// <summary>
    /// No collision with the victim during the front flip.
    /// </summary>
    private void OnHammerFinisherPreventCollide(Entity<ProperKungFuHammerFinisherComponent> ent, ref Robust.Shared.Physics.Events.PreventCollideEvent args)
    {
        if (args.OtherEntity == ent.Comp.Victim)
            args.Cancelled = true;
    }
}
