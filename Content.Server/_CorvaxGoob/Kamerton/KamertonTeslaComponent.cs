using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// The Kamerton tesla: a scarlet tesla ball twice the normal size. Every so often it throws a ring of
/// tentacle-shaped lightning far out, it breeds normal tesla balls and singularities around itself,
/// and singularities cannot consume it.
/// </summary>
[RegisterComponent]
public sealed partial class KamertonTeslaComponent : Component
{
    /// <summary>Shortest pause between two tentacle bursts, seconds.</summary>
    [DataField]
    public float BurstMinInterval = 15f;

    /// <summary>Longest pause between two tentacle bursts, seconds.</summary>
    [DataField]
    public float BurstMaxInterval = 20f;

    [DataField]
    public int MinTentacles = 8;

    [DataField]
    public int MaxTentacles = 12;

    /// <summary>How far a tentacle reaches, tiles.</summary>
    [DataField]
    public float TentacleRange = 50f;

    /// <summary>Length of one tentacle segment, tiles.</summary>
    [DataField]
    public float SegmentLength = 2.5f;

    /// <summary>Time a tentacle takes to grow to its full length, seconds.</summary>
    [DataField]
    public float GrowTime = 1f;

    /// <summary>How hard a tentacle bends per segment, radians.</summary>
    [DataField]
    public float Curl = 0.35f;

    [DataField]
    public EntProtoId TentaclePrototype = "KamertonTeslaTentacle";

    [DataField]
    public EntProtoId NodePrototype = "KamertonTeslaTentacleNode";

    /// <summary>Seconds between two offspring.</summary>
    [DataField]
    public float SpawnInterval = 30f;

    [DataField]
    public float SpawnMinRange = 8f;

    [DataField]
    public float SpawnMaxRange = 15f;

    /// <summary>Chance that the offspring is a singularity rather than a tesla ball.</summary>
    [DataField]
    public float SingularityChance = 0.5f;

    [DataField]
    public EntProtoId TeslaPrototype = "TeslaEnergyBall";

    /// <summary>The same small singularity the Kamerton collapse throws out.</summary>
    [DataField]
    public EntProtoId SingularityPrototype = "Singularity";

    /// <summary>Seconds left until the next tentacle burst.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float BurstTimer = 15f;

    /// <summary>Seconds left until the next offspring.</summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float SpawnTimer = 30f;

    /// <summary>Tentacle segments still waiting to light up while the tentacles grow.</summary>
    [ViewVariables]
    public List<KamertonTentacleSegment> Pending = new();

    /// <summary>Beam controller shared by the segments of the current burst.</summary>
    [ViewVariables]
    public EntityUid? BurstController;
}

public record struct KamertonTentacleSegment(EntityUid From, EntityUid To, float Delay);
