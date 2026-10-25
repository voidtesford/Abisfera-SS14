using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// A time stop circle (melee kunai finisher): every mob, projectile, thrown or moving body inside is frozen with
/// the Goob <c>FrozenComponent</c> until <see cref="End"/>, except <see cref="User"/>.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuTimeStopZoneComponent : Component
{
    [ViewVariables]
    public EntityUid User;

    [ViewVariables]
    public float Radius = 20f;

    [ViewVariables]
    public TimeSpan End;

    [ViewVariables]
    public TimeSpan NextScan;
}

/// <summary>
/// On everything frozen by a <see cref="ProperKungFuTimeStopZoneComponent"/> (unfrozen when that zone ends) or held
/// by a Wolf Gaze cutscene (<see cref="Zone"/> null, unfrozen by the cutscene).
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuTimeStoppedComponent : Component
{
    [ViewVariables]
    public EntityUid? Zone;
}

/// <summary>
/// On the user while Wolf Judgment plays out.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuJudgmentComponent : Component
{
    [ViewVariables]
    public ProperKungFuWolfJudgmentActionEvent? Event;

    [ViewVariables]
    public MapId Map;

    /// <summary>
    /// From the user towards the cursor.
    /// </summary>
    [ViewVariables]
    public Vector2 Direction;

    /// <summary>
    /// Where the user jumped from (the cross area starts here).
    /// </summary>
    [ViewVariables]
    public Vector2 Origin;

    [ViewVariables]
    public int Stage;

    [ViewVariables]
    public int Count;

    [ViewVariables]
    public TimeSpan NextStep;

    [ViewVariables]
    public TimeSpan EndBy;

    /// <summary>
    /// Everyone caught by the first cut (the second hits the same people).
    /// </summary>
    [ViewVariables]
    public List<EntityUid> Hit = new();

    /// <summary>
    /// Whether this use plays the full version (dash and combo).
    /// </summary>
    [ViewVariables]
    public bool Full;

    [ViewVariables]
    public EntityUid Victim;

    /// <summary>
    /// Where the victim is held during the combo.
    /// </summary>
    [ViewVariables]
    public Vector2 Center;

    [ViewVariables]
    public EntityUid? Tint;
}

/// <summary>
/// On the user during the Wolf Dance finisher's katana clone phase (after the final strike).
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuKatanaClonesComponent : Component
{
    [ViewVariables]
    public EntityUid Victim;

    [ViewVariables]
    public MapId Map;

    [ViewVariables]
    public Vector2 Center;

    [ViewVariables]
    public Vector2 Away;

    [ViewVariables]
    public WolfDanceActionEvent? Event;

    [ViewVariables]
    public List<EntityUid> Clones = new();

    /// <summary>
    /// Ring positions the clones stand on, and where each one ends its dash (the opposite side).
    /// </summary>
    [ViewVariables]
    public List<Vector2> From = new();

    [ViewVariables]
    public TimeSpan DashStart;

    [ViewVariables]
    public bool Dashed;

    [ViewVariables]
    public bool Struck;

    [ViewVariables]
    public TimeSpan End;
}

/// <summary>
/// Wolf Gaze stance on the user: waits for an attack from within range.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuGazeStanceComponent : Component
{
    [ViewVariables]
    public ProperKungFuWolfGazeActionEvent? Event;

    [ViewVariables]
    public TimeSpan EndTime;

    [ViewVariables]
    public EntityUid? Aura;

    /// <summary>Set by the blocked attack; the cutscene starts on the next update.</summary>
    [ViewVariables]
    public EntityUid? Attacker;

    /// <summary>The action that started the stance: put on the long cooldown when nobody attacks.</summary>
    [ViewVariables]
    public EntityUid? Action;
}

/// <summary>
/// The Wolf Gaze cutscene, on the user.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuGazeComponent : Component
{
    [ViewVariables]
    public ProperKungFuWolfGazeActionEvent? Event;

    [ViewVariables]
    public EntityUid Attacker;

    /// <summary>0 tension, 1 freeze-frame after the thrust.</summary>
    [ViewVariables]
    public int Stage;

    [ViewVariables]
    public TimeSpan ThrustAt;

    [ViewVariables]
    public TimeSpan ReleaseAt;

    [ViewVariables]
    public TimeSpan NextBeat;

    [ViewVariables]
    public Vector2 Direction;

    [ViewVariables]
    public EntityUid? Eye;
}

/// <summary>
/// On the user while a Wolf Seal plays out (uppercut, apex, double palm, release).
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuSealComponent : Component
{
    [ViewVariables]
    public ProperKungFuWolfSealActionEvent? Event;

    [ViewVariables]
    public EntityUid Victim;

    [ViewVariables]
    public MapId Map;

    /// <summary>From the user towards the victim.</summary>
    [ViewVariables]
    public Vector2 Direction;

    /// <summary>0 uppercut done, 1 apex shown, 2.. palms landed, then the release.</summary>
    [ViewVariables]
    public int Stage;

    [ViewVariables]
    public TimeSpan NextStep;

    /// <summary>Afterimages left behind the flying victim after the release.</summary>
    [ViewVariables]
    public int Trails;

    [ViewVariables]
    public TimeSpan EndBy;
}
