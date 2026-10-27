using System.Numerics;

namespace Content.Server._CorvaxGoob.Redspace;

/// <summary>
/// A coordinates disk (the same CoordinatesDisk the ftldisk command makes) that also stores exact target
/// coordinates for a Euclidean gate. Printed by a gate with the <c>ftldiskgate</c> admin command.
/// While it is inserted, the gate always opens its exit portal at <see cref="Position"/> on <see cref="Map"/>,
/// whatever was typed in the console.
/// </summary>
[RegisterComponent, Access(typeof(RedspaceGateSystem))]
public sealed partial class RedspaceGateDiskComponent : Component
{
    /// <summary>Target map entity (also written to the disk's ShuttleDestinationCoordinates).</summary>
    [DataField]
    public EntityUid? Map;

    /// <summary>Exact world position of the exit portal on <see cref="Map"/>.</summary>
    [DataField]
    public Vector2 Position;
}
