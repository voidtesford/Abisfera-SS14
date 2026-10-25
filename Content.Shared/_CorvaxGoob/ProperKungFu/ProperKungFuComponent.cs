using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Added to an entity that has learned the "Proper Kung Fu" martial art.
/// Holds the granted action buttons and the (future) ultimate meter state.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ProperKungFuComponent : Component
{
    /// <summary>
    /// Action prototypes granted while this art is known (normal mode).
    /// </summary>
    [DataField]
    public List<EntProtoId> BaseActions = new()
    {
        "ActionProperKungFuWolfDance",
        "ActionProperKungFuClawWhirlwind",
        "ActionProperKungFuHammer",
        "ActionProperKungFuWolfInstinct",
        "ActionProperKungFuWolfPack",
        "ActionProperKungFuKunai",
        "ActionProperKungFuJoinPack",
        "ActionProperKungFuUltimate",
    };

    /// <summary>
    /// A pupil (taught by "Join the Pack"): only the three regular combos, with every non-damage effect scaled by
    /// <see cref="PupilEffectMultiplier"/>. No skills, passives, finishers, synergies or ultimate.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Pupil;

    /// <summary>
    /// A Wolf Pack clone: keeps the skills and combos but has none of the passives (attack rate, sprint, stamina,
    /// gunshot and bleed modifiers, the CQC / dragon bypass, Bloodlust, finisher heal) and never charges the ultimate.
    /// Set before the component is added so its startup already sees it.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Clone;

    /// <summary>
    /// Whether the master's passives apply: not for a pupil and not for a Wolf Pack clone.
    /// </summary>
    public bool HasPassives => !Pupil && !Clone;

    /// <summary>
    /// Scale of the non-damage effects of a pupil's Claw Series and Wolf Grip (stun time, knockback, breath block).
    /// A pupil has none of the master's passives (attack rate, stamina, sprint, gunshot and bleed modifiers,
    /// the CQC bypass) and no ranged weapon ban.
    /// </summary>
    [DataField]
    public float PupilEffectMultiplier = 0.5f;

    /// <summary>
    /// Spawned action entities, removed again when the art is lost.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<EntityUid> ActionEntities = new();

    /// <summary>
    /// Passive: multiplier of the unarmed (fist) attack rate.
    /// </summary>
    [DataField]
    public float UnarmedAttackRateMultiplier = 1.5f;

    /// <summary>
    /// Passive: multiplier of the movement speed while sprinting (Goob sprint). Walking is unaffected.
    /// </summary>
    [DataField]
    public float SprintSpeedMultiplier = 1.15f;

    /// <summary>
    /// Passive: added to the stamina crit threshold (max stamina) while the art is known.
    /// </summary>
    [DataField]
    public float BonusStamina = 10f;

    /// <summary>
    /// Melee arc that replaces the user's fist hit / wide / miss animations while the art is known (red wolf claw scratch).
    /// </summary>
    [DataField]
    public EntProtoId ClawAnimation = "WeaponArcProperKungFuClaw";

    /// <summary>
    /// The user's own fist animations, restored when the art is lost.
    /// </summary>
    [ViewVariables]
    public EntProtoId? OriginalAnimation;

    [ViewVariables]
    public EntProtoId? OriginalWideAnimation;

    [ViewVariables]
    public EntProtoId? OriginalMissAnimation;

    /// <summary>
    /// How much stamina was actually added, so exactly that is removed again when the art is lost.
    /// </summary>
    [ViewVariables]
    public float AppliedBonusStamina;

    // Ultimate: a red 0-100 bar with a percentage above the hotbar (client ProperKungFuUltimateBarSystem).
    // Spec: +1 per point of damage taken, +5 per combo, +5 per special move hit (already wired via ProperKungFuSpecialHitEvent).
    // When the owner enters crit with a full bar: after 5 s fully heal, stand up, and enable the ultimate mode
    // for 4 minutes (swap BaseActions for UltimateActions), then reset the bar to 0.

    /// <summary>
    /// Current ultimate meter charge, clamped to [0, <see cref="MaxUltimateCharge"/>].
    /// </summary>
    [DataField, AutoNetworkedField]
    public float UltimateCharge;

    [DataField]
    public float MaxUltimateCharge = 100f;

    /// <summary>
    /// While true the meter does not fill (ultimate mode is running).
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool UltimateActive;

    /// <summary>
    /// Charge gained per successful special move hit.
    /// </summary>
    [DataField]
    public float ChargePerSpecialHit = 5f;

    /// <summary>
    /// Charge gained per performed combo (for later use).
    /// </summary>
    [DataField]
    public float ChargePerCombo = 5f;

    /// <summary>
    /// Charge gained per point of damage taken (for later use).
    /// </summary>
    [DataField]
    public float ChargePerDamage = 1f;

    /// <summary>
    /// Action prototypes that replace <see cref="BaseActions"/> during the ultimate.
    /// </summary>
    [DataField]
    public List<EntProtoId> UltimateActions = new()
    {
        "ActionProperKungFuUltimateClones",
        "ActionProperKungFuSummonPack",
        "ActionProperKungFuWolfJudgment",
        "ActionProperKungFuWolfGaze",
        "ActionProperKungFuWolfSeal",
        "ActionProperKungFuFanKunai",
    };

    /// <summary>
    /// When the running ultimate mode ends (for the client bar), null outside of it.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan? UltimateEndTime;

    /// <summary>
    /// Set when the ultimate is about to start (the 5 s delay after a full-bar crit); null otherwise.
    /// </summary>
    [ViewVariables]
    public TimeSpan? UltimatePendingAt;

    /// <summary>
    /// Ultimate-mode action entities, removed again when the mode ends.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> UltimateActionEntities = new();

    /// <summary>
    /// Whether "Twenty Shadows" was already used in the running ultimate mode: later uses add a smaller wave.
    /// </summary>
    [ViewVariables]
    public bool UltimateClonesUsed;

    /// <summary>
    /// Whether the full "Wolf Judgment" (dash and combo) was already used in the running ultimate mode:
    /// later uses are only the claw cross. Reset when a new mode starts.
    /// </summary>
    [ViewVariables]
    public bool UltimateJudgmentUsed;

    /// <summary>
    /// Wolf Gaze triggers once per ultimate activation: spent only by a successful trigger; a stance that ends
    /// without an attack is not spent (the action gets a 2 minute cooldown instead).
    /// </summary>
    [ViewVariables]
    public bool UltimateGazeUsed;

    /// <summary>
    /// While the ultimate runs the fist attack rate uses this multiplier instead of
    /// <see cref="UnarmedAttackRateMultiplier"/> (replaced, not stacked).
    /// </summary>
    [DataField]
    public float UltimateAttackRateMultiplier = 2.5f;

    /// <summary>
    /// Combo list (comboList prototype id) swapped in while the ultimate runs: every combo shortened to 2 inputs.
    /// </summary>
    [DataField]
    public string UltimateCombos = "ProperKungFuUltimateMoves";

    /// <summary>
    /// Combo list swapped back in when the ultimate ends.
    /// </summary>
    [DataField]
    public string RegularCombos = "ProperKungFuMoves";

    /// <summary>
    /// Bloodlust: how long a victim hurt by the user stays marked with "Blood Harvest".
    /// </summary>
    [DataField]
    public TimeSpan BloodlustDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bloodlust: a marked victim heals the user only while within this many tiles of them.
    /// </summary>
    [DataField]
    public float BloodlustRange = 10f;

    /// <summary>
    /// Bloodlust: healed per bleed tick of a marked victim (the bloodstream's own bleed tick rate-limits it).
    /// </summary>
    [DataField]
    public float BloodlustHeal = 5f;

    [DataField]
    public TimeSpan UltimateDuration = TimeSpan.FromMinutes(4);

    [DataField]
    public TimeSpan UltimateActivationDelay = TimeSpan.FromSeconds(5);

    // Claw Series finisher: a target that already had ClawSeriesFinisherThreshold damage before the combo
    // gets pushed back by a burgundy double, ten fast claw strikes and a big cross slash that severs an arm and a leg.

    /// <summary>
    /// Total damage the target must already have (before the Claw Series bonus) for the finisher. 0 disables it.
    /// </summary>
    [DataField]
    public float ClawSeriesFinisherThreshold = 80f;

    /// <summary>
    /// Wait after the combo (the normal slide back) before the double appears.
    /// </summary>
    [DataField]
    public TimeSpan ClawSeriesFinisherCloneDelay = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Time between the double appearing and its strike.
    /// </summary>
    [DataField]
    public TimeSpan ClawSeriesFinisherCloneWindup = TimeSpan.FromSeconds(0.15);

    /// <summary>
    /// How long the double's push carries the target back to the user.
    /// </summary>
    [DataField]
    public TimeSpan ClawSeriesFinisherPushDuration = TimeSpan.FromSeconds(0.3);

    /// <summary>
    /// Distance from the user at which the pushed target stops.
    /// </summary>
    [DataField]
    public float ClawSeriesFinisherPushStop = 1f;

    /// <summary>
    /// How far behind the target the double appears.
    /// </summary>
    [DataField]
    public float ClawSeriesFinisherCloneOffset = 0.8f;

    [DataField]
    public EntProtoId ClawSeriesFinisherClone = "ProperKungFuSeriesClone";

    [DataField]
    public int ClawSeriesFinisherStrikes = 10;

    [DataField]
    public TimeSpan ClawSeriesFinisherStrikeInterval = TimeSpan.FromSeconds(0.1);

    [DataField]
    public DamageSpecifier ClawSeriesFinisherStrikeDamage = new() { DamageDict = { ["Slash"] = 2 } };

    [DataField]
    public DamageSpecifier ClawSeriesFinisherFinalDamage = new() { DamageDict = { ["Slash"] = 100 } };

    [DataField]
    public float ClawSeriesFinisherKnockback = 3f;

    [DataField]
    public TimeSpan ClawSeriesFinisherKnockbackDuration = TimeSpan.FromSeconds(0.35);

    [DataField]
    public TimeSpan ClawSeriesFinisherKnockdownTime = TimeSpan.FromSeconds(3);

    [DataField]
    public EntProtoId ClawSeriesFinisherCross = "ProperKungFuSeriesFinisherCross";

    [DataField]
    public SoundSpecifier ClawSeriesFinisherCloneSound = new SoundPathSpecifier("/Audio/Weapons/genhit3.ogg");

    [DataField]
    public SoundSpecifier ClawSeriesFinisherStrikeSound = new SoundCollectionSpecifier("AlienClaw", AudioParams.Default.WithVolume(-2f));

    [DataField]
    public SoundSpecifier ClawSeriesFinisherFinalSound = new SoundPathSpecifier("/Audio/Weapons/Xeno/alien_claw_flesh3.ogg", AudioParams.Default.WithVolume(4f));

    /// <summary>
    /// Every finisher (any skill or combo) heals the user by this much, spread over the damage types they have.
    /// </summary>
    [DataField]
    public float FinisherHeal = 25f;

    /// <summary>
    /// Passive: damage from bullets and other projectiles / hitscan shots is multiplied by this (0.6 = -40%).
    /// </summary>
    [DataField]
    public float GunshotDamageModifier = 0.6f;

    /// <summary>
    /// Passive: the user's own bleeding (new or growing wounds) is multiplied by this.
    /// </summary>
    [DataField]
    public float OwnBleedModifier = 0.5f;

    /// <summary>
    /// Passive: bleeding from wounds the user inflicts (fists, skills, combos) is multiplied by this.
    /// </summary>
    [DataField]
    public float DealtBleedModifier = 1.5f;
}
