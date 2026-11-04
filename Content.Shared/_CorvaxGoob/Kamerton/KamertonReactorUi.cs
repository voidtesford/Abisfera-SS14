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
    /// <summary>int 0..15: like <see cref="Links"/>, but for heat-pipe joints (radiator-radiator, radiator-heat sink).</summary>
    HeatLinks,
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
    float temperature,
    float collapseTemperature,
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
    public readonly float Temperature = temperature;
    public readonly float CollapseTemperature = collapseTemperature;
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
}

[Serializable, NetSerializable]
public sealed class KamertonReactorStartMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class KamertonReactorStopMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class KamertonReactorScramMessage : BoundUserInterfaceMessage;
