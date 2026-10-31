using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Gilbert;

public enum GilbertAIState : byte
{
    /// <summary>Passive: slow wandering, idle lines.</summary>
    Idle,
    /// <summary>Ordered someone to surrender and waits for compliance, then cuffs them.</summary>
    Surrender,
    /// <summary>Fighting a visible enemy.</summary>
    Combat,
    /// <summary>Lost the target: goes to the last known position and searches around it.</summary>
    Search,
    /// <summary>No enemy around and below half health: patches himself up with real medkit contents.</summary>
    Heal,
    /// <summary>Heard something (speech, scream, gunshot, explosion, running): turns, goes to check the spot, looks around.</summary>
    Investigate,
    /// <summary>
    /// A cuffed person kept hanging around him: he walked up, punched them («Убирайтесь!») and now stands at that
    /// spot facing them. Still near and in view after <see cref="GilbertAIComponent.ExpelWatchTime"/>: combat.
    /// </summary>
    Expel,
}

/// <summary>
/// Kinds of sound Gilbert reacts to.
/// </summary>
public enum GilbertNoise : byte
{
    None,
    Speech,
    Scream,
    Gunshot,
    Explosion,
    Sprint,
}

/// <summary>
/// A multi-step manual job Gilbert performs with his hands (reload, grenade, C4, cuffs, healing...).
/// </summary>
public enum GilbertTask : byte
{
    None,
    Reload,
    Grenade,
    PlantC4,
    Cuff,
    HealItem,
    /// <summary>Walk to a useful item on the floor and pick it up (shells into his boxes).</summary>
    Pickup,
    /// <summary>
    /// Search a cuffed / crit / dead person: knock a standing cuffed one down, frisk (do-after), then strip
    /// every useful item with the normal strip do-afters, including taking off and emptying their backpack.
    /// </summary>
    Loot,
    /// <summary>v9: walk to one of his own lost weapons, pick it up and put it back where it belongs.</summary>
    Recover,
}

public enum GilbertWeaponMode : byte
{
    Ranged,
    Melee,
}

/// <summary>
/// Gilbert's own AI. Not HTN: a deterministic state machine that drives the stock NPC steering and
/// NPC ranged/melee combat components and uses his real equipment through normal interactions.
/// </summary>
[RegisterComponent]
public sealed partial class GilbertAIComponent : Component
{
    // ---- Perception ----

    [DataField]
    public float VisionRange = 15f;

    /// <summary>Full width of the vision cone in degrees.</summary>
    [DataField]
    public float VisionAngle = 180f;

    [DataField]
    public TimeSpan ThinkInterval = TimeSpan.FromSeconds(0.1);

    /// <summary>
    /// 360° awareness: anyone this close (his own tile and the adjacent ones) counts as seen even behind him.
    /// </summary>
    [DataField]
    public float ProximityAwarenessRange = 1.5f;

    // ---- Search ----

    /// <summary>How long he looks around at each spot he checks while searching.</summary>
    [DataField]
    public TimeSpan SearchLookTime = TimeSpan.FromSeconds(1.5); // v9: a short glance, he doesn't stand around

    /// <summary>Adjacent rooms around the last known position he checks before widening the sweep.</summary>
    [DataField]
    public int SearchRooms = 3;

    // ---- Exploring (passive patrol) ----

    /// <summary>Patrol destinations are picked this far away (world units / tiles).</summary>
    [DataField]
    public float ExploreMinDistance = 10f;

    [DataField]
    public float ExploreMaxDistance = 30f;

    /// <summary>Visited-area memory works on square cells of this many tiles.</summary>
    [DataField]
    public int ExploreCellSize = 4;

    /// <summary>Random destinations scored per pick.</summary>
    [DataField]
    public int ExploreCandidates = 32;

    /// <summary>Cells he visited longer ago than this count as unvisited again.</summary>
    [DataField]
    public TimeSpan ExploreMemory = TimeSpan.FromMinutes(10);

    /// <summary>Short pause on arrival before the next destination (always under the stall limit).</summary>
    [DataField]
    public TimeSpan ExplorePauseMin = TimeSpan.FromSeconds(0); // v9: no standing around

    [DataField]
    public TimeSpan ExplorePauseMax = TimeSpan.FromSeconds(0.15);

    // ---- Watchdogs ----

    /// <summary>Standing still this long without a reason (fight in view, do-after, looking around) counts as a stall.</summary>
    [DataField]
    public TimeSpan StallTimeout = TimeSpan.FromSeconds(1.2);

    /// <summary>A single walk that hasn't arrived after this long is abandoned.</summary>
    [DataField]
    public TimeSpan MoveTimeout = TimeSpan.FromSeconds(25);

    /// <summary>A task (reload, C4, heal...) that makes no progress for this long is aborted.</summary>
    [DataField]
    public TimeSpan TaskStallTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Closed doors this close in his walking direction get opened (he carries an all-access ID).</summary>
    [DataField]
    public float DoorOpenRange = 1.5f;

