using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Server.Body.Components;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Wolf Grip" (Волчья хватка) combo and its Claw Whirlwind synergy (the two-handed cross strike).
/// </summary>
public sealed partial class ProperKungFuSystem
{
    private const string WolfGripStrikeMove = "WolfGripStrike";

    /// <summary>
    /// Harm, Disarm, Grab: Slash damage, no breathing for 10 s, and the user takes the target into a hard grab
    /// right in front of themselves. Leaves the Wolf Grip mark for the Claw Whirlwind synergy.
    /// </summary>
    private void OnWolfGrip(Entity<ProperKungFuComponent> ent, ref ProperKungFuWolfGripPerformedEvent args)
    {
        if (!TryComp<CanPerformComboComponent>(ent, out var combo)
            || combo.CurrentTarget is not { } target
            || target == ent.Owner
            || TerminatingOrDeleted(target)
            || !_proto.TryIndex(combo.BeingPerformed, out var proto)
            || !IsKungFuComboForm(ent, proto.MartialArtsForm)
            || !TryComp<MartialArtsKnowledgeComponent>(ent, out var knowledge)
            || knowledge.MartialArtsForm != proto.MartialArtsForm
            || knowledge.Blocked)
            return;

        combo.LastAttacks.Clear();
        Dirty(ent.Owner, combo);

        if (!proto.CanDoWhileProne && TryComp<StandingStateComponent>(ent, out var standing)
            && standing.CurrentState != StandingState.Standing)
            return;

        // Finisher check uses the damage the target already had before this combo's bonus damage.
        var preDamage = TryComp<DamageableComponent>(target, out var targetDamageable)
            ? targetDamageable.TotalDamage.Float()
            : 0f;
        var scale = EffectScale(ent);
        var finisher = !ent.Comp.Pupil && !IsWolfPackClone(ent) && args.FinisherThreshold > 0f
                       && preDamage >= args.FinisherThreshold
                       && !_mobState.IsDead(target)
                       && !HasComp<ProperKungFuGripSuplexComponent>(target);

        var damage = new DamageSpecifier();
        damage.DamageDict.Add(proto.DamageType, proto.ExtraDamage);
        DealKungFuDamage(target, damage, origin: ent, targetPart: SkillTargetPart, canMiss: false);

        BlockBreathing(target, args.BreathBlockTime * scale);

        // Hard grab (the grab that finished the combo is usually only a soft one).
        TakeIntoHardGrab(ent, target, args.HoldDistance);

        // Raising the grab stage counts as another Grab attack: don't let it start a new chain.
        combo.LastAttacks.Clear();
        Dirty(ent.Owner, combo);

        // The Claw Whirlwind synergy mark; a pupil has no synergies.
        if (!ent.Comp.Pupil)
        {
            var mark = EnsureComp<ProperKungFuWolfGripMarkComponent>(target);
            mark.Marker = ent;
            mark.ExpiresAt = _timing.CurTime + args.MarkDuration;
        }

        _audio.PlayPvs(new Robust.Shared.Audio.SoundPathSpecifier("/Audio/Weapons/genhit1.ogg"), target);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-grip-user", ("target", target)), ent, ent, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-grip-target", ("user", ent.Owner)), target, target, PopupType.LargeCaution);

        if (finisher)
            StartGripSuplex(ent, target, args);

        if (!ent.Comp.UltimateActive)
            AddUltimateCharge(ent, ent.Comp.ChargePerCombo);
    }

    /// <summary>
    /// Uses the Krav Maga "blocked breathing" component: the respirator treats its owner as unable to breathe
    /// until the time runs out (the shared martial arts system removes it then). Never shortens a longer block.
    /// </summary>
    private void BlockBreathing(EntityUid target, TimeSpan time)
    {
        if (!HasComp<RespiratorComponent>(target))
            return;

        var blocked = EnsureComp<KravMagaBlockedBreathingComponent>(target);
        var until = _timing.CurTime + time;
        if (blocked.BlockedTime < until)
            blocked.BlockedTime = until;
        Dirty(target, blocked);
    }

    private void TakeIntoHardGrab(EntityUid user, EntityUid target, float holdDistance)
    {
        if (!TryComp<PullerComponent>(user, out var puller) || !TryComp<PullableComponent>(target, out var pullable))
            return;

        if (puller.Pulling != target && !_pulling.TryStartPull(user, target, puller, pullable, GrabStage.Hard))
            return;

        if (puller.Pulling != target)
            return;

        if (puller.GrabStage < GrabStage.Hard || pullable.GrabStage < GrabStage.Hard)
            _pulling.TrySetGrabStages((user, puller), (target, pullable), GrabStage.Hard);

        // Held right in front of the user, facing them.
        var userPos = _transform.GetWorldPosition(user);
        var delta = _transform.GetWorldPosition(target) - userPos;
        var dir = delta.LengthSquared() > 0.0001f ? Vector2.Normalize(delta) : _transform.GetWorldRotation(user).ToWorldVec();
        var hold = userPos + dir * holdDistance;
        if (IsPathBlocked(target, _transform.GetMapId(user), userPos, hold, out var free))
            hold = free;

        RemComp<ProperKungFuSlidingComponent>(target);
        _physics.SetLinearVelocity(target, Vector2.Zero);
        _transform.SetWorldPosition(target, hold);
        _transform.SetWorldRotation(user, dir.ToWorldAngle());
        _transform.SetWorldRotation(target, (-dir).ToWorldAngle());
    }

    private bool TryConsumeWolfGripMark(EntityUid user, EntityUid target)
    {
        if (!TryComp<ProperKungFuWolfGripMarkComponent>(target, out var mark))
            return false;

        // Not while it is being carried over the user's back; the mark is renewed when it lands.
        if (HasComp<ProperKungFuGripSuplexComponent>(target))
            return false;

        if (mark.Marker != user || _timing.CurTime >= mark.ExpiresAt)
            return false;

        RemComp(target, mark);
        return true;
    }

    /// <summary>
    /// Claw Whirlwind on a Wolf-Grip-marked target: a two-handed strike into the chest that breaks the ribs,
    /// hurts the lungs, stops breathing for a minute and throws the target 8 tiles away, floored for 10 s.
    /// </summary>
    private void DoWolfGripStrike(EntityUid user, EntityUid target, ClawWhirlwindActionEvent args)
    {
        // Let go first, the target is thrown out of the hands.
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);

        if (HasComp<ClawWhirlwindedComponent>(target))
            EndWhirlwind(target, landed: false);

        var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(user);
        var direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(user).ToWorldVec();
        _transform.SetWorldRotation(user, direction.ToWorldAngle());

        DealKungFuDamage(target, args.GripStrikeDamage, origin: user, targetPart: SkillTargetPart, canMiss: false);

        var fraction = args.GripLungDamageFraction;
        InflictPartTrauma(target, TargetBodyPart.Chest, "Blunt",
            (organ, organComp) => HasComp<LungComponent>(organ) ? organComp.IntegrityCap * fraction : null, source: user);

        BlockBreathing(target, args.GripBreathBlockTime);

        if (args.GripImpactEffect is { } effect)
            Spawn(effect, _transform.GetMapCoordinates(target));

        SpawnSkillFlashes(target);

        _stun.TryKnockdown(target, args.GripKnockdownTime, true);
        StartKnockback(target,
            direction,
            args.GripKnockbackDistance,
            TimeSpan.FromSeconds(args.GripKnockbackDistance / MathF.Max(0.1f, args.GripKnockbackSpeed)),
            dust: false);

        _audio.PlayPvs(args.GripSound, target);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-grip-strike-user", ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-grip-strike-target", ("user", user)), target, target, PopupType.LargeCaution);

        // +5 ult charge.
        var hitEv = new ProperKungFuSpecialHitEvent(target, WolfGripStrikeMove, false);
        RaiseLocalEvent(user, ref hitEv);
    }

    private void UpdateWolfGripMarks(TimeSpan now)
    {
        var marks = EntityQueryEnumerator<ProperKungFuWolfGripMarkComponent>();
        while (marks.MoveNext(out var uid, out var mark))
        {
            if (now >= mark.ExpiresAt)
                RemCompDeferred(uid, mark);
        }
    }
}
