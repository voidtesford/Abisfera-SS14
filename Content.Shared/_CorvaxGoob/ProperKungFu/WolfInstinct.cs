using Content.Shared.Actions;
using Content.Shared.Damage;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Wolf Instinct" (Волчье чутьё): a self-cast counter stance.
/// While it lasts:
/// - every melee attack on the user is parried and answered with a two-palm strike into the attacker's chest;
/// - a gun hit (the damage still applies) makes the user dash at the shooter, knock them down, knock their gun
///   out of their hands and take them into a hard grab;
/// - a thrown object does no damage and flies back at the thrower twice as fast.
/// </summary>
public sealed partial class WolfInstinctActionEvent : InstantActionEvent
{
    /// <summary>
    /// How long the stance lasts.
    /// </summary>
    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(3);

    // ---- Melee: parry and two-palm counter ----

    /// <summary>
    /// If true the parried melee attack does nothing at all (no damage, no on-hit effects).
    /// </summary>
    [DataField]
    public bool CancelMeleeDamage = true;

    /// <summary>
    /// If false the stance ends after the first melee counter.
    /// </summary>
    [DataField]
    public bool AllowMultipleCounters = true;

    /// <summary>
    /// Damage of the two-palm counter strike, always into the chest.
    /// </summary>
    [DataField]
    public DamageSpecifier CounterDamage = new();

    [DataField]
    public float CounterStaminaDamage = 75f;

    /// <summary>
    /// How far (tiles) the attacker is thrown back on their feet.
    /// </summary>
    [DataField]
    public float CounterKnockbackDistance = 3f;

    [DataField]
    public float CounterKnockbackSpeed = 10f;

    [DataField]
    public EntProtoId? CounterEffect = "ProperKungFuInstinctClawSlash";

    [DataField]
    public SoundSpecifier? CounterSound;

    // ---- Guns: dash at the shooter ----

    /// <summary>
    /// Max dash length (tiles). A shooter further away is missed: the user dashes this far and stops.
    /// </summary>
    [DataField]
    public float DashRange = 8f;

    /// <summary>
    /// Dash speed in tiles per second.
    /// </summary>
    [DataField]
    public float DashSpeed = 24f;

    /// <summary>
    /// The dash stops this far before the shooter's center.
    /// </summary>
    [DataField]
    public float DashStopDistance = 0.6f;

    /// <summary>
    /// At the end of the dash the shooter has to be this close to be caught.
    /// </summary>
    [DataField]
    public float DashReach = 1.5f;

    [DataField]
    public TimeSpan ShooterKnockdownTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How far knocked out guns fly.
    /// </summary>
    [DataField]
    public float GunThrowDistance = 1.5f;

    [DataField]
    public float GrabHoldDistance = 0.6f;

    /// <summary>
    /// If true a gun hit ends the stance (one dash per stance).
    /// </summary>
    [DataField]
    public bool GunEndsStance = true;

    [DataField]
    public SoundSpecifier? DashHitSound;

    [DataField]
    public EntProtoId? AfterimageEffect = "ProperKungFuDashAfterimage";

    // ---- Thrown objects: reflect ----

    /// <summary>
    /// If true thrown objects do no damage to the user.
    /// </summary>
    [DataField]
    public bool BlockThrownDamage = true;

    /// <summary>
    /// Speed of the reflected object relative to the speed it hit the user with.
    /// </summary>
    [DataField]
    public float ReflectSpeedMultiplier = 2f;

    /// <summary>
    /// Used when the incoming speed can't be read (the object was already stopped).
    /// </summary>
    [DataField]
    public float FallbackThrowSpeed = 10f;

    [DataField]
    public SoundSpecifier? ReflectSound;

    // ---- Visuals ----

    /// <summary>
    /// White-red aura attached to the user for the stance (the trembling "test me" text is drawn by the client from the stance component).
    /// </summary>
    [DataField]
    public EntProtoId? AuraEffect = "ProperKungFuInstinctAura";

    /// <summary>
    /// Loud stomp on activation.
    /// </summary>
    [DataField]
    public SoundSpecifier? StanceSound;

    /// <summary>
    /// The ring that follows the stomp.
    /// </summary>
    [DataField]
    public SoundSpecifier? StanceRingSound;
}

/// <summary>
/// The user is in the Wolf Instinct counter stance until <see cref="EndTime"/>.
/// Networked so the client predicts the parry too.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class WolfInstinctStanceComponent : Component
{
    [DataField, AutoNetworkedField, AutoPausedField]
    public TimeSpan EndTime;

    /// <summary>
    /// Mirrors <see cref="WolfInstinctActionEvent.CancelMeleeDamage"/> so the client predicts the same.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool CancelMeleeDamage = true;

    /// <summary>
    /// Server only: settings of the action that started the stance.
    /// </summary>
    [ViewVariables]
    public WolfInstinctActionEvent? Event;

    [ViewVariables]
    public EntityUid? Aura;

    /// <summary>
    /// Server only: a gun projectile / hitscan from this shooter is about to deal damage.
    /// </summary>
    [ViewVariables]
    public EntityUid? PendingShooter;

    /// <summary>
    /// Server only: confirmed gun hit, the dash starts on the next update.
    /// </summary>
    [ViewVariables]
    public EntityUid? DashAt;

    /// <summary>
    /// Server only: attackers to answer with the palm strike on the next update.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> PendingCounters = new();

    /// <summary>
    /// Server only: thrown objects to send back on the next update, with the speed they came in at.
    /// </summary>
    [ViewVariables]
    public List<(EntityUid Item, EntityUid Thrower, float Speed)> PendingReflects = new();
}

/// <summary>
/// Raised on the user when the stance parries a melee attack (server reacts with the counter strike).
/// </summary>
[ByRefEvent]
public record struct WolfInstinctParriedEvent(EntityUid Attacker);

/// <summary>
/// Parries melee attacks during the stance on both the server and the client (prediction).
/// </summary>
public sealed class SharedWolfInstinctSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<WolfInstinctStanceComponent, BeforeHarmfulActionEvent>(OnBeforeHarmfulAction);
    }

    public bool IsActive(WolfInstinctStanceComponent comp)
    {
        return _timing.CurTime < comp.EndTime;
    }

    private void OnBeforeHarmfulAction(EntityUid uid, WolfInstinctStanceComponent comp, BeforeHarmfulActionEvent args)
    {
        if (args.Cancelled
            || args.Type != HarmfulActionType.Harm
            || args.User == uid
            || !IsActive(comp))
            return;

        if (comp.CancelMeleeDamage)
            args.Cancel();

        var ev = new WolfInstinctParriedEvent(args.User);
        RaiseLocalEvent(uid, ref ev);
    }
}
