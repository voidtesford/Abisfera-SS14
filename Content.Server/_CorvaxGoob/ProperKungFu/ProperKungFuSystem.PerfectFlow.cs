using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Server.Speech.EntitySystems;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Jittering;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Perfect Flow" (Идеальный поток) combo: Disarm, Harm, Disarm, Harm.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly SlurredSystem _slurred = default!;
    [Dependency] private readonly SharedJitteringSystem _jitter = default!;

    private static readonly TargetBodyPart[] PerfectFlowParts =
    {
        TargetBodyPart.LeftArm,
        TargetBodyPart.RightArm,
        TargetBodyPart.Chest,
    };

    private void OnPerfectFlow(Entity<ProperKungFuComponent> ent, ref ProperKungFuPerfectFlowPerformedEvent args)
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

        var user = ent.Owner;

        // Both thresholds are measured on the target's damage before this combo hits.
        var total = TryComp<DamageableComponent>(target, out var damageable) ? damageable.TotalDamage : FixedPoint2.Zero;

        // 10 Blunt into each arm and the torso; the hands are not touched. A pupil only hits the torso.
        foreach (var part in PerfectFlowParts)
        {
            if (ent.Comp.Pupil && part != TargetBodyPart.Chest)
                continue;

            var damage = new DamageSpecifier();
            damage.DamageDict.Add(args.DamageType, args.PartDamage);
            DealKungFuDamage(target, damage, origin: user, targetPart: part, canMiss: false);
        }

        // A battered target gets both arms broken. A pupil forces no fractures (bones only break from damage as usual).
        if (!ent.Comp.Pupil && total >= args.FractureThreshold)
        {
            InflictPartTrauma(target, TargetBodyPart.LeftArm, args.DamageType, (_, _) => null, breakBones: true);
            InflictPartTrauma(target, TargetBodyPart.RightArm, args.DamageType, (_, _) => null, breakBones: true);
            _audio.PlayPvs(args.FractureSound, target);
        }

        // A pupil's Perfect Flow neither disorients nor stuns.
        if (!ent.Comp.Pupil)
            ApplyPerfectFlowDisorientation(target, args, args.DisorientTime);
        if (ent.Comp.Pupil)
        {
            if (args.FlashEffect is { } chestFlash)
                SpawnAttachedTo(chestFlash, new EntityCoordinates(target, PerfectFlowFlashOffsets[2]));
        }
        else
        {
            SpawnPerfectFlowMarks(target, args);
        }
        _audio.PlayPvs(args.HitSound, target);

        if (!ent.Comp.Pupil && total >= args.FinisherThreshold && !TerminatingOrDeleted(target) && !_mobState.IsDead(target))
        {
            DoPerfectFlowFinisher(user, target, args);
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-perfect-flow-user", ("target", target)), user, user, PopupType.Medium);
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-perfect-flow-target", ("user", user)), target, target, PopupType.LargeCaution);
        }

        if (!ent.Comp.UltimateActive)
            AddUltimateCharge(ent, ent.Comp.ChargePerCombo);
    }

    private void ApplyPerfectFlowDisorientation(EntityUid target, ProperKungFuPerfectFlowPerformedEvent args, TimeSpan time)
    {
        ApplyDisorientation(target, args.DisorientEffect, time);
    }

    /// <summary>
    /// The Perfect Flow disorientation: the status effect (staggering, screen overlay), jitter and slurred speech.
    /// </summary>
    private void ApplyDisorientation(EntityUid target, EntProtoId disorientEffect, TimeSpan time)
    {
        _statusNew.TryUpdateStatusEffectDuration(target, disorientEffect, time);

        if (TryComp<ProperKungFuDisorientedComponent>(target, out var disoriented))
            disoriented.NextStagger = _timing.CurTime + TimeSpan.FromSeconds(disoriented.StaggerInterval.X);

        var amplitude = 18f;
        var frequency = 7f;
        if (_proto.TryIndex(disorientEffect, out var effectProto)
            && effectProto.TryGetComponent<ProperKungFuDisorientedStatusEffectComponent>(out var effectComp, EntityManager.ComponentFactory))
        {
            amplitude = effectComp.JitterAmplitude;
            frequency = effectComp.JitterFrequency;
        }

        _jitter.DoJitter(target, time, true, amplitude, frequency);
        _slurred.DoSlur(target, time);
    }

    /// <summary>
    /// Flash offsets on the target, in hit order: left arm, right arm, chest.
    /// </summary>
    private static readonly Vector2[] PerfectFlowFlashOffsets =
    {
        new(-0.25f, 0.02f),
        new(0.25f, 0.02f),
        new(0f, 0.08f),
    };

    private readonly List<(TimeSpan Due, EntityUid Target, EntProtoId Proto, Vector2 Offset)> _pendingFlowFlashes = new();

    private static readonly EntProtoId SkillFlashEffect = "ProperKungFuFlowFlash";
    private static readonly EntProtoId WeakSkillFlashEffect = "ProperKungFuFlowFlashWeak";
    private const float SkillFlashInterval = 0.1f;
    private const float WeakSkillFlashInterval = 0.06f;

    /// <summary>
    /// Sequential white-red flashes, one per hit location, <see cref="ProperKungFuPerfectFlowPerformedEvent.FlashInterval"/> apart.
    /// </summary>
    private void SpawnPerfectFlowMarks(EntityUid target, ProperKungFuPerfectFlowPerformedEvent args)
    {
        if (args.FlashEffect is not { } flash)
            return;

        SpawnFlashSequence(target, flash, args.FlashInterval);
    }

    /// <summary>
    /// The Perfect Flow white-red flash sequence (left arm, right arm, chest), also used by Hammer, the Wolf Grip +
    /// Claw Whirlwind synergy, Wolf Instinct and (with a weaker flash) Wolf Dance.
    /// </summary>
    private void SpawnFlashSequence(EntityUid target, EntProtoId flash, float interval)
    {
        if (TerminatingOrDeleted(target))
            return;

        SpawnAttachedTo(flash, new EntityCoordinates(target, PerfectFlowFlashOffsets[0]));
        for (var i = 1; i < PerfectFlowFlashOffsets.Length; i++)
        {
            _pendingFlowFlashes.Add((_timing.CurTime + TimeSpan.FromSeconds(interval * i), target, flash, PerfectFlowFlashOffsets[i]));
        }
    }

    /// <summary>
    /// Full-strength flash sequence for Hammer, the grip synergy and Wolf Instinct.
    /// </summary>
    private void SpawnSkillFlashes(EntityUid target)
    {
        SpawnFlashSequence(target, SkillFlashEffect, SkillFlashInterval);
    }

    /// <summary>
    /// Shorter, fainter flash sequence for Wolf Dance.
    /// </summary>
    private void SpawnWeakSkillFlashes(EntityUid target)
    {
        SpawnFlashSequence(target, WeakSkillFlashEffect, WeakSkillFlashInterval);
    }

    private void UpdatePerfectFlowFlashes(TimeSpan now)
    {
        for (var i = _pendingFlowFlashes.Count - 1; i >= 0; i--)
        {
            var (due, target, proto, offset) = _pendingFlowFlashes[i];
            if (now < due)
                continue;

            _pendingFlowFlashes.RemoveAt(i);
            if (!TerminatingOrDeleted(target))
                SpawnAttachedTo(proto, new EntityCoordinates(target, offset));
        }
    }

    /// <summary>
    /// The user appears behind the target (on the side opposite to where they stood), leaves a burgundy
    /// afterimage at the old spot, spins and kicks the target in the head.
    /// </summary>
    private void DoPerfectFlowFinisher(EntityUid user, EntityUid target, ProperKungFuPerfectFlowPerformedEvent args)
    {
        HealFinisherUser(user);

        var map = _transform.GetMapId(user);
        var userPos = _transform.GetWorldPosition(user);
        var targetPos = _transform.GetWorldPosition(target);
        var delta = targetPos - userPos;
        var dir = delta.LengthSquared() > 0.0001f ? Vector2.Normalize(delta) : _transform.GetWorldRotation(user).ToWorldVec();

        if (args.AfterimageEffect is { } afterimage)
            SpawnAfterimage(user, map, userPos, afterimage);

        var behind = targetPos + dir * args.BehindDistance;
        if (IsPathBlocked(user, map, targetPos, behind, out var free))
            behind = free;

        RemComp<ProperKungFuSlidingComponent>(user);
        if (HasComp<PhysicsComponent>(user))
            _physics.SetLinearVelocity(user, Vector2.Zero);
        _transform.SetWorldPosition(user, behind);
        _transform.SetWorldRotation(user, (-dir).ToWorldAngle());

        RaiseNetworkEvent(new ProperKungFuFlowSpinEvent(GetNetEntity(user), args.SpinDuration),
            Filter.Pvs(user, entityManager: EntityManager));

        var kick = new DamageSpecifier();
        kick.DamageDict.Add(args.FinisherDamageType, args.FinisherDamage);
        DealKungFuDamage(target, kick, origin: user, targetPart: TargetBodyPart.Head, canMiss: false);

        if (args.AxeKickEffect is { } axe)
            SpawnAttachedTo(axe, new EntityCoordinates(target, new Vector2(0f, 0.3f)));

        _audio.PlayPvs(args.FinisherSound, target);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-perfect-flow-finisher-user", ("target", target)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-perfect-flow-finisher-target", ("user", user)), target, target, PopupType.LargeCaution);
    }

    /// <summary>
    /// Disoriented mobs stumble to a random side every now and then.
    /// </summary>
    private void UpdatePerfectFlow(TimeSpan now)
    {
        UpdatePerfectFlowFlashes(now);

        var query = EntityQueryEnumerator<ProperKungFuDisorientedComponent, PhysicsComponent>();
        while (query.MoveNext(out var uid, out var disoriented, out var physics))
        {
            if (now < disoriented.NextStagger)
                continue;

            var interval = _instinctRandom.NextFloat(disoriented.StaggerInterval.X, disoriented.StaggerInterval.Y);
            disoriented.NextStagger = now + TimeSpan.FromSeconds(interval);

            if (_container.IsEntityInContainer(uid) || _mobState.IsIncapacitated(uid)
                || HasComp<ClawWhirlwindedComponent>(uid) || HasComp<ProperKungFuSlidingComponent>(uid))
                continue;

            var stumble = _instinctRandom.NextAngle().ToVec() * disoriented.StaggerSpeed;
            _physics.SetLinearVelocity(uid, physics.LinearVelocity + stumble, body: physics);
        }
    }
}
