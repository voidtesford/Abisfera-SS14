using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// A short-lived Wolf Pack copy of a Proper Kung Fu user. It fights the pack's target with fists and the art's
/// skills, deals reduced damage, never hurts its summoner or the other clones, and vanishes in smoke on death
/// or when its time runs out.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuCloneComponent : Component
{
    [ViewVariables]
    public EntityUid Summoner;

    [ViewVariables]
    public EntityUid? Target;

    [ViewVariables]
    public TimeSpan ExpiresAt;

    [ViewVariables]
    public float DamageModifier = 0.25f;

    [ViewVariables]
    public EntProtoId VanishEffect = "ProperKungFuWolfPackVanish";

    [ViewVariables]
    public TimeSpan NextThink;

    /// <summary>
    /// Earliest time of the next special move, so the clone does not fire every skill at once.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextSkill;

    [ViewVariables]
    public bool Vanished;

    /// <summary>
    /// An ultimate-mode clone: no Proper Kung Fu at all, just a held weapon, attacks every living thing except
    /// its summoner, the summoner's clones and Pack Dog pupils, and leaves when the ultimate ends.
    /// </summary>
    [ViewVariables]
    public bool Ultimate;
}

/// <summary>
/// On a Proper Kung Fu user with living Wolf Pack clones: steers them at whoever attacks the user or is attacked by them.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuPackLeaderComponent : Component
{
    [ViewVariables]
    public HashSet<EntityUid> Clones = new();
}

/// <summary>
/// On the master while the ultimate's "Twenty Shadows" are still coming out, a batch per second.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuUltimateSpawnerComponent : Component
{
    [ViewVariables]
    public int Remaining;

    [ViewVariables]
    public TimeSpan NextBatch;

    [ViewVariables]
    public int Spawned;

    [ViewVariables]
    public Content.Shared._CorvaxGoob.ProperKungFu.ProperKungFuUltimateClonesActionEvent Args = default!;
}
