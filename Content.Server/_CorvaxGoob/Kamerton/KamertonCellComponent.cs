using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Kamerton;

public enum KamertonCellKind : byte
{
    /// <summary>Super-energy cell, charged from stable energy (bluespace).</summary>
    Stable,
    /// <summary>Unstable super-energy cell, charged from unstable energy (redspace).</summary>
    Unstable,
}

/// <summary>
/// A Kamerton fuel cell. Charged and empty cells are separate prototypes that point at each other,
/// so charging/draining swaps the entity (only charged stable cells carry <c>RedspaceFuelCell</c>,
/// which keeps the suppressor beacon picky).
/// </summary>
[RegisterComponent]
public sealed partial class KamertonCellComponent : Component
{
    [DataField(required: true)]
    public KamertonCellKind Kind;

    [DataField]
    public bool Charged = true;

    /// <summary>Prototype this cell turns into when charged (empty cell) or drained (charged cell).</summary>
    [DataField(required: true)]
    public EntProtoId Swap;

    /// <summary>Seconds of reactor work left in a charged cell; -1 = untouched (full lifetime).</summary>
    [DataField]
    public float Remaining = -1f;
}
