using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.Kamerton;

/// <summary>What a piece of the Kamerton reactor is. The core is the anchor the layout is read around.</summary>
[Serializable, NetSerializable]
public enum KamertonPartKind : byte
{
    Core,
    StableArray,
    UnstableArray,
    Synchronizer,
    HeatSink,
    Radiator,
    Suppressor,
    Console,
    PowerUnit,
    FuelStorage,
    /// <summary>SZK, the Kamerton refuelling system: a shell part that lays a rail lattice over the reactor.</summary>
    Szk,
}

[Serializable, NetSerializable]
public enum KamertonReactorUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum KamertonReactorVisuals : byte
{
    /// <summary>bool: the part is working.</summary>
    On,
    /// <summary>int 0..3: core heat glow / radiator heat.</summary>
    Heat,
    /// <summary>int 0..4: cells in a fuel array.</summary>
    Cells,
    /// <summary>int 0..255: world-space neighbours in the same reactor node group (N=1, S=2, E=4, W=8, NE=16, NW=32, SE=64, SW=128), for hull smoothing.</summary>
    Links,
    /// <summary>int 0..255: like <see cref="Links"/>, but for heat-pipe joints; bits 0-3 radiator-radiator and
    /// radiator-heat sink, bits 4-7 heat sink to heat sink (sink chains).</summary>
    HeatLinks,
    /// <summary>int 0..5: charged stable cells in a fuel storage, in steps of two.</summary>
    StorageStable,
    /// <summary>int 0..5: charged unstable cells in a fuel storage, in steps of two.</summary>
    StorageUnstable,
    /// <summary>int 0..15: rail neighbours of an SZK rail tile (N=1, S=2, E=4, W=8).</summary>
    Rails,
    /// <summary>int <see cref="KamertonSzkCarriageLook"/>: what the SZK carriage carries.</summary>
    Carriage,
}

[Serializable, NetSerializable]
public enum KamertonReactorVisualLayers : byte
{
    On,
    Heat,
    Cells,
    HullNE,
    HullSE,
    HullSW,
    HullNW,
    HeatLink,
    StorageStable,
    StorageUnstable,
    /// <summary>Joints between chained heat sinks (<c>sinklink_0</c>..<c>sinklink_15</c>).</summary>
    SinkLink,
    /// <summary>Rail lattice tile / carriage load.</summary>
    Rails,
    Load,
}

/// <summary>
/// AME-style hull smoothing of a Kamerton part: four corner layers (<c>hull_ne_0</c>..<c>hull_nw_7</c>) picked
/// from <see cref="KamertonReactorVisuals.Links"/>, plus the heat-pipe channels (<c>heat_0</c>..<c>heat_15</c>)
/// from <see cref="KamertonReactorVisuals.HeatLinks"/>. The part's own look sits on other layers on top.
/// </summary>
[RegisterComponent]
public sealed partial class KamertonLinkVisualsComponent : Component
{
    [DataField]
    public string HullStatePrefix = "hull_";

    [DataField]
    public string HeatStatePrefix = "heat_";

    [DataField]
    public string SinkStatePrefix = "sinklink_";
}

/// <summary>One fuel array as the console shows it.</summary>
[Serializable, NetSerializable]
public sealed class KamertonArrayInfo(bool stable, int charged, int empty, int slots, float minRemaining)
{
    public readonly bool Stable = stable;
    public readonly int Charged = charged;
    public readonly int Empty = empty;
    public readonly int Slots = slots;
    /// <summary>Seconds left on the most drained charged cell, -1 when none.</summary>
    public readonly float MinRemaining = minRemaining;
}

/// <summary>One heat sink as the console shows it.</summary>
[Serializable, NetSerializable]
public sealed class KamertonHeatSinkInfo(float flowMoles, float inletTemperature, float outletTemperature, float heatRemoved)
{
    /// <summary>Gas moved through the sink last second, mol/s.</summary>
    public readonly float FlowMoles = flowMoles;
    public readonly float InletTemperature = inletTemperature;
    public readonly float OutletTemperature = outletTemperature;
    /// <summary>Heat taken out of the core last second, W.</summary>
    public readonly float HeatRemoved = heatRemoved;
}

