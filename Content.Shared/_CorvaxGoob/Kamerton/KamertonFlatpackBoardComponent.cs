using Content.Shared.Materials;
using Robust.Shared.Prototypes;

namespace Content.Shared._CorvaxGoob.Kamerton;

/// <summary>
/// A circuit board that the flatpacker («упаковщик 1001») turns into a ready Kamerton flatpack.
/// It is not a machine board, so a machine frame will not take it: these parts can only arrive as flatpacks.
/// </summary>
[RegisterComponent]
public sealed partial class KamertonFlatpackBoardComponent : Component
{
    /// <summary>The finished flatpack that comes out of the flatpacker.</summary>
    [DataField(required: true)]
    public EntProtoId Flatpack;

    /// <summary>What the flatpack unpacks into, shown in the flatpacker window.</summary>
    [DataField(required: true)]
    public EntProtoId Entity;

    /// <summary>Material units the flatpacker takes for this board (on top of nothing else).</summary>
    [DataField(required: true)]
    public Dictionary<ProtoId<MaterialPrototype>, int> Cost = new();
}
