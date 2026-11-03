using System.Numerics;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared._CorvaxGoob.Redspace.Physics;

/// <summary>
/// Every tunable value of the redspace map physics. A <see cref="RedspacePresetPrototype"/> holds a full set;
/// a map's RedspaceMap component takes a preset and may override single fields by their YAML name.
/// Times are in seconds, distances in tiles (metres).
/// </summary>
[DataDefinition]
public sealed partial class RedspaceSettings
{
    #region Scan

    /// <summary>Mobs within this many tiles of a living player are affected. Nothing outside this range is touched.</summary>
    [DataField]
    public float ActiveRange = 15f;

    /// <summary>Radius around an open Euclidean gate (and its exit portal) where redspace is suppressed.</summary>
    [DataField]
    public float GateSuppressionRadius = 10f;

    #endregion

    #region Inertia

    [DataField]
    public bool InertiaEnabled = true;

    /// <summary>Friction multiplier for affected mobs (lower = more slide).</summary>
    [DataField]
    public float MobFriction = 0.35f;

    /// <summary>Acceleration multiplier for affected mobs (lower = slower to change direction).</summary>
    [DataField]
    public float MobAcceleration = 0.6f;

    /// <summary>Tile friction multiplier for items thrown on the map.</summary>
    [DataField]
    public float ThrownFriction = 0.15f;

    /// <summary>Sideways drift applied to projectiles, m/s². 0 disables.</summary>
    [DataField]
    public float ProjectileDrift = 2f;

    #endregion

    #region Gravity

    [DataField]
    public bool CurrentEnabled = true;

    /// <summary>Drift acceleration of the current, m/s².</summary>
    [DataField]
    public float CurrentStrength = 1.2f;

    /// <summary>Direction of the current in degrees (0 = east, 90 = north). Ignored when <see cref="CurrentRandomDirection"/>.</summary>
    [DataField]
    public float CurrentDirection = 0f;

    /// <summary>Pick a random current direction when the map is activated.</summary>
    [DataField]
    public bool CurrentRandomDirection = true;

    /// <summary>How fast the current slowly turns, degrees per minute (0 = fixed).</summary>
    [DataField]
    public float CurrentTurnRate = 20f;

    [DataField]
    public bool WavesEnabled = true;

    [DataField]
    public float WaveIntervalMin = 45f;

    [DataField]
    public float WaveIntervalMax = 90f;

    /// <summary>Velocity given by a wave, m/s.</summary>
    [DataField]
    public float WaveStrength = 4f;

    [DataField]
    public SoundSpecifier? WaveSound = new SoundPathSpecifier("/Audio/Effects/teleport_departure.ogg", AudioParams.Default.WithVolume(-4f));

    [DataField]
    public bool WeightlessEnabled = true;

    [DataField]
    public float WeightlessIntervalMin = 90f;

    [DataField]
    public float WeightlessIntervalMax = 180f;

    [DataField]
    public float WeightlessDuration = 8f;

    #endregion

    #region Non-Euclidean

    /// <summary>Walking off the edge of <see cref="WrapMin"/>..<see cref="WrapMax"/> puts the mob on the opposite edge.</summary>
    [DataField]
    public bool WrapEnabled = false;

    [DataField]
    public Vector2 WrapMin = new(-100f, -100f);

    [DataField]
    public Vector2 WrapMax = new(100f, 100f);

    [DataField]
    public bool BlinkEnabled = true;

    /// <summary>Chance per affected mob per minute to blink 1-3 tiles.</summary>
    [DataField]
    public float BlinkChancePerMinute = 0.5f;

    [DataField]
    public int BlinkMinDistance = 1;

    [DataField]
    public int BlinkMaxDistance = 3;

    [DataField]
    public SoundSpecifier? BlinkSound = new SoundPathSpecifier("/Audio/Effects/teleport_arrival.ogg", AudioParams.Default.WithVolume(-6f));

    [DataField]
    public bool AnomaliesEnabled = true;

    /// <summary>Speed multiplier inside an anomaly patch (below 1 slows, above 1 speeds up).</summary>
    [DataField]
    public float AnomalySpeed = 0.6f;

