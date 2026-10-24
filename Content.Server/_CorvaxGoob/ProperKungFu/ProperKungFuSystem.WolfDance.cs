using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Physics.Events;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Plain Wolf Dance (no Claw Series / Whirlwind / Hammer synergy): a fast dash straight through the target,
/// which is tossed up into the air in place and lands knocked down. The user stays on their feet.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    private readonly List<(EntityUid Target, TimeSpan LandTime, TimeSpan Knockdown)> _pendingTosses = new();

    /// <summary>
    /// Any Wolf Dance hit (every variant) marks the target for the Hammer's Wolf Dance variant.
    /// </summary>
    private void MarkWolfDance(EntityUid user, EntityUid target)
    {
        var mark = EnsureComp<ProperKungFuWolfDanceMarkComponent>(target);
        mark.Marker = user;
        mark.ExpiresAt = _timing.CurTime + WolfDanceMarkTime;
    }

    private static readonly TimeSpan WolfDanceMarkTime = TimeSpan.FromSeconds(2);

    private bool HasFreshClawMark(EntityUid user, EntityUid target)
    {
        return TryComp<ProperKungFuClawMarkComponent>(target, out var mark)
               && mark.Marker == user
               && _timing.CurTime < mark.ExpiresAt;
    }

    private void StartPassThrough(EntityUid user, EntityUid target, MapCoordinates userCoords, Vector2 direction, float distance, WolfDanceActionEvent args)
    {
        var dancing = EnsureComp<WolfDancingComponent>(user);
        dancing.Target = target;
        dancing.Event = args;
        dancing.Map = userCoords.MapId;
        dancing.WhirlwindVariant = false;
        dancing.PassThrough = true;
        dancing.PassHit = false;
        // The finisher looks at the damage from before the dance.
        dancing.FinisherPending = args.FinisherPasses > 0
                                  && TryComp<DamageableComponent>(target, out var damageable)
                                  && damageable.TotalDamage >= args.FinisherThreshold;
        dancing.MaxDashLength = distance + args.PassThroughDistance + args.ChaseDistance;
        dancing.AfterimagesSpawned = 0;
        StartPhase(user, dancing, WolfDancePhase.PassThrough, userCoords.Position,
            userCoords.Position + direction * (distance + args.PassThroughDistance), args.PassThroughDashDuration);

        // Drop an already existing contact with the target, so the dash goes through it.
        _physics.RegenerateContacts(user);
    }

    /// <summary>
    /// No collision with the target during the plain dash through it.
    /// </summary>
    private void OnDancingPreventCollide(Entity<WolfDancingComponent> ent, ref PreventCollideEvent args)
    {
        if (ent.Comp.PassThrough && args.OtherEntity == ent.Comp.Target)
            args.Cancelled = true;
    }

    private void UpdatePassThrough(EntityUid user, WolfDancingComponent dancing, WolfDanceActionEvent args, TimeSpan now, bool targetValid)
    {
        var target = dancing.Target;
        var current = _transform.GetWorldPosition(user);

        // Re-aims at a moving target until the hit, then goes on straight.
        if (!dancing.PassHit && targetValid)
        {
            var toTarget = _transform.GetWorldPosition(target) - dancing.PhaseFrom;
            var len = toTarget.Length();
            if (len > 0.01f)
            {
                var length = Math.Clamp(len + args.PassThroughDistance, 0f, MathF.Max(0f, dancing.MaxDashLength));
                dancing.PhaseTo = dancing.PhaseFrom + toTarget / len * length;
            }
        }

        var duration = dancing.PhaseDuration.TotalSeconds;
        var t = duration <= 0 ? 1f : (float) Math.Clamp((now - dancing.PhaseStart).TotalSeconds / duration, 0, 1);
        // A fast, even dash that only eases off at the very end.
        var eased = t < 0.85f ? t / 0.85f * 0.95f : 0.95f + (t - 0.85f) / 0.15f * 0.05f;

        var desired = Vector2.Lerp(dancing.PhaseFrom, dancing.PhaseTo, eased);
        var hitWall = IsPathBlocked(user, dancing.Map, current, desired, out var free);
        var position = hitWall ? free : desired;
        _transform.SetWorldPosition(user, position);
        _physics.SetLinearVelocity(user, Vector2.Zero);

        if (args.AfterimageEffect != null
            && dancing.AfterimagesSpawned < args.Afterimages
            && now >= dancing.NextAfterimage)
        {
            SpawnAfterimage(user, dancing.Map, position, args.AfterimageEffect.Value);
            dancing.AfterimagesSpawned++;
            dancing.NextAfterimage = now + args.AfterimageInterval;
        }

        var dashDir = dancing.PhaseTo - dancing.PhaseFrom;
        dashDir = dashDir.LengthSquared() > 0.0001f ? Vector2.Normalize(dashDir) : _transform.GetWorldRotation(user).ToWorldVec();

        // The hit lands as the user passes through the target.
        if (!dancing.PassHit && targetValid)
        {
            var rel = _transform.GetWorldPosition(target) - position;
            if (Vector2.Dot(rel, dashDir) <= 0.25f && rel.Length() <= args.KickRange)
            {
                dancing.PassHit = true;
                dancing.PassLanded = DoPassThroughHit(user, target, dashDir, args);
            }
        }

        if (t < 1f && !hitWall)
            return;

        var hit = dancing.PassHit;
        if (!hit && targetValid
            && Vector2.Distance(position, _transform.GetWorldPosition(target)) <= args.KickRange)
        {
            hit = true;
            dancing.PassLanded = DoPassThroughHit(user, target, dashDir, args);
        }

        var finisher = hit && dancing.PassLanded && dancing.FinisherPending && targetValid;
        StopDance(user, false);

        if (finisher)
        {
            StartDanceFinisher(user, target, dancing.Map, dashDir, args);
            return;
        }

        if (!hit)
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-miss"), user, user, PopupType.SmallCaution);

        if (args.PlainUserFalls)
            _stun.TryKnockdown(user, args.UserKnockdownTime, true);
    }

    /// <summary>
    /// 20 Blunt into the torso, the target is stunned and tossed up in place, landing knocked down.
    /// Same shockwave, kick sound and shield rule as the flying kick.
    /// </summary>
    private bool DoPassThroughHit(EntityUid user, EntityUid target, Vector2 direction, WolfDanceActionEvent args)
    {
        if (args.ShockwaveEffect is { } shockwave)
            Spawn(shockwave, _transform.GetMapCoordinates(target));

        if (TryKnockShieldAway(user, target, direction, args))
        {
            var blockedEv = new ProperKungFuSpecialHitEvent(target, WolfDanceMove, true);
            RaiseLocalEvent(user, ref blockedEv);
            return false;
        }

        _audio.PlayPvs(args.HitSound, target);
        _damageable.TryChangeDamage(target, args.Damage, origin: user, targetPart: SkillTargetPart, canMiss: false);

        // Tossed straight up on the spot: no knockback. Stunned now, knocked down on landing (2 s in total).
        RemComp<ProperKungFuSlidingComponent>(target);
        _physics.SetLinearVelocity(target, Vector2.Zero);
        _stun.TryStun(target, args.StunTime, true);
        var toss = args.TossDuration < args.StunTime ? args.TossDuration : args.StunTime;
        _pendingTosses.Add((target, _timing.CurTime + toss, args.StunTime - toss));
        RaiseNetworkEvent(new WolfDanceTossAnimationEvent(GetNetEntity(target), (float) toss.TotalSeconds, args.TossHeight, args.TossSpins, direction),
            Filter.Pvs(target, entityManager: EntityManager));

        MarkWolfDance(user, target);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-toss-user", ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-toss-target", ("user", user)), target, target, PopupType.MediumCaution);

        var hitEv = new ProperKungFuSpecialHitEvent(target, WolfDanceMove, false);
        RaiseLocalEvent(user, ref hitEv);
        return true;
    }

    private void UpdateWolfDanceTosses(TimeSpan now)
    {
        for (var i = _pendingTosses.Count - 1; i >= 0; i--)
        {
            var (target, land, knockdown) = _pendingTosses[i];
            if (now < land)
                continue;

            _pendingTosses.RemoveAt(i);
            if (TerminatingOrDeleted(target))
                continue;

            _stun.TryKnockdown(target, knockdown > TimeSpan.Zero ? knockdown : TimeSpan.FromSeconds(0.1), true);
        }
    }

    /// <summary>
    /// Plain Wolf Dance finisher: the target is locked in place, the user dashes through it again and again.
    /// </summary>
    private void StartDanceFinisher(EntityUid user, EntityUid target, MapId map, Vector2 direction, WolfDanceActionEvent args)
    {
        var finisher = EnsureComp<ProperKungFuDanceFinisherComponent>(user);
        finisher.Victim = target;
        finisher.Map = map;
        finisher.Center = _transform.GetWorldPosition(target);
        finisher.LastDirection = direction;
        finisher.PassesDone = 0;
        finisher.Event = args;
        finisher.Turn = 1f;

        // Locked for the whole sequence; knocked down only by the final strike.
        _pendingTosses.RemoveAll(t => t.Target == target);
        RemComp<ProperKungFuSlidingComponent>(target);
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);
        var total = args.FinisherPassDuration * args.FinisherPasses + TimeSpan.FromSeconds(0.2);
        _stun.TryStun(target, total, true);

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-finisher-user", ("target", target)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-finisher-target", ("user", user)), target, target, PopupType.LargeCaution);

        StartFinisherPass(user, finisher, args);
        _physics.RegenerateContacts(user);
    }

    /// <summary>
    /// Next dash: from where the user is, through the target, out 90 degrees from the last exit direction.
    /// </summary>
    private void StartFinisherPass(EntityUid user, ProperKungFuDanceFinisherComponent finisher, WolfDanceActionEvent args)
    {
        var last = finisher.LastDirection;
        var exit = new Vector2(-last.Y, last.X) * finisher.Turn;
        finisher.PassFrom = _transform.GetWorldPosition(user);
        finisher.PassTo = finisher.Center + exit * args.FinisherPassDistance;
        finisher.LastDirection = exit;
        finisher.PassStart = _timing.CurTime;
        finisher.PassHit = false;

        var dash = finisher.PassTo - finisher.PassFrom;
        SendAnimation(user, WolfDancePhase.PassThrough, (float) args.FinisherPassDuration.TotalSeconds,
            dash.LengthSquared() > 0.0001f ? Vector2.Normalize(dash) : exit);
    }

    private void UpdateDanceFinishers(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuDanceFinisherComponent, TransformComponent>();
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
                    _stun.TryKnockdown(victim, args?.FinisherKnockdownTime ?? TimeSpan.FromSeconds(1), true);
                continue;
            }

            // The victim can't move or escape.
            _transform.SetWorldPosition(victim, finisher.Center);
            _physics.SetLinearVelocity(victim, Vector2.Zero);

            // Through the victim: from the start to the victim, then out to the end, at an even speed.
            var first = Vector2.Distance(finisher.PassFrom, finisher.Center);
            var second = Vector2.Distance(finisher.Center, finisher.PassTo);
            var length = MathF.Max(0.01f, first + second);
            var duration = args.FinisherPassDuration.TotalSeconds;
            var t = duration <= 0 ? 1f : (float) Math.Clamp((now - finisher.PassStart).TotalSeconds / duration, 0, 1);
            var along = t * length;
            var desired = along <= first
                ? Vector2.Lerp(finisher.PassFrom, finisher.Center, first <= 0.01f ? 1f : along / first)
                : Vector2.Lerp(finisher.Center, finisher.PassTo, second <= 0.01f ? 1f : (along - first) / second);

            var current = _transform.GetWorldPosition(uid);
            var blocked = false;
            if (along > first && IsPathBlocked(uid, finisher.Map, current, desired, out var free))
            {
                blocked = true;
                desired = free;
            }
            _transform.SetWorldPosition(uid, desired);
            _physics.SetLinearVelocity(uid, Vector2.Zero);

            if (args.AfterimageEffect is { } afterimage)
                SpawnAfterimage(uid, finisher.Map, desired, afterimage);

            if (!finisher.PassHit && along >= first)
            {
                finisher.PassHit = true;
                DoFinisherPassHit(uid, victim, finisher.LastDirection, args);
            }

            if (t < 1f && !blocked)
                continue;

            finisher.PassesDone++;
            if (finisher.PassesDone < args.FinisherPasses)
            {
                StartFinisherPass(uid, finisher, args);
                continue;
            }

            RemCompDeferred(uid, finisher);
            SendAnimation(uid, WolfDancePhase.Cancel, 0f, finisher.LastDirection);
            DoDanceFinisherStrike(uid, victim, finisher, args);
        }
    }

    /// <summary>
    /// Every pass: 5 Blunt to the torso, a flash and shockwave, the target tossed up again.
    /// </summary>
    private void DoFinisherPassHit(EntityUid user, EntityUid victim, Vector2 direction, WolfDanceActionEvent args)
    {
        _damageable.TryChangeDamage(victim, args.FinisherPassDamage, origin: user, targetPart: SkillTargetPart, canMiss: false);
        var coords = _transform.GetMapCoordinates(victim);
        if (args.ShockwaveEffect is { } shockwave)
            Spawn(shockwave, coords);
        if (args.FinisherPassEffect is { } flash)
            Spawn(flash, coords);
        _audio.PlayPvs(args.FinisherPassSound, victim);

        RaiseNetworkEvent(new WolfDanceTossAnimationEvent(GetNetEntity(victim), (float) args.FinisherTossDuration.TotalSeconds,
                args.FinisherTossHeight, 0.5f, direction),
            Filter.Pvs(victim, entityManager: EntityManager));
    }

    /// <summary>
    /// Final strike: claws across every body part, the target thrown back and knocked down.
    /// </summary>
    private void DoDanceFinisherStrike(EntityUid user, EntityUid victim, ProperKungFuDanceFinisherComponent finisher, WolfDanceActionEvent args)
    {
        foreach (var part in args.FinisherFinalParts)
        {
            DealChunkedDamage(user, victim, args.FinisherFinalDamage, part);
        }

        if (args.FinisherFinalEffect is { } effect)
            Spawn(effect, _transform.GetMapCoordinates(victim));
        if (args.ShockwaveEffect is { } shockwave)
            Spawn(shockwave, _transform.GetMapCoordinates(victim));

        // Away from the user.
        var away = finisher.Center - _transform.GetWorldPosition(user);
        away = away.LengthSquared() > 0.0001f ? Vector2.Normalize(away) : -finisher.LastDirection;
        var slide = TimeSpan.FromSeconds(args.FinisherKnockbackDistance / MathF.Max(0.1f, args.FinisherKnockbackSpeed));
        StartKnockback(victim, away, args.FinisherKnockbackDistance, slide, dust: true);
        _stun.TryKnockdown(victim, slide + args.FinisherKnockdownTime, true);

        _audio.PlayPvs(args.FinisherFinalSound, victim);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-finisher-final-user", ("target", victim)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-finisher-final-target", ("user", user)), victim, victim, PopupType.LargeCaution);
    }

    /// <summary>
    /// No collision with the victim during the finisher dashes.
    /// </summary>
    private void OnDanceFinisherPreventCollide(Entity<ProperKungFuDanceFinisherComponent> ent, ref PreventCollideEvent args)
    {
        if (args.OtherEntity == ent.Comp.Victim)
            args.Cancelled = true;
    }

    /// <summary>
    /// Damage to one part in small hits: Shitmed only rolls random dismemberment (and bone/organ traumas)
    /// when a wound grows by more than 10 at once.
    /// </summary>
    private void DealChunkedDamage(EntityUid user, EntityUid target, DamageSpecifier damage, TargetBodyPart part)
    {
        var total = damage.GetTotal().Float();
        var hits = Math.Max(1, (int) MathF.Ceiling(total / SafeDamageChunk));
        var chunk = damage / hits;
        for (var i = 0; i < hits; i++)
        {
            _damageable.TryChangeDamage(target, chunk, origin: user, targetPart: part, canMiss: false);
        }
    }

    /// <summary>
    /// Max damage per hit of <see cref="DealChunkedDamage"/> (Shitmed's random trauma rolls need more than 10).
    /// </summary>
    private const float SafeDamageChunk = 5f;
}
