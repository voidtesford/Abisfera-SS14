using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Present on an entity while it performs the Wolf Dance movement (a straight flying kick).
/// Its presence is networked so the client also blocks movement input (no prediction fight).
/// The movement state itself is only used by the server.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class WolfDancingComponent : Component
{
    [ViewVariables]
    public EntityUid Target;

    /// <summary>
    /// Whirlwind variant: the target was hit by Claw Whirlwind, so it's the hard kick and a longer knockback.
    /// </summary>
    [ViewVariables]
    public bool WhirlwindVariant;

    /// <summary>
    /// Plain variant (no synergy): a dash through the target, no collision with it, the target is tossed up in place.
    /// </summary>
    [ViewVariables]
    public bool PassThrough;

    /// <summary>
    /// Plain variant: the target was already hit while passing through it.
    /// </summary>
    [ViewVariables]
    public bool PassHit;

    /// <summary>
    /// Plain variant: the target had enough damage before the dance for the finisher.
    /// </summary>
    [ViewVariables]
    public bool FinisherPending;

    /// <summary>
    /// Plain variant: the pass hit landed (not blocked by a shield).
    /// </summary>
    [ViewVariables]
    public bool PassLanded;

    [ViewVariables]
    public WolfDancePhase Phase = WolfDancePhase.Dash;

    [ViewVariables]
    public TimeSpan PhaseStart;

    [ViewVariables]
    public TimeSpan PhaseDuration;

    [ViewVariables]
    public Vector2 PhaseFrom;

    [ViewVariables]
    public Vector2 PhaseTo;

    /// <summary>
    /// Maximum length of the dash phase, so the user can't chase a target across the map.
    /// </summary>
    [ViewVariables]
    public float MaxDashLength;

    [ViewVariables]
    public MapId Map;

    [ViewVariables]
    public TimeSpan NextAfterimage;

    [ViewVariables]
    public int AfterimagesSpawned;

    /// <summary>
    /// Parameters of the action that started this move.
    /// </summary>
    [ViewVariables]
    public WolfDanceActionEvent? Event;
}

[Serializable, NetSerializable]
public enum WolfDancePhase : byte
{
    /// <summary>Straight flying kick, feet first, into the target.</summary>
    Dash,
    /// <summary>The kick connects (or is blocked), then the user drops to the floor.</summary>
    Kick,
    /// <summary>The move was interrupted or missed, reset the visuals.</summary>
    Cancel,
    /// <summary>Plain variant: a fast upright dash through the target.</summary>
    PassThrough,
}

/// <summary>
/// Plain Wolf Dance: the target is tossed straight up with a flip and lands on the same spot.
/// </summary>
[Serializable, NetSerializable]
public sealed class WolfDanceTossAnimationEvent : EntityEventArgs
{
    public NetEntity Target;
    public float Duration;
    public float Height;
    public float Spins;
    public Vector2 Direction;

    public WolfDanceTossAnimationEvent(NetEntity target, float duration, float height, float spins, Vector2 direction)
    {
        Target = target;
        Duration = duration;
        Height = height;
        Spins = spins;
        Direction = direction;
    }
}

/// <summary>
/// Sent by the server to clients in PVS so they play the sprite animation of the current Wolf Dance phase.
/// </summary>
[Serializable, NetSerializable]
public sealed class WolfDanceAnimationEvent : EntityEventArgs
{
    public NetEntity User;
    public WolfDancePhase Phase;
    public float Duration;
    public Vector2 Direction;

    public WolfDanceAnimationEvent(NetEntity user, WolfDancePhase phase, float duration, Vector2 direction)
    {
        User = user;
        Phase = phase;
        Duration = duration;
        Direction = direction;
    }
}
