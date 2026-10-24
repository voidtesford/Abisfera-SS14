using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// On the user during the Wolf Instinct finisher: a series of claw slashes on a locked victim while both travel
/// forward together, closed by a cross claw strike that throws the victim back.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuInstinctFinisherComponent : Component
{
    [ViewVariables]
    public EntityUid Victim;

    [ViewVariables]
    public MapId Map;

    /// <summary>
    /// Victim position at the start, world.
    /// </summary>
    [ViewVariables]
    public Vector2 VictimFrom;

    /// <summary>
    /// Direction user -> victim.
    /// </summary>
    [ViewVariables]
    public Vector2 Direction;

    /// <summary>
    /// User position relative to the victim, kept the whole sequence.
    /// </summary>
    [ViewVariables]
    public Vector2 UserOffset;

    [ViewVariables]
    public TimeSpan StartTime;

    /// <summary>
    /// When the cross strike lands (end of the travel).
    /// </summary>
    [ViewVariables]
    public TimeSpan EndTime;

    [ViewVariables]
    public int SlashesDone;

    [ViewVariables]
    public WolfInstinctActionEvent? Event;
}
