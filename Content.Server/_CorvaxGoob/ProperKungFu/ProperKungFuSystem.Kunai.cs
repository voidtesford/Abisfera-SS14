using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Kunai": a summoned throwing kunai. Thrown by its summoner, it pulls them to where it stops or sticks.
/// Throwing is not covered by the art's ban on ranged weapons (that ban only blocks guns).
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    /// <summary>
    /// After sticking into a mob or a wall the user lands this far back along the throw, not inside it.
    /// </summary>
    private const float KunaiEmbedPullBack = 0.6f;

    private void InitializeKunai()
    {
        SubscribeLocalEvent<ProperKungFuComponent, KunaiActionEvent>(OnKunaiAction);
        SubscribeLocalEvent<ProperKungFuKunaiComponent, ThrownEvent>(OnKunaiThrown);
        SubscribeLocalEvent<ProperKungFuKunaiComponent, StopThrowEvent>(OnKunaiStopThrow);
        SubscribeLocalEvent<ProperKungFuKunaiComponent, EmbedEvent>(OnKunaiEmbed);
    }

    private void OnKunaiAction(Entity<ProperKungFuComponent> ent, ref KunaiActionEvent args)
    {
        if (args.Handled || HasComp<ProperKungFuCloneComponent>(ent))
            return;

        var user = args.Performer;
        string? hand = _hands.GetActiveHand(user);
        if (hand == null || !_hands.HandIsEmpty(user, hand))
        {
            if (!_hands.TryGetEmptyHand(user, out var empty))
            {
                _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-no-hand"), user, user);
                return;
            }

            hand = empty;
        }

        // Only one summoned kunai at a time.
        var old = EntityQueryEnumerator<ProperKungFuKunaiComponent>();
        while (old.MoveNext(out var oldUid, out var oldComp))
        {
            if (oldComp.Summoner == user)
                QueueDel(oldUid);
        }

        var coords = Transform(user).Coordinates;
        var kunai = Spawn(args.Kunai, coords);
        var comp = EnsureComp<ProperKungFuKunaiComponent>(kunai);
        comp.Summoner = user;
        comp.ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(args.Lifetime);
        comp.SmokeEffect = args.SmokeEffect;

        if (!_hands.TryPickup(user, kunai, hand, checkActionBlocker: false, animate: false))
        {
            QueueDel(kunai);
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-no-hand"), user, user);
            return;
        }

        Spawn(args.SmokeEffect, coords);
        _audio.PlayPvs(args.Sound, user);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-user"), user, user);
        args.Handled = true;
    }

    private void OnKunaiThrown(Entity<ProperKungFuKunaiComponent> ent, ref ThrownEvent args)
    {
        _appearance.SetData(ent, ProperKungFuKunaiVisuals.State, ProperKungFuKunaiState.Thrown);
        if (args.User is not { } user || user != ent.Comp.Summoner)
        {
            ent.Comp.ThrowOrigin = null;
            return;
        }

        ent.Comp.ThrowOrigin = _transform.GetMapCoordinates(user);
        ent.Comp.Arrived = false;
        ent.Comp.Embedded = false;
    }

    private void OnKunaiStopThrow(Entity<ProperKungFuKunaiComponent> ent, ref StopThrowEvent args)
    {
        if (!ent.Comp.Embedded)
            _appearance.SetData(ent, ProperKungFuKunaiVisuals.State, ProperKungFuKunaiState.Idle);

        if (ent.Comp.ThrowOrigin != null)
            ent.Comp.Arrived = true;
    }

    private void OnKunaiEmbed(Entity<ProperKungFuKunaiComponent> ent, ref EmbedEvent args)
    {
        _appearance.SetData(ent, ProperKungFuKunaiVisuals.State, ProperKungFuKunaiState.Embedded);
        if (ent.Comp.ThrowOrigin == null)
            return;

        ent.Comp.Embedded = true;
        ent.Comp.Arrived = true;
    }

    private void UpdateKunai(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuKunaiComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var kunai, out var xform))
        {
            if (kunai.Arrived && kunai.ThrowOrigin is { } origin && kunai.Summoner is { } user && !TerminatingOrDeleted(user))
            {
                TeleportToKunai(user, (uid, kunai, xform), origin);
                QueueDel(uid);
                continue;
            }

            if (now >= kunai.ExpiresAt || kunai.Summoner is not { } owner || TerminatingOrDeleted(owner))
            {
                // A kunai still in hand also fades; an embedded one too.
                Spawn(kunai.SmokeEffect, _transform.GetMapCoordinates(uid, xform));
                QueueDel(uid);
            }
        }
    }

    private void TeleportToKunai(EntityUid user, Entity<ProperKungFuKunaiComponent, TransformComponent> kunai, MapCoordinates origin)
    {
        var dest = _transform.GetMapCoordinates(kunai.Owner, kunai.Comp2);
        var userXform = Transform(user);
        if (dest.MapId != userXform.MapID || _container.IsEntityInContainer(user))
            return;

        var from = _transform.GetWorldPosition(userXform);
        var target = dest.Position;
        if (kunai.Comp1.Embedded && origin.MapId == dest.MapId)
        {
            var back = origin.Position - target;
            if (back.LengthSquared() > 0.0001f)
                target += Vector2.Normalize(back) * MathF.Min(KunaiEmbedPullBack, back.Length());
        }

        // Never through or into a wall.
        if (IsPathBlocked(user, dest.MapId, from, target, out var free))
            target = free;

        Spawn(kunai.Comp1.SmokeEffect, _transform.GetMapCoordinates(user, userXform));
        _transform.SetMapCoordinates((user, userXform), new MapCoordinates(target, dest.MapId));
        Spawn(kunai.Comp1.SmokeEffect, new MapCoordinates(target, dest.MapId));
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-kunai-arrive"), user, user, PopupType.Small);
    }
}
