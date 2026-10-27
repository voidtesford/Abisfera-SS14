using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._CorvaxGoob.Redspace;

/// <summary>
/// A flatpack that, when used with a tool of <see cref="QualityNeeded"/> (multitool by default),
/// is replaced in place by <see cref="Unfolding"/> - a dedicated entity that plays the long unfold
/// animation and only then turns into the gate. Unlike the stock flatpack this is not instant.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class RedspaceGateFlatpackComponent : Component
{
    /// <summary>
    /// Entity spawned in place of the flatpack; it carries a <see cref="RedspaceGateUnfoldingComponent"/>.
    /// </summary>
    [DataField(required: true)]
    public EntProtoId Unfolding;

    /// <summary>
    /// Tool quality required to start unfolding.
    /// </summary>
    [DataField]
    public string QualityNeeded = "Pulsing";
}
