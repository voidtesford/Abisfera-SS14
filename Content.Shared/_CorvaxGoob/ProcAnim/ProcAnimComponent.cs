using System.Numerics;
using Content.Shared.Mobs;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._CorvaxGoob.ProcAnim;

/// <summary>
/// Present on every actor while a <see cref="ProcAnimScenePrototype"/> cutscene plays (added by the server ProcAnimSystem).
/// Locks movement, turning, attacks and interactions; the client animates the sprite layers from
/// <see cref="StartTime"/>, the server moves the entities and plays the sounds.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), AutoGenerateComponentPause]
public sealed partial class ProcAnimComponent : Component
{
    [DataField, AutoNetworkedField]
    public ProtoId<ProcAnimScenePrototype> Scene;

    /// <summary>This entity's index in <see cref="ProcAnimScenePrototype.Actors"/> / <see cref="Actors"/>.</summary>
    [DataField, AutoNetworkedField]
    public int Actor;

    /// <summary>Every actor of the scene, in role order (this one included).</summary>
    [DataField, AutoNetworkedField]
    public List<EntityUid> Actors = new();

    /// <summary>The default partner: the second actor (the first one for the second actor).</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? Partner;

    /// <summary>Real time of animation time 0 (after the <see cref="Intro"/>, so it can lie in the future).</summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan StartTime;

    /// <summary>
    /// Seconds of the prototype's intro (animation time -Intro..0, the holster beat) played before
    /// <see cref="StartTime"/>; 0 when skipped (no holstering actor had anything in its hands).
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Intro;

    /// <summary>
    /// World-space unit vector of the forward frame's +X: from the first actor towards the second, along a screen axis
    /// of <see cref="View"/> (so it is not world-cardinal on rotated grids).
    /// </summary>
    [DataField, AutoNetworkedField]
    public Vector2 Forward = new(1, 0);

    /// <summary>
    /// World rotation of the screen frame the scene was laid out in (= minus the eye rotation of the lead actor's camera,
    /// which follows its grid): screen axis <c>v</c> is <c>View.RotateVec(v)</c> in world space.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Angle View;

    /// <summary>World position of the first actor at t=0 (server only).</summary>
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
    /// Mob state held by <see cref="ProcAnimActor.HoldAlive"/> / <see cref="ProcAnimActor.HoldCrit"/> (worse states
    /// are refused) until the kill beat / the end (server only).
    /// </summary>
    [ViewVariables]
    public MobState? HeldState;

    /// <summary>Actors with <see cref="ProcAnimActor.KnockDown"/> are left lying when the cutscene ends.</summary>
    [DataField, AutoNetworkedField]
    public bool KeepDown = true;

    /// <summary>
    /// Seconds of the stow pre-beat (<see cref="ProcAnimActor.StowHands"/>) before the intro: the scene clock reads
    /// below <c>-Intro</c> during it. The same on every actor.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float PreBeat;

    /// <summary>
    /// The real entity bound to each <see cref="ProcAnimScenePrototype.Items"/> entry (<see cref="EntityUid.Invalid"/>:
    /// not found). The same list on every actor.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<EntityUid> Items = new();

    /// <summary>This actor's pre-beat reaches (stows, then start draws), in order: true = with the right hand.</summary>
    [DataField, AutoNetworkedField]
    public List<bool> StowRight = new();

    /// <summary>Server: this actor's bound items and where each came from.</summary>
    [ViewVariables]
    public List<ProcAnimBoundItem> Bound = new();

    /// <summary>Server: items put away by the stow pre-beat (given back at the end when the stow restores).</summary>
    [ViewVariables]
    public List<ProcAnimStowedItem> Stowed = new();

    [ViewVariables]
    public int NextStow;
}

/// <summary>Server bookkeeping of one <see cref="ProcAnimItem"/> bound to a real entity.</summary>
public sealed class ProcAnimBoundItem
{
    public int Index;
    public EntityUid Item;
    /// <summary>Inventory slot it was worn in (null: in a container / hand).</summary>
    public string? Slot;
    /// <summary>Container it was in (storage, holster...), with the entity owning it.</summary>
    public EntityUid? ContainerOwner;
    public string? ContainerId;
    public bool FromHand;
    public bool Drawn;
    public bool Returned;
    /// <summary>Held items unwielded to free a hand for it (wielded again when it goes back).</summary>
    public List<EntityUid> Unwielded = new();
}

/// <summary>Server bookkeeping of one item the stow pre-beat put away.</summary>
public sealed class ProcAnimStowedItem
{
    public EntityUid Item;
    /// <summary>It was a toggle (e.g. an extended shield) switched off before stowing.</summary>
    public bool WasActive;
    /// <summary>Hand it was taken from (it goes back there).</summary>
    public string Hand = "";
}
