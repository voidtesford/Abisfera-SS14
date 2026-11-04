// SPDX-FileCopyrightText: 2024 IProduceWidgets <107586145+IProduceWidgets@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using Content.Server._CorvaxGoob.Shuttles; // CorvaxGoob
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Labels.EntitySystems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Console;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Shuttles.Commands;

/// <summary>
/// Creates FTL disks, to maps, grids, or entities.
/// </summary>
[AdminCommand(AdminFlags.Fun)]

public sealed class FTLDiskCommand : LocalizedCommands
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IEntitySystemManager _entSystemManager = default!;

    public override string Command => "ftldisk";

    public static readonly EntProtoId CoordinatesDisk = "CoordinatesDisk";
    public static readonly EntProtoId DiskCase = "DiskCase";
    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteError(Loc.GetString("shell-need-minimum-one-argument"));
            return;
        }

        var player = shell.Player;

        if (player == null)
        {
            shell.WriteLine(Loc.GetString("shell-only-players-can-run-this-command"));
            return;
        }

        if (player.AttachedEntity == null)
        {
            shell.WriteLine(Loc.GetString("shell-must-be-attached-to-entity"));
            return;
        }

        EntityUid entity = player.AttachedEntity.Value;
        var coords = _entManager.GetComponent<TransformComponent>(entity).Coordinates;

        var handsSystem = _entSystemManager.GetEntitySystem<SharedHandsSystem>();
        var labelSystem = _entSystemManager.GetEntitySystem<LabelSystem>();
        var mapSystem = _entSystemManager.GetEntitySystem<SharedMapSystem>();
        var storageSystem = _entSystemManager.GetEntitySystem<SharedStorageSystem>();

        // CorvaxGoob: ftldisk <map> <x> <y> <direction> - the shuttle console arrives exactly at x y facing direction.
        (System.Numerics.Vector2 Position, Direction Direction)? exact = null;
        if (args.Length == 4 && FTLDiskExactTargetSystem.TryParseDirection(args[3], out var exactDir))
        {
            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                shell.WriteError(Loc.GetString("cmd-ftldisk-bad-coordinates", ("x", args[1]), ("y", args[2])));
                return;
            }

            exact = (new System.Numerics.Vector2(x, y), exactDir);
            args = new[] { args[0] };
        }
        else if (args.Length == 4 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                 && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                 && !_entManager.TryParseNetEntity(args[3], out _))
        {
            shell.WriteError(Loc.GetString("cmd-ftldisk-bad-direction", ("direction", args[3])));
            return;
        }

        foreach (var destinations in args)
        {
            DebugTools.AssertNotNull(destinations);

            // make sure destination is an id.
            EntityUid dest;

            if (_entManager.TryParseNetEntity(destinations, out var nullableDest))
            {
                DebugTools.AssertNotNull(nullableDest);

                dest = (EntityUid) nullableDest;

                // we need to go to a map, so check if the EntID is something else then try for its map
                if (!_entManager.HasComponent<MapComponent>(dest))
                {
                    if (!_entManager.TryGetComponent<TransformComponent>(dest, out var entTransform))
                    {
                        shell.WriteLine(Loc.GetString("cmd-ftldisk-no-transform", ("destination", destinations)));
                        continue;
                    }

                    if (!mapSystem.TryGetMap(entTransform.MapID, out var mapDest))
                    {
                        shell.WriteLine(Loc.GetString("cmd-ftldisk-no-map", ("destination", destinations)));
                        continue;
                    }

                    DebugTools.AssertNotNull(mapDest);
                    dest = mapDest!.Value; // explicit cast here should be fine since the previous if should catch it.
                }

                // find and verify the map is not somehow unusable.
                if (!_entManager.TryGetComponent<MapComponent>(dest, out var mapComp)) // We have to check for a MapComponent here and above since we could have changed our dest entity.
                {
                    shell.WriteLine(Loc.GetString("cmd-ftldisk-no-map-comp", ("destination", destinations), ("map", dest)));
                    continue;
                }
                if (mapComp.MapInitialized == false)
                {
                    shell.WriteLine(Loc.GetString("cmd-ftldisk-map-not-init", ("destination", destinations), ("map", dest)));
                    continue;
                }
                if (mapComp.MapPaused == true)
                {
                    shell.WriteLine(Loc.GetString("cmd-ftldisk-map-paused", ("destination", destinations), ("map", dest)));
                    continue;
                }

                // check if our destination works already, if not, make it.
                if (!_entManager.TryGetComponent<FTLDestinationComponent>(dest, out var ftlDestComp))
                {
                    FTLDestinationComponent ftlDest = _entManager.AddComponent<FTLDestinationComponent>(dest);
                    ftlDest.RequireCoordinateDisk = true;

                    if (_entManager.HasComponent<MapGridComponent>(dest))
                    {
                        ftlDest.BeaconsOnly = true;

                        shell.WriteLine(Loc.GetString("cmd-ftldisk-planet", ("destination", destinations), ("map", dest)));
                    }
                }
                else
                {
                    // we don't do these automatically, since it isn't clear what the correct resolution is. Instead we provide feedback to the user and carry on like they know what theyre doing.
                    if (ftlDestComp.Enabled == false)
                        shell.WriteLine(Loc.GetString("cmd-ftldisk-already-dest-not-enabled", ("destination", destinations), ("map", dest)));

                    if (ftlDestComp.BeaconsOnly == true)
                        shell.WriteLine(Loc.GetString("cmd-ftldisk-requires-ftl-point", ("destination", destinations), ("map", dest)));
                }

                // create the FTL disk
                EntityUid cdUid = _entManager.SpawnEntity(CoordinatesDisk, coords);
                var cd = _entManager.EnsureComponent<ShuttleDestinationCoordinatesComponent>(cdUid);
                cd.Destination = dest;
                _entManager.Dirty(cdUid, cd);

                // CorvaxGoob: exact console position and facing
                if (exact is { } target)
                {
                    _entSystemManager.GetEntitySystem<FTLDiskExactTargetSystem>().SetTarget(cdUid, dest, target.Position, target.Direction);
                    shell.WriteLine(Loc.GetString("cmd-ftldisk-exact",
                        ("map", dest), ("x", target.Position.X), ("y", target.Position.Y),
                        ("direction", Loc.GetString($"cmd-ftldisk-direction-{target.Direction.ToString().ToLowerInvariant()}"))));
                }

                // create disk case
                EntityUid cdCaseUid = _entManager.SpawnEntity(DiskCase, coords);

                // apply labels
                if (_entManager.TryGetComponent<MetaDataComponent>(dest, out var meta) && meta != null && meta.EntityName != null)
                {
                    // CorvaxGoob: exact disks also carry the point in their label
                    var label = exact is { } l
                        ? $"{meta.EntityName} ({l.Position.X.ToString(CultureInfo.InvariantCulture)}, {l.Position.Y.ToString(CultureInfo.InvariantCulture)})"
                        : meta.EntityName;
                    labelSystem.Label(cdUid, label);
                    labelSystem.Label(cdCaseUid, label);
                }

                // if the case has a storage, try to place the disk in there and then the case inhand

                if (_entManager.TryGetComponent<StorageComponent>(cdCaseUid, out var storage) && storageSystem.Insert(cdCaseUid, cdUid, out _, storageComp: storage, playSound: false))
                {
                    if (_entManager.TryGetComponent<HandsComponent>(entity, out var handsComponent) && handsSystem.TryGetEmptyHand((entity, handsComponent), out var emptyHand))
                    {
                        handsSystem.TryPickup(entity, cdCaseUid, emptyHand, checkActionBlocker: false, handsComp: handsComponent);
                    }
                }
                else // the case was messed up, put disk inhand
                {
                    _entManager.DeleteEntity(cdCaseUid); // something went wrong so just yeet the chaf

                    if (_entManager.TryGetComponent<HandsComponent>(entity, out var handsComponent) && handsSystem.TryGetEmptyHand((entity, handsComponent), out var emptyHand))
                    {
                        handsSystem.TryPickup(entity, cdUid, emptyHand, checkActionBlocker: false, handsComp: handsComponent);
                    }
                }
            }
            else
            {
                shell.WriteLine(Loc.GetString("shell-invalid-entity-uid", ("uid", destinations)));
            }
        }
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        // CorvaxGoob: ftldisk <map> [x y direction]; several maps without coordinates still work as before.
        switch (args.Length)
        {
            case 1:
                return CompletionResult.FromHintOptions(CompletionHelper.MapUids(_entManager), Loc.GetString("cmd-ftldisk-hint"));
            case 2:
                return CompletionResult.FromHintOptions(CompletionHelper.MapUids(_entManager), Loc.GetString("cmd-ftldisk-hint-x"));
            case 3:
                return CompletionResult.FromHint(Loc.GetString("cmd-ftldisk-hint-y"));
            case 4:
                return CompletionResult.FromHintOptions(new[]
                {
                    new CompletionOption("north", Loc.GetString("cmd-ftldisk-direction-north")),
                    new CompletionOption("south", Loc.GetString("cmd-ftldisk-direction-south")),
                    new CompletionOption("east", Loc.GetString("cmd-ftldisk-direction-east")),
                    new CompletionOption("west", Loc.GetString("cmd-ftldisk-direction-west")),
                }, Loc.GetString("cmd-ftldisk-hint-direction"));
            case > 4:
                return CompletionResult.FromHintOptions(CompletionHelper.MapUids(_entManager), Loc.GetString("cmd-ftldisk-hint"));
        }

        return CompletionResult.Empty;
    }
}