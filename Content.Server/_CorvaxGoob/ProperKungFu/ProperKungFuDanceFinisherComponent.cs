using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// On the user during the plain Wolf Dance finisher: fast dashes through a locked target, each one turned
/// 90 degrees from the previous, then a final strike that throws the target back.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuDanceFinisherComponent : Component
{
    [ViewVariables]
    public EntityUid Victim;

    [ViewVariables]
    public MapId Map;

    /// <summary>
    /// Where the victim is held, world.
    /// </summary>
    [ViewVariables]
    public Vector2 Center;

    /// <summary>
    /// Exit direction of the last dash.
    /// </summary>
    [ViewVariables]
    public Vector2 LastDirection;

    /// <summary>
    /// Current dash: start, the victim (through), end.
    /// </summary>
    [ViewVariables]
    public Vector2 PassFrom;

    [ViewVariables]
    public Vector2 PassTo;

    [ViewVariables]
    public TimeSpan PassStart;

    [ViewVariables]
    public bool PassHit;

    [ViewVariables]
    public int PassesDone;

    /// <summary>
    /// 1 or -1: which way the dashes turn.
    /// </summary>
    [ViewVariables]
    public float Turn = 1f;

    [ViewVariables]
    public WolfDanceActionEvent? Event;
}
