using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// Gilbert's second phase, «Берсерк» (v6). Lives on every Gilbert from spawn with <see cref="Phase"/> None;
/// when only 50 of his 500 HP are left (<see cref="TriggerDamage"/>) the server's GilbertBerserkSystem runs
/// Transition (6.9 s) -> Rage (5 min) -> Dying (4 s) -> Dead. From the trigger on he can't be hurt or healed,
/// and once Dead he can't be revived.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class GilbertBerserkComponent : Component
{
    /// <summary>Total damage at which the phase starts (500 max HP, 50 left).</summary>
    [DataField]
    public float TriggerDamage = 450f;

    /// <summary>
    /// v9: exactly as long as <see cref="TransitionSound"/> (the growl-into-roar clip is 6.898 s), so the sound and
    /// the transition start and end together (was 10 s with the clip ending 3 s early).
    /// </summary>
    [DataField]
    public TimeSpan TransitionDuration = TimeSpan.FromSeconds(6.9);

    /// <summary>
    /// When during the transition the charge peaks and bursts out (and the 20 Shock lands). v9: 4.75 s, where the
    /// growl in the transition clip breaks into the roar.
    /// </summary>
    [DataField]
    public TimeSpan BurstAt = TimeSpan.FromSeconds(4.75);

    [DataField]
    public TimeSpan RageDuration = TimeSpan.FromMinutes(5);

    [DataField]
    public TimeSpan DyingDuration = TimeSpan.FromSeconds(4);

    /// <summary>When during the dying sequence he falls down (the explosion follows at <see cref="DyingDuration"/>).</summary>
    [DataField]
    public TimeSpan FallAt = TimeSpan.FromSeconds(3.4);

    /// <summary>Radius (tiles) of the burst and of the death explosion.</summary>
    [DataField]
    public float BurstRadius = 10f;

    /// <summary>Shock dealt to every living mob within <see cref="BurstRadius"/> by the burst (and by the death explosion).</summary>
    [DataField]
    public DamageSpecifier BurstDamage = new() { DamageDict = { ["Shock"] = 20 } };

    /// <summary>Added to every punch while raging.</summary>
    [DataField]
    public DamageSpecifier PunchBonus = new() { DamageDict = { ["Shock"] = 15, ["Structural"] = 100 } };

    /// <summary>Run (sprint) speed multiplier while raging.</summary>
    [DataField]
    public float SprintModifier = 1.5f;

    /// <summary>
    /// v8: berserk vision. 360°, this many tiles, needs line of sight (windows, glass doors and other mobs are
    /// see-through). Beyond it (or behind walls) only noises within <see cref="HearingRange"/> lead him.
    /// </summary>
    [DataField]
    public float VisionRange = 10f;

    /// <summary>v8: speech, screams, gunshots and explosions within this many tiles send him to the spot.</summary>
    [DataField]
    public float HearingRange = 15f;

    /// <summary>v8: his fists reach this much further while raging (v9: ×1.3, was ×1.5).</summary>
    [DataField]
    public float MeleeRangeMultiplier = 1.3f;

    /// <summary>Fist range before the rage (to put it back); server only.</summary>
    [ViewVariables]
    public float? BaseMeleeRange;

    /// <summary>Upper bound for the victim lookup (v8: vision itself is <see cref="VisionRange"/>).</summary>
    [DataField]
    public float HuntRange = 30f;

    // Screen interference while he is in the second phase (see GilbertProximitySystem).
    [DataField]
    public float InterferenceRange = 18f;

    [DataField]
    public float InterferenceFullRange = 9f;

    /// <summary>Peak interference while berserk; above 1 the shader goes into its stronger "rage" mode.</summary>
    [DataField]
    public float InterferenceStrength = 2f;

    /// <summary>
    /// v9: the dying sequence's only sound: «Прости! Прости!» cut from his original voice track (1.6 s), said once
    /// <see cref="DeathLineDelay"/> into the 4 s of shaking, before the energy explosion. Replaces the zombie wails.
    /// </summary>
    [DataField]
    public SoundSpecifier DeathLineSound = new SoundPathSpecifier("/Audio/_CorvaxGoob/Gilbert/gilbert_death_sorry_1.ogg",
        AudioParams.Default.WithVolume(6f).WithMaxDistance(20f));

    [DataField]
    public TimeSpan DeathLineDelay = TimeSpan.FromSeconds(0.4);

    /// <summary>Set once the death line has been said (server only).</summary>
    [ViewVariables]
    public bool DeathLineSaid;

    /// <summary>
    /// v8: one growl-into-roar clip (6.898 s) played once at the start of the transition, instead of the
    /// overlapping zombie screams. v9: the transition lasts exactly as long (<see cref="TransitionDuration"/>).
    /// </summary>
    [DataField]
    public SoundSpecifier TransitionSound = new SoundPathSpecifier("/Audio/_CorvaxGoob/Gilbert/gilbert_berserk_transition_1.ogg",
        AudioParams.Default.WithVolume(6f).WithMaxDistance(20f));

    /// <summary>
    /// v8: soundtrack of the berserk phase proper (5:02, mono, about -16 LUFS), positional on him, 15 tiles.
    /// Starts <see cref="RageMusicDelay"/> after the rage begins (after the skull-crack shout), stops when the
    /// dying sequence starts, on death and on deletion. Looped only as a safety net (the rage lasts 5:00).
    /// </summary>
    [DataField]
    public SoundSpecifier RageMusicSound = new SoundPathSpecifier("/Audio/_CorvaxGoob/Gilbert/gilbert_berserk_ambience_1.ogg",
        AudioParams.Default.WithVolume(-6f).WithMaxDistance(15f).WithLoop(true));

    /// <summary>Delay after the rage begins so the music doesn't step on the skull-crack shout (about 3.2 s).</summary>
    [DataField]
    public TimeSpan RageMusicDelay = TimeSpan.FromSeconds(3.5);

    /// <summary>The playing berserk soundtrack (server only).</summary>
    [ViewVariables]
    public EntityUid? RageMusic;

    /// <summary>Set once the berserk soundtrack has been started (it never starts twice).</summary>
    [ViewVariables]
    public bool RageMusicStarted;

    [DataField]
    public SoundSpecifier BurstSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningbolt.ogg",
        AudioParams.Default.WithVolume(6f).WithMaxDistance(20f));

    [DataField]
    public SoundSpecifier ExplosionSound = new SoundPathSpecifier("/Audio/Effects/explosion1.ogg",
        AudioParams.Default.WithVolume(6f).WithMaxDistance(25f));

    [DataField]
    public SoundSpecifier CrackleSound = new SoundCollectionSpecifier("sparks",
        AudioParams.Default.WithVolume(-2f).WithVariation(0.2f));

    /// <summary>
    /// Shouted the moment the transition ends and the rage begins, together with the chat shout
    /// «Я ТЕБЕ ЧЕРЕП ПРОЛОМЛЮ!!!». The transition screams are cut off right before it so it is heard.
    /// </summary>
    [DataField]
    public SoundSpecifier SkullShoutSound = new SoundPathSpecifier("/Audio/_CorvaxGoob/Gilbert/gilbert_berserk_skull_1.ogg",
        AudioParams.Default.WithVolume(8f).WithMaxDistance(20f));

    /// <summary>Screams (and the burst bang) still playing; stopped when he shouts. Server only.</summary>
    [ViewVariables]
    public List<EntityUid> LoudAudio = new();

    /// <summary>
    /// Rage punch hit sound (replaces the normal punch): a heavy thump layered with an electric zap.
    /// </summary>
    [DataField]
    public SoundSpecifier PunchSound = new SoundCollectionSpecifier("GilbertBerserkPunch",
        AudioParams.Default.WithVolume(2f).WithVariation(0.08f).WithMaxDistance(12f));

    [DataField]
    public EntProtoId ArcPrototype = "GilbertSpawnArc";

    [DataField]
    public EntProtoId BurstRingPrototype = "GilbertBerserkBurstRing";

    [DataField]
    public EntProtoId StreamPrototype = "GilbertBerserkStream";

    [DataField]
    public EntProtoId CracklePrototype = "GilbertBerserkCrackle";

    [DataField]
    public EntProtoId PunchPrototype = "GilbertBerserkPunch";

    [DataField]
    public EntProtoId PunchShardPrototype = "GilbertBerserkPunchShard";

    [DataField]
    public EntProtoId ExplosionPrototype = "GilbertBerserkExplosion";

    /// <summary>Number of energy streams running out from him along the floor at the burst.</summary>
    [DataField]
    public int StreamCount = 16;

    [ViewVariables, AutoNetworkedField]
    public GilbertBerserkPhase Phase = GilbertBerserkPhase.None;

    /// <summary>When the current <see cref="Phase"/> started.</summary>
    [ViewVariables, AutoNetworkedField]
    public TimeSpan PhaseStart;

    /// <summary>Server only: while true, damage passes (the clamped trigger hit and the final death).</summary>
    [ViewVariables]
    public bool AllowDamage;

    [ViewVariables]
    public bool BurstDone;

    [ViewVariables]
    public bool Fallen;

    [ViewVariables]
    public TimeSpan NextArc;

    [ViewVariables]
    public TimeSpan NextCrackle;

    /// <summary>Next tile (1..burst radius) the burst streams reach; 0 = no streams running.</summary>
    [ViewVariables]
    public int StreamStep;

    [ViewVariables]
    public TimeSpan NextStreamStep;

    [ViewVariables]
    public List<Angle> StreamAngles = new();
}

[Serializable, NetSerializable]
public enum GilbertBerserkPhase : byte
{
    /// <summary>First phase, his normal self.</summary>
    None,

    /// <summary>6.9 s: stopped, shaking and screaming, charging up and bursting out.</summary>
    Transition,

    /// <summary>5 min: hunting everything alive, fists only.</summary>
    Rage,

    /// <summary>4 s: stopped again, shaking and screaming, then falls and explodes.</summary>
    Dying,

    /// <summary>Exploded: dead for good.</summary>
    Dead,
}
