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

    #region Anomaly events

    // Zone events: every AnomalyEventInterval seconds one event is picked by weight (0 = never) and started next to a
    // random exposed living player. Never inside a suppressed area, never on the immune.

    [DataField]
    public bool AnomalyEventsEnabled = true;

    [DataField]
    public float AnomalyEventIntervalMin = 120f;

    [DataField]
    public float AnomalyEventIntervalMax = 300f;

    /// <summary>Most zone events (rifts, knots, rain, mirror corridors, doppelgangers) alive at once on a map.</summary>
    [DataField]
    public int AnomalyEventMax = 3;

    [DataField]
    public float RiftWeight = 1f;

    [DataField]
    public float TimeLoopWeight = 1f;

    [DataField]
    public float DoppelgangerWeight = 1f;

    [DataField]
    public float BloodRainWeight = 1f;

    [DataField]
    public float MirrorCorridorWeight = 1f;

    [DataField]
    public float WhispersWeight = 1.5f;

    [DataField]
    public float GravityKnotWeight = 1f;

    /// <summary>Rift: open time, seconds.</summary>
    [DataField]
    public float RiftDurationMin = 20f;

    [DataField]
    public float RiftDurationMax = 40f;

    /// <summary>Rift: pull reach, tiles, and pull acceleration, m/s².</summary>
    [DataField]
    public float RiftRadius = 6f;

    [DataField]
    public float RiftPull = 4f;

    /// <summary>Rift: Blunt damage dealt to a mob it swallows and throws out somewhere else.</summary>
    [DataField]
    public float RiftDamage = 12f;

    /// <summary>Rift: how far (tiles) a swallowed thing may land.</summary>
    [DataField]
    public float RiftThrowMin = 8f;

    [DataField]
    public float RiftThrowMax = 30f;

    /// <summary>Time loop: seconds until the player is pulled back to the snapshot.</summary>
    [DataField]
    public float TimeLoopDelay = 5f;

    /// <summary>Doppelganger: lifetime, seconds; how late it repeats the moves; damage per hit and hit cooldown.</summary>
    [DataField]
    public float DoppelgangerLifetime = 60f;

    [DataField]
    public float DoppelgangerDelay = 1.5f;

    [DataField]
    public float DoppelgangerDamage = 5f;

    [DataField]
    public float DoppelgangerHitCooldown = 2.5f;

    /// <summary>Blood rain: radius, tiles; duration, seconds; Blunt damage per second inside; slip chance per second while moving.</summary>
    [DataField]
    public float BloodRainRadius = 3f;

    [DataField]
    public float BloodRainDuration = 30f;

    [DataField]
    public float BloodRainDamage = 0.3f;

    [DataField]
    public float BloodRainSlipChance = 0.12f;

    /// <summary>Mirror corridor: duration, seconds; length and width, tiles. Movement input is inverted inside.</summary>
    [DataField]
    public float MirrorCorridorDuration = 30f;

    [DataField]
    public float MirrorCorridorLength = 7f;

    [DataField]
    public float MirrorCorridorWidth = 3f;

    /// <summary>Whispers: how old (seconds) a remembered phrase may be; chance it comes "over the radio".</summary>
    [DataField]
    public float WhispersMemory = 600f;

    [DataField]
    public float WhispersRadioChance = 0.4f;

    /// <summary>Gravity knot: duration, seconds; reach, tiles; orbit and inward acceleration, m/s².</summary>
    [DataField]
    public float GravityKnotDuration = 40f;

    [DataField]
    public float GravityKnotRadius = 5f;

    [DataField]
    public float GravityKnotOrbit = 3f;

    [DataField]
    public float GravityKnotInward = 1.5f;

    #endregion

    #region In-world visuals

    /// <summary>
    /// Client-only decorations around exposed players: floating debris and dust, flickering "reality patches",
    /// shadow silhouettes, discharge arcs with afterglow, footprints. Rate multiplies how often they appear (0 = off).
    /// No screen shaders and no colour tint, only entities, sprites and lights.
    /// </summary>
    [DataField]
    public bool VisualsEnabled = true;

    [DataField]
    public float VisualsRate = 1f;

    #endregion

    #region Body effects

    /// <summary>Temporary mutations (distorted voice, slowdown, extra eye) after this many seconds of continuous exposure.</summary>
    [DataField]
    public bool MutationsEnabled = true;

    [DataField]
    public float MutationExposure = 120f;

    [DataField]
    public float MutationChancePerMinute = 1f;

    [DataField]
    public float MutationDuration = 180f;

    [DataField]
    public float MutationSlowdown = 0.8f;

    /// <summary>Private hallucinations: fake enemies and footsteps only the player sees and hears.</summary>
    [DataField]
    public bool PrivateHallucinationsEnabled = true;

    [DataField]
    public float PrivateHallucinationIntervalMin = 45f;

    [DataField]
    public float PrivateHallucinationIntervalMax = 120f;

    /// <summary>Item decay on what exposed players carry: food rots faster, batteries drain, guns jam.</summary>
    [DataField]
    public bool ItemDecayEnabled = true;

    /// <summary>Perishable food rots this many times faster.</summary>
    [DataField]
    public float FoodRotMultiplier = 4f;

    /// <summary>Share of a battery's capacity lost per minute.</summary>
    [DataField]
    public float BatteryDrainPerMinute = 0.05f;

    /// <summary>Chance per minute that a gun held in hand jams, and for how long.</summary>
    [DataField]
    public float GunJamChancePerMinute = 0.5f;

    [DataField]
    public float GunJamDuration = 4f;

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
