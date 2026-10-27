using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

using Robust.Shared.Prototypes;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// Marks the Gilbert NPC (SHOGKK operative). Handles his body setup, spawn effect and glowing eyes.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class GilbertComponent : Component
{
    /// <summary>
    /// Integrity of every body part (limb, chest, head...) once the body is set up.
    /// </summary>
    [DataField]
    public float PartIntegrity = 250f;

    /// <summary>
    /// v8: per part type integrity that replaces <see cref="PartIntegrity"/> (his torso and groin take 500 each,
    /// so damage keeps landing on him until the 450 berserk trigger and the 500 death threshold).
    /// </summary>
    [DataField]
    public Dictionary<Content.Shared.Body.Part.BodyPartType, float> PartIntegrityOverrides = new()
    {
        { Content.Shared.Body.Part.BodyPartType.Chest, 500f },
        { Content.Shared.Body.Part.BodyPartType.Groin, 500f },
    };

    /// <summary>
    /// Spawn sequence, part 1: his red energy silhouette assembles (converging streaks, the silhouette fading
    /// in from scattered sparks). He is invulnerable and inactive the whole sequence.
    /// </summary>
    [DataField]
    public TimeSpan AssembleDuration = TimeSpan.FromSeconds(1.7);

    /// <summary>
    /// Spawn sequence, part 3: after the small energy burst (at <see cref="AssembleDuration"/>) he keeps a
    /// fading red tint and becomes active (AI on, can be hurt, can move) this long after spawning.
    /// </summary>
    [DataField]
    public TimeSpan ActivateDelay = TimeSpan.FromSeconds(2.2);

    /// <summary>Converging red energy streaks shown while the silhouette assembles.</summary>
    [DataField]
    public EntProtoId ConvergePrototype = "GilbertSpawnConverge";

    /// <summary>The small energy explosion that ends the assembly (purely visual, no damage).</summary>
    [DataField]
    public EntProtoId BurstPrototype = "GilbertSpawnBurst";

    [DataField]
    public SoundSpecifier AssembleSound = new SoundPathSpecifier("/Audio/Effects/teleport_arrival.ogg",
        AudioParams.Default.WithVolume(-2f));

    [DataField]
    public SoundSpecifier BurstSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningshock.ogg",
        AudioParams.Default.WithVolume(2f));

    [DataField]
    public SoundSpecifier BurstBoomSound = new SoundPathSpecifier("/Audio/Effects/explosion_small1.ogg",
        AudioParams.Default.WithVolume(-6f));

    /// <summary>
    /// v8: looped background ambience of the first phase, positional and attached to him (about 15 tiles).
    /// The file is mastered at about -16 LUFS; -9 dB here keeps it well under his voice lines and footsteps.
    /// Starts when he activates after the spawn sequence, stops for good when the berserk transition begins,
    /// on death or on deletion.
    /// </summary>
    [DataField]
    public SoundSpecifier SoundtrackSound = new SoundPathSpecifier("/Audio/_CorvaxGoob/Gilbert/gilbert_ambience_1.ogg",
        AudioParams.Default.WithVolume(-9f).WithMaxDistance(15f).WithLoop(true));

    /// <summary>The playing soundtrack audio entity (server only).</summary>
    [ViewVariables]
    public EntityUid? Soundtrack;

    /// <summary>Set once the soundtrack has been stopped for good (berserk / death): it never restarts.</summary>
    [ViewVariables]
    public bool SoundtrackFinished;

    /// <summary>
    /// Short arcs crackle around the silhouette while it assembles, starting this long after spawning.
    /// </summary>
    [DataField]
    public TimeSpan SpawnArcDelay = TimeSpan.FromSeconds(0.3);

    /// <summary>
    /// Time between two arcs.
    /// </summary>
    [DataField]
    public TimeSpan SpawnArcInterval = TimeSpan.FromSeconds(0.12);

    /// <summary>
    /// Arcs appear within this distance of his centre.
    /// </summary>
    [DataField]
    public float SpawnArcRadius = 0.7f;

    /// <summary>
    /// Short white-red arc / spark effect (purely visual).
    /// </summary>
    [DataField]
    public EntProtoId SpawnArcPrototype = "GilbertSpawnArc";

    /// <summary>
    /// Hair style marking («Деловая 3»).
    /// </summary>
    [DataField]
    public string HairStyle = "HumanHairBusiness3";

    [DataField]
    public Color HairColor = Color.FromHex("#0e0e10");

    [DataField]
    public Color EyeColor = Color.FromHex("#ff1a1a");

    /// <summary>
    /// Human skin tone (0-100), 20 is the palest valid tone.
    /// </summary>
    [DataField]
    public int SkinTone = 20;

    /// <summary>When he spawned (start of the spawn sequence); the client animates the silhouette from it.</summary>
    [ViewVariables, AutoNetworkedField]
    public TimeSpan SpawnTime;

    /// <summary>False during the spawn sequence: no AI, no damage, no movement or attacks.</summary>
    [ViewVariables, AutoNetworkedField]
    public bool Active;

    [ViewVariables]
    public bool PartsSetUp;

    [ViewVariables]
    public bool SequenceStarted;

    [ViewVariables]
    public bool BurstDone;

    [ViewVariables]
    public TimeSpan NextArc;
}

[Serializable, NetSerializable]
public enum GilbertVisuals : byte
{
    /// <summary>
    /// bool: whether the red spawn tint is active (after the burst, until he becomes active).
    /// </summary>
    SpawnTint,

    /// <summary>
    /// bool: whether he is still a red energy silhouette assembling (before the burst).
    /// </summary>
    Silhouette,

    /// <summary>
    /// bool: whether his eyes glow (false once he is dead).
    /// </summary>
    EyesGlow,
}

public enum GilbertVisualLayers : byte
{
    Eyes,

    /// <summary>Soft red glow around the eyes, drawn just under them.</summary>
    EyesHalo,
}
