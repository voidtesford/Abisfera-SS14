using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Wolf Instinct: the user is dashing at a shooter (moved kinematically by ProperKungFuSystem).
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuInstinctDashComponent : Component
{
    [ViewVariables]
    public EntityUid Shooter;

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
    public TimeSpan NextAfterimage;

    [ViewVariables]
    public WolfInstinctActionEvent? Event;
}
