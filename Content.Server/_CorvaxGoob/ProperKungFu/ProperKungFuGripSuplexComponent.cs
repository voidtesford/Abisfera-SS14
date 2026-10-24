using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// On the victim of the Wolf Grip finisher: held in the grab, then carried over the user's back and slammed
/// head first into the floor behind them.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuGripSuplexComponent : Component
{
    [ViewVariables]
    public EntityUid User;

    [ViewVariables]
    public MapId Map;

    [ViewVariables]
    public ProperKungFuWolfGripPerformedEvent? Event;

    /// <summary>
    /// When the lift starts (after the normal grab).
    /// </summary>
    [ViewVariables]
    public TimeSpan StartTime;

    [ViewVariables]
    public bool Lifted;

    [ViewVariables]
    public Vector2 From;

    [ViewVariables]
    public Vector2 To;
}