    // ---- Reloading (v8) ----

    /// <summary>After a reload that put no shell in, he fights with what he has (melee) this long before trying again.</summary>
    [DataField]
    public TimeSpan ReloadRetryCooldown = TimeSpan.FromSeconds(8);

    /// <summary>The fill do-after putting no new shell in the gun for this long counts as a failed reload.</summary>
    [DataField]
    public TimeSpan ReloadNoGainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The reload line is said at most this often (and only when shells actually start going in).</summary>
    [DataField]
    public TimeSpan ReloadLineCooldown = TimeSpan.FromSeconds(12);

    /// <summary>A box that failed to load the gun isn't tried again for this long.</summary>
    [DataField]
    public TimeSpan BadBoxTime = TimeSpan.FromSeconds(60);

    // ---- Hearing ----

    /// <summary>Speech, screams, gunshots, explosions and sprinting are heard within this range.</summary>
    [DataField]
    public float HearingRange = 15f;

    /// <summary>Loud emotes that count as a scream-like noise.</summary>
    [DataField]
    public HashSet<string> HeardEmotes = new() { "Scream", "Laugh", "Crying", "Whistle", "Clap", "Snap", "Honk" };

    /// <summary>A sprinting entity is re-heard this often while it keeps sprinting.</summary>
    [DataField]
    public TimeSpan SprintHearInterval = TimeSpan.FromSeconds(0.5);

    /// <summary>How long he looks around once he reaches the spot he heard something at.</summary>
    [DataField]
    public TimeSpan InvestigateLookTime = TimeSpan.FromSeconds(3);

    /// <summary>Gives up investigating a sound after this long.</summary>
    [DataField]
    public TimeSpan InvestigateTimeout = TimeSpan.FromSeconds(30);

    // ---- Idle ----

    [DataField]
    public TimeSpan IdleLineInterval = TimeSpan.FromMinutes(2);

    [DataField]
    public float WanderRadius = 6f;

    /// <summary>Walk speed multiplier while passive / ordering surrender.</summary>
    [DataField]
    public float IdleSpeedModifier = 0.45f;

    // ---- Surrender ----

    [DataField]
    public float SurrenderRange = 5f;

    [DataField]
    public TimeSpan SurrenderWait = TimeSpan.FromSeconds(5);

    /// <summary>Moving this far from the spot where they were ordered to stop gets a warning.</summary>
    [DataField]
    public float ArrestWarnDistance = 3f;

    /// <summary>After the warning: moving this much further means he engages.</summary>
    [DataField]
    public float ArrestChaseDistance = 5f;

    /// <summary>After the warning they have this long to stop moving before he engages.</summary>
    [DataField]
    public TimeSpan ArrestStopTime = TimeSpan.FromSeconds(3);

    /// <summary>Speed below which the target counts as standing still.</summary>
    [DataField]
    public float ArrestStillSpeed = 0.3f;

    // ---- Combat ----

    /// <summary>How long a target may be out of sight before Gilbert considers it lost.</summary>
    [DataField]
    public TimeSpan LoseTargetDelay = TimeSpan.FromSeconds(4);

    /// <summary>Without an enemy in the vision cone for this long, Gilbert goes back to passive mode.</summary>
    [DataField]
    public TimeSpan SearchTimeout = TimeSpan.FromMinutes(3);

    [DataField]
    public float RangedKeepDistance = 4f;

    [DataField]
    public float MeleeSwitchRange = 1.6f;

    [DataField]
    public float RangedSwitchRange = 4.5f;

    [DataField]
    public TimeSpan WeaponSwitchCooldown = TimeSpan.FromSeconds(6);

    [DataField]
    public float GrenadeMinRange = 4f;

    [DataField]
    public float GrenadeMaxRange = 9f;

    [DataField]
    public TimeSpan GrenadeCooldown = TimeSpan.FromSeconds(25);

    [DataField]
    public TimeSpan C4Cooldown = TimeSpan.FromSeconds(40);

    [DataField]
    public float DashMinRange = 2.2f;

    [DataField]
    public float DashMaxRange = 6f;

    // ---- Healing ----

    /// <summary>Start healing at or above this much damage (500 HP total, so 250 = below half health).</summary>
    [DataField]
    public FixedPoint2 HealStartDamage = 250;

    /// <summary>Stop healing once damage is at or below this.</summary>
    [DataField]
    public FixedPoint2 HealStopDamage = 100;

    /// <summary>Never heal if he got hurt more recently than this.</summary>
    [DataField]
    public TimeSpan HealSafeDelay = TimeSpan.FromSeconds(5);

    /// <summary>Unattributed damage below this per hit (e.g. medicine side effects) does not count as being under fire.</summary>
    [DataField]
    public FixedPoint2 UnderFireDamage = 3;

    /// <summary>v9: starts treating bleeding this fast (bandages, gauze, tourniquets, styptics).</summary>
    [DataField]
    public float HealBleedThreshold = 1.5f;

