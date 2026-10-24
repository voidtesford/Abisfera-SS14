using System.Linq;
using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared.Body.Part;
using Content.Shared.Damage;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Claw Series finisher: burgundy double, ten fast claw strikes and a big cross slash that severs an arm and a leg.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    private void StartClawSeriesFinisher(Entity<ProperKungFuComponent> ent, EntityUid target, Vector2 direction)
    {
        var comp = ent.Comp;
        var finisher = EnsureComp<ProperKungFuSeriesFinisherComponent>(ent);
        finisher.Victim = target;
        finisher.Map = _transform.GetMapId(target);
        finisher.Phase = ClawSeriesFinisherPhase.Wait;
        finisher.PhaseStart = _timing.CurTime;
        finisher.Direction = direction;
        finisher.Clone = null;
        finisher.Strikes = 0;

        // Locked for the whole sequence; the final cross slash knocks it down afterwards.
        var total = comp.ClawSeriesFinisherCloneDelay + comp.ClawSeriesFinisherCloneWindup + comp.ClawSeriesFinisherPushDuration
                    + comp.ClawSeriesFinisherStrikeInterval * (comp.ClawSeriesFinisherStrikes + 1) + TimeSpan.FromSeconds(0.5);
        _stun.TryStun(target, total, true);

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-series-finisher-user", ("target", target)), ent, ent, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-series-finisher-target", ("user", ent.Owner)), target, target, PopupType.LargeCaution);
    }

    private void UpdateClawSeriesFinishers(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuSeriesFinisherComponent, ProperKungFuComponent>();
        while (query.MoveNext(out var uid, out var finisher, out var kungFu))
        {
            UpdateClawSeriesFinisher((uid, kungFu), finisher, now);
        }
    }

    private void UpdateClawSeriesFinisher(Entity<ProperKungFuComponent> ent, ProperKungFuSeriesFinisherComponent finisher, TimeSpan now)
    {
        var user = ent.Owner;
        var comp = ent.Comp;
        var victim = finisher.Victim;

        if (TerminatingOrDeleted(victim)
            || _transform.GetMapId(victim) != finisher.Map
            || _transform.GetMapId(user) != finisher.Map
            || _container.IsEntityInContainer(victim)
            || _container.IsEntityInContainer(user)
            || _mobState.IsIncapacitated(user))
        {
            StopClawSeriesFinisher(user, finisher);
            return;
        }

        var elapsed = now - finisher.PhaseStart;
        switch (finisher.Phase)
        {
            case ClawSeriesFinisherPhase.Wait:
            {
                if (elapsed < comp.ClawSeriesFinisherCloneDelay || HasComp<ProperKungFuSlidingComponent>(victim))
                    return;

                // The double appears behind the target, facing the user.
                var userPos = _transform.GetWorldPosition(user);
                var victimPos = _transform.GetWorldPosition(victim);
                var delta = victimPos - userPos;
                if (delta.LengthSquared() > 0.0001f)
                    finisher.Direction = Vector2.Normalize(delta);

                var clonePos = victimPos + finisher.Direction * comp.ClawSeriesFinisherCloneOffset;
                finisher.Clone = SpawnAfterimage(user, finisher.Map, clonePos, comp.ClawSeriesFinisherClone);
                if (finisher.Clone is { } clone)
                    _transform.SetWorldRotation(clone, (-finisher.Direction).ToWorldAngle());

                finisher.Phase = ClawSeriesFinisherPhase.Clone;
                finisher.PhaseStart = now;
                return;
            }
            case ClawSeriesFinisherPhase.Clone:
            {
                if (elapsed < comp.ClawSeriesFinisherCloneWindup)
                    return;

                // The double's Claw Series strike carries the target back until it stands right in front of the user.
                var userPos = _transform.GetWorldPosition(user);
                var victimPos = _transform.GetWorldPosition(victim);
                var back = userPos - victimPos;
                var distance = back.Length();
                var push = MathF.Max(0f, distance - comp.ClawSeriesFinisherPushStop);
                if (distance > 0.0001f && push > 0f)
                    StartKnockback(victim, back / distance, push, comp.ClawSeriesFinisherPushDuration, dust: true);

                Spawn(PalmFlashEffect, _transform.GetMapCoordinates(victim));
                _audio.PlayPvs(comp.ClawSeriesFinisherCloneSound, victim);

                finisher.Phase = ClawSeriesFinisherPhase.Push;
                finisher.PhaseStart = now;
                return;
            }
            case ClawSeriesFinisherPhase.Push:
            {
                if (elapsed < comp.ClawSeriesFinisherPushDuration && HasComp<ProperKungFuSlidingComponent>(victim))
                    return;

                RemCompDeferred<ProperKungFuSlidingComponent>(victim);
                finisher.Hold = _transform.GetWorldPosition(victim);
                var delta = finisher.Hold - _transform.GetWorldPosition(user);
                if (delta.LengthSquared() > 0.0001f)
                    finisher.Direction = Vector2.Normalize(delta);

                finisher.Phase = ClawSeriesFinisherPhase.Strikes;
                finisher.PhaseStart = now;
                finisher.NextStrike = now;
                return;
            }
            case ClawSeriesFinisherPhase.Strikes:
            {
                _transform.SetWorldPosition(victim, finisher.Hold);
                if (now < finisher.NextStrike)
                    return;

                if (finisher.Strikes < comp.ClawSeriesFinisherStrikes)
                {
                    ClawSeriesFinisherStrike(ent, victim, finisher);
                    finisher.Strikes++;
                    finisher.NextStrike = now + comp.ClawSeriesFinisherStrikeInterval;
                    return;
                }

                ClawSeriesFinisherCross(ent, victim, finisher);
                StopClawSeriesFinisher(user, finisher);
                return;
            }
        }
    }

    private void ClawSeriesFinisherStrike(Entity<ProperKungFuComponent> ent, EntityUid victim, ProperKungFuSeriesFinisherComponent finisher)
    {
        var user = ent.Owner;
        var victimPos = _transform.GetWorldPosition(victim);
        var userXform = Transform(user);
        var delta = victimPos - _transform.GetWorldPosition(userXform);
        if (delta.LengthSquared() > 0.0001f)
            _transform.SetWorldRotation(user, delta.ToWorldAngle());

        // The normal unarmed swing: the 3-claw scratch arc, alternating sides.
        var localPos = Vector2.Transform(victimPos, _transform.GetInvWorldMatrix(userXform));
        localPos = userXform.LocalRotation.RotateVec(localPos);
        if (localPos.Length() > 1f)
            localPos = localPos.Normalized() * 1f;

        if (localPos.LengthSquared() > 0f)
            _melee.DoLunge(user, user, Angle.FromDegrees(60), localPos, ent.Comp.ClawAnimation, Angle.Zero, finisher.Strikes % 2 == 1, predicted: false);

        _damageable.TryChangeDamage(victim, ent.Comp.ClawSeriesFinisherStrikeDamage, origin: user, targetPart: SkillTargetPart, canMiss: false);
        _audio.PlayPvs(ent.Comp.ClawSeriesFinisherStrikeSound, victim);
    }

    private void ClawSeriesFinisherCross(Entity<ProperKungFuComponent> ent, EntityUid victim, ProperKungFuSeriesFinisherComponent finisher)
    {
        var user = ent.Owner;
        var comp = ent.Comp;

        Spawn(comp.ClawSeriesFinisherCross, _transform.GetMapCoordinates(victim));
        _audio.PlayPvs(comp.ClawSeriesFinisherFinalSound, victim);

        DealChunkedDamage(user, victim, comp.ClawSeriesFinisherFinalDamage, SkillTargetPart);

        // One arm and one leg, a random side each.
        SeverRandomLimb(victim, BodyPartType.Arm);
        SeverRandomLimb(victim, BodyPartType.Leg);

        if (!TerminatingOrDeleted(victim))
        {
            _stun.TryKnockdown(victim, comp.ClawSeriesFinisherKnockdownTime, true);
            StartKnockback(victim, finisher.Direction, comp.ClawSeriesFinisherKnockback, comp.ClawSeriesFinisherKnockbackDuration, dust: true);
        }

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-series-finisher-final-user", ("target", victim)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-series-finisher-final-target", ("user", user)), victim, victim, PopupType.LargeCaution);
    }

    private void SeverRandomLimb(EntityUid victim, BodyPartType type)
    {
        var first = _instinctRandom.Prob(0.5f) ? BodyPartSymmetry.Left : BodyPartSymmetry.Right;
        var second = first == BodyPartSymmetry.Left ? BodyPartSymmetry.Right : BodyPartSymmetry.Left;

        foreach (var symmetry in new[] { first, second })
        {
            foreach (var (part, _) in _body.GetBodyChildrenOfType(victim, type, symmetry: symmetry).ToList())
            {
                if (!TryComp<WoundableComponent>(part, out var woundable)
                    || woundable.ParentWoundable is not { } parent
                    || !woundable.CanRemove)
                    continue;

                _wound.AmputateWoundable(parent, part, woundable);
                return;
            }
        }
    }

    private void StopClawSeriesFinisher(EntityUid user, ProperKungFuSeriesFinisherComponent finisher)
    {
        RemCompDeferred(user, finisher);
    }
}
