using Content.Server.Administration.Logs;
using Content.Shared._CorvaxGoob.Redspace;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Tools.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.Redspace;

/// <summary>
/// Multitool on the flatpack: swap it for the anchored unfolding stand-in (see <see cref="RedspaceGateUnfoldingSystem"/>).
/// </summary>
public sealed class RedspaceGateFlatpackSystem : SharedRedspaceGateFlatpackSystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedToolSystem _tool = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceGateFlatpackComponent, InteractUsingEvent>(OnInteractUsing);
    }

    private void OnInteractUsing(Entity<RedspaceGateFlatpackComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled || !_tool.HasQuality(args.Used, ent.Comp.QualityNeeded))
            return;

        args.Handled = true;
        TryStartUnfold(ent, args.User, out _);
    }

    /// <summary>
    /// Replaces the flatpack with the unfolding stand-in on its tile. <paramref name="failure"/> is a locale id on failure.
    /// </summary>
    public bool TryStartUnfold(Entity<RedspaceGateFlatpackComponent> ent, EntityUid? user, out string? failure)
    {
        return TryStartUnfold(ent, user, out failure, out _);
    }

    public bool TryStartUnfold(Entity<RedspaceGateFlatpackComponent> ent, EntityUid? user, out string? failure, out EntityUid? unfolding)
    {
        failure = null;
        unfolding = null;
        var (uid, comp) = ent;

        if (TerminatingOrDeleted(uid) || EntityManager.IsQueuedForDeletion(uid))
            return false;

        EntityCoordinates? target = null;
        if (_container.IsEntityInContainer(uid))
            failure = "redspace-gate-flatpack-in-container";
        else if (Transform(uid).GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
            failure = "redspace-gate-flatpack-no-grid";
        else
        {
            var indices = _map.TileIndicesFor(grid, gridComp, Transform(uid).Coordinates);
            if (_turf.IsTileBlocked(grid, indices, CollisionGroup.Impassable, gridComp) || UnfoldingOn(grid, gridComp, indices))
                failure = "redspace-gate-flatpack-no-room";
            else
                target = _map.GridTileToLocal(grid, gridComp, indices); // snapped to the tile centre
        }

        if (failure != null || target == null)
        {
            if (user != null && failure != null)
                _popup.PopupEntity(Loc.GetString(failure), uid, user.Value);
            return false;
        }

        // A dedicated anchored entity plays the one-shot animation from frame 0; the flatpack itself is gone.
        var spawned = Spawn(comp.Unfolding, target.Value);
        _transform.SetLocalRotation(spawned, Angle.Zero);
        if (!Transform(spawned).Anchored)
            _transform.AnchorEntity(spawned);
        unfolding = spawned;

        if (user != null)
        {
            _popup.PopupEntity(Loc.GetString("redspace-gate-flatpack-unfolding"), spawned, user.Value);
            _adminLog.Add(LogType.Construction, LogImpact.Low,
                $"{ToPrettyString(user.Value):player} started unfolding {ToPrettyString(uid):entity} into {ToPrettyString(spawned):entity} at {target.Value}");
        }

        QueueDel(uid);
        return true;
    }

    private bool UnfoldingOn(EntityUid grid, MapGridComponent gridComp, Vector2i indices)
    {
        var anchored = _map.GetAnchoredEntitiesEnumerator(grid, gridComp, indices);
        while (anchored.MoveNext(out var ent))
        {
            if (HasComp<RedspaceGateUnfoldingComponent>(ent))
                return true;
        }

        return false;
    }
}