    /// <summary>v9: starts restoring blood below this fraction (saline, iron, blood packs)...</summary>
    [DataField]
    public float HealBloodThreshold = 0.8f;

    /// <summary>v9: ...and keeps at it up to this one.</summary>
    [DataField]
    public float HealBloodStop = 0.92f;

    /// <summary>v9: an injection is worth it only against at least this much damage of the kinds it treats.</summary>
    [DataField]
    public float InjectorMinDamage = 40f;

    /// <summary>v9: topicals (bruise packs, ointment, gauze) are preferred by this factor.</summary>
    [DataField]
    public float TopicalPreference = 1.25f;

    /// <summary>v9: this hurt, he uses an autoinjector mid-fight once the enemy is out of sight.</summary>
    [DataField]
    public FixedPoint2 HealCriticalDamage = 380;

    [DataField]
    public TimeSpan HealCriticalDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>Damage group each injector (by prototype) treats. Topicals are matched by their HealingComponent.</summary>
    [DataField]
    public Dictionary<EntProtoId, string> InjectorGroups = new()
    {
        { "BruteAutoInjector", "Brute" },
        { "BurnAutoInjector", "Burn" },
    };

    // ---- Lines ----

    [DataField]
    public string AudioPath = "/Audio/_CorvaxGoob/Gilbert";

    [DataField]
    public Dictionary<string, int> LineCounts = new()
    {
        { "idle", 7 },
        { "found", 2 },
        { "surrender", 1 },
        { "noncomply", 1 },
        { "lost", 3 },
        { "halfhealth", 3 },
        { "reload", 2 },
        { "kill", 6 },
        { "getout", 1 },
    };

    /// <summary>
    /// v9: an enemy he saw within this long is not "found" again (no contact / «Я же говорил...» line) when he
    /// re-spots them; after a longer absence the «Я же говорил...» re-encounter line is used.
    /// </summary>
    [DataField]
    public TimeSpan ReencounterMemory = TimeSpan.FromSeconds(45);

    /// <summary>v9: no two lines closer together than this (orders like the surrender demand are exempt).</summary>
    [DataField]
    public TimeSpan GlobalLineCooldown = TimeSpan.FromSeconds(4);

    /// <summary>v9: the same kind of line is not repeated sooner than this.</summary>
    [DataField]
    public Dictionary<string, TimeSpan> LineCooldowns = new()
    {
        { "found", TimeSpan.FromSeconds(30) },
        { "lost", TimeSpan.FromSeconds(30) },
        { "kill", TimeSpan.FromSeconds(10) },
        { "halfhealth", TimeSpan.FromSeconds(30) },
        { "noncomply", TimeSpan.FromSeconds(15) },
    };

    /// <summary>v9: never held back by the cooldowns: they are orders, the gameplay depends on hearing them.</summary>
    [DataField]
    public HashSet<string> EssentialLines = new() { "surrender", "getout" };

    // ---- Scavenging ----

    /// <summary>How far he notices useful loot (it must also be in sight).</summary>
    [DataField]
    public float ScavengeRange = 8f;

    [DataField]
    public TimeSpan ScavengeInterval = TimeSpan.FromSeconds(1);

    /// <summary>Distance at which he strips a body.</summary>
    [DataField]
    public float LootRange = 1.5f;

    /// <summary>He stops collecting handcuffs once he carries this many.</summary>
    [DataField]
    public int MaxCuffs = 10;

    /// <summary>Items he failed to get are ignored this long.</summary>
    [DataField]
    public TimeSpan ScavengeIgnoreTime = TimeSpan.FromSeconds(60);

    // ---- Searching people ----

    /// <summary>The same person is searched at most once per this long.</summary>
    [DataField]
    public TimeSpan FriskCooldown = TimeSpan.FromMinutes(2);

    /// <summary>The visible "frisking" do-after before he starts taking things.</summary>
    [DataField]
    public TimeSpan FriskTime = TimeSpan.FromSeconds(3);

    /// <summary>A standing cuffed person is knocked down for this long (refreshed while the search runs).</summary>
    [DataField]
    public TimeSpan FriskKnockdownTime = TimeSpan.FromSeconds(8);

    /// <summary>The whole search (walk up, knock down, frisk, every strip) has to fit into this.</summary>
    [DataField]
    public TimeSpan FriskTaskTimeout = TimeSpan.FromSeconds(75);

    // ---- Cuffed loiterers ----

    /// <summary>
    /// "Near" him: within this many tiles with nothing solid in between (a window counts as solid here).
    /// v9 (t262u): 3 → 8 tiles, but beyond <see cref="LingerCloseRange"/> only while he actually sees them
    /// (in his view cone, nothing opaque in between): someone behind a wall doesn't count.
    /// </summary>
    [DataField]
    public float LingerRange = 8f;

    /// <summary>Up to this distance a loiterer counts without him having to look at them (the old v3 range).</summary>
    [DataField]
    public float LingerCloseRange = 3f;

