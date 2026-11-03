using Content.Shared.Radio;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

public enum ShogkkOperativeAIState : byte
{
    /// <summary>Walks the map: explores the least-known areas, reports in on the radio.</summary>
    Patrol,
    /// <summary>Shooting (or knifing) a visible enemy.</summary>
    Combat,
    /// <summary>Lost sight of the enemy: checks the last known position and looks around it.</summary>
    Search,
    /// <summary>Heading to a spot from the radio: an enemy an ally reported, or an ally calling for help.</summary>
    Assist,
    /// <summary>Patching himself up with his first-aid kit.</summary>
    Heal,
}

public enum ShogkkOperativeTask : byte
{
    None,
    Reload,
    Heal,
}

public enum ShogkkOperativeWeaponMode : byte
{
    Ranged,
    Melee,
}

public enum ShogkkOperativePhrase : byte
{
    /// <summary>«Вижу противника» + coordinates.</summary>
    EnemySpotted,
    /// <summary>«Нужна помощь».</summary>
    NeedHelp,
    /// <summary>«Докладываю».</summary>
    Report,
}

/// <summary>An enemy this operative knows about, seen himself or heard over the radio.</summary>
public struct ShogkkKnownEnemy
{
    public EntityCoordinates Position;

    public TimeSpan Time;

    public ShogkkKnownEnemy(EntityCoordinates position, TimeSpan time)
    {
        Position = position;
        Time = time;
    }
}

/// <summary>
/// The brains of a SHOGKK operative (rank and file, see ShogkkOperativeComponent): patrols and explores the map with
/// a memory of where he has been, attacks enemies on sight with his rifle / SMG / pump shotgun, reloads from his bag,
/// switches to the combat knife when out of ammo, patches himself up when safe and talks to the other operatives over
/// the SHOGKK radio (three phrases, each on a cooldown).
/// </summary>
[RegisterComponent]
public sealed partial class ShogkkOperativeAIComponent : Component
{
    #region Thinking / vision

    [ViewVariables]
    public ShogkkOperativeAIState State = ShogkkOperativeAIState.Patrol;

    /// <summary>How often the AI thinks. The first think is randomized, so a squad never thinks on the same tick.</summary>
    [DataField]
    public TimeSpan ThinkInterval = TimeSpan.FromSeconds(0.35);

    [ViewVariables]
    public TimeSpan NextThink;

    [DataField]
    public float VisionRange = 15f;

    /// <summary>Full angle of the forward vision cone, degrees.</summary>
    [DataField]
    public float VisionAngle = 200f;

    /// <summary>360° awareness within this range.</summary>
    [DataField]
    public float ProximityAwarenessRange = 2.5f;

    [DataField]
    public float DoorOpenRange = 1.6f;

    #endregion

    #region Combat

    [ViewVariables]
    public EntityUid? Target;

    [ViewVariables]
    public TimeSpan LastSeenTarget;

    [ViewVariables]
    public EntityCoordinates? LastKnownPosition;

    /// <summary>Out of sight this long: off to search for him.</summary>
    [DataField]
    public TimeSpan LoseTargetDelay = TimeSpan.FromSeconds(5);

    [DataField]
    public float RangedKeepDistance = 7f;

    [ViewVariables]
    public ShogkkOperativeWeaponMode WeaponMode = ShogkkOperativeWeaponMode.Ranged;

    /// <summary>Main weapon (given on spawn).</summary>
    [ViewVariables]
    public EntityUid? Gun;

    /// <summary>Nothing left to reload with: knife only, until he finds ammo again.</summary>
    [ViewVariables]
    public bool OutOfAmmo;

    [ViewVariables]
    public TimeSpan LastDamaged = TimeSpan.Zero;

    #endregion

    #region Tasks

    [ViewVariables]
    public ShogkkOperativeTask Task;

    [ViewVariables]
    public int TaskStep;

    [ViewVariables]
    public TimeSpan TaskNextStep;

    [ViewVariables]
    public TimeSpan TaskTimeout;

    [ViewVariables]
    public EntityUid? TaskItem;

    [ViewVariables]
    public int TaskCounter;

    [ViewVariables]
    public TimeSpan TaskMark;

    [ViewVariables]
    public float TaskDamageBefore;

    /// <summary>The gun the task is about (reload) or that is put back in his hands after it (healing).</summary>
    [ViewVariables]
    public EntityUid? TaskGun;

    /// <summary>Time between pulling the empty magazine and seating the new one.</summary>
    [DataField]
    public TimeSpan MagazineSwapTime = TimeSpan.FromSeconds(1.2);

    /// <summary>Successful reloads, for tests / admins.</summary>
    [ViewVariables]
    public int Reloads;

