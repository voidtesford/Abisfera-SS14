using Content.Shared._Shitmed.Targeting;
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
    /// Duration of the straight flying kick (feet first) into the target.
    /// </summary>
    [DataField]
    public TimeSpan DashDuration = TimeSpan.FromSeconds(0.12);

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
    /// Duration of the flying kick of the Whirlwind variant.
    /// </summary>
    [DataField]
    public TimeSpan WhirlwindDashDuration = TimeSpan.FromSeconds(0.07);

    [DataField]
    public TimeSpan WhirlwindKnockdownTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Duration of the near-instant flying kick right after the Hammer's Wolf Grip synergy (normal kick).
    /// </summary>
    [DataField]
    public TimeSpan HammerDashDuration = TimeSpan.FromSeconds(0.03);

    /// <summary>
    /// The user lands on the floor after every flying kick (hit, blocked or missed), in every variant.
    /// </summary>
    [DataField]
    public TimeSpan UserKnockdownTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Speed of the shield flying out of the target's hands when the kick is blocked.
    /// </summary>
    [DataField]
    public float ShieldThrowSpeed = 6f;

    // CorvaxGoob-ProperKungFu-Start: plain Wolf Dance (no synergy) is a dash through the target that tosses it up in place.
    /// <summary>
    /// Plain variant: the user dashes through the target and stops this far (tiles) past its center.
    /// </summary>
    [DataField]
    public float PassThroughDistance = 1f;

    /// <summary>
    /// Plain variant: duration of the fast dash through the target.
    /// </summary>
    [DataField]
    public TimeSpan PassThroughDashDuration = TimeSpan.FromSeconds(0.12);

    /// <summary>
    /// Plain variant: how long the target spends in the air (it lands on the same spot, knocked down).
    /// </summary>
    [DataField]
    public TimeSpan TossDuration = TimeSpan.FromSeconds(0.6);

    /// <summary>
    /// Plain variant: how high (tiles) the target sprite is tossed.
    /// </summary>
    [DataField]
    public float TossHeight = 0.9f;

    /// <summary>
    /// Plain variant: full flips of the target in the air.
    /// </summary>
    [DataField]
    public float TossSpins = 1f;

    /// <summary>
    /// Plain variant: whether the user falls after the dash (no by default, no dropkick pose).
    /// </summary>
    [DataField]
    public bool PlainUserFalls;

    /// <summary>
    /// Plain variant finisher: starts when the target already had at least this much total damage before the dance.
    /// </summary>
    [DataField]
    public float FinisherThreshold = 85f;

    /// <summary>
    /// Finisher: extra dashes through the locked target after the first hit, each turned 90 degrees.
    /// </summary>
    [DataField]
    public int FinisherPasses = 10;

    /// <summary>
    /// Finisher: every extra dash ends this far (tiles) past the target.
    /// </summary>
    [DataField]
    public float FinisherPassDistance = 3f;

    /// <summary>
    /// Finisher: duration of one extra dash.
    /// </summary>
    [DataField]
    public TimeSpan FinisherPassDuration = TimeSpan.FromSeconds(0.16);

    /// <summary>
    /// Finisher: damage of every extra pass (to the torso).
    /// </summary>
    [DataField]
    public DamageSpecifier FinisherPassDamage = new();

    [DataField]
    public EntProtoId? FinisherPassEffect;

    [DataField]
    public SoundSpecifier? FinisherPassSound;

    /// <summary>
    /// Finisher: toss of the target on every pass (client only).
    /// </summary>
    [DataField]
    public TimeSpan FinisherTossDuration = TimeSpan.FromSeconds(0.3);

    [DataField]
    public float FinisherTossHeight = 0.5f;

    /// <summary>
    /// Final strike: damage dealt to each of <see cref="FinisherFinalParts"/>.
    /// </summary>
    [DataField]
    public DamageSpecifier FinisherFinalDamage = new();

    [DataField]
    public List<TargetBodyPart> FinisherFinalParts = new()
    {
        TargetBodyPart.Head,
        TargetBodyPart.Chest,
        TargetBodyPart.LeftArm,
        TargetBodyPart.RightArm,
        TargetBodyPart.LeftLeg,
        TargetBodyPart.RightLeg,
    };

    [DataField]
    public float FinisherKnockbackDistance = 7f;

    [DataField]
    public float FinisherKnockbackSpeed = 14f;

    [DataField]
    public TimeSpan FinisherKnockdownTime = TimeSpan.FromSeconds(3);

    [DataField]
    public EntProtoId? FinisherFinalEffect;

    [DataField]
    public SoundSpecifier? FinisherFinalSound;
    // CorvaxGoob-ProperKungFu-End
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
