using System.Numerics;
using Content.Shared.Actions;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Hammer" (Молот): one sweeping downward claw strike into the head of a target within fist reach.
/// Priority of the variants: target in crit (the skull breaks, the head stays on) > Wolf Grip synergy (backflip, then an instant
/// Wolf Dance) > Wolf Dance mark (body hit and a knockback) > target already lying (forced sleep and a much harder
/// hit) > the base hit (knockdown). The crit, grip, lying and base variants also hurt the eyes and the brain.
/// </summary>
public sealed partial class HammerActionEvent : EntityTargetActionEvent
{
    /// <summary>
    /// Base hit, always to the head.
    /// </summary>
    [DataField]
    public DamageSpecifier Damage = new();

    /// <summary>
    /// The base hit floors the target and it can't get up this long.
    /// </summary>
    [DataField]
    public TimeSpan KnockdownTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Part of the eyes' integrity removed by the hit (0.3 = 30%).
    /// </summary>
    [DataField]
    public float EyeDamageFraction = 0.3f;

    /// <summary>
    /// Part of the brain's integrity removed by the hit (0.3 = 30%).
    /// </summary>
    [DataField]
    public float BrainDamageFraction = 0.3f;

    /// <summary>
    /// Damage when the target was already lying on the floor.
    /// </summary>
    [DataField]
    public DamageSpecifier LyingDamage = new();

    /// <summary>
    /// A target that was already lying falls asleep this long.
    /// </summary>
    [DataField]
    public TimeSpan SleepTime = TimeSpan.FromSeconds(30);

    [DataField]
    public EntProtoId SleepStatusEffect = "StatusEffectForcedSleeping";

    /// <summary>
    /// Wolf Grip synergy: damage to the head (no knockdown, so the Wolf Dance follow-up connects).
    /// </summary>
    [DataField]
    public DamageSpecifier GripDamage = new();

    /// <summary>
    /// Wolf Grip synergy: how far back (tiles) the user's backflip takes them.
    /// </summary>
    [DataField]
    public float BackflipDistance = 1f;

    [DataField]
    public TimeSpan BackflipDuration = TimeSpan.FromSeconds(0.35);

    /// <summary>
    /// Wolf Grip synergy: how long a Wolf Dance on the same target is an instant straight dash.
    /// </summary>
    [DataField]
    public TimeSpan DanceWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Downward claw swipe (base, lying and crit variants).
    /// </summary>
    [DataField]
    public EntProtoId? ImpactEffect = "ProperKungFuHammerStrike";

    /// <summary>
    /// Upward claw swipe of the Wolf Grip synergy.
    /// </summary>
    [DataField]
    public EntProtoId? GripImpactEffect = "ProperKungFuHammerUpStrike";

    [DataField]
    public SoundSpecifier? HitSound;

    /// <summary>
    /// Wolf Dance variant: damage to the body (torso), no eye/brain trauma and no sleep.
    /// </summary>
    [DataField]
    public DamageSpecifier DanceDamage = new();

    /// <summary>
    /// Wolf Dance variant: exact knockback (tiles) away from the user.
    /// </summary>
    [DataField]
    public float DanceKnockbackDistance = 5f;

    [DataField]
    public float DanceKnockbackSpeed = 10f;

    /// <summary>
    /// Wolf Dance variant: the target stays down at least this long after landing.
    /// </summary>
    [DataField]
    public TimeSpan DanceKnockdownTime = TimeSpan.FromSeconds(1.5);
}

/// <summary>
/// Left on the target by any Wolf Dance hit: while it lasts, the same user's Hammer hits the body and knocks it back.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuWolfDanceMarkComponent : Component
{
    [ViewVariables]
    public EntityUid Marker;

    [ViewVariables]
    public TimeSpan ExpiresAt;
}

/// <summary>
/// Left by the Hammer's Wolf Grip synergy: while it lasts, a Wolf Dance from the same user on this target
/// is an instant straight dash with the normal kick.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuHammerMarkComponent : Component
{
    [ViewVariables]
    public EntityUid Marker;

    [ViewVariables]
    public TimeSpan ExpiresAt;
}

/// <summary>
/// Sent to clients in PVS so they animate the user's backflip (the movement itself is done by the server).
/// </summary>
[Serializable, NetSerializable]
public sealed class ProperKungFuBackflipEvent : EntityEventArgs
{
    public NetEntity User;
    public float Duration;

    /// <summary>
    /// Direction the user flies (away from the target).
    /// </summary>
    public Vector2 Direction;

    public ProperKungFuBackflipEvent(NetEntity user, float duration, Vector2 direction)
    {
        User = user;
        Duration = duration;
        Direction = direction;
    }
}
