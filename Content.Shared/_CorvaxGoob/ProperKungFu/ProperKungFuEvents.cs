using Content.Shared.Actions;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Wolf Dance: forward roll toward the target, an angled dash into it, and a single roundhouse kick.
/// The roll and the dash are silent (owner's request); only the kick and the shield block make a sound.
/// </summary>
public sealed partial class WolfDanceActionEvent : EntityTargetActionEvent
{
    [DataField]
    public DamageSpecifier Damage = new();

    [DataField]
    public TimeSpan StunTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How far the target is knocked back, in tiles.
    /// </summary>
    [DataField]
    public float KnockbackDistance = 3f;

    /// <summary>
    /// Average knockback speed in tiles per second (sets how long the push takes).
    /// </summary>
    [DataField]
    public float KnockbackSpeed = 10f;

    /// <summary>
    /// Max distance (tiles) to the target. Should match the action's TargetAction range.
    /// </summary>
    [DataField]
    public float MaxRange = 5.5f;

    /// <summary>
    /// Distance from the target's center where the dash stops.
    /// </summary>
    [DataField]
    public float StopDistance = 0.8f;

    /// <summary>
    /// Duration of phase 1, the forward roll.
    /// </summary>
    [DataField]
    public TimeSpan RollDuration = TimeSpan.FromSeconds(0.45);

    /// <summary>
    /// Duration of phase 2, the angled dash ending with the kick.
    /// </summary>
    [DataField]
    public TimeSpan DashDuration = TimeSpan.FromSeconds(0.25);

    /// <summary>
    /// Part of the total travel distance covered by the roll (0-1). The dash covers the rest.
    /// </summary>
    [DataField]
    public float RollFraction = 0.45f;

    /// <summary>
    /// How far (tiles) the roll drifts to the side, so the dash comes into the target at an angle.
    /// Automatically reduced for short distances and when a wall is in the way.
    /// </summary>
    [DataField]
    public float SideOffset = 0.75f;

    /// <summary>
    /// The kick only lands if the target is within this distance (tiles) when the dash ends.
    /// </summary>
    [DataField]
    public float KickRange = 1.5f;

    /// <summary>
    /// The dash re-aims at a moving target, but can be at most this much (tiles) longer than planned.
    /// </summary>
    [DataField]
    public float ChaseDistance = 1.5f;

    /// <summary>
    /// Afterimages are left behind with this interval while moving.
    /// </summary>
    [DataField]
    public TimeSpan AfterimageInterval = TimeSpan.FromSeconds(0.06);

    /// <summary>
    /// Max amount of afterimages per move (0 disables them).
    /// </summary>
    [DataField]
    public int Afterimages = 12;

    [DataField]
    public EntProtoId? AfterimageEffect = "ProperKungFuDashAfterimage";

    [DataField]
    public EntProtoId? ShockwaveEffect = "ProperKungFuShockwaveEffect";

    [DataField]
    public SoundSpecifier? HitSound;

    [DataField]
    public SoundSpecifier? BlockedSound;

    /// <summary>
    /// Damage of the kick when the target still carries a Claw Series mark (replaces <see cref="Damage"/>).
    /// </summary>
    [DataField]
    public DamageSpecifier SynergyDamage = new();

    /// <summary>
    /// Knockback (tiles) of the kick on a Claw Series marked target.
    /// </summary>
    [DataField]
    public float SynergyKnockbackDistance = 5f;

    /// <summary>
    /// A marked target is only knocked down for this long, without the stun.
    /// </summary>
    [DataField]
    public TimeSpan SynergyKnockdownTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Damage of the kick on a target hit by Claw Whirlwind in the last 2 s (takes priority over the Claw Series mark).
    /// </summary>
    [DataField]
    public DamageSpecifier WhirlwindSynergyDamage = new();

    /// <summary>
    /// Knockback (tiles) of the Whirlwind variant.
    /// </summary>
    [DataField]
    public float WhirlwindKnockbackDistance = 5f;

    /// <summary>
    /// Duration of the single straight dash of the Whirlwind variant (no roll).
    /// </summary>
    [DataField]
    public TimeSpan WhirlwindDashDuration = TimeSpan.FromSeconds(0.2);

    [DataField]
    public TimeSpan WhirlwindKnockdownTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Duration of the near-instant straight dash right after the Hammer's Wolf Grip synergy (no roll, normal kick).
    /// </summary>
    [DataField]
    public TimeSpan HammerDashDuration = TimeSpan.FromSeconds(0.08);

    /// <summary>
    /// The user falls after the Whirlwind variant connects.
    /// </summary>
    [DataField]
    public TimeSpan WhirlwindUserKnockdownTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Speed of the shield flying out of the target's hands when the kick is blocked.
    /// </summary>
    [DataField]
    public float ShieldThrowSpeed = 6f;
}

/// <summary>
/// Raised on the user whenever a Proper Kung Fu special move connects with a target.
/// Hook for the ultimate meter (+5 per special hit).
/// </summary>
[ByRefEvent]
public readonly record struct ProperKungFuSpecialHitEvent(EntityUid Target, string Move, bool Blocked);

/// <summary>
/// Result event of the "Claw Series" combo (Harm, Harm, Harm).
/// </summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class ProperKungFuClawSeriesPerformedEvent : EntityEventArgs;
