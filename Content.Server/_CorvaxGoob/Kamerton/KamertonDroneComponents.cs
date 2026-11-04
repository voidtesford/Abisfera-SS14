using System.Numerics;
using Content.Shared._CorvaxGoob.Kamerton;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton fuel storage: a shell part next to the console that holds charged cells for the RDZT drone,
/// <see cref="Capacity"/> of each kind. Players load it from outside by using a charged cell on it.
/// </summary>
[RegisterComponent, Access(typeof(KamertonDroneSystem))]
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
/// RDZT («РДЗТ»), the spider-like maintenance drone of a Kamerton reactor. It docks at the fuel storage and
/// feeds cells to the arrays through the hull (any array of its own reactor group), and pulls spent cells
/// out the same way, dropping them at its feet next to the storage. With no job it wanders around the core.
/// </summary>
[RegisterComponent, Access(typeof(KamertonDroneSystem))]
public sealed partial class KamertonDroneComponent : Component
{
    [ViewVariables]
    public EntityUid? Core;

    [ViewVariables]
    public KamertonDroneState State = KamertonDroneState.Idle;

    /// <summary>Current job while <see cref="KamertonDroneState.Working"/>: Load or Unload.</summary>
    [ViewVariables]
    public KamertonDroneCommand? Task;

    /// <summary>Unload spent cells and refill arrays on its own whenever the storage can do it.</summary>
    [DataField]
    public bool AutoRefuel = true;

    /// <summary>Idle wander radius around the core, tiles.</summary>
    [DataField]
    public float WanderRadius = 6f;

    /// <summary>Further than this from the core the drone drops everything and walks back, tiles.</summary>
    [DataField]
    public float LeashRadius = 8f;

    /// <summary>How close to the fuel storage the drone has to be to dock, tiles.</summary>
    [DataField]
    public float DockRange = 1.6f;

    /// <summary>Walking speed, tiles per second.</summary>
    [DataField]
    public float Speed = 2.5f;

    /// <summary>Seconds per moved cell while docked.</summary>
    [DataField]
    public float ActionInterval = 1f;

    [ViewVariables] public float Accumulator;
    [ViewVariables] public float WanderTimer;
    [ViewVariables] public Vector2? Goal;
}

/// <summary>Spawns an RDZT drone linked to the core when this flatpack unpacks a Kamerton core.</summary>
[RegisterComponent]
public sealed partial class KamertonDroneSpawnerComponent : Component
{
    [DataField]
    public EntProtoId Drone = "KamertonDroneRDZT";
}
