using System.Numerics;
using Robust.Shared.GameStates;

namespace Content.Shared._CorvaxGoob.Redspace.Physics;

/// <summary>
/// Put by the server on mobs that redspace is acting on right now (unsuppressed, near a player).
/// Holds only what prediction needs; the server dirties it only when a value changes.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class RedspaceAffectedComponent : Component
{
    [DataField, AutoNetworkedField]
    public float Friction = 1f;

    [DataField, AutoNetworkedField]
    public float Acceleration = 1f;

    /// <summary>Movement speed multiplier (anomaly patches).</summary>
    [DataField, AutoNetworkedField]
    public float Speed = 1f;

    /// <summary>Drift of the gravity current, m/s².</summary>
    [DataField, AutoNetworkedField]
    public Vector2 Drift;

    [DataField, AutoNetworkedField]
    public bool Weightless;

    [DataField, AutoNetworkedField]
    public float DoAfter = 1f;

    // Server-side timers (seconds left), not networked.
    [ViewVariables] public float DamageAccumulator;
    [ViewVariables] public float NextHallucination = -1f;
    [ViewVariables] public float NextEcho = -1f;
    [ViewVariables] public float NextDischarge = -1f;
    [ViewVariables] public float NextFlicker = -1f;
    [ViewVariables] public float NextStatic = -1f;
    [ViewVariables] public EntityUid? Ambient;
}

/// <summary>A thrown item sliding on a redspace map: its tile friction is multiplied.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class RedspaceSlideComponent : Component
{
    [DataField, AutoNetworkedField]
    public float Friction = 0.15f;
}

/// <summary>Translucent shadow that replays its source's movement a few seconds late. The client copies the source's sprite.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class RedspaceEchoComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntityUid? Source;
}
