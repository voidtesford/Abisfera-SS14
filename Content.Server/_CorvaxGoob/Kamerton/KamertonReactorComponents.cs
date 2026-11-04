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

/// <summary>
/// Heat sink: a straight pipe through the reactor shell (one pipe node, so gas passes through like an ordinary
/// pipe and through neighbouring heat sinks) that hands core heat to the gas in its pipe.
/// </summary>
[RegisterComponent]
public sealed partial class KamertonHeatSinkComponent : Component
{
    [DataField]
    public string PipeName = "pipe";

    /// <summary>Litres of pipe gas that exchange heat with the core per second.</summary>
    [DataField]
    public float TransferRate = 100f;

    /// <summary>Fraction of the core/gas temperature gap the exchanged gas closes.</summary>
    [DataField]
    public float Efficiency = 0.5f;

    [ViewVariables] public float LastFlow;
    [ViewVariables] public float LastInletTemperature;
    [ViewVariables] public float LastOutletTemperature;
    [ViewVariables] public float LastHeatRemoved;
}

/// <summary>Radiator: every radiator in the reactor raises its heat limit; linked radiators carry heat to heat sinks.</summary>
[RegisterComponent]
public sealed partial class KamertonRadiatorComponent : Component;

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
[RegisterComponent, Access(typeof(KamertonReactorSystem), typeof(KamertonDroneSystem), typeof(KamertonMeltdownSystem))]
public sealed partial class KamertonReactorCoreComponent : Component
{
    #region Settings

    /// <summary>W of electricity per stable+unstable pair.</summary>
    [DataField]
    public float PowerPerPair = 50000f;

    /// <summary>Pairs one synchronizer can drive.</summary>
    [DataField]
    public int MaxPairsPerSynchronizer = 4;

    /// <summary>Heat units per second per active pair.</summary>
    [DataField]
    public float HeatPerPair = 1f;

    /// <summary>Heat limit without radiators; reaching the limit collapses the reactor.</summary>
    [DataField]
    public float BaseMaxHeat = 500f;

    /// <summary>Heat limit added by every radiator of the reactor.</summary>
    [DataField]
    public float MaxHeatPerRadiator = 250f;

    /// <summary>Share of the heat limit where meltdown starts: no stop, no SCRAM, arcs around the core.</summary>
    [DataField]
    public float MeltdownFraction = 0.75f;

    /// <summary>Heat units per second a stopped core loses on its own, down to 0.</summary>
    [DataField]
    public float PassiveCooling = 2f;

    /// <summary>Joules per heat unit, used when the heat sinks exchange heat with gas.</summary>
    [DataField]
    public float JoulesPerHeat = 20000f;

    /// <summary>Gas temperature that matches heat 0; one heat unit is one kelvin above it.</summary>
    [DataField]
    public float AmbientTemperature = 293.15f;

    /// <summary>Meltdown arcs reach this far, tiles.</summary>
    [DataField]
    public float ArcRange = 10f;

    /// <summary>Seconds between meltdown arcs.</summary>
    [DataField]
    public float ArcInterval = 0.3f;

    /// <summary>Damage of one meltdown arc.</summary>
    [DataField]
    public DamageSpecifier ArcDamage = new();

    /// <summary>A reactor node group with more parts than this does not run.</summary>
    [DataField]
    public int MaxParts = 96;

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

    /// <summary>Core heat, 0 = room temperature.</summary>
    [DataField]
    public float Heat;

    [ViewVariables]
    public bool Meltdown;

    [ViewVariables] public float ArcAccumulator;

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

    /// <summary>Radiators linked to a heat sink through a chain of radiators.</summary>
    [ViewVariables] public readonly HashSet<EntityUid> LinkedRadiators = new();

    /// <summary>Heat sinks touched by a linked radiator; only these take heat into their gas.</summary>
    [ViewVariables] public readonly HashSet<EntityUid> LinkedHeatSinks = new();
    [ViewVariables] public readonly List<EntityUid> RingParts = new();
    [ViewVariables] public int RingMissing;
    [ViewVariables] public EntityUid? Console;
    [ViewVariables] public EntityUid? PowerUnit;

    /// <summary>Fuel storage in the shell (the first one found).</summary>
    [ViewVariables] public EntityUid? Storage;

    /// <summary>The RDZT maintenance drone that came with this core.</summary>
    [ViewVariables] public EntityUid? Drone;

    /// <summary>Another core leads this node group; this one stays idle.</summary>
    [ViewVariables] public bool Slave;

    /// <summary>The node group has more than <see cref="MaxParts"/> parts.</summary>
    [ViewVariables] public bool Oversized;

    /// <summary>Synchronizer -> pairs it drives this second.</summary>
    [ViewVariables] public readonly Dictionary<EntityUid, int> SyncPairs = new();

    #endregion
}