    /// <summary>A cuffed person who has spent this long near him in total gets punched. v9 (t262u): 60 → 15 s.</summary>
    [DataField]
    public TimeSpan LingerPunchTime = TimeSpan.FromSeconds(15);

    /// <summary>The accumulated time is forgotten if the person stays away this long.</summary>
    [DataField]
    public TimeSpan LingerForget = TimeSpan.FromMinutes(2);

    /// <summary>After the punch: still near and in view for this long (in total) means combat. v9 (t262u): 30 → 10 s.</summary>
    [DataField]
    public TimeSpan ExpelWatchTime = TimeSpan.FromSeconds(10);

    /// <summary>After the punch: out of range / sight this long and he goes back to his business.</summary>
    [DataField]
    public TimeSpan ExpelGiveUp = TimeSpan.FromSeconds(10);

    /// <summary>He never stands there longer than this (e.g. a person lying still next to him the whole time).</summary>
    [DataField]
    public TimeSpan ExpelMaxWait = TimeSpan.FromMinutes(3);

    /// <summary>Lying on the floor without moving or speaking this long: ignored until they move or speak.</summary>
    [DataField]
    public TimeSpan DormantDelay = TimeSpan.FromSeconds(3);

    // ---- Threats ----

    /// <summary>He only drops his current (visible) target for one whose threat score is better by this much.</summary>
    [DataField]
    public float TargetSwitchMargin = 2f;

    /// <summary>Out of shells with an enemy in view: he looks for cover this far away before reloading.</summary>
    [DataField]
    public float CoverSearchRange = 5f;

    // ---- Runtime ----

    [ViewVariables] public GilbertAIState State = GilbertAIState.Idle;
    [ViewVariables] public GilbertTask Task = GilbertTask.None;
    [ViewVariables] public int TaskStep;
    [ViewVariables] public TimeSpan TaskNextStep;
    [ViewVariables] public TimeSpan TaskDeadline;
    [ViewVariables] public EntityUid? TaskItem;
    [ViewVariables] public EntityUid? TaskTarget;

    [ViewVariables] public GilbertWeaponMode WeaponMode = GilbertWeaponMode.Ranged;
    [ViewVariables] public TimeSpan NextWeaponSwitch;

    [ViewVariables] public TimeSpan NextThink;
    [ViewVariables] public TimeSpan NextIdleLine;
    [ViewVariables] public TimeSpan NextWander;
    [ViewVariables] public TimeSpan NextGrenade;
    [ViewVariables] public TimeSpan NextC4;

    /// <summary>Armed charges he knows about (his own, seen or heard ticking). See GilbertAISystem.Danger.</summary>
    [ViewVariables] public HashSet<EntityUid> KnownDangers = new();

    /// <summary>Currently running from a blast zone.</summary>
    [ViewVariables] public bool FleeingDanger;

    /// <summary>Where he's running to get clear of the blast.</summary>
    [ViewVariables] public EntityCoordinates? FleePoint;

    [ViewVariables] public TimeSpan FleeReplan;
    [ViewVariables] public TimeSpan LastDamaged;
    [ViewVariables] public TimeSpan NextHealAttempt;

    /// <summary>Charges (stack count / solution volume) of the heal item when he started using it.</summary>
    [ViewVariables] public float HealChargesBefore;
    [ViewVariables] public FixedPoint2 HealDamageBefore;
    [ViewVariables] public int HealFailures;
    /// <summary>Medical items that turned out not to be consumed on use (or kept failing); never used again.</summary>
    [ViewVariables] public HashSet<EntityUid> HealBlacklist = new();
    /// <summary>v9: an emergency mid-fight heal (fast injectors only).</summary>
    [ViewVariables] public bool HealCritical;

    [ViewVariables] public TimeSpan NextScavenge;
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> ScavengeIgnore = new();
    /// <summary>Slot (inventory slot or hand id) being stripped by the Loot task.</summary>
    [ViewVariables] public string? LootSlot;
    [ViewVariables] public bool LootIsHand;

    [ViewVariables] public EntityUid? Target;
    [ViewVariables] public EntityCoordinates? LastKnownPosition;
    /// <summary>Last time any enemy was inside his vision cone.</summary>
    [ViewVariables] public TimeSpan LastSeenEnemy;
    /// <summary>Last time the current target was seen.</summary>
    [ViewVariables] public TimeSpan LastSeenTarget;
    [ViewVariables] public float LastTargetDistance;
    [ViewVariables] public bool ArrivedAtLastKnown;

    [ViewVariables] public TimeSpan SurrenderOrderedAt;
    [ViewVariables] public bool SurrenderOrdered;
    [ViewVariables] public EntityCoordinates SurrenderTargetStart;
    [ViewVariables] public bool ArrestWarned;
    [ViewVariables] public TimeSpan ArrestWarnedAt;
    [ViewVariables] public EntityCoordinates ArrestWarnPosition;

