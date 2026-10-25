using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// On the user during the Hammer finisher: four strikes in a row, the victim stunned and unable to escape.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuHammerFinisherComponent : Component
{
    [ViewVariables]
    public EntityUid Victim;

    [ViewVariables]
    public MapId Map;

    /// <summary>
    /// From the user to the victim at the start; every strike pushes along it.
    /// </summary>
    [ViewVariables]
    public Vector2 Direction;

    [ViewVariables]
    public TimeSpan StartTime;

    /// <summary>
    /// 1 = the first strike landed, 2 = straight strike, 3 = flip started, 4 = flip strike,
    /// 5 = spinning strike (the victim is flung), 6 = dash at the landed victim (the cross slash follows).
    /// </summary>
    [ViewVariables]
    public int Step;

    /// <summary>
    /// Steps 5 and 6: when the next one starts (the victim landed / the dash arrived).
    /// </summary>
    [ViewVariables]
    public TimeSpan NextAt;

    [ViewVariables]
    public HammerActionEvent? Event;
}
