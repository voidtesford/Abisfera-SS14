using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CorvaxGoob.Redspace.Commands;

/// <summary>
/// ftldiskgate &lt;gate&gt; &lt;mapId&gt; &lt;x&gt; &lt;y&gt; (or &lt;x,y&gt;): makes a Euclidean gate print a coordinates disk
/// (the same CoordinatesDisk as ftldisk) that also stores the exact coordinates, so the gate opens its
/// exit portal right there when activated with it.
/// </summary>
[AdminCommand(AdminFlags.Fun)] // same as ftldisk
public sealed class FTLDiskGateCommand : LocalizedEntityCommands
{
    [Dependency] private readonly RedspaceGateSystem _gate = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;

    public override string Command => "ftldiskgate";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 3 or > 4)
        {
            shell.WriteError(Loc.GetString("cmd-ftldiskgate-usage"));
            return;
        }

        if (!TryGetGate(args[0], out var gate, out var gateComp))
        {
            shell.WriteError(Loc.GetString("cmd-ftldiskgate-no-gate", ("gate", args[0])));
            return;
        }

        if (!TryGetMap(args[1], out var mapUid, out var mapComp))
        {
            shell.WriteError(Loc.GetString("cmd-ftldiskgate-no-map", ("map", args[1])));
            return;
        }

        if (!mapComp.MapInitialized)
        {
            shell.WriteError(Loc.GetString("cmd-ftldiskgate-map-not-init", ("map", args[1])));
            return;
        }

        if (mapComp.MapPaused)
        {
            shell.WriteError(Loc.GetString("cmd-ftldiskgate-map-paused", ("map", args[1])));
            return;
        }

        if (!TryParseCoordinates(args.Skip(2).ToArray(), out var position))
        {
            shell.WriteError(Loc.GetString("cmd-ftldiskgate-bad-coordinates", ("coords", string.Join(' ', args.Skip(2)))));
            return;
        }

        if (Math.Abs(position.X) > gateComp.MaxCoordinate || Math.Abs(position.Y) > gateComp.MaxCoordinate)
        {
            shell.WriteError(Loc.GetString("cmd-ftldiskgate-out-of-range", ("max", gateComp.MaxCoordinate)));
            return;
        }

        // Same as ftldisk: make the map an FTL destination, so the disk also works in shuttle consoles.
        if (!EntityManager.HasComponent<FTLDestinationComponent>(mapUid))
        {
            var ftlDest = EntityManager.AddComponent<FTLDestinationComponent>(mapUid);
            ftlDest.RequireCoordinateDisk = true;
            if (EntityManager.HasComponent<MapGridComponent>(mapUid))
                ftlDest.BeaconsOnly = true;
        }

        var disk = _gate.PrintCoordinatesDisk((gate, gateComp), mapUid, position, shell.Player?.AttachedEntity);

        shell.WriteLine(Loc.GetString("cmd-ftldiskgate-success",
            ("disk", EntityManager.GetNetEntity(disk).ToString()),
            ("gate", EntityManager.GetNetEntity(gate).ToString()),
            ("map", mapComp.MapId.ToString()),
            ("x", RedspaceGateSystem.FormatCoord(position.X)),
            ("y", RedspaceGateSystem.FormatCoord(position.Y))));
    }

    /// <summary>NetEntity (as shown in VV / the admin menu) first, then a server EntityUid.</summary>
    private bool TryGetGate(string arg, out EntityUid gate, out RedspaceGateComponent comp)
    {
        gate = EntityUid.Invalid;
        comp = default!;

        if (EntityManager.TryParseNetEntity(arg, out var net)
            && EntityManager.TryGetComponent(net, out RedspaceGateComponent? netComp))
        {
            gate = net.Value;
            comp = netComp;
            return true;
        }

        if (EntityUid.TryParse(arg, out var local)
            && EntityManager.TryGetComponent(local, out RedspaceGateComponent? localComp))
        {
            gate = local;
            comp = localComp;
            return true;
        }

        return false;
    }

    /// <summary>MapId first (as in the map list / F7), then the map's NetEntity.</summary>
    private bool TryGetMap(string arg, out EntityUid mapUid, out MapComponent comp)
    {
        mapUid = EntityUid.Invalid;
        comp = default!;

        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && _mapSystem.TryGetMap(new MapId(id), out var byId)
            && EntityManager.TryGetComponent(byId, out MapComponent? idComp))
        {
            mapUid = byId.Value;
            comp = idComp;
            return true;
        }

        if (EntityManager.TryParseNetEntity(arg, out var net)
            && EntityManager.TryGetComponent(net, out MapComponent? netComp))
        {
            mapUid = net.Value;
            comp = netComp;
            return true;
        }

        return false;
    }

    /// <summary>Accepts "x y", "x, y" and "x,y" (also "x;y").</summary>
    public static bool TryParseCoordinates(string[] args, out Vector2 position)
    {
        position = default;
        var parts = string.Join(' ', args)
            .Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.IsFinite(x) || !float.IsFinite(y))
            return false;

        position = new Vector2(x, y);
        return true;
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        switch (args.Length)
        {
            case 1:
                return CompletionResult.FromHintOptions(
                    CompletionHelper.Components<RedspaceGateComponent>(args[0], EntityManager, limit: 64),
                    Loc.GetString("cmd-ftldiskgate-hint-gate"));
            case 2:
                var maps = new List<CompletionOption>();
                var query = EntityManager.AllEntityQueryEnumerator<MapComponent, MetaDataComponent>();
                while (query.MoveNext(out var map, out var meta))
                {
                    maps.Add(new CompletionOption(map.MapId.ToString(), meta.EntityName));
                }
                return CompletionResult.FromHintOptions(maps.OrderBy(o => o.Value), Loc.GetString("cmd-ftldiskgate-hint-map"));
            case 3:
                return CompletionResult.FromHint(Loc.GetString("cmd-ftldiskgate-hint-x"));
            case 4:
                return CompletionResult.FromHint(Loc.GetString("cmd-ftldiskgate-hint-y"));
            default:
                return CompletionResult.Empty;
        }
    }
}