    [DataField]
    public float AnomalyRadius = 2.5f;

    [DataField]
    public float AnomalyLifetime = 90f;

    [DataField]
    public float AnomalyIntervalMin = 30f;

    [DataField]
    public float AnomalyIntervalMax = 60f;

    /// <summary>Most random patches alive on one map at once.</summary>
    [DataField]
    public int AnomalyMax = 6;

    #endregion

    #region Time

    /// <summary>Multiplier for do-after durations of affected mobs.</summary>
    [DataField]
    public float DoAfterMultiplier = 1.5f;

    [DataField]
    public bool EchoEnabled = true;

    /// <summary>The echo replays the player's movement this many seconds late.</summary>
    [DataField]
    public float EchoDelay = 2.5f;

    /// <summary>How long one echo lives.</summary>
    [DataField]
    public float EchoDuration = 8f;

    [DataField]
    public float EchoIntervalMin = 60f;

    [DataField]
    public float EchoIntervalMax = 150f;

    #endregion

    #region Organism

    [DataField]
    public bool OrganismEnabled = true;

    /// <summary>Cellular damage per second for an unprotected mob.</summary>
    [DataField]
    public float CellularPerSecond = 0.5f;

    /// <summary>Shock damage per second for an unprotected mob.</summary>
    [DataField]
    public float ShockPerSecond = 0.17f;

    /// <summary>Damage is applied in steps of this many seconds.</summary>
    [DataField]
    public float DamageInterval = 2f;

    /// <summary>Chance per damage step to start jittering.</summary>
    [DataField]
    public float JitterChance = 0.25f;

    [DataField]
    public float VomitChancePerMinute = 0.2f;

    [DataField]
    public bool HallucinationsEnabled = true;

    [DataField]
    public float HallucinationIntervalMin = 25f;

    [DataField]
    public float HallucinationIntervalMax = 70f;

    [DataField]
    public SoundSpecifier? HallucinationSound = new SoundCollectionSpecifier("RedspaceWhispers", AudioParams.Default.WithVolume(-6f));

    #endregion

    #region Energy

    [DataField]
    public bool DischargesEnabled = true;

    [DataField]
    public float DischargeIntervalMin = 15f;

    [DataField]
    public float DischargeIntervalMax = 40f;

    /// <summary>Discharges happen this far from the player at most.</summary>
    [DataField]
    public float DischargeRange = 7f;

    [DataField]
    public float DischargeShockRadius = 1.5f;

    [DataField]
    public int DischargeShockDamage = 8;

    [DataField]
    public float DischargeStun = 1f;

    /// <summary>Chance that a discharge shocks the mobs next to it at all.</summary>
    [DataField]
    public float DischargeShockChance = 0.35f;

    [DataField]
    public EntProtoId DischargeEffect = "RedspaceDischargeEffect";

    [DataField]
    public bool LightFlickerEnabled = true;

    [DataField]
    public float LightFlickerIntervalMin = 20f;

    [DataField]
    public float LightFlickerIntervalMax = 50f;

    [DataField]
    public float LightFlickerRadius = 8f;

    [DataField]
    public float LightFlickerDuration = 3f;

    [DataField]
    public bool RadioStaticEnabled = true;

    [DataField]
    public float RadioStaticIntervalMin = 40f;

    [DataField]
    public float RadioStaticIntervalMax = 100f;

    [DataField]
    public SoundSpecifier? RadioStaticSound = new SoundCollectionSpecifier("sparks", AudioParams.Default.WithVolume(-10f));

    #endregion

    #region Ambient

    [DataField]
    public bool AmbientEnabled = true;

    [DataField]
    public SoundSpecifier? AmbientSound = new SoundPathSpecifier("/Audio/Ambience/anomaly_drone.ogg", AudioParams.Default.WithVolume(-8f).WithLoop(true));

    #endregion
}

/// <summary>A named set of <see cref="RedspaceSettings"/> (calm / normal / storm).</summary>
[Prototype("redspacePreset")]
public sealed partial class RedspacePresetPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public RedspaceSettings Settings = new();
}
