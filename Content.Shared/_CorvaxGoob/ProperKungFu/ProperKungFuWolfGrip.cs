using System.Numerics;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
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

    // Finisher: a target that already had FinisherThreshold damage before the combo is lifted over the user's back
    // and driven head first into the floor behind them (a bridge suplex).

    /// <summary>
    /// Total damage the target must already have before the combo. 0 disables the finisher.
    /// </summary>
    [DataField]
    public float FinisherThreshold = 80f;

    /// <summary>
    /// The normal hard grab is held this long before the suplex starts.
    /// </summary>
    [DataField]
    public TimeSpan FinisherDelay = TimeSpan.FromSeconds(0.25);

    /// <summary>
    /// How long the target flies over the user's back.
    /// </summary>
    [DataField]
    public TimeSpan FinisherDuration = TimeSpan.FromSeconds(0.55);

    /// <summary>
    /// Peak height of the arc (sprite offset, tiles).
    /// </summary>
    [DataField]
    public float FinisherHeight = 0.9f;

    /// <summary>
    /// Where the target lands behind the user (tiles, center to center).
    /// </summary>
    [DataField]
    public float FinisherBehindDistance = 1f;

    [DataField]
    public DamageSpecifier FinisherHeadDamage = new() { DamageDict = { ["Blunt"] = 25 } };

    [DataField]
    public DamageSpecifier FinisherTorsoDamage = new() { DamageDict = { ["Blunt"] = 50 } };

    [DataField]
    public TimeSpan FinisherKnockdownTime = TimeSpan.FromSeconds(4);

    [DataField]
    public List<EntProtoId> FinisherImpactEffects = new();

    [DataField]
    public SoundSpecifier? FinisherSound;

    [DataField]
    public SoundSpecifier? FinisherLiftSound;
}

/// <summary>
/// Sent to clients in PVS so they animate the Wolf Grip suplex: the target arcs over the user's back with a flip
/// and lands head first behind them. The movement itself is done by the server.
/// </summary>
[Serializable, NetSerializable]
public sealed class ProperKungFuGripSuplexAnimationEvent : EntityEventArgs
{
    public NetEntity Target;
    public float Duration;
    public float Height;
    public Vector2 Direction;

    public ProperKungFuGripSuplexAnimationEvent(NetEntity target, float duration, float height, Vector2 direction)
    {
        Target = target;
        Duration = duration;
        Height = height;
        Direction = direction;
    }
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
