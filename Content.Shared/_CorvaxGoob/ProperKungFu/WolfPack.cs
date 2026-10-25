using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Wolf Pack" (Волчья стая): calls 1-3 short-lived copies of the user out of smoke. The copies look and dress like
/// the user (cosmetic clothes only), know Proper Kung Fu, have little health, deal reduced damage and fight the
/// chosen target, whoever attacks the user and whoever the user attacks.
/// </summary>
public sealed partial class WolfPackActionEvent : EntityTargetActionEvent
{
    [DataField]
    public int MinClones = 1;

    [DataField]
    public int MaxClones = 3;

    /// <summary>
    /// Seconds a clone lives before it vanishes.
    /// </summary>
    [DataField]
    public float Lifetime = 30f;

    /// <summary>
    /// Total damage that kills a clone; its mob thresholds are scaled to this.
    /// </summary>
    [DataField]
    public float CloneHealth = 20f;

    /// <summary>
    /// Multiplier for every bit of damage a clone deals.
    /// </summary>
    [DataField]
    public float DamageModifier = 0.25f;

    /// <summary>
    /// Clones appear this far from the user.
    /// </summary>
    [DataField]
    public float SpawnRadius = 1f;

    [DataField]
    public EntProtoId SpawnEffect = "ProperKungFuWolfPackSmoke";

    [DataField]
    public EntProtoId VanishEffect = "ProperKungFuWolfPackVanish";

    [DataField]
    public SoundSpecifier? Sound;
}

/// <summary>
/// "Kunai" (Кунай): puts a summoned kunai into the user's hand. Thrown, it pulls the user to where it stops.
/// </summary>
public sealed partial class KunaiActionEvent : InstantActionEvent
{
    [DataField]
    public EntProtoId Kunai = "ProperKungFuKunai";

    [DataField]
    public EntProtoId SmokeEffect = "ProperKungFuWolfPackSmoke";

    /// <summary>
    /// An unused kunai vanishes after this many seconds.
    /// </summary>
    [DataField]
    public float Lifetime = 60f;

    [DataField]
    public SoundSpecifier? Sound = new SoundPathSpecifier("/Audio/Effects/smoke.ogg");
}

/// <summary>
/// "Join the Pack" (Пополнение в стаю): after a <see cref="Delay"/> of uninterrupted teaching the target learns
/// "Proper Kung Fu (pupil)": the three regular combos with weaker effects and nothing else.
/// </summary>
public sealed partial class JoinPackActionEvent : EntityTargetActionEvent
{
    [DataField]
    public TimeSpan Delay = TimeSpan.FromSeconds(10);
}

[Serializable, NetSerializable]
public sealed partial class ProperKungFuJoinPackDoAfterEvent : SimpleDoAfterEvent;
