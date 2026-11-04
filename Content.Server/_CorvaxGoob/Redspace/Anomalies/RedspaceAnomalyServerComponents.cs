using System.Numerics;
using Content.Shared._CorvaxGoob.Redspace.Anomalies;
using Content.Shared.Damage;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.Redspace.Anomalies;

/// <summary>Per-map timer of the redspace zone events.</summary>
[RegisterComponent]
public sealed partial class RedspaceAnomalyMapComponent : Component
{
    /// <summary>Seconds until the next random event; -1 = not rolled yet.</summary>
    [ViewVariables] public float NextEvent = -1f;
}

/// <summary>A zone event in the world: rift, gravity knot, blood rain or mirror corridor.</summary>
[RegisterComponent]
public sealed partial class RedspaceZoneEventComponent : Component
{
    [DataField(required: true)]
    public RedspaceAnomalyEvent Kind;

    [ViewVariables] public float Left;
    [ViewVariables] public float Duration;
    [ViewVariables] public float Radius;
    [ViewVariables] public float Tick;

    /// <summary>Mirror corridor: half length / half width in tiles; whoever is mirrored by it right now.</summary>
    [ViewVariables] public Vector2 HalfSize;
    [ViewVariables] public readonly HashSet<EntityUid> Members = new();
}

/// <summary>The player will be thrown back to <see cref="Origin"/> with the health they had then.</summary>
[RegisterComponent]
public sealed partial class RedspaceTimeLoopComponent : Component
{
    [ViewVariables] public float Left;
    [ViewVariables] public EntityCoordinates Origin;
    [ViewVariables] public DamageSpecifier? Snapshot;
    [ViewVariables] public EntityUid? Marker;
}

/// <summary>Server side of a doppelganger: whom it copies and the recent track of that player.</summary>
[RegisterComponent]
public sealed partial class RedspaceDoppelgangerStateComponent : Component
{
    [ViewVariables] public EntityUid Target;
    [ViewVariables] public float Left;
    [ViewVariables] public float Clock;
    [ViewVariables] public float HitCooldown;
    /// <summary>Where the doppelganger appeared and where the target stood at that moment.</summary>
    [ViewVariables] public Vector2 Start;
    [ViewVariables] public Vector2 Anchor;
    [ViewVariables] public readonly List<(float Time, Vector2 Position, Angle Rotation)> Samples = new();
}
