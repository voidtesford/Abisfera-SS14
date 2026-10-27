using Content.Goobstation.Maths.FixedPoint;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// Gilbert's abilities: Scream (wordless howl + shockwave), Dash and Shove (counter-attack).
/// </summary>
[RegisterComponent]
public sealed partial class GilbertAbilitiesComponent : Component
{
    // ---- Scream ----

    /// <summary>Damage that has to be taken within <see cref="ScreamWindow"/> to trigger the scream.</summary>
    [DataField]
    public FixedPoint2 ScreamDamageThreshold = 75;

    [DataField]
    public TimeSpan ScreamWindow = TimeSpan.FromSeconds(30);

    [DataField]
    public float ScreamRange = 5f;

    [DataField]
    public TimeSpan ScreamStun = TimeSpan.FromSeconds(5);

    /// <summary>Blood every victim throws up.</summary>
    [DataField]
    public FixedPoint2 ScreamBloodVomit = 30;

    [DataField]
    public SoundSpecifier ScreamSound = new SoundCollectionSpecifier("GilbertScream");

    [DataField]
    public EntProtoId ScreamEffect = "GilbertShockwaveEffect";

    /// <summary>Red energy crackles thrown out together with the white shockwave.</summary>
    [DataField]
    public EntProtoId ScreamCrackleEffect = "GilbertScreamCrackle";

    /// <summary>How many red sparks fly out around him when he screams.</summary>
    [DataField]
    public int ScreamSparks = 6;

    /// <summary>Red energy burst left where a dash starts and where it ends.</summary>
    [DataField]
    public EntProtoId DashBurstEffect = "GilbertDashBurst";

    /// <summary>Red afterimage segment left along the dash path.</summary>
    [DataField]
    public EntProtoId DashTrailEffect = "GilbertDashTrail";

    /// <summary>Distance in tiles between two dash trail segments.</summary>
    [DataField]
    public float DashTrailSpacing = 0.5f;

    /// <summary>Red impact flash on the victim of his shove.</summary>
    [DataField]
    public EntProtoId ShoveImpactEffect = "GilbertShoveImpact";

    /// <summary>Small red spark on whoever his bare-handed punch hits.</summary>
    [DataField]
    public EntProtoId PunchSparkEffect = "GilbertPunchSpark";

    /// <summary>Damage events taken recently, used for the scream trigger.</summary>
    [ViewVariables]
    public List<(TimeSpan Time, FixedPoint2 Amount)> RecentDamage = new();

    // ---- Dash ----

    [DataField]
    public float DashDistance = 4f;

    [DataField]
    public TimeSpan DashStun = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan DashCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Max distance between Gilbert (after the dash) and his target for the dash to hit.</summary>
    [DataField]
    public float DashHitRange = 1.5f;

    [DataField]
    public SoundSpecifier DashSound = new SoundPathSpecifier("/Audio/Effects/thudswoosh.ogg");

    [ViewVariables]
    public TimeSpan NextDash;

    // ---- Shove ----

    [DataField]
    public TimeSpan ShoveStun = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan ShoveCooldown = TimeSpan.FromSeconds(180);

    [DataField]
    public SoundSpecifier ShoveSound = new SoundCollectionSpecifier("GilbertShove");

    [ViewVariables]
    public TimeSpan NextShove;
}
