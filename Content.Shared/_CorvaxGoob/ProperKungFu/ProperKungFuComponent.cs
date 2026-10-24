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
    };

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
    /// Claw Series swipes alternate left / right; this flips on every Harm hit.
    /// </summary>
    [ViewVariables]
    public bool SwipeFlipped;

    /// <summary>
    /// How much stamina was actually added, so exactly that is removed again when the art is lost.
    /// </summary>
    [ViewVariables]
    public float AppliedBonusStamina;

    // TODO ProperKungFu ultimate: show this as a red 0-100 bar with a percentage above the hotbar (client UI).
    // Spec: +1 per point of damage taken, +5 per combo, +5 per special move hit (already wired via ProperKungFuSpecialHitEvent).
    // When the owner enters crit with a full bar: after 5 s fully heal, stand up, and enable the ultimate mode
    // for 5 minutes (swap BaseActions for UltimateActions), then reset the bar to 0.

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
    /// Action prototypes that replace <see cref="BaseActions"/> during the ultimate (not implemented yet).
    /// </summary>
    [DataField]
    public List<EntProtoId> UltimateActions = new();

    [DataField]
    public TimeSpan UltimateDuration = TimeSpan.FromMinutes(5);

    [DataField]
    public TimeSpan UltimateActivationDelay = TimeSpan.FromSeconds(5);
}
