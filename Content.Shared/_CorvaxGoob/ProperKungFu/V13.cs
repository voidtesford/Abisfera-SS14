using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Actions;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// How a <see cref="ProperKungFuTintZoneComponent"/> recolours the world inside its circle.
/// </summary>
[Serializable, NetSerializable]
public enum ProperKungFuTintMode : byte
{
    /// <summary>Time stop: everything grey.</summary>
    TimeStop,

    /// <summary>Wolf Judgment red flurry: a deep crimson tint.</summary>
    Crimson,

    /// <summary>Wolf Judgment barrage: harsh black and white.</summary>
    Noir,

    /// <summary>Normal colours (the zone draws nothing).</summary>
    None,

    /// <summary>Wolf Seal apex: black, white silhouettes (edges) and horizontal white speed lines.</summary>
    Silhouette,
}

/// <summary>
/// A circle of the world recoloured on every client that sees it (client ProperKungFuTintOverlay):
/// grey inside a time stop, crimson / black-and-white during Wolf Judgment. Fades in after <see cref="Start"/>
/// and out before <see cref="End"/>.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ProperKungFuTintZoneComponent : Component
{
    [DataField, AutoNetworkedField]
    public float Radius = 20f;

    [DataField, AutoNetworkedField]
    public ProperKungFuTintMode Mode = ProperKungFuTintMode.TimeStop;

    [DataField, AutoNetworkedField]
    public TimeSpan Start;

    [DataField, AutoNetworkedField]
    public TimeSpan End;

    /// <summary>Seconds of the fade in after <see cref="Start"/> (the circle also grows from the centre).</summary>
    [DataField, AutoNetworkedField]
    public float FadeIn = 0.3f;

    /// <summary>Seconds of the fade out before <see cref="End"/>.</summary>
    [DataField, AutoNetworkedField]
    public float FadeOut = 0.4f;
}

/// <summary>
/// Ultimate-only "Wolf Judgment" (Волчий суд): a jump and a huge cross of two claw cuts over a 3x6 area in front
/// (towards the cursor), 20 Slash each to everyone in it, knocking them down; never the user, their clones or the
/// Pack Dogs. The first use with a hit in each ultimate mode is the full version: a dash to the nearest victim and
/// the red-flurry / black-and-white barrage / double palm combo (120 more Slash to the chest, 20 tiles of knockback).
/// Every later use in the same mode is only the cross.
/// </summary>
public sealed partial class ProperKungFuWolfJudgmentActionEvent : WorldTargetActionEvent
{
    /// <summary>Damage of each of the two cross cuts.</summary>
    [DataField]
    public DamageSpecifier CutDamage = new() { DamageDict = { ["Slash"] = FixedPoint2.New(20) } };

    [DataField]
    public float AreaLength = 6f;

    [DataField]
    public float AreaWidth = 3f;

    [DataField]
    public TimeSpan KnockdownTime = TimeSpan.FromSeconds(3);

    [DataField]
    public SoundSpecifier? CutSound = new SoundPathSpecifier("/Audio/Weapons/Xeno/alien_claw_flesh3.ogg");

    [DataField]
    public SoundSpecifier? JumpSound = new SoundPathSpecifier("/Audio/Effects/thudswoosh.ogg");
}

/// <summary>
/// Short sprite poses played on clients in PVS (client ProperKungFuPoseAnimationSystem).
/// </summary>
[Serializable, NetSerializable]
public enum ProperKungFuPose : byte
{
    /// <summary>Up and back down with a slight stretch (Wolf Judgment jump).</summary>
    Jump,

    /// <summary>Squashed low, then back up (the crouch after Wolf Judgment).</summary>
    Crouch,

    /// <summary>Starts high in the air and slowly falls, spinning all the way down.</summary>
    SlowFall,

    /// <summary>Kept in the air, spinning, for the whole duration (launched far).</summary>
    Launch,
}

[Serializable, NetSerializable]
public sealed class ProperKungFuPoseAnimationEvent : EntityEventArgs
{
    public NetEntity Target;
    public ProperKungFuPose Pose;
    public float Duration;
    public float Height;
    public float Spins;

    public ProperKungFuPoseAnimationEvent(NetEntity target, ProperKungFuPose pose, float duration, float height, float spins)
    {
        Target = target;
        Pose = pose;
        Duration = duration;
        Height = height;
        Spins = spins;
    }
}

/// <summary>
/// Ultimate-only "Wolf Gaze" (Волчий взгляд), the Claw Mastery version of Wolf Instinct: a short stance; the first
/// attack (melee, thrown or shot) from someone within <see cref="TriggerRange"/> tiles is blocked, the stance ends and
/// a cutscene starts: the user catches the attacker, both freeze for a few seconds of tension, then a spear-hand
/// thrust deals <see cref="Damage"/> to the chest and launches the attacker <see cref="KnockbackDistance"/> tiles.
/// Only a successful trigger spends the one use per ultimate activation; a stance that ends without an attack puts
/// the action on <see cref="MissCooldown"/> instead.
/// </summary>
public sealed partial class ProperKungFuWolfGazeActionEvent : InstantActionEvent
{
    /// <summary>
    /// Cooldown put on the action when the stance ends without anyone attacking. That use is not spent: the counter
    /// can be used again later in the same ultimate. Only a successful trigger spends the one use per ultimate.
    /// </summary>
    [DataField]
    public TimeSpan MissCooldown = TimeSpan.FromMinutes(2);

    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(3);

