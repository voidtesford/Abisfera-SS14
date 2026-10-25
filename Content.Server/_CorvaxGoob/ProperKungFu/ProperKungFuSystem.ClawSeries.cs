using System.Linq;
using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared.Armor;
using Content.Shared.Body.Part;
using Content.Shared.Damage;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Claw Series finisher: burgundy double, ten fast claw cuts from every side (the user blinks around the victim,
/// leaving red afterimages) and a big cross slash that severs an arm and a leg unless the torso armor is strong.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    /// <summary>
    /// Claw cut shown on the victim for every small strike of the finisher.
    /// </summary>
    private const string SeriesClawCutEffect = "ProperKungFuSeriesClawCut";

    /// <summary>
    /// Red silhouette left where the user stood before each blink.
    /// </summary>
    private const string DashAfterimageEffect = "ProperKungFuDashAfterimage";

    /// <summary>
    /// How far from the victim the user stands for each strike of the finisher.
    /// </summary>
    private const float SeriesStrikeRadius = 0.9f;

    /// <summary>
    /// Each strike comes from 108 degrees further around the victim: ten strikes cover every side.
    /// </summary>
    private static readonly Angle SeriesStrikeStep = Angle.FromDegrees(108);

    /// <summary>
    /// Torso armor with this Slash coefficient or lower (at least 30% Slash protection) saves the limbs.
    /// </summary>
    public const float StrongTorsoSlashCoefficient = 0.7f;

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

                // The double is gone before the user's first cut.
                if (finisher.Clone is { } clone)
                    QueueDel(clone);
                finisher.Clone = null;

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
        var victimPos = finisher.Hold;
        var userPos = _transform.GetWorldPosition(user);

        // Blink to the next side of the victim, leaving a red silhouette where the user stood.
        var baseAngle = (-finisher.Direction).ToAngle();
        var dir = (baseAngle + SeriesStrikeStep * finisher.Strikes).ToVec();
        var spot = victimPos + dir * SeriesStrikeRadius;
        if (IsPathBlocked(user, finisher.Map, victimPos, spot, out var free))
            spot = free;

        SpawnAfterimage(user, finisher.Map, userPos, DashAfterimageEffect);
        _transform.SetWorldPosition(user, spot);
        _physics.SetLinearVelocity(user, Vector2.Zero);

        var delta = victimPos - spot;
        if (delta.LengthSquared() > 0.0001f)
        {
            _transform.SetWorldRotation(user, delta.ToWorldAngle());
            finisher.Direction = Vector2.Normalize(delta);
        }

        // The cut shows on the victim itself, at a random slant.
        var cut = Spawn(SeriesClawCutEffect, new MapCoordinates(victimPos, finisher.Map));
        _transform.SetWorldRotation(cut, Angle.FromDegrees(_instinctRandom.NextFloat(0f, 360f)));

        DealKungFuDamage(victim, ent.Comp.ClawSeriesFinisherStrikeDamage, origin: user, targetPart: SkillTargetPart, canMiss: false);
        _audio.PlayPvs(ent.Comp.ClawSeriesFinisherStrikeSound, victim);
    }

    private void ClawSeriesFinisherCross(Entity<ProperKungFuComponent> ent, EntityUid victim, ProperKungFuSeriesFinisherComponent finisher)
    {
        var user = ent.Owner;
        var comp = ent.Comp;
        HealFinisherUser(user);

        Spawn(comp.ClawSeriesFinisherCross, _transform.GetMapCoordinates(victim));
        _audio.PlayPvs(comp.ClawSeriesFinisherFinalSound, victim);

        DealChunkedDamage(user, victim, comp.ClawSeriesFinisherFinalDamage, SkillTargetPart);

        // One arm and one leg, a random side each; strong torso armor (Slash coefficient <= 0.7) saves them.
        if (!HasStrongTorsoSlashArmor(victim))
        {
            SeverRandomLimb(victim, BodyPartType.Arm);
            SeverRandomLimb(victim, BodyPartType.Leg);
        }

        if (!TerminatingOrDeleted(victim))
        {
            _stun.TryKnockdown(victim, comp.ClawSeriesFinisherKnockdownTime, true);
            StartKnockback(victim, finisher.Direction, comp.ClawSeriesFinisherKnockback, comp.ClawSeriesFinisherKnockbackDuration, dust: true);
        }

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-series-finisher-final-user", ("target", victim)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-series-finisher-final-target", ("user", user)), victim, victim, PopupType.LargeCaution);
    }

    /// <summary>
    /// True when the worn armor covering the torso cuts Slash damage to <see cref="StrongTorsoSlashCoefficient"/>
    /// or less (the coefficients of every torso-covering piece multiply).
    /// </summary>
    public bool HasStrongTorsoSlashArmor(EntityUid victim)
    {
        var coefficient = 1f;
        var slots = _inventory.GetSlotEnumerator(victim);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } item
                || !TryComp<ArmorComponent>(item, out var armor))
                continue;

            var coverage = armor.ArmorCoverage;
            if (!coverage.Contains(BodyPartType.Chest))
                continue;

            var modifiers = armor.Modifiers;
            if (!modifiers.Coefficients.TryGetValue("Slash", out var slash))
                continue;

            coefficient *= slash;
        }

        return coefficient <= StrongTorsoSlashCoefficient + 0.0001f;
    }

    private void SeverRandomLimb(EntityUid victim, BodyPartType type, EntityUid? source = null)
    {
        if (source is { } src && IsWolfPackClone(src))
            return;

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
