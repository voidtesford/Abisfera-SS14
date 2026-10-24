using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Actions;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Claw Whirlwind": throw the target over yourself, knee it in the jaw (skull fracture + brain trauma),
/// the target slowly spins up into the air and crashes down. Whole sequence is about 2 s, the target can't move.
/// </summary>
public sealed partial class ClawWhirlwindActionEvent : EntityTargetActionEvent
{
    /// <summary>
    /// Damage of the knee strike, always dealt to the head.
    /// </summary>
    [DataField]
    public DamageSpecifier Damage = new();

    /// <summary>
    /// How far behind the user (tiles from the user's center) the target lands after the throw.
    /// </summary>
    [DataField]
    public float FlipDistance = 0.9f;

    [DataField]
    public TimeSpan FlipDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Time in the air after the knee: slow rise while spinning, then the fall.
    /// </summary>
    [DataField]
    public TimeSpan AirDuration = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The target stays held (can't move) this long after landing, so the whole move lasts about 2 s.
    /// </summary>
    [DataField]
    public TimeSpan LandHold = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Max visual height (tiles) of the launch.
    /// </summary>
    [DataField]
    public float LaunchHeight = 0.8f;

    /// <summary>
    /// Full turns of the spin while in the air.
    /// </summary>
    [DataField]
    public float Spins = 2f;

    [DataField]
    public TimeSpan KnockdownTime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long after the knee a Wolf Dance from the same user gets the Whirlwind variant.
    /// </summary>
    [DataField]
    public TimeSpan MarkDuration = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Integrity removed from the brain (an organ has 15; below 10 it's "damaged", 0 destroys it).
    /// </summary>
    [DataField]
    public FixedPoint2 BrainTraumaSeverity = 6;

    [DataField]
    public EntProtoId? ImpactEffect = "ProperKungFuKneeBurst";

    [DataField]
    public SoundSpecifier? FlipSound;

    [DataField]
    public SoundSpecifier? KneeSound;

    [DataField]
    public SoundSpecifier? LandSound;
}

[Serializable, NetSerializable]
public enum ClawWhirlwindPhase : byte
{
    /// <summary>Thrown over the user to the other side.</summary>
    Flip,
    /// <summary>After the knee: slow rise with a continuous spin, then the fall.</summary>
    Air,
    /// <summary>Landed or interrupted: reset the sprite.</summary>
    Land,
}

/// <summary>
/// On the target while the Claw Whirlwind holds it. Networked so the client also blocks movement input.
/// The timeline itself is only used by the server.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ClawWhirlwindedComponent : Component
{
    [ViewVariables]
    public EntityUid User;

    [ViewVariables]
    public ClawWhirlwindPhase Phase = ClawWhirlwindPhase.Flip;

    [ViewVariables]
    public TimeSpan PhaseStart;

    [ViewVariables]
    public Vector2 From;

    [ViewVariables]
    public Vector2 To;

    [ViewVariables]
    public MapId Map;

    /// <summary>
    /// When the target is released (end of the hold after landing).
    /// </summary>
    [ViewVariables]
    public TimeSpan ReleaseAt;

    [ViewVariables]
    public bool Landed;

    [ViewVariables]
    public ClawWhirlwindActionEvent? Event;
}

/// <summary>
/// Left by the Claw Whirlwind knee for 2 s: a Wolf Dance from the same user becomes a single straight dash
/// that sends the target flying and makes the user fall.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuWhirlwindMarkComponent : Component
{
    [ViewVariables]
    public EntityUid Marker;

    [ViewVariables]
    public TimeSpan ExpiresAt;
}

/// <summary>
/// Sent to clients in PVS so they animate the target's sprite for the current Claw Whirlwind phase.
/// </summary>
[Serializable, NetSerializable]
public sealed class ClawWhirlwindAnimationEvent : EntityEventArgs
{
    public NetEntity Target;
    public ClawWhirlwindPhase Phase;
    public float Duration;
    public Vector2 Direction;
    public float Height;
    public float Spins;

    public ClawWhirlwindAnimationEvent(NetEntity target, ClawWhirlwindPhase phase, float duration, Vector2 direction, float height, float spins)
    {
        Target = target;
        Phase = phase;
        Duration = duration;
        Direction = direction;
        Height = height;
        Spins = spins;
    }
}
