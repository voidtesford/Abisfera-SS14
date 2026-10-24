using System.Linq;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.CCVar;
using Content.Shared._Shitmed.Damage;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Passives of Proper Kung Fu: less damage from gunshots, less own bleeding, more bleeding on the victims,
/// and the finisher heal.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;

    /// <summary>
    /// Users that were just hit by a bullet or a hitscan shot: the damage of that same tick is reduced.
    /// </summary>
    private readonly Dictionary<EntityUid, GameTick> _gunshotPending = new();

    private void InitializePassives()
    {
        SubscribeLocalEvent<ProperKungFuComponent, ProjectileReflectAttemptEvent>(OnPassiveProjectile);
        SubscribeLocalEvent<ProperKungFuComponent, HitScanReflectAttemptEvent>(OnPassiveHitscan);
        SubscribeLocalEvent<ProperKungFuComponent, DamageModifyEvent>(OnPassiveDamageModify);
        SubscribeLocalEvent<ProperKungFuComponent, MeleeHitEvent>(OnPassiveMeleeHit);
        SubscribeLocalEvent<ProperKungFuComponent, WoundAddedOnBodyEvent>(OnOwnWoundAdded);
        SubscribeLocalEvent<ProperKungFuComponent, WoundSeverityPointChangedOnBodyEvent>(OnOwnWoundChanged);
        SubscribeLocalEvent<ProperKungFuBleedBoostComponent, WoundAddedOnBodyEvent>(OnBoostWoundAdded);
        SubscribeLocalEvent<ProperKungFuBleedBoostComponent, WoundSeverityPointChangedOnBodyEvent>(OnBoostWoundChanged);
    }

    private void OnPassiveProjectile(Entity<ProperKungFuComponent> ent, ref ProjectileReflectAttemptEvent args)
    {
        _gunshotPending[ent] = _timing.CurTick;
    }

    private void OnPassiveHitscan(Entity<ProperKungFuComponent> ent, ref HitScanReflectAttemptEvent args)
    {
        _gunshotPending[ent] = _timing.CurTick;
    }

    private void OnPassiveDamageModify(EntityUid uid, ProperKungFuComponent comp, DamageModifyEvent args)
    {
        var ent = new Entity<ProperKungFuComponent>(uid, comp);
        if (!_gunshotPending.Remove(ent, out var tick) || tick != _timing.CurTick || comp.Pupil)
            return;

        if (!args.Damage.AnyPositive())
            return;

        args.Damage *= ent.Comp.GunshotDamageModifier;
    }

    /// <summary>
    /// Fist hits mark the victims so their new wounds bleed more.
    /// </summary>
    private void OnPassiveMeleeHit(EntityUid uid, ProperKungFuComponent comp, MeleeHitEvent args)
    {
        var ent = new Entity<ProperKungFuComponent>(uid, comp);
        if (args.User != ent.Owner || args.Weapon != ent.Owner)
            return;

        foreach (var hit in args.HitEntities)
        {
            MarkBleedBoost(ent, hit);
        }

        if (args.IsHit && args.HitEntities.Count > 0 && !HasComp<ProperKungFuCloneComponent>(ent))
            OnLeaderHit(ent, args.HitEntities[0]);
    }

    private void MarkBleedBoost(EntityUid attacker, EntityUid target)
    {
        // A pupil has none of the master's passives, the extra bleeding included.
        if (attacker == target || !TryComp<ProperKungFuComponent>(attacker, out var kungFu) || kungFu.Pupil)
            return;

        var boost = EnsureComp<ProperKungFuBleedBoostComponent>(target);
        boost.Attacker = attacker;
        boost.Tick = _timing.CurTick;
        boost.Modifier = kungFu.DealtBleedModifier;
    }

    /// <summary>
    /// Every damage dealt by a Proper Kung Fu move goes through here, so the victim's wounds bleed more.
    /// </summary>
    private DamageSpecifier? DealKungFuDamage(EntityUid target, DamageSpecifier damage, EntityUid? origin = null,
        TargetBodyPart? targetPart = null, bool canMiss = true)
    {
        if (origin is { } attacker && damage.AnyPositive())
        {
            // A Wolf Pack clone never hurts its summoner or its packmates, and deals only a part of its damage.
            if (TryComp<ProperKungFuCloneComponent>(attacker, out var clone))
            {
                if (IsCloneFriend(attacker, target))
                    return null;

                damage = damage * clone.DamageModifier;
            }
            else
            {
                OnLeaderHit(attacker, target);
            }

            MarkBleedBoost(attacker, target);
        }

        return _damageable.TryChangeDamage(target, damage, origin: origin, targetPart: targetPart, canMiss: canMiss);
    }

    private void OnOwnWoundAdded(Entity<ProperKungFuComponent> ent, ref WoundAddedOnBodyEvent args)
    {
        ApplyBleedModifiers(ent, args.Wound);
    }

    private void OnOwnWoundChanged(Entity<ProperKungFuComponent> ent, ref WoundSeverityPointChangedOnBodyEvent args)
    {
        ApplyBleedModifiers(ent, args.Wound);
    }

    private void OnBoostWoundAdded(Entity<ProperKungFuBleedBoostComponent> ent, ref WoundAddedOnBodyEvent args)
    {
        ApplyBleedModifiers(ent, args.Wound);
    }

    private void OnBoostWoundChanged(Entity<ProperKungFuBleedBoostComponent> ent, ref WoundSeverityPointChangedOnBodyEvent args)
    {
        ApplyBleedModifiers(ent, args.Wound);
    }

    /// <summary>
    /// Shitmed has just set the wound's raw bleeding to severity * trade: scale it once by both factors
    /// (own bleeding of a Proper Kung Fu body, and bleeding inflicted by a Proper Kung Fu attacker this tick).
    /// The equality check keeps it from compounding when both subscriptions see the same wound.
    /// </summary>
    private void ApplyBleedModifiers(EntityUid body, Entity<WoundComponent> wound)
    {
        if (!TryComp<BleedInflicterComponent>(wound, out var bleeds) || bleeds.BleedingAmountRaw <= FixedPoint2.Zero)
            return;

        var expected = wound.Comp.WoundSeverityPoint * _cfg.GetCVar(SurgeryCVars.BleedingSeverityTrade);
        if (FixedPoint2.Abs(bleeds.BleedingAmountRaw - expected) > FixedPoint2.New(0.011))
            return;

        var factor = 1f;
        if (TryComp<ProperKungFuComponent>(body, out var own) && !own.Pupil)
            factor *= own.OwnBleedModifier;

        if (TryComp<ProperKungFuBleedBoostComponent>(body, out var boost)
            && boost.Tick == _timing.CurTick
            && boost.Attacker != body)
            factor *= boost.Modifier;

        if (MathHelper.CloseTo(factor, 1f))
            return;

        bleeds.BleedingAmountRaw = bleeds.BleedingAmountRaw * factor;
        Dirty(wound, bleeds);
    }

    private void UpdateBleedBoosts()
    {
        var query = EntityQueryEnumerator<ProperKungFuBleedBoostComponent>();
        while (query.MoveNext(out var uid, out var boost))
        {
            if (boost.Tick != _timing.CurTick)
                RemCompDeferred(uid, boost);
        }

        if (_gunshotPending.Count > 0)
        {
            foreach (var (uid, tick) in _gunshotPending.ToArray())
            {
                if (tick != _timing.CurTick)
                    _gunshotPending.Remove(uid);
            }
        }
    }

    /// <summary>
    /// Every finisher heals the user (never a clone's summoner): a negative damage spread over the types they have.
    /// </summary>
    private void HealFinisherUser(EntityUid user)
    {
        if (HasComp<ProperKungFuCloneComponent>(user)
            || TryComp<ProperKungFuComponent>(user, out var pupil) && pupil.Pupil
            || !TryComp<ProperKungFuComponent>(user, out var kungFu)
            || kungFu.FinisherHeal <= 0f
            || !TryComp<DamageableComponent>(user, out var damageable))
            return;

        var total = damageable.TotalDamage.Float();
        if (total <= 0f)
            return;

        var amount = MathF.Min(kungFu.FinisherHeal, total);
        var heal = new DamageSpecifier();
        foreach (var (type, value) in damageable.Damage.DamageDict)
        {
            if (value <= FixedPoint2.Zero)
                continue;

            heal.DamageDict[type] = -value * (amount / total);
        }

        _damageable.TryChangeDamage(user, heal, true, false, targetPart: TargetBodyPart.All,
            splitDamage: SplitDamageBehavior.SplitEnsureAllDamaged, canMiss: false);
    }
}
