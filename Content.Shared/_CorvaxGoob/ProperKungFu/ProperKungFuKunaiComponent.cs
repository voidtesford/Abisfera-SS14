using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// A kunai summoned by the Proper Kung Fu "Kunai" action. When its summoner throws it, the summoner teleports to where
/// it lands or sticks, and the kunai disappears. Handled by the server ProperKungFuSystem.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ProperKungFuKunaiComponent : Component
{
    [ViewVariables]
    public EntityUid? Summoner;

    [ViewVariables]
    public TimeSpan ExpiresAt;

    /// <summary>
    /// Where the summoner stood when it threw the kunai; null while it was not thrown by the summoner.
    /// </summary>
    [ViewVariables]
    public MapCoordinates? ThrowOrigin;

    /// <summary>
    /// Set when the kunai stopped or stuck; the teleport happens on the next update.
    /// </summary>
    [ViewVariables]
    public bool Arrived;

    [ViewVariables]
    public bool Embedded;

    [DataField]
    public string SmokeEffect = "ProperKungFuWolfPackSmoke";
}

[Serializable, NetSerializable]
public enum ProperKungFuKunaiVisuals : byte
{
    State,
}

[Serializable, NetSerializable]
public enum ProperKungFuKunaiState : byte
{
    Idle,
    Thrown,
    Embedded,
}
