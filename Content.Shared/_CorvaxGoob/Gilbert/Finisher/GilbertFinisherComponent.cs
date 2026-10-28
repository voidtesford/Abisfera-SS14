using System.Numerics;
using Content.Shared.Mobs;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._CorvaxGoob.Gilbert.Finisher;

/// <summary>
/// Present on both participants while a <see cref="GilbertFinisherPrototype"/> cutscene plays.
/// Locks movement, turning, attacks and interactions; the client animates the sprite layers from
/// <see cref="StartTime"/>, the server moves the entities and plays the sounds.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), AutoGenerateComponentPause]
public sealed partial class GilbertFinisherComponent : Component
{
    [DataField, AutoNetworkedField]
    public ProtoId<GilbertFinisherPrototype> Finisher = "GilbertFinisherPhase1";

    [DataField, AutoNetworkedField]
    public GilbertFinisherRole Role;

    [DataField, AutoNetworkedField]
    public EntityUid? Partner;

    /// <summary>Real time of animation time 0 (after the <see cref="Intro"/>, so it can lie in the future).</summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan StartTime;

    /// <summary>
    /// Seconds of the prototype's intro (animation time -Intro..0, the holster beat) played before
    /// <see cref="StartTime"/>; 0 when skipped (the attacker had nothing in its hands).
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Intro;

    /// <summary>World-space unit vector from the attacker towards the victim (a cardinal direction).</summary>
    [DataField, AutoNetworkedField]
    public Vector2 Forward = new(1, 0);

    /// <summary>World position of the attacker at t=0 (server only).</summary>
    [ViewVariables]
    public Vector2 Origin;

    /// <summary>Clamp for the forward movement when a wall is closer than the prototype needs (server only).</summary>
    [ViewVariables]
    public float MaxForward = float.MaxValue;

    /// <summary>
    /// Fraction of the prototype's flight displacement actually used (shortened near walls / space; server only).
    /// Negative until the flight starts.
    /// </summary>
    [ViewVariables]
    public float FlightScale = -1f;

    /// <summary>Index of the next event to fire (server sounds / client effects keep their own cursor).</summary>
    [ViewVariables]
    public int NextEvent;

    /// <summary>
    /// Victim mob state held by <see cref="GilbertFinisherPrototype.HoldAlive"/> (worse states are refused) until the
    /// kill beat / the end (server only).
    /// </summary>
    [ViewVariables]
    public MobState? HeldState;

    /// <summary>Leave the victim lying (knocked down) when the cutscene ends.</summary>
    [DataField, AutoNetworkedField]
    public bool KeepDown = true;
}
