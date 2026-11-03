using Content.Shared._CorvaxGoob.Kamerton;
using Content.Shared.Damage;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Any piece of the Kamerton reactor. The core re-reads the layout around itself every second and
/// writes itself into <see cref="Core"/> of every part it found.
/// </summary>
[RegisterComponent, Access(typeof(KamertonReactorSystem))]
public sealed partial class KamertonReactorPartComponent : Component
{
    [DataField(required: true)]
    public KamertonPartKind Kind;

    /// <summary>The core this part is assembled into.</summary>
    [ViewVariables]
    public EntityUid? Core;
}

/// <summary>Fuel array: holds cells in item slots <c>cell_1..cell_N</c>.</summary>
[RegisterComponent]
public sealed partial class KamertonFuelArrayComponent : Component
{
    [DataField]
    public KamertonCellKind Kind = KamertonCellKind.Stable;

    [DataField]
    public int Slots = 4;

    /// <summary>Seconds a charged cell lasts while it is in an active pair.</summary>
    [DataField]
    public float CellLifetime = 1800f;

    /// <summary>Synchronizer that claimed this array on the last scan.</summary>
    [ViewVariables]
    public EntityUid? Synchronizer;
}

/// <summary>Corner heat sink: pumps gas inlet -> outlet and hands core heat to it.</summary>
[RegisterComponent]
public sealed partial class KamertonHeatSinkComponent : Component
{
    [DataField]
    public string InletName = "inlet";

    [DataField]
    public string OutletName = "outlet";

    /// <summary>Litres of inlet gas moved per second.</summary>
    [DataField]
    public float TransferRate = 100f;

    /// <summary>Fraction of the core/gas temperature gap the passing gas closes.</summary>
    [DataField]
    public float Efficiency = 0.5f;

    [ViewVariables] public float LastFlow;
    [ViewVariables] public float LastInletTemperature;
    [ViewVariables] public float LastOutletTemperature;
    [ViewVariables] public float LastHeatRemoved;
}

/// <summary>Radiator: extra heat capacity for the core and a slow heat leak into its pipe.</summary>
[RegisterComponent]
public sealed partial class KamertonRadiatorComponent : Component
{
    [DataField]
    public string PipeName = "pipe";

    /// <summary>J/K added to the core.</summary>
    [DataField]
    public float HeatCapacity = 25000f;

    /// <summary>Fraction of the core/gas gap the pipe gas closes each second.</summary>
    [DataField]
    public float Efficiency = 0.05f;
}

/// <summary>HV output of the reactor: its PowerSupplier gets the core's total power.</summary>
[RegisterComponent]
public sealed partial class KamertonPowerUnitComponent : Component;

/// <summary>Controller console. Linked to a core when it stands in the core's suppressor ring.</summary>
[RegisterComponent]
public sealed partial class KamertonControlModuleComponent : Component
{
    [ViewVariables]
    public float UiAccumulator;
}

/// <summary>
/// Kamerton reactor core: the layout anchor and holder of the reactor state
/// (temperature, total power, assembly).
/// </summary>
[RegisterComponent, Access(typeof(KamertonReactorSystem))]
public sealed partial class KamertonReactorCoreComponent : Component
{
    #region Settings

    /// <summary>W of electricity per stable+unstable pair.</summary>
    [DataField]
    public float PowerPerPair = 50000f;

    /// <summary>Pairs one synchronizer can drive.</summary>
    [DataField]
    public int MaxPairsPerSynchronizer = 4;

    /// <summary>W of waste heat per active pair.</summary>
    [DataField]
    public float HeatPerPair = 150000f;

    /// <summary>Core heat capacity without radiators, J/K.</summary>
    [DataField]
    public float BaseHeatCapacity = 200000f;

    [DataField]
    public float AmbientTemperature = 293.15f;

    /// <summary>Above this the reactor collapses.</summary>
    [DataField]
    public float CollapseTemperature = 2000f;

    /// <summary>Console warning threshold.</summary>
    [DataField]
    public float WarningTemperature = 1500f;

    /// <summary>Half-size of the suppressor ring square (3 = 7x7 ring around a 5x5 block).</summary>
    [DataField]
    public int RingRadius = 3;

    /// <summary>Max Chebyshev distance of an assembled part from the core.</summary>
    [DataField]
    public int MaxPartRange = 2;

    /// <summary>Range of the weak redspace leak when the ring is broken, tiles.</summary>
    [DataField]
    public float LeakRange = 20f;

    /// <summary>Damage per second from the leak.</summary>
    [DataField]
    public DamageSpecifier LeakDamage = new();

    [DataField]
    public float ScramLockout = 30f;

    [DataField]
    public EntProtoId CollapseTesla = "TeslaEnergyBall";

    [DataField]
    public EntProtoId CollapseSingularity = "Singularity";

    [DataField]
    public int CollapseSingularities = 5;

    [DataField]
    public float CollapseRange = 120f;

    [DataField]
    public string CollapsePreset = "RedspaceNormal";

    #endregion

    #region State

    [DataField]
    public float Temperature = 293.15f;

    [ViewVariables]
    public bool Working;

    [ViewVariables]
    public float ScramCooldown;

    [ViewVariables]
    public float TotalPower;

    [ViewVariables]
    public int ActivePairs;

    [ViewVariables]
    public bool Collapsed;

    [ViewVariables] public float Accumulator;
    [ViewVariables] public float LeakPopupAccumulator;

    #endregion

    #region Assembly (last scan)

    [ViewVariables] public readonly List<EntityUid> Parts = new();
    [ViewVariables] public readonly List<EntityUid> Synchronizers = new();
    [ViewVariables] public readonly List<EntityUid> StableArrays = new();
    [ViewVariables] public readonly List<EntityUid> UnstableArrays = new();
    [ViewVariables] public readonly List<EntityUid> HeatSinks = new();
    [ViewVariables] public readonly List<EntityUid> Radiators = new();
    [ViewVariables] public readonly List<EntityUid> RingParts = new();
    [ViewVariables] public int RingMissing;
    [ViewVariables] public EntityUid? Console;
    [ViewVariables] public EntityUid? PowerUnit;

    /// <summary>Synchronizer -> pairs it drives this second.</summary>
    [ViewVariables] public readonly Dictionary<EntityUid, int> SyncPairs = new();

    #endregion
}