    /// <summary>Where he wants to get to after blowing the blocking wall / airlock.</summary>
    [ViewVariables] public EntityCoordinates? BreachTarget;

    /// <summary>Visited-area memory: (grid, cell) -> when he was last there.</summary>
    [ViewVariables] public Dictionary<(EntityUid Grid, Vector2i Cell), TimeSpan> Visited = new();

    // Watchdogs / movement
    [ViewVariables] public System.Numerics.Vector2 ProgressPosition;
    [ViewVariables] public TimeSpan LastProgress;
    [ViewVariables] public EntityCoordinates? MoveTarget;
    [ViewVariables] public TimeSpan MoveStarted;
    [ViewVariables] public int StallCount;
    [ViewVariables] public TimeSpan SearchLookUntil;
    [ViewVariables] public int SearchRoomsChecked;
    [ViewVariables] public bool HoldingFacing;

    // Hearing / investigating
    [ViewVariables] public GilbertNoise LastNoise;
    [ViewVariables] public TimeSpan LastHeardAt;
    [ViewVariables] public EntityCoordinates? NoisePosition;
    [ViewVariables] public EntityUid? NoiseSource;
    [ViewVariables] public int HeardCount;

    // v8: a sound heard while a task he can't drop runs (reload, grenade, C4, cuffing, healing) is checked afterwards.
    [ViewVariables] public EntityCoordinates? PendingNoise;
    [ViewVariables] public TimeSpan PendingNoiseAt;
    // v8: unreachable spot: how many nearby fallback points he has tried.
    [ViewVariables] public int InvestigateFallbacks;
    [ViewVariables] public int InvestigationsStarted;
    [ViewVariables] public TimeSpan NextSprintCheck;
    [ViewVariables] public bool InvestigateArrived;
    [ViewVariables] public TimeSpan InvestigateLookUntil;
    [ViewVariables] public TimeSpan InvestigateDeadline;
    [ViewVariables] public TimeSpan NextLookTurn;

    // Searching people
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> FriskCooldowns = new();
    [ViewVariables] public bool FriskDone;
    /// <summary>The victim's backpack he took off and dropped, being emptied.</summary>
    [ViewVariables] public EntityUid? LootBag;
    /// <summary>The item (or backpack) currently being stripped off the victim.</summary>
    [ViewVariables] public EntityUid? LootItem;

    // Cuffed loiterers
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> Linger = new();
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> LingerLastNear = new();
    /// <summary>Last time each cuffed person nearby moved, stood or spoke.</summary>
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> LastActive = new();
    [ViewVariables] public EntityUid? ExpelTarget;
    [ViewVariables] public bool ExpelPunched;
    [ViewVariables] public EntityCoordinates ExpelSpot;
    [ViewVariables] public TimeSpan ExpelStarted;
    [ViewVariables] public TimeSpan ExpelLastSeen;
    [ViewVariables] public TimeSpan ExpelWatch;
    /// <summary>People he fights even though they are cuffed (loiterers who ignored the punch).</summary>
    [ViewVariables] public HashSet<EntityUid> ForcedHostile = new();

    // Threats / cover
    [ViewVariables] public EntityUid? LastAttacker;
    [ViewVariables] public TimeSpan LastAttackedAt;
    [ViewVariables] public EntityCoordinates? CoverPoint;
    [ViewVariables] public TimeSpan CoverUntil;
    [ViewVariables] public TimeSpan LastThink;
    [ViewVariables] public TimeSpan ThinkDelta;

    [ViewVariables] public bool SaidHalfHealth;
    [ViewVariables] public string? LastLine;
    /// <summary>v9: when each enemy was last in sight (re-encounter lines).</summary>
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> EnemyLastSeen = new();
    [ViewVariables] public Dictionary<string, TimeSpan> LineSaidAt = new();
    [ViewVariables] public TimeSpan LastLineAt = TimeSpan.MinValue;
    /// <summary>How many times each line category was said (v8: tests check there is no reload spam).</summary>
    [ViewVariables] public Dictionary<string, int> SaidCounts = new();

    // Reloading (v8)
    /// <summary>The storage (webbing / bag / pocket slot owner) the reload box came out of; it goes back there.</summary>
    [ViewVariables] public EntityUid? ReloadBoxOrigin;
    [ViewVariables] public string? ReloadBoxSlot;
    /// <summary>Live shells in the gun when the fill started / last time it went up.</summary>
    [ViewVariables] public int ReloadShellsBefore;
    [ViewVariables] public TimeSpan ReloadLastGain;
    [ViewVariables] public bool ReloadAnnounced;
    /// <summary>Failed reloads in a row; after a failure he doesn't try again before <see cref="NextReloadAttempt"/>.</summary>
    [ViewVariables] public int ReloadFailures;
    [ViewVariables] public TimeSpan NextReloadAttempt;
    [ViewVariables] public TimeSpan NextReloadLine;
    /// <summary>Boxes that failed to load the gun (wrong shells...): skipped until this time.</summary>
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> BadReloadBoxes = new();
    /// <summary>Total reloads begun / that put at least one shell in (tests, admin view).</summary>
    [ViewVariables] public int ReloadsStarted;
    [ViewVariables] public int ReloadsSucceeded;

