using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Result event of the "Wolf Grip" combo (Harm, Disarm, Grab): Slash damage, the target can't breathe for a while
/// and the user takes it into a hard grab, holding it in front of themselves.
/// The damage amount and type come from the combo prototype (extraDamage / damageType).
/// </summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class ProperKungFuWolfGripPerformedEvent : EntityEventArgs
{
    /// <summary>
    /// How long the target can't breathe.
    /// </summary>
    [DataField]
    public TimeSpan BreathBlockTime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a Claw Whirlwind from the same user turns into the two-handed cross strike.
    /// </summary>
    [DataField]
    public TimeSpan MarkDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Distance (tiles, center to center) the grabbed target is pulled in front of the user.
    /// </summary>
    [DataField]
    public float HoldDistance = 0.6f;
}

/// <summary>
/// Left by the "Wolf Grip" combo. While it lasts, a Claw Whirlwind from the same user becomes the
/// two-handed cross strike and consumes the mark.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuWolfGripMarkComponent : Component
{
    [ViewVariables]
    public EntityUid Marker;

    [ViewVariables]
    public TimeSpan ExpiresAt;
}
