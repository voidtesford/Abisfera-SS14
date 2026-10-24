using Content.Shared.Actions;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Wolf Dance: dash to the target and finish with a single roundhouse kick.
/// </summary>
public sealed partial class WolfDanceActionEvent : EntityTargetActionEvent
{
    [DataField]
    public DamageSpecifier Damage = new();

    [DataField]
    public TimeSpan StunTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How far the target is knocked back, in tiles.
    /// </summary>
    [DataField]
    public float KnockbackDistance = 3f;

    [DataField]
    public float KnockbackSpeed = 10f;

    /// <summary>
    /// Max distance (tiles) to the target. Should match the action's TargetAction range.
    /// </summary>
    [DataField]
    public float MaxRange = 5.5f;

    /// <summary>
    /// Distance from the target's center where the dash stops.
    /// </summary>
    [DataField]
    public float StopDistance = 0.8f;

    /// <summary>
    /// How many afterimages to leave along the dash path.
    /// </summary>
    [DataField]
    public int Afterimages = 4;

    [DataField]
    public EntProtoId? AfterimageEffect = "ProperKungFuDashAfterimage";

    [DataField]
    public EntProtoId? ShockwaveEffect = "ProperKungFuShockwaveEffect";

    [DataField]
    public SoundSpecifier? DashSound;

    [DataField]
    public SoundSpecifier? HitSound;

    [DataField]
    public SoundSpecifier? BlockedSound;

    /// <summary>
    /// Speed of the shield flying out of the target's hands when the kick is blocked.
    /// </summary>
    [DataField]
    public float ShieldThrowSpeed = 6f;
}

/// <summary>
/// Raised on the user whenever a Proper Kung Fu special move connects with a target.
/// Hook for the ultimate meter (+5 per special hit).
/// </summary>
[ByRefEvent]
public readonly record struct ProperKungFuSpecialHitEvent(EntityUid Target, string Move, bool Blocked);