    // Berserk (v6)
    /// <summary>Whoever he is beating to death right now in his second phase.</summary>
    [ViewVariables] public EntityUid? BerserkTarget;
    [ViewVariables] public TimeSpan NextBerserkPick;
    [ViewVariables] public Vector2 BerserkLastPos;
    [ViewVariables] public TimeSpan BerserkLastProgress;

    /// <summary>v9: the goal his progress is measured against, the closest he has got to it, the last victim picked.</summary>
    [ViewVariables] public Vector2 BerserkProgressGoal = new(float.MaxValue, float.MaxValue);
    [ViewVariables] public float BerserkBestGoalDist = float.MaxValue;
    [ViewVariables] public EntityUid? BerserkLastPicked;
    /// <summary>v9 (t258u) diagnostics: airlocks ripped open in the rage.</summary>
    [ViewVariables] public int BerserkDoorsRipped;

    /// <summary>Berserk: the obstacle he is breaking through to get at <see cref="BerserkTarget"/>.</summary>
    [ViewVariables] public EntityUid? BerserkObstacle;

    // Berserk (v8): vision, memory, hearing, smashing
    /// <summary>Where he last saw (or heard) his prey; he goes there and searches around once it's out of sight.</summary>
    [ViewVariables] public EntityCoordinates? BerserkLastKnown;
    [ViewVariables] public TimeSpan BerserkLastSeen;
    /// <summary>Searching around <see cref="BerserkLastKnown"/> until then.</summary>
    [ViewVariables] public TimeSpan BerserkSearchUntil;
    [ViewVariables] public TimeSpan NextBerserkSearchStep;
    /// <summary>Started on the current <see cref="BerserkObstacle"/> (to give up on one he can't get at).</summary>
    [ViewVariables] public TimeSpan BerserkObstacleSince;
    [ViewVariables] public TimeSpan NextBerserkRepath;
    /// <summary>Diagnostics / tests.</summary>
    [ViewVariables] public int BerserkRepaths;
    [ViewVariables] public int BerserkSmashPunches;
    [ViewVariables] public int BerserkUnstucks;

    // ---- v13: rage route choice (walk round vs smash through) ----
    /// <summary>He smashes through only if the walk-round path costs more than this many times the smash path...</summary>
    [DataField] public float BerserkDetourRatio = 2f;
    /// <summary>...and more than this many tiles extra.</summary>
    [DataField] public float BerserkDetourExtra = 8f;
    /// <summary>On a walk-round route: no progress for this long counts as stuck (sidestep, re-path).</summary>
    [DataField] public float BerserkWalkStuckTime = 2.5f;
    /// <summary>Stuck this many times on a walk-round route to the same goal: smash after all.</summary>
    [DataField] public int BerserkWalkStuckLimit = 2;
    /// <summary>The current goal is best reached by smashing through (no walk path, or a far longer one).</summary>
    [ViewVariables] public bool BerserkSmashRoute;
    [ViewVariables] public Vector2 BerserkRouteGoal = new(float.NaN, float.NaN);

    // ---- v13: rage finisher (phase 2) ----
    /// <summary>Victims the rage finisher was already started on (never twice on the same one).</summary>
    [ViewVariables] public HashSet<EntityUid> RageFinished = new();
    /// <summary>No new rage-finisher attempt before this (after a failed one).</summary>
    [ViewVariables] public TimeSpan NextRageFinisherTry;
    [ViewVariables] public TimeSpan BerserkRouteCheckedAt;
    [ViewVariables] public float? BerserkWalkCost;
    [ViewVariables] public float? BerserkSmashCost;
    [ViewVariables] public int BerserkWalkStucks;
    /// <summary>Diagnostics / tests: route evaluations finished.</summary>
    [ViewVariables] public int BerserkRouteChecks;
    public System.Threading.Tasks.Task<float?>? BerserkWalkTask;
    public System.Threading.Tasks.Task<float?>? BerserkSmashTask;
    public System.Threading.CancellationTokenSource? BerserkRouteCancel;

    // ---- v9: patrol memory, anti-oscillation, prediction, own gear ----

    /// <summary>
    /// v9: visit memory fades: a cell visited this long ago counts as half-forgotten (the score is exponential).
    /// </summary>
    [DataField]
    public TimeSpan VisitHalfLife = TimeSpan.FromMinutes(3);

    /// <summary>v9: walking somewhere but staying inside <see cref="OscillationRadius"/> this long = stuck / pacing.</summary>
    [DataField]
    public TimeSpan OscillationWindow = TimeSpan.FromSeconds(5);

    [DataField]
    public TimeSpan BerserkOscillationWindow = TimeSpan.FromSeconds(3);

