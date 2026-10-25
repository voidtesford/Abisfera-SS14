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
    /// Thrown kunai hit: ten crimson doubles throw at the victim and vanish, the user dashes through the victim with
    /// a long kunai slash, turns and backflips while a volley of ten kunai flies from behind them.
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
    /// Thrown finisher: the crimson doubles still waiting to throw, in throw order.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> Doubles = new();

    /// <summary>
    /// Thrown finisher: the direction of the dash through the victim.
    /// </summary>
    [ViewVariables]
    public Vector2 DashDirection;

    /// <summary>
    /// Thrown finisher: the user went through the victim and stands behind it.
    /// </summary>
    [ViewVariables]
    public bool Dashed;

    /// <summary>
    /// Safety: the sequence is dropped after this time whatever happens.
    /// </summary>
    [ViewVariables]
    public TimeSpan EndBy;

    /// <summary>
    /// Melee finisher: the time stop zone around the death punch.
    /// </summary>
    [ViewVariables]
    public EntityUid? Zone;

    /// <summary>
    /// Melee finisher: time starts again (the rest of the knockback follows).
    /// </summary>
    [ViewVariables]
    public TimeSpan ZoneEnd;

    /// <summary>
    /// Melee finisher: the victim's flight inside the stopped time.
    /// </summary>
    [ViewVariables]
    public Vector2 FlightFrom;

    [ViewVariables]
    public Vector2 FlightTo;

    [ViewVariables]
    public TimeSpan FlightStart;

    [ViewVariables]
    public TimeSpan NextTrail;

    [ViewVariables]
    public int Trails;
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
