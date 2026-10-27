using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._CorvaxGoob.Redspace;

/// <summary>
/// The Euclidean gate while it is unfolding: an anchored stand-in that plays the one-shot unfold animation
/// (pillars rise from the base, join at the top, the ring lights up) and is swapped for <see cref="Gate"/>
/// once <see cref="Duration"/> has passed.
/// </summary>
/// <remarks>
/// State is generated with raiseAfterAutoHandleState so the client can resync the animation to
/// <see cref="Start"/> (late joiners, PVS re-entry).
/// </remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), AutoGenerateComponentPause]
public sealed partial class RedspaceGateUnfoldingComponent : Component
{
    /// <summary>
    /// Entity spawned when unfolding finishes.
    /// </summary>
    [DataField(required: true), AutoNetworkedField]
    public EntProtoId Gate;

    /// <summary>
    /// Total time until the gate appears. Matches the unfold animation up to its final (held) frame.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan Duration = TimeSpan.FromSeconds(5.4);

    /// <summary>
    /// When the two pillars meet at the top (light flare, connection sound). Matches the flare frame.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan ConnectTime = TimeSpan.FromSeconds(4.1);

    /// <summary>
    /// Offsets from the start at which <see cref="RiseSound"/> plays while the pillars rise.
    /// </summary>
    [DataField]
    public List<TimeSpan> RiseSoundTimes = new() { TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(3.0) };

    [DataField]
    public SoundSpecifier? StartSound = new SoundPathSpecifier("/Audio/Machines/airlock_creaking.ogg");

    [DataField]
    public SoundSpecifier? RiseSound = new SoundPathSpecifier("/Audio/Items/ratchet.ogg");

    [DataField]
    public SoundSpecifier? ConnectSound = new SoundPathSpecifier("/Audio/Machines/boltsdown.ogg");

    [DataField]
    public SoundSpecifier? ConnectSparksSound = new SoundCollectionSpecifier("sparks");

    [DataField]
    public SoundSpecifier? FinishSound = new SoundPathSpecifier("/Audio/Machines/phasein.ogg");

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan Start;

    /// <summary>Server bookkeeping: how many of the timed sounds already played.</summary>
    [ViewVariables]
    public int RiseSoundsPlayed;

    [ViewVariables]
    public bool Connected;
}

[Serializable, NetSerializable]
public enum RedspaceGateUnfoldingLayers : byte
{
    Unfold,
}