    [DataField]
    public float OscillationRadius = 1.5f;

    /// <summary>v9: a destination he got stuck on is avoided this long.</summary>
    [DataField]
    public TimeSpan BlacklistTime = TimeSpan.FromSeconds(60);

    /// <summary>v9: how far ahead along the enemy's heading he guesses where they went.</summary>
    [DataField]
    public float PredictMaxDistance = 12f;

    /// <summary>v9: doors within this angle (degrees) of the enemy's heading are where they likely went.</summary>
    [DataField]
    public float PredictDoorCone = 65f;

    /// <summary>v9 berserk: he "smells" living things this far away and roams towards them.</summary>
    [DataField]
    public float BerserkScentRange = 40f;

    /// <summary>v9 berserk: a roam destination not reached in this long is dropped.</summary>
    [DataField]
    public TimeSpan BerserkRoamTimeout = TimeSpan.FromSeconds(20);

    /// <summary>v9: he goes back for his own lost weapons he sees within this range.</summary>
    [DataField]
    public float RecoverRange = 12f;

    /// <summary>v9: in a fight he only grabs a lost weapon this close (or once the enemy is out of sight).</summary>
    [DataField]
    public float RecoverCombatRange = 3f;

    [ViewVariables] public Dictionary<(EntityUid Grid, Vector2i Cell), TimeSpan> Blacklist = new();
    [ViewVariables] public Vector2 OscillationAnchor;
    [ViewVariables] public TimeSpan OscillationSince;

    /// <summary>v9: NoPath replans allowed while standing in a doorway, per walk target.</summary>
    [ViewVariables] public int DoorwayRepaths;
    [ViewVariables] public EntityCoordinates? OscillationGoal;
    /// <summary>Diagnostics / tests.</summary>
    [ViewVariables] public int Oscillations;

    /// <summary>v9: the enemy's velocity (map space) the last time he saw them.</summary>
    [ViewVariables] public Vector2 LastSeenVelocity;
    /// <summary>v9: guessed escape route after losing sight: doors / points along their heading, in order.</summary>
    [ViewVariables] public List<EntityCoordinates> PursuitPlan = new();
    /// <summary>v9: where the room search is centred and which way it leans (the predicted escape direction).</summary>
    [ViewVariables] public EntityCoordinates? SearchCenter;
    [ViewVariables] public Vector2 SearchBias;

    [ViewVariables] public EntityCoordinates? BerserkRoamGoal;
    [ViewVariables] public TimeSpan BerserkRoamStarted;
    [ViewVariables] public int BerserkRoams;

    /// <summary>v9: his own spawned weapons (shotgun, truncheon, shield) and where each one belongs.</summary>
    [ViewVariables] public Dictionary<EntityUid, string> OwnGear = new();
    [ViewVariables] public bool OwnGearRecorded;
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> RecoverIgnore = new();
    [ViewVariables] public int Recovered;

    /// <summary>
    /// v10.1 (t292u): in combat with a hostile in reach and in plain view but no shot / swing for this long (no
    /// cutscene, no task): the combat state is reset and the best weapon re-equipped into the active hand.
    /// </summary>
    [DataField]
    public TimeSpan AttackWatchdogDelay = TimeSpan.FromSeconds(3);

    [ViewVariables] public TimeSpan AttackStallSince;
    [ViewVariables] public long AttackSignature;
    [ViewVariables] public int AttackWatchdogResets;

    // ---- v9 (t256u): mental map of the station, routes over it, 5-minute trail ----

    /// <summary>The remembered map is scanned this often (not every tick).</summary>
    [DataField]
    public TimeSpan MapScanInterval = TimeSpan.FromSeconds(1);

    /// <summary>Tiles within this many tiles that he can see are remembered (walls, doors, floor).</summary>
    [DataField]
    public int MapScanRadius = 8;

    /// <summary>Route graph nodes are square chunks of this many tiles (a coarse room / corridor level graph).</summary>
    [DataField]
    public int MapChunkSize = 4;

    /// <summary>v9 (t259u): extra route cost (tiles) of a chunk with a table: the 1.5 s vault do-after ≈ that much walking.</summary>
    [DataField]
    public float ClimbRouteCost = 5f;

    /// <summary>v9 (t259u): the same in the rage (he vaults ×3 faster and runs).</summary>
    [DataField]
    public float BerserkClimbRouteCost = 2f;

    /// <summary>v9 (t258u): seconds to pry an airlock open with his bare hands (phase 1; bolts still stop him).</summary>
    [DataField]
    public float HandPryTime = 5f;

    /// <summary>v9 (t259u): do-after multiplier in the rage (climbing over tables: 1.5 s -> 0.45 s).</summary>
    [DataField]
    public float BerserkDoAfterMultiplier = 0.3f;

    /// <summary>v9: last watchdog tick (a pry / climb do-after pauses the walk timeout by that much).</summary>
    [ViewVariables]
    public TimeSpan LastWatchdog;

