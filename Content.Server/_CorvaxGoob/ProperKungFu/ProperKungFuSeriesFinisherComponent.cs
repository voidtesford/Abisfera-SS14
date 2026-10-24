using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

public enum ClawSeriesFinisherPhase : byte
{
    /// <summary>The normal Claw Series slide back is still playing.</summary>
    Wait,
    /// <summary>The burgundy double stands behind the target and winds up.</summary>
    Clone,
    /// <summary>The double's strike carries the target back to the user.</summary>
    Push,
    /// <summary>Ten fast claw strikes from the user.</summary>
    Strikes,
}

/// <summary>
/// On the user during the Claw Series finisher. The victim stays stunned and locked in place for the whole sequence.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuSeriesFinisherComponent : Component
{
    [ViewVariables]
    public EntityUid Victim;

    [ViewVariables]
    public MapId Map;

    [ViewVariables]
    public ClawSeriesFinisherPhase Phase;

    [ViewVariables]
    public TimeSpan PhaseStart;

    /// <summary>
    /// Direction from the user to the victim when the combo landed, world.
    /// </summary>
    [ViewVariables]
    public Vector2 Direction;

    /// <summary>
    /// Where the victim is held during the strikes, world.
    /// </summary>
    [ViewVariables]
    public Vector2 Hold;

    [ViewVariables]
    public EntityUid? Clone;

    [ViewVariables]
    public int Strikes;

    [ViewVariables]
    public TimeSpan NextStrike;
}
