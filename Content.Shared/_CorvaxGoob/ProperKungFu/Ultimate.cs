using Content.Shared.Actions;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Ultimate" (Ультимейт): with a full bar, starts the ultimate mode right away.
/// The mode also starts by itself 5 seconds after the master falls into crit with a full bar (healed and back on their feet).
/// </summary>
public sealed partial class ProperKungFuUltimateActionEvent : InstantActionEvent;

/// <summary>
/// Ultimate-only "Twenty Shadows" (Двадцать теней): the first use in a mode brings twenty fragile copies of the user,
/// five per second, each with a random weapon, attacking every living thing around except the user and the Pack Dogs.
/// Every later use in the same mode brings <see cref="ReuseCount"/> more. Each copy lives <see cref="CloneLifetime"/>
/// and they all vanish early if the mode ends first.
/// </summary>
public sealed partial class ProperKungFuUltimateClonesActionEvent : InstantActionEvent
{
    [DataField]
    public int Count = 20;

    /// <summary>
    /// Copies brought by every later use in the same ultimate mode.
    /// </summary>
    [DataField]
    public int ReuseCount = 5;

    [DataField]
    public int PerSecond = 5;

    [DataField]
    public TimeSpan CloneLifetime = TimeSpan.FromSeconds(60);

    [DataField]
    public float CloneHealth = 5f;

    /// <summary>
    /// Damage multiplier of the copies' attacks: they are neither buffed nor weakened.
    /// </summary>
    [DataField]
    public float DamageModifier = 1f;

    [DataField]
    public float SpawnRadius = 2f;

    /// <summary>
    /// Weapons handed out at random; null means unarmed.
    /// </summary>
    [DataField]
    public List<EntProtoId?> Weapons = new()
    {
        "ProperKungFuKunai",
        "Katana",
        null,
        "MonkStaff",
        "CombatKnife",
    };

    [DataField]
    public EntProtoId SpawnEffect = "ProperKungFuWolfPackSmoke";

    [DataField]
    public EntProtoId VanishEffect = "ProperKungFuWolfPackVanish";

    /// <summary>
    /// A sharp wolf howl on every use (stock Vulpkanin howl).
    /// </summary>
    [DataField]
    public SoundSpecifier? Sound = new SoundPathSpecifier("/Audio/_DV/Voice/Vulpkanin/howl.ogg");
}

/// <summary>
/// Ultimate-only "Summon the Dogs" (Призыв псов): teleports up to <see cref="MaxPupils"/> living, conscious and uncuffed
/// Pack Dogs (pupils) on the same map, nearest first, to around the user in white-red smoke.
/// </summary>
public sealed partial class ProperKungFuSummonPackActionEvent : InstantActionEvent
{
    [DataField]
    public int MaxPupils = 2;

    [DataField]
    public float Radius = 1f;

    [DataField]
    public EntProtoId SmokeEffect = "ProperKungFuWolfPackSmoke";

    [DataField]
    public EntProtoId ArriveEffect = "ProperKungFuWolfPackVanish";

    [DataField]
    public SoundSpecifier? Sound = new SoundPathSpecifier("/Audio/Effects/smoke.ogg");
}

/// <summary>
/// Sent only to a Pack Dog that was just summoned: its screen shows the alpha's call in big letters for a moment.
/// </summary>
[Serializable, NetSerializable]
public sealed class ProperKungFuPackSummonedEvent : EntityEventArgs
{
    public float Duration;

    public ProperKungFuPackSummonedEvent(float duration)
    {
        Duration = duration;
    }
}
