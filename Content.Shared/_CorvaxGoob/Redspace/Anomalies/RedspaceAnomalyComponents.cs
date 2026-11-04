using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.Redspace.Anomalies;

/// <summary>The zone events of redspace (see RedspaceAnomalyEventSystem on the server).</summary>
public enum RedspaceAnomalyEvent : byte
{
    Rift,
    TimeLoop,
    Doppelganger,
    BloodRain,
    MirrorCorridor,
    Whispers,
    GravityKnot,
}

/// <summary>Inside a mirror corridor: movement input is inverted (predicted).</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class RedspaceMirroredComponent : Component;

/// <summary>
/// Temporary redspace mutations from long exposure. Removed when the mob gets protected, becomes immune, or each
/// one runs out (server-side timers).
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class RedspaceMutationComponent : Component
{
    /// <summary>Speech comes out distorted (stammered, echoed syllables).</summary>
    [DataField, AutoNetworkedField]
    public bool Voice;

    /// <summary>Movement speed multiplier while slowed (1 = not slowed).</summary>
    [DataField, AutoNetworkedField]
    public float Slowdown = 1f;

    /// <summary>A third eye opens on the forehead (client overlay layer).</summary>
    [DataField, AutoNetworkedField]
    public bool ExtraEye;

    [ViewVariables] public float VoiceLeft;
    [ViewVariables] public float SlowLeft;
    [ViewVariables] public float EyeLeft;
}

/// <summary>A gun jammed by redspace: it can't fire until the timer runs out.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class RedspaceJammedComponent : Component
{
    [ViewVariables] public float Left;
}

/// <summary>Distorted copy of <see cref="Source"/>; the client copies its sprite and adds a glitch layer.</summary>
/// <remarks>Raises <see cref="AfterAutoHandleStateEvent"/> so the client can dress the copy as soon as the source arrives.</remarks>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class RedspaceDoppelgangerComponent : Component
{
    [DataField, AutoNetworkedField]
    public EntityUid? Source;
}

/// <summary>What a private hallucination looks like.</summary>
public enum RedspaceHallucinationKind : byte
{
    /// <summary>A figure that creeps up to the player and vanishes.</summary>
    Stalker,
    /// <summary>A low many-legged thing that scuttles at the player.</summary>
    Crawler,
}

/// <summary>Sent to one client only: show a fake enemy at <see cref="Coordinates"/> that walks at the player.</summary>
[Serializable, NetSerializable]
public sealed class RedspaceHallucinationEvent : EntityEventArgs
{
    public NetCoordinates Coordinates;
    public RedspaceHallucinationKind Kind;
    public float Duration;

    public RedspaceHallucinationEvent(NetCoordinates coordinates, RedspaceHallucinationKind kind, float duration)
    {
        Coordinates = coordinates;
        Kind = kind;
        Duration = duration;
    }
}