    [DataField]
    public float TriggerRange = 3f;

    /// <summary>Seconds of the frozen tension before the thrust.</summary>
    [DataField]
    public float TensionTime = 3f;

    /// <summary>Seconds of the grey freeze-frame after the thrust.</summary>
    [DataField]
    public float FreezeFrameTime = 0.7f;

    [DataField]
    public DamageSpecifier Damage = new() { DamageDict = { ["Blunt"] = FixedPoint2.New(150) } };

    [DataField]
    public float KnockbackDistance = 10f;

    [DataField]
    public TimeSpan KnockdownTime = TimeSpan.FromSeconds(3);

    [DataField]
    public SoundSpecifier? CatchSound = new SoundPathSpecifier("/Audio/Weapons/punchmiss.ogg");

    [DataField]
    public SoundSpecifier? HeartbeatSound = new SoundPathSpecifier("/Audio/_Goobstation/Heretic/heartbeat.ogg");

    [DataField]
    public SoundSpecifier? ThrustSound = new SoundPathSpecifier("/Audio/Effects/metal_slam5.ogg", AudioParams.Default.WithVolume(6));
}

/// <summary>
/// Sent to the two participants of a Wolf Gaze cutscene: a closing dark vignette for <see cref="Duration"/> seconds,
/// and (for the victim only, <see cref="ShowText"/>) a yellow "THIS POWE-" line during the freeze-frame.
/// </summary>
[Serializable, NetSerializable]
public sealed class ProperKungFuGazeCinematicEvent : EntityEventArgs
{
    public float Duration;
    public float TextAt;
    public bool ShowText;

    public ProperKungFuGazeCinematicEvent(float duration, float textAt, bool showText)
    {
        Duration = duration;
        TextAt = textAt;
        ShowText = showText;
    }
}

/// <summary>
/// Ultimate-only "Wolf Seal" (Волчья печать): dash in (if not adjacent), an uppercut that pops the target up, a
/// black-and-white apex, a double palm thrust into the chest and a release that throws the target
/// <see cref="KnockbackDistance"/> tiles. The target can't breathe for <see cref="BreathBlockTime"/> (cleared on
/// death) and is strongly dazed (knocked down, stunned and disoriented) for <see cref="DazeTime"/>.
/// Not limited per ultimate: only the cooldown.
/// </summary>
public sealed partial class ProperKungFuWolfSealActionEvent : EntityTargetActionEvent
{
    /// <summary>Furthest the target may be (tiles); beyond arm's reach the user blinks next to it first.</summary>
    [DataField]
    public float MaxRange = 3.5f;

    [DataField]
    public DamageSpecifier UppercutDamage = new() { DamageDict = { ["Blunt"] = FixedPoint2.New(10) } };

    /// <summary>Dealt once per palm (<see cref="PalmHits"/> palms).</summary>
    [DataField]
    public DamageSpecifier PalmDamage = new() { DamageDict = { ["Blunt"] = FixedPoint2.New(10) } };

    [DataField]
    public int PalmHits = 2;

    [DataField]
    public float KnockbackDistance = 5f;

    [DataField]
    public TimeSpan BreathBlockTime = TimeSpan.FromSeconds(20);

    [DataField]
    public TimeSpan DazeTime = TimeSpan.FromSeconds(5);

    [DataField]
    public EntProtoId DisorientEffect = "StatusEffectProperKungFuDisoriented";

    [DataField]
    public SoundSpecifier? UppercutSound = new SoundPathSpecifier("/Audio/Effects/hit_kick.ogg", AudioParams.Default.WithVolume(3));

    [DataField]
    public SoundSpecifier? PalmSound = new SoundPathSpecifier("/Audio/Weapons/punch4.ogg", AudioParams.Default.WithVolume(3));

    [DataField]
    public SoundSpecifier? ReleaseSound = new SoundPathSpecifier("/Audio/Effects/explosion_small1.ogg", AudioParams.Default.WithVolume(3));
}

/// <summary>
/// The Claw Mastery fan kunai: thrown by its summoner it splits into one kunai per <see cref="Angles"/> entry
/// (degrees from the throw direction). None of them pulls the user or starts a kunai finisher, and every one of them
/// is gone after it hits or lands. In melee it is a normal kunai.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuFanKunaiComponent : Component
{
    [DataField]
    public List<float> Angles = new() { -30f, -15f, 0f, 15f, 30f };

    /// <summary>A kunai of the fan spawned by the split (never splits again).</summary>
    [DataField]
    public bool Child;

    /// <summary>Set once it left a hand as a throw.</summary>
    [ViewVariables]
    public bool Flying;

    /// <summary>Throw vector and speed saved from the throw (BeforeThrowEvent) to throw the rest of the fan alike.</summary>
    [ViewVariables]
    public System.Numerics.Vector2? Direction;

    [ViewVariables]
    public float Speed;
}