    #endregion

    #region Healing

    /// <summary>Patches up once this much damage is taken...</summary>
    [DataField]
    public float HealThreshold = 20f;

    /// <summary>...and nothing has hit him for this long (and no enemy is in sight).</summary>
    [DataField]
    public TimeSpan HealSafeDelay = TimeSpan.FromSeconds(8);

    [ViewVariables]
    public TimeSpan NextHealAttempt;

    [ViewVariables]
    public HashSet<EntityUid> HealBlacklist = new();

    #endregion

    #region Radio

    [DataField]
    public ProtoId<RadioChannelPrototype> RadioChannel = "GilbertShogkk";

    /// <summary>Who hears a phrase (other operatives) and what it shares: 40 tiles.</summary>
    [DataField]
    public float RadioRange = 40f;

    /// <summary>
    /// t340u: every phrase has its own cooldown per operative, on top of its own trigger ("докладываю" still fires on
    /// its 6-minute patrol cycle; "вижу противника" once per new enemy and then no more than once per cooldown).
    /// </summary>
    [DataField]
    public TimeSpan RadioCooldown = TimeSpan.FromSeconds(60);

    [ViewVariables]
    public Dictionary<ShogkkOperativePhrase, TimeSpan> NextPhrase = new();

    /// <summary>Times each phrase was actually said (tests / admins).</summary>
    [ViewVariables]
    public Dictionary<ShogkkOperativePhrase, int> PhraseCount = new();

    /// <summary>Enemies he has already called out ("one phrase per new enemy").</summary>
    [ViewVariables]
    public HashSet<EntityUid> ReportedEnemies = new();

    /// <summary>«Нужна помощь»: at this much health (or less) left before going critical...</summary>
    [DataField]
    public float HelpHealthMargin = 40f;

    /// <summary>...with no allied operative within this range.</summary>
    [DataField]
    public float HelpAllyRange = 10f;

    /// <summary>How many of the nearest free allies come to help.</summary>
    [DataField]
    public int HelpResponders = 3;

    /// <summary>«Докладываю»: once every this long while patrolling.</summary>
    [DataField]
    public TimeSpan ReportInterval = TimeSpan.FromMinutes(6);

    [ViewVariables]
    public TimeSpan NextReport;

    /// <summary>Allies' positions from their reports / help calls.</summary>
    [ViewVariables]
    public Dictionary<EntityUid, ShogkkKnownEnemy> AllyPositions = new();

    #endregion

    #region Memory / patrol

    [ViewVariables]
    public Dictionary<EntityUid, ShogkkKnownEnemy> KnownEnemies = new();

    /// <summary>How long an enemy position stays in memory.</summary>
    [DataField]
    public TimeSpan EnemyMemory = TimeSpan.FromMinutes(5);

    /// <summary>Explored map cells (grid, cell of <see cref="CellSize"/> tiles) and when he (or an ally) was last there.</summary>
    [ViewVariables]
    public Dictionary<(EntityUid Grid, Vector2i Cell), TimeSpan> Visited = new();

    [DataField]
    public int CellSize = 4;

    /// <summary>Memory cap: the oldest cells are forgotten first.</summary>
    [DataField]
    public int MaxVisited = 600;

    /// <summary>A cell visited this long ago is as good as unexplored.</summary>
    [DataField]
    public TimeSpan VisitedForget = TimeSpan.FromMinutes(10);

    [ViewVariables]
    public EntityCoordinates? Destination;

    [ViewVariables]
    public TimeSpan DestinationTimeout;

    [ViewVariables]
    public TimeSpan PauseUntil;

    [DataField]
    public float PatrolMinDistance = 5f;

    [DataField]
    public float PatrolMaxDistance = 18f;

    /// <summary>No progress for this long: the destination is given up and its cell avoided for a while.</summary>
    [DataField]
    public TimeSpan StallTimeout = TimeSpan.FromSeconds(6);

    [ViewVariables]
    public System.Numerics.Vector2 ProgressPosition;

    [ViewVariables]
    public TimeSpan ProgressTime;

    [ViewVariables]
    public Dictionary<(EntityUid Grid, Vector2i Cell), TimeSpan> Blacklist = new();

    #endregion

    #region Search / assist

    [ViewVariables]
    public TimeSpan StateUntil;

    [DataField]
    public TimeSpan SearchDuration = TimeSpan.FromSeconds(40);

    /// <summary>Going to an ally who called for help (followed while he moves).</summary>
    [ViewVariables]
    public EntityUid? AssistAlly;

    [ViewVariables]
    public EntityCoordinates? AssistPosition;

    [DataField]
    public TimeSpan AssistTimeout = TimeSpan.FromSeconds(90);

    #endregion
}
