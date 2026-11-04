using System.Numerics;
using Robust.Shared.Audio;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

public enum ShogkkZombifiedAIState : byte
{
    /// <summary>Roams the place from one random spot to another (or trails his horde's leader).</summary>
    Wander,

    /// <summary>A victim in view (or just lost): after him, fists or the tool in hand.</summary>
    Chase,

    /// <summary>Lost the victim, or heard another zombified groan: off to where the victim was last.</summary>
    Hunt,
}

/// <summary>
/// AI of an ordinary SHOGKK zombified (ShogkkZombifiedAISystem): wanders the place at random looking for victims
/// (alive humanoids that are neither operatives nor zombified, Gilbert included), chases the one he sees and beats him
/// in melee, groans to call other zombified in earshot to the hunt, and drifts into loose hordes with those he meets.
/// Never touches guns, no radio.
/// </summary>
[RegisterComponent]
public sealed partial class ShogkkZombifiedAIComponent : Component
{
    [ViewVariables]
    public ShogkkZombifiedAIState State = ShogkkZombifiedAIState.Wander;

    [DataField]
    public TimeSpan ThinkInterval = TimeSpan.FromSeconds(0.5);

    [ViewVariables]
    public TimeSpan NextThink;

    /// <summary>Same eyes as the operatives: a forward cone out to this range, 360° up close, walls block.</summary>
    [DataField]
    public float VisionRange = 12f;

    [DataField]
    public float VisionAngle = 200f;

    [DataField]
    public float ProximityAwarenessRange = 2.5f;

    [DataField]
    public float DoorOpenRange = 1.6f;

    #region Victim

    [ViewVariables]
    public EntityUid? Target;

    [ViewVariables]
    public TimeSpan LastSeenTarget;

    [ViewVariables]
    public EntityCoordinates? LastKnownPosition;

    /// <summary>Out of sight this long: he stops chasing and hunts at the last known position.</summary>
    [DataField]
    public TimeSpan LoseTargetDelay = TimeSpan.FromSeconds(4);

    /// <summary>How long a hunt (lost victim, or a groan heard) lasts before he wanders again.</summary>
    [DataField]
    public TimeSpan HuntDuration = TimeSpan.FromSeconds(20);

    [ViewVariables]
    public TimeSpan HuntUntil;

    #endregion

    #region Groan

    /// <summary>Other zombified within this range hear his groan and join the hunt.</summary>
    [DataField]
    public float GroanRange = 15f;

    [DataField]
    public TimeSpan GroanCooldown = TimeSpan.FromSeconds(8);

    [ViewVariables]
    public TimeSpan NextGroan;

    [DataField]
    public SoundSpecifier GroanSound = new SoundCollectionSpecifier("GoonZGroan");

    [DataField]
    public LocId GroanEmote = "shogkk-zombified-groan-emote";

    #endregion

    #region Horde

    /// <summary>Zombified who see each other within this range band together.</summary>
    [DataField]
    public float HordeRange = 12f;

    /// <summary>This close (with nothing solid between) they find each other whichever way they face.</summary>
    [DataField]
    public float HordeHearRange = 4f;

    /// <summary>Further than this from his leader (or the leader dead): he is on his own again.</summary>
    [DataField]
    public float HordeLeaveRange = 25f;

    [DataField]
    public TimeSpan HordeCheckInterval = TimeSpan.FromSeconds(2);

    [ViewVariables]
    public TimeSpan NextHordeCheck;

    /// <summary>
    /// Whom he trails. Always a zombified with a lower uid (so no loops); a chain is followed up to its root, the
    /// horde's leader.
    /// </summary>
    [ViewVariables]
    public EntityUid? Leader;

    /// <summary>His spot around the leader (so the horde is a loose crowd, not a single file).</summary>
    [ViewVariables]
    public Vector2 FollowOffset;

    #endregion

    #region Wander

    [DataField]
    public float WanderMinDistance = 8f;

    [DataField]
    public float WanderMaxDistance = 40f;

    [ViewVariables]
    public EntityCoordinates? Destination;

    [ViewVariables]
    public TimeSpan DestinationTimeout;

    [ViewVariables]
    public TimeSpan PauseUntil;

    [ViewVariables]
    public Vector2 ProgressPosition;

    [ViewVariables]
    public TimeSpan ProgressTime;

    [DataField]
    public TimeSpan StallTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Spots (4x4 tile cells) he gave up on, avoided until the time given.</summary>
    [ViewVariables]
    public Dictionary<(EntityUid Grid, Vector2i Cell), TimeSpan> Blacklist = new();

    #endregion
}
