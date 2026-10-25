using System.Numerics;
using Content.Shared.Actions;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Hammer" (Молот): one sweeping downward claw strike into the head of a target within fist reach.
/// Priority of the variants: Wolf Grip synergy (backflip, then an instant Wolf Dance) > Wolf Dance mark (body hit and
/// a knockback) > finisher (a living target with at least 80 damage: four strikes in a row) > target already lying
/// (forced sleep and a much harder hit) > the base hit (knockdown). The grip, lying and base variants also hurt the
/// eyes and the brain. The skull is never broken.
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
    /// A target that was already lying is knocked down, stunned and disoriented this long.
    /// </summary>
    [DataField]
    public TimeSpan LyingStunTime = TimeSpan.FromSeconds(10);

    [DataField]
    public EntProtoId LyingDisorientEffect = "StatusEffectProperKungFuDisoriented";

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

    /// <summary>
    /// Finisher: a living target with at least this much total damage (before the hit).
    /// </summary>
    [DataField]
    public float FinisherThreshold = 80f;

    /// <summary>
    /// Finisher step 1: the normal downward Hammer strike, but only this much damage to the head (no trauma or sleep).
    /// </summary>
    [DataField]
    public DamageSpecifier FinisherFirstDamage = new();

    /// <summary>
    /// Finisher step 2: a straight strike into the torso with a horizontal claw swipe.
    /// </summary>
    [DataField]
    public DamageSpecifier FinisherStraightDamage = new();

    [DataField]
    public float FinisherStraightKnockback = 2f;

    /// <summary>
    /// Finisher step 3: after a front flip towards the target, another normal Hammer strike into the torso.
    /// </summary>
    [DataField]
    public DamageSpecifier FinisherFlipDamage = new();

    /// <summary>
    /// Finisher step 4: a spinning strike with a diagonal claw swipe into the head.
    /// </summary>
    [DataField]
    public DamageSpecifier FinisherSpinDamage = new();

    /// <summary>
    /// Finisher step 4: the target flies off spinning and lands this many tiles away from the user.
    /// </summary>
    [DataField]
    public float FinisherFinalDistance = 5f;

    [DataField]
    public float FinisherKnockbackSpeed = 10f;

    [DataField]
    public TimeSpan FinisherKnockdownTime = TimeSpan.FromSeconds(3);

    [DataField]
    public TimeSpan FinisherStraightDelay = TimeSpan.FromSeconds(0.35);

    [DataField]
    public TimeSpan FinisherFlipDelay = TimeSpan.FromSeconds(0.7);

    [DataField]
    public TimeSpan FinisherFlipDuration = TimeSpan.FromSeconds(0.35);

    [DataField]
    public TimeSpan FinisherSpinDelay = TimeSpan.FromSeconds(1.35);

    [DataField]
    public TimeSpan FinisherSpinDuration = TimeSpan.FromSeconds(0.3);

    [DataField]
    public float FinisherTossSpins = 1.5f;

    [DataField]
    public float FinisherTossHeight = 0.6f;

    [DataField]
    public EntProtoId? FinisherHorizontalEffect = "ProperKungFuHammerStrikeHorizontal";

    [DataField]
    public EntProtoId? FinisherDiagonalEffect = "ProperKungFuHammerStrikeDiagonal";

    [DataField]
    public SoundSpecifier? FinisherFinalSound;

    /// <summary>
    /// Finisher, last hit: once the victim lands after the fling, the user dashes at it this fast (tiles per second)...
    /// </summary>
    [DataField]
    public float FinisherDashSpeed = 16f;

    /// <summary>
    /// ...after this pause on landing...
    /// </summary>
    [DataField]
    public TimeSpan FinisherDashPause = TimeSpan.FromSeconds(0.15);

    /// <summary>
    /// ...and slashes it with a cross of claws: this damage to the torso, armor and resistances ignored.
    /// </summary>
    [DataField]
    public DamageSpecifier FinisherCrossDamage = new() { DamageDict = { ["Slash"] = 90 } };

    [DataField]
    public EntProtoId? FinisherCrossEffect = "ProperKungFuInstinctFinisherCross";

    [DataField]
    public SoundSpecifier? FinisherCrossSound = new SoundPathSpecifier("/Audio/Weapons/Xeno/alien_claw_flesh3.ogg");
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
    /// Direction the user flies (away from the target for the backflip, towards it for the front flip).
    /// </summary>
    public Vector2 Direction;

    /// <summary>
    /// A front flip towards the target (Hammer finisher) instead of a backflip away from it. Flying the same way,
    /// both turn the same way, so the client animation is shared.
    /// </summary>
    public bool Front;

    public ProperKungFuBackflipEvent(NetEntity user, float duration, Vector2 direction, bool front = false)
    {
        User = user;
        Duration = duration;
        Direction = direction;
        Front = front;
    }
}