    /// <summary>Patrol destinations further than this (path length, tiles) are walked via remembered waypoints.</summary>
    [DataField]
    public float RouteMinDistance = 12f;

    /// <summary>Upper bound on route-graph nodes expanded per plan (keeps a plan cheap on big stations).</summary>
    [DataField]
    public int RouteMaxExpanded = 6000;

    /// <summary>How long his footsteps are remembered: patrol prefers chunks not walked within this window.</summary>
    [DataField]
    public TimeSpan TrailMemory = TimeSpan.FromMinutes(5);

    [ViewVariables] public GilbertMentalMap MentalMap = new();
    [ViewVariables] public TimeSpan NextMapScan;
    /// <summary>Remaining patrol waypoints (planned over the mental map), in order; the last is the destination.</summary>
    [ViewVariables] public List<EntityCoordinates> Route = new();
    /// <summary>The waypoint he is walking to now (a different move in between cancels the route).</summary>
    [ViewVariables] public EntityCoordinates? RouteLeg;
    /// <summary>Diagnostics / tests: patrol trips planned over the mental map.</summary>
    [ViewVariables] public int RoutesPlanned;

    // ---- v10 (t277u): finisher after a point-blank shotgun hit (phase 1 only) ----

    /// <summary>Master switch for the AI finisher.</summary>
    [DataField]
    public bool FinisherEnabled = true;

    /// <summary>Shots at a target further than this (tiles) never lead to a finisher.</summary>
    [DataField]
    public float FinisherShotRange = 4f;

    /// <summary>After the shot the victim must be alive (not crit) with at most this much damage left before crit.</summary>
    [DataField]
    public FixedPoint2 FinisherCritMargin = 20;

    /// <summary>v10.1: walks up to someone he fought who already lies in crit within <see cref="FinisherSeekRange"/>.</summary>
    [DataField]
    public bool FinisherSeekCrit = true;

    /// <summary>Tiles.</summary>
    [DataField]
    public float FinisherSeekRange = 4f;

    /// <summary>The pellets need a moment to land: the margin is checked this long after the shot...</summary>
    [DataField]
    public TimeSpan FinisherShotSettle = TimeSpan.FromSeconds(0.2);

    /// <summary>...and the shot is forgotten after this long.</summary>
    [DataField]
    public TimeSpan FinisherShotWindow = TimeSpan.FromSeconds(1.2);

    /// <summary>Distance (tiles) from the victim at which the cutscene starts.</summary>
    [DataField]
    public float FinisherStartRange = 1.3f;

    /// <summary>The run-up is dropped when the victim gets further than this (tiles) or it takes longer than <see cref="FinisherApproachTimeout"/>.</summary>
    [DataField]
    public float FinisherAbortRange = 5f;

    [DataField]
    public TimeSpan FinisherApproachTimeout = TimeSpan.FromSeconds(4);

    /// <summary>The same victim can't get another finisher attempt for this long (after a start or an abort).</summary>
    [DataField]
    public TimeSpan FinisherVictimCooldown = TimeSpan.FromSeconds(30);

    /// <summary>No finisher while another hostile is this close (tiles) to him or the victim; 0 disables the check.</summary>
    [DataField]
    public float FinisherClearRadius = 6f;

    /// <summary>Weighted random pick of the variant (finisher prototype id -> weight). Shotgun variants need the shotgun in hand.</summary>
    [DataField]
    public Dictionary<string, float> FinisherWeights = new()
    {
        { "GilbertFinisherPhase1", 1f },
        { "GilbertFinisherBarrel", 1f },
        { "GilbertFinisherGroundPound", 1f },
    };

    /// <summary>Last own shot at a combat target (victim, time), checked once the pellets landed.</summary>
    [ViewVariables] public (EntityUid Target, TimeSpan At)? FinisherShot;
    /// <summary>Run-up in progress: the victim and when it started.</summary>
    [ViewVariables] public EntityUid? FinisherVictim;
    [ViewVariables] public TimeSpan FinisherApproachStart;
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> FinisherCooldowns = new();
    /// <summary>Diagnostics / tests: last finisher variant started by the AI.</summary>
    [ViewVariables] public string? LastFinisher;

    // ---- v10 (t278u): admin orders ----

    /// <summary><c>gilbertgoto</c>: where he goes (normal navigation) and then holds / patrols.</summary>
    [ViewVariables] public EntityCoordinates? GotoOrder;
    [ViewVariables] public bool GotoArrived;

    /// <summary>Patrol radius (tiles) around the ordered point once there.</summary>
    [DataField]
    public float GotoHoldRadius = 2.5f;

    /// <summary>Further than this (tiles) from the point (e.g. after a fight) he walks back to it.</summary>
    [DataField]
    public float GotoReturnDistance = 4f;

    /// <summary><c>gilberttarget</c>: hunted to death (always knows where it is, no warning / arrest).</summary>
    [ViewVariables] public EntityUid? HuntTarget;
}
