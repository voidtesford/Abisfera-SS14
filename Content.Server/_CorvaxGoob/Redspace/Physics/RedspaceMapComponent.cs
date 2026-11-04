using System.Numerics;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Redspace.Physics;

/// <summary>
/// Turns a map into redspace: inertia, gravity currents and waves, blinks, anomaly patches, slowed time,
/// organism damage and energy discharges. Every effect is tuned through <see cref="RedspaceSettings"/>:
/// pick a <see cref="Preset"/> and override single fields by their YAML name in <see cref="Overrides"/>.
/// </summary>
/// <example>
/// - type: RedspaceMap
///   preset: RedspaceNormal
///   overrides:
///     wavesEnabled: false
///     cellularPerSecond: 0.8
/// </example>
[RegisterComponent]
public sealed partial class RedspaceMapComponent : Component
{
    [DataField]
    public bool Enabled = true;

    [DataField]
    public ProtoId<RedspacePresetPrototype> Preset = "RedspaceNormal";

    /// <summary>Field name (as in YAML, e.g. <c>mobFriction</c>) to value.</summary>
    [DataField]
    public Dictionary<string, string> Overrides = new();

    /// <summary>Preset plus overrides, rebuilt on startup and by <see cref="RedspaceMapSystem.ApplySettings"/>.</summary>
    [ViewVariables]
    public RedspaceSettings Settings = new();

    [ViewVariables] public float CurrentAngle;
    [ViewVariables] public float NextWave;
    [ViewVariables] public float NextWeightless;
    [ViewVariables] public float WeightlessLeft;
    [ViewVariables] public float NextAnomaly;
    [ViewVariables] public List<EntityUid> Anomalies = new();
}

/// <summary>Anything that suppresses redspace in <see cref="Radius"/> tiles around itself while <see cref="Active"/>.</summary>
[RegisterComponent]
public sealed partial class RedspaceSuppressorComponent : Component
{
    [DataField]
    public float Radius = 1.5f;

    /// <summary>Set by the owning system (collar, beacon). Suppressors with no owner are always on.</summary>
    [DataField]
    public bool Active = true;
}

/// <summary>A patch of bent space: mobs inside move at <see cref="Speed"/> times their speed.</summary>
[RegisterComponent]
public sealed partial class RedspaceAnomalyPatchComponent : Component
{
    [DataField]
    public float Radius = 2.5f;

    [DataField]
    public float Speed = 0.6f;
}

/// <summary>Remembers where its owner was so an echo can replay it late.</summary>
[RegisterComponent]
public sealed partial class RedspaceTrailComponent : Component
{
    /// <summary>(time, position, rotation) samples, oldest first.</summary>
    [ViewVariables]
    public Queue<(TimeSpan Time, Vector2 Position, Angle Rotation)> Samples = new();
}

/// <summary>Server half of the echo: what it follows and when it fades.</summary>
[RegisterComponent]
public sealed partial class RedspaceEchoFollowComponent : Component
{
    [ViewVariables] public EntityUid Source;
    [ViewVariables] public TimeSpan Delay;
    [ViewVariables] public TimeSpan End;
}
