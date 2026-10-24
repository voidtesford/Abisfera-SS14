using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// A target being knocked back by Proper Kung Fu (moved kinematically by ProperKungFuSystem).
/// The Claw Series finisher slides it back on its feet with white / red dust puffs (<see cref="Dust"/>).
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuSlidingComponent : Component
{
    [ViewVariables]
    public MapId Map;

    [ViewVariables]
    public Vector2 From;

    [ViewVariables]
    public Vector2 To;

    [ViewVariables]
    public TimeSpan StartTime;

    [ViewVariables]
    public TimeSpan EndTime;

    [ViewVariables]
    public bool Dust;

    [ViewVariables]
    public TimeSpan NextPuff;

    [ViewVariables]
    public int Puffs;
}