[Serializable, NetSerializable]
public sealed class KamertonReactorBoundUserInterfaceState(
    bool linked,
    bool assembled,
    bool running,
    float scramCooldown,
    float heat,
    float maxHeat,
    float totalPower,
    float maxPower,
    int activePairs,
    int synchronizers,
    bool ringComplete,
    int ringMissing,
    bool powerUnitLinked,
    float powerSupplied,
    List<KamertonArrayInfo> arrays,
    List<KamertonHeatSinkInfo> heatSinks,
    int radiators) : BoundUserInterfaceState
{
    public readonly bool Linked = linked;
    public readonly bool Assembled = assembled;
    public readonly bool Running = running;
    public readonly float ScramCooldown = scramCooldown;
    /// <summary>Core heat, 0 = room temperature.</summary>
    public readonly float Heat = heat;
    /// <summary>Heat limit: the reactor collapses on reaching it.</summary>
    public readonly float MaxHeat = maxHeat;
    public readonly float TotalPower = totalPower;
    public readonly float MaxPower = maxPower;
    public readonly int ActivePairs = activePairs;
    public readonly int Synchronizers = synchronizers;
    public readonly bool RingComplete = ringComplete;
    public readonly int RingMissing = ringMissing;
    public readonly bool PowerUnitLinked = powerUnitLinked;
    public readonly float PowerSupplied = powerSupplied;
    public readonly List<KamertonArrayInfo> Arrays = arrays;
    public readonly List<KamertonHeatSinkInfo> HeatSinks = heatSinks;
    public readonly int Radiators = radiators;

    /// <summary>Fuel storage and SZK section of the console.</summary>
    public KamertonSzkInfo Szk = new();

    /// <summary>The core tile is in vacuum: the reactor is stopped and will not start.</summary>
    public bool CoreInVacuum;
}

[Serializable, NetSerializable]
public enum KamertonSzkState : byte
{
    /// <summary>Carriage parked at the fuel storage, nothing to do.</summary>
    Idle,
    /// <summary>Running a Load or Unload job.</summary>
    Working,
    /// <summary>Stopped: the carriage puts back what it holds and parks at the storage.</summary>
    Parking,
}

[Serializable, NetSerializable]
public enum KamertonSzkCommand : byte
{
    /// <summary>Fill free array slots with charged cells from the storage.</summary>
    Load,
    /// <summary>Take spent cells out of the arrays.</summary>
    Unload,
    /// <summary>Drop the job, switch auto mode off and park at the storage.</summary>
    Stop,
    ToggleAuto,
}

/// <summary>What the SZK carriage is carrying, for its sprite.</summary>
[Serializable, NetSerializable]
public enum KamertonSzkCarriageLook : byte
{
    Empty,
    Stable,
    Unstable,
    Spent,
}

/// <summary>Console view of the fuel storage and the SZK.</summary>
[Serializable, NetSerializable]
public sealed class KamertonSzkInfo
{
    public bool HasSzk;
    /// <summary>The rail lattice is laid over the reactor.</summary>
    public bool Deployed;
    public KamertonSzkState State;
    public KamertonSzkCommand? Task;
    public bool Auto;
    /// <summary>Carriage tile relative to the core.</summary>
    public int CarriageX;
    public int CarriageY;
    /// <summary>Carriage cargo.</summary>
    public KamertonSzkCarriageLook Holding;
    public bool HasStorage;
    public int StorageStable;
    public int StorageUnstable;
    public int StorageCapacity;
    /// <summary>Free array slots by cell kind.</summary>
    public int FreeStable;
    public int FreeUnstable;
    /// <summary>Spent (empty) cells still sitting in arrays.</summary>
    public int Spent;
}

[Serializable, NetSerializable]
public sealed class KamertonSzkCommandMessage(KamertonSzkCommand command) : BoundUserInterfaceMessage
{
    public readonly KamertonSzkCommand Command = command;
}

[Serializable, NetSerializable]
public sealed class KamertonReactorStartMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class KamertonReactorStopMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class KamertonReactorScramMessage : BoundUserInterfaceMessage;
