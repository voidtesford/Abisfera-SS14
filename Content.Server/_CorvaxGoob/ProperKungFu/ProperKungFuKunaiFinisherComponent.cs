using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

public enum KunaiFinisherKind : byte
{
    /// <summary>
    /// Kunai hit in melee: kick, neck slash, then the one-inch death punch.
    /// </summary>
    Melee,

    /// <summary>
    /// Thrown kunai hit: ten crimson doubles throw at the victim, then a volley of ten kunai from behind the user.
    /// </summary>
    Thrown,
}

/// <summary>
/// On the user while a kunai finisher plays out.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuKunaiFinisherComponent : Component
{
    [ViewVariables]
    public KunaiFinisherKind Kind;

    [ViewVariables]
    public EntityUid Victim;

    [ViewVariables]
    public MapId Map;

    /// <summary>
    /// From the user to the victim at the start.
    /// </summary>
    [ViewVariables]
    public Vector2 Direction;

    [ViewVariables]
    public int Phase;

    /// <summary>
    /// How many steps of the current phase are done (doubles or volley kunai).
    /// </summary>
    [ViewVariables]
    public int Count;

    [ViewVariables]
    public TimeSpan NextStep;

    /// <summary>
    /// Safety: the sequence is dropped after this time whatever happens.
    /// </summary>
    [ViewVariables]
    public TimeSpan EndBy;
}

public enum FlyingKunaiKind : byte
{
    /// <summary>
    /// Melee finisher kunai 1: neck slash, all blood lost.
    /// </summary>
    Neck,

    /// <summary>
    /// Melee finisher kunai 2: the user follows it and lands the one-inch punch.
    /// </summary>
    Punch,

    /// <summary>
    /// A plain hit with the pierce effect (doubles' kunai and the volley).
    /// </summary>
    Pierce,
}

/// <summary>
/// A purely visual kunai flying straight at a victim; its effect lands on arrival.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuFlyingKunaiComponent : Component
{
    [ViewVariables]
    public EntityUid User;

    [ViewVariables]
    public EntityUid Target;

    [ViewVariables]
    public FlyingKunaiKind Kind;

    [ViewVariables]
    public float Speed = 20f;

    [ViewVariables]
    public float Damage = 1f;

    [ViewVariables]
    public TimeSpan ExpiresAt;
}

/// <summary>
/// The one-inch death punch's breathing block: removed (with the block itself) when the victim dies.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuDeathPunchBreathComponent : Component
{
    [ViewVariables]
    public TimeSpan ExpiresAt;
}
