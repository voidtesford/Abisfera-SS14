using Content.Goobstation.Common.MartialArts;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.DoAfter;
using Content.Shared.Humanoid;
using Content.Shared.Popups;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Join the Pack": teaching another humanoid the trimmed "Proper Kung Fu (pupil)" form over a 10 second do-after.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly SharedDoAfterSystem _joinDoAfter = default!;
    [Dependency] private readonly PlayTimeTrackingManager _packPlayTime = default!;

    private void InitializeJoinPack()
    {
        SubscribeLocalEvent<ProperKungFuComponent, JoinPackActionEvent>(OnJoinPackAction);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuJoinPackDoAfterEvent>(OnJoinPackDoAfter);
        SubscribeLocalEvent<ProperKungFuPackHoundComponent, ComponentStartup>(OnPackHoundStartup);
    }

    /// <summary>
    /// The "Pack Hound" trait: the character starts the round already taught the pupil form.
    /// </summary>
    private void OnPackHoundStartup(Entity<ProperKungFuPackHoundComponent> ent, ref ComponentStartup args)
    {
        if (!HasComp<MartialArtsKnowledgeComponent>(ent) && !HasComp<ProperKungFuComponent>(ent))
            _martialArts.GrantProperKungFuPupil(ent);

        RemCompDeferred<ProperKungFuPackHoundComponent>(ent);
    }

    /// <summary>
    /// Unlocks the "Pack Hound" trait for the player controlling <paramref name="target"/>.
    /// The unlock is stored as time on a dedicated play time tracker, which the database keeps between rounds.
    /// Returns true if the trait was locked before.
    /// </summary>
    public bool UnlockPackHound(EntityUid target)
    {
        if (!TryComp<ActorComponent>(target, out var actor)
            || !_packPlayTime.TryGetTrackerTimes(actor.PlayerSession, out var times))
            return false;

        var wasLocked = times.GetValueOrDefault(ProperKungFuPackHound.Tracker) <= TimeSpan.Zero;
        if (!wasLocked)
            return false;

        _packPlayTime.AddTimeToTracker(actor.PlayerSession, ProperKungFuPackHound.Tracker, ProperKungFuPackHound.UnlockTime);
        _packPlayTime.QueueSendTimers(actor.PlayerSession);
        _packPlayTime.SaveSession(actor.PlayerSession);
        return true;
    }

    /// <summary>
    /// Why the target can't be taught, as a locale id, or null if it can.
    /// </summary>
    public string? JoinPackFailReason(EntityUid user, EntityUid target)
    {
        if (target == user || TerminatingOrDeleted(target))
            return "proper-kung-fu-join-pack-invalid";
        if (!HasComp<HumanoidAppearanceComponent>(target) || HasComp<ProperKungFuCloneComponent>(target)
            || _mobState.IsDead(target))
            return "proper-kung-fu-join-pack-invalid";
        if (HasComp<MartialArtsKnowledgeComponent>(target) || HasComp<ProperKungFuComponent>(target))
            return "proper-kung-fu-join-pack-already";
        return null;
    }

    private void OnJoinPackAction(Entity<ProperKungFuComponent> ent, ref JoinPackActionEvent args)
    {
        if (args.Handled || ent.Comp.Pupil || HasComp<ProperKungFuCloneComponent>(ent))
            return;

        var user = args.Performer;
        var target = args.Target;
        if (JoinPackFailReason(user, target) is { } reason)
        {
            _popup.PopupEntity(Loc.GetString(reason, ("target", target)), user, user, PopupType.SmallCaution);
            return;
        }

        var doAfter = new DoAfterArgs(EntityManager, user, args.Delay, new ProperKungFuJoinPackDoAfterEvent(), user, target: target)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = false,
            DistanceThreshold = 1.5f,
        };

        if (!_joinDoAfter.TryStartDoAfter(doAfter))
            return;

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-join-pack-start-user", ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-join-pack-start-target", ("user", user)), target, target, PopupType.Medium);
        args.Handled = true;
    }

    private void OnJoinPackDoAfter(Entity<ProperKungFuComponent> ent, ref ProperKungFuJoinPackDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled || args.Target is not { } target)
            return;

        args.Handled = true;
        var user = ent.Owner;
        if (JoinPackFailReason(user, target) is { } reason)
        {
            _popup.PopupEntity(Loc.GetString(reason, ("target", target)), user, user, PopupType.SmallCaution);
            return;
        }

        if (!_martialArts.GrantProperKungFuPupil(target))
            return;

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-join-pack-done-user", ("target", target)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-join-pack-done-target", ("user", user)), target, target, PopupType.Large);

        if (UnlockPackHound(target))
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-pack-hound-unlocked"), target, target, PopupType.Medium);
    }
}
