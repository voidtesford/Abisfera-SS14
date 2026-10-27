using Content.Shared.Stacks;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._CorvaxGoob.Redspace;

/// <summary>
/// A fuelled gate that opens a two-way portal to arbitrary X,Y coordinates.
/// Coordinates are on the gate's own map, unless a coordinates disk (ftldisk) is inserted,
/// in which case the disk's map is used.
/// Based on the exploration <see cref="Content.Server.Gateway.Components.GatewayComponent"/>.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class RedspaceGateComponent : Component
{
    /// <summary>Exit portal spawned at the target coordinates.</summary>
    [DataField]
    public EntProtoId PortalPrototype = "RedspacePortal";

    /// <summary>Item slot that accepts coordinate disks.</summary>
    [DataField]
    public string DiskSlotId = "disk_slot";

    // --- fuel ---

    /// <summary>Redspace crystals are loaded into the gate itself (crimson sockets on the frame).</summary>
    [DataField]
    public int RedspaceCapacity = 24;

    /// <summary>Bluespace crystals are loaded into the storage rack at the bottom-right of the gate.</summary>
    [DataField]
    public int BluespaceCapacity = 50;

    /// <summary>One more socketed crimson crystal is drawn on the frame per this many redspace crystals.</summary>
    [DataField]
    public int RedspacePerSocket = 2;

    /// <summary>One more socketed crystal is drawn in the rack per this many bluespace crystals.</summary>
    [DataField]
    public int BluespacePerSlot = 5;

    [DataField]
    public List<ProtoId<StackPrototype>> RedspaceStacks = new() { "RedspaceCrystal" };

    [DataField]
    public List<ProtoId<StackPrototype>> BluespaceStacks = new() { "BSCrystal" };

    /// <summary>How long one redspace crystal keeps the portal open.</summary>
    [DataField]
    public TimeSpan RedspaceBurnTime = TimeSpan.FromMinutes(10);

    /// <summary>How long one bluespace crystal keeps the portal open.</summary>
    [DataField]
    public TimeSpan BluespaceBurnTime = TimeSpan.FromMinutes(1);

    [DataField]
    public int Redspace;

    [DataField]
    public int Bluespace;

    // --- portal ---

    /// <summary>Absolute limit for entered coordinates.</summary>
    [DataField]
    public float MaxCoordinate = 100000f;

    /// <summary>Minimum distance between gate and exit portal on the same map.</summary>
    [DataField]
    public float MinDistance = 2f;

    [ViewVariables]
    public EntityUid? Portal;

    [ViewVariables]
    public MapCoordinates? Target;

    /// <summary>Last coordinates entered, shown again in the UI.</summary>
    [ViewVariables]
    public System.Numerics.Vector2? LastTarget;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextConsume;

    /// <summary>Crackle interval while idle (portal closed): arcs along the frame, about as often as a crystal.</summary>
    [DataField]
    public TimeSpan IdleCrackleMin = TimeSpan.FromSeconds(4);

    [DataField]
    public TimeSpan IdleCrackleMax = TimeSpan.FromSeconds(9);

    /// <summary>Crackle interval while the portal is open: around the ring, stronger and more often.</summary>
    [DataField]
    public TimeSpan OpenCrackleMin = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan OpenCrackleMax = TimeSpan.FromSeconds(5);

    [DataField]
    public SoundSpecifier OpenSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningbolt.ogg");

    [DataField]
    public SoundSpecifier CloseSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningbolt.ogg");

    [DataField]
    public SoundSpecifier DenySound = new SoundPathSpecifier("/Audio/Machines/custom_deny.ogg");

    [DataField]
    public SoundSpecifier FuelSound = new SoundPathSpecifier("/Audio/Machines/ame_fuelinjection.ogg");

    // --- disk printing (ftldiskgate) ---

    /// <summary>Disk printed by the ftldiskgate command: the same disk the ftldisk command makes.</summary>
    [DataField]
    public EntProtoId DiskPrototype = "CoordinatesDisk";

    [DataField]
    public SoundSpecifier PrintSound = new SoundPathSpecifier("/Audio/Machines/printer.ogg");
}

/// <summary>
/// Marks the exit portal spawned by a <see cref="RedspaceGateComponent"/>.
/// </summary>
[RegisterComponent, Access(typeof(RedspaceGateSystem))]
public sealed partial class RedspaceGatePortalComponent : Component
{
    [ViewVariables]
    public EntityUid? Gate;
}
