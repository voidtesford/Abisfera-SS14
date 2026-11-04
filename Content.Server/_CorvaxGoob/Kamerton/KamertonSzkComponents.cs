using Content.Shared._CorvaxGoob.Kamerton;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton fuel storage: a shell part that holds charged cells for the SZK, <see cref="Capacity"/> of each
/// kind. Players load it from outside by using a charged cell on it.
/// </summary>
[RegisterComponent, Access(typeof(KamertonSzkSystem))]
public sealed partial class KamertonFuelStorageComponent : Component
{
    [DataField]
    public int Capacity = 10;

    [DataField]
    public string StableContainer = "stable_cells";

    [DataField]
    public string UnstableContainer = "unstable_cells";
}

/// <summary>
/// SZK («СЗК», the Kamerton refuelling system): a shell part. Once it is anchored and belongs to an assembled
/// reactor it lays a lattice of rails over every tile of the reactor and puts a manipulator carriage on it.
/// The carriage runs tile by tile between the fuel storage and the arrays: it carries charged cells in and
/// spent cells out. Spent cells leave through the storage hatch onto the free tile next to the storage (the
/// storage itself only keeps charged cells). The arrays are walled in, so without an SZK they cannot be
/// refuelled at all.
/// </summary>
[RegisterComponent]
public sealed partial class KamertonSzkComponent : Component
{
    [DataField]
    public EntProtoId RailPrototype = "KamertonSzkRail";

    [DataField]
    public EntProtoId CarriagePrototype = "KamertonSzkCarriage";

    [DataField]
    public EntProtoId PickupEffect = "KamertonSzkPickupEffect";

    [DataField]
    public EntProtoId InsertEffect = "KamertonSzkInsertEffect";

    [DataField]
    public SoundSpecifier PickupSound = new SoundPathSpecifier("/Audio/Machines/machine_switch.ogg");

    [DataField]
    public SoundSpecifier InsertSound = new SoundCollectionSpecifier("sparks");

    /// <summary>Container on the SZK block that holds the cell the carriage carries.</summary>
    [DataField]
    public string CarriageContainer = "szk_carriage";

    /// <summary>Seconds per rail tile the carriage moves.</summary>
    [DataField]
    public float StepInterval = 0.25f;

    /// <summary>Seconds per grab or insert.</summary>
    [DataField]
    public float ActionInterval = 0.5f;

    /// <summary>Unload spent cells and refill arrays on its own whenever the storage can do it.</summary>
    [DataField]
    public bool Auto = true;

    [ViewVariables] public EntityUid? Core;
    [ViewVariables] public EntityUid? Grid;
    [ViewVariables] public readonly Dictionary<Vector2i, EntityUid> Rails = new();
    [ViewVariables] public EntityUid? Carriage;
    [ViewVariables] public Vector2i CarriageTile;
    [ViewVariables] public KamertonSzkState State = KamertonSzkState.Idle;
    [ViewVariables] public KamertonSzkCommand? Task;
    [ViewVariables] public readonly List<Vector2i> Path = new();
    [ViewVariables] public float Accumulator;
}

/// <summary>One tile of the SZK rail lattice; spawned and removed by its SZK.</summary>
[RegisterComponent]
public sealed partial class KamertonSzkRailComponent : Component
{
    [ViewVariables] public EntityUid? Szk;
}

/// <summary>The SZK manipulator carriage; spawned and removed by its SZK.</summary>
[RegisterComponent]
public sealed partial class KamertonSzkCarriageComponent : Component
{
    [ViewVariables] public EntityUid? Szk;
}
