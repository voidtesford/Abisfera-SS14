using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// v13: Gilbert's phase-2 (rage) abilities: a pouncing leap that drags the victim into a stomp-and-throw scene,
/// a cone of red discharges, and levitation with a cascade of thin red lightning. Run by
/// <see cref="GilbertRageAbilitiesSystem"/>, used by his AI while raging and by the <c>gilbertability</c> command.
/// </summary>
[RegisterComponent]
public sealed partial class GilbertRageAbilitiesComponent : Component
{
    /// <summary>Gap between any two of these abilities, so they don't all go off in the same second.</summary>
    [DataField]
    public TimeSpan GlobalGap = TimeSpan.FromSeconds(4);

    [ViewVariables]
    public TimeSpan NextAny;

    // ---- Leap ----

    [DataField]
    public TimeSpan LeapCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Crouched wind-up with discharges running over him before he jumps.</summary>
    [DataField]
    public TimeSpan LeapWindup = TimeSpan.FromSeconds(0.9);

    [DataField]
    public float LeapRange = 9f;

    [DataField]
    public float LeapMinRange = 2.5f;

    /// <summary>He lands next to the victim; this close (or closer) when he lands, it's a hit.</summary>
    [DataField]
    public float LeapHitRange = 1.6f;

    [DataField]
    public TimeSpan LeapKnockdown = TimeSpan.FromSeconds(3);

    [DataField]
    public ProtoId<Content.Shared._CorvaxGoob.ProcAnim.ProcAnimScenePrototype> LeapScene = "GilbertLeapPounce";

    /// <summary>
    /// v13.1: how far (tiles) the leap's victim is thrown forward from where he lets go of it. The scene's own flight
    /// covers it (shortened by walls); the real throw at the end only adds whatever the scene did not.
    /// </summary>
    [DataField]
    public float LeapThrowDistance = 5f;

    [DataField]
    public float LeapThrowSpeed = 9f;

    [DataField]
    public SoundSpecifier LeapSound = new SoundPathSpecifier("/Audio/Effects/thudswoosh.ogg");

    [DataField]
    public SoundSpecifier LandSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningshock.ogg");

    [ViewVariables]
    public TimeSpan NextLeap;

    [ViewVariables]
    public EntityUid? LeapTarget;

    [ViewVariables]
    public TimeSpan LeapAt;

    [ViewVariables]
    public TimeSpan NextLeapFx;

    [ViewVariables]
    public int Leaps;

    [ViewVariables]
    public int LeapHits;

    /// <summary>Victims actually thrown (after the scene, or straight away if it couldn't play).</summary>
    [ViewVariables]
    public int LeapThrows;

    /// <summary>Right after the pounce throw his rage punches wait until then: the victim flies off first.</summary>
    [ViewVariables]
    public TimeSpan PunchGraceUntil;

    /// <summary>Levitation bolts that hit something alive (the rest hit the floor).</summary>
    [ViewVariables]
    public int LevitateLivingHits;

    // ---- Red discharge cone ----

    [DataField]
    public TimeSpan ConeCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Discharges run over him for this long before the cone goes out.</summary>
    [DataField]
    public TimeSpan ConeWindup = TimeSpan.FromSeconds(0.7);

    [DataField]
    public float ConeRange = 15f;

    [DataField]
    public float ConeHalfAngle = 30f;

    /// <summary>Speed of the wave front, tiles per second.</summary>
    [DataField]
    public float ConeSpeed = 22f;

    [DataField]
    public FixedPoint2 ConeShock = 15;

    [DataField]
    public TimeSpan ConeStun = TimeSpan.FromSeconds(3);

    [DataField]
    public SoundSpecifier ConeSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningbolt.ogg");

    [ViewVariables]
    public TimeSpan NextCone;

    [ViewVariables]
    public TimeSpan ConeAt;

    /// <summary>Set while the wind-up or the wave runs.</summary>
    [ViewVariables]
    public bool ConeActive;

    [ViewVariables]
    public bool ConeFired;

    [ViewVariables]
    public EntityUid? ConeTarget;

    [ViewVariables]
    public Vector2 ConeDir;

    [ViewVariables]
    public Vector2 ConeOrigin;

    [ViewVariables]
    public float ConeFront;

    /// <summary>Free distance along each of the cone's rays (walls stop the wave).</summary>
    [ViewVariables]
    public float[] ConeRays = Array.Empty<float>();

    [ViewVariables]
    public HashSet<EntityUid> ConeHit = new();

    [ViewVariables]
    public TimeSpan NextConeFx;

    [ViewVariables]
    public int Cones;

    // ---- Levitation ----

    /// <summary>75 s: between the cone (30 s) and the leap (60 s) it'd be too often, 90 s too rare for a 5 min rage.</summary>
    [DataField]
    public TimeSpan LevitateCooldown = TimeSpan.FromSeconds(75);

    /// <summary>Rising on the threads before the first bolt.</summary>
    [DataField]
    public TimeSpan LevitateRise = TimeSpan.FromSeconds(1.2);

    [DataField]
    public TimeSpan LevitateDuration = TimeSpan.FromSeconds(15);

    [DataField]
    public int LevitateStrikes = 100;

    [DataField]
    public float LevitateRange = 8f;

    [DataField]
    public FixedPoint2 LevitateShock = 5;

    /// <summary>Chance a bolt goes for a living thing in reach (if there is one) rather than a random spot.</summary>
    [DataField]
    public float LevitateLivingChance = 0.8f;

    [DataField]
    public SoundSpecifier BoltSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningshock.ogg");

    [ViewVariables]
    public TimeSpan NextLevitate;

    [ViewVariables]
    public bool Levitating;

    [ViewVariables]
    public TimeSpan LevitateStart;

    [ViewVariables]
    public int StrikesDone;

    [ViewVariables]
    public TimeSpan NextLevitateFx;

    [ViewVariables]
    public int Levitations;
}

/// <summary>v13: hit by Gilbert's red cone: stunned, jittering, red discharges crawling all over them.</summary>
[RegisterComponent]
public sealed partial class GilbertShockedComponent : Component
{
    [ViewVariables]
    public TimeSpan End;

    [ViewVariables]
    public TimeSpan NextFx;
}

/// <summary>v13: on the leap's victim between the scene's "release" beat and its end: thrown away for real then.</summary>
[RegisterComponent]
public sealed partial class GilbertLeapThrowComponent : Component
{
    [ViewVariables]
    public EntityUid Gilbert;

    /// <summary>World direction (normalized) of the throw: the way Gilbert faces.</summary>
    [ViewVariables]
    public Vector2 Dir;

    /// <summary>v13.1: the victim's world position at the release - the 5 tiles are counted from here.</summary>
    [ViewVariables]
    public Vector2 From;
}
