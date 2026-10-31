using System.Globalization;
using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>Shared parsing for the Gilbert order commands (v10, t278u).</summary>
public static class GilbertCommandUtil
{
    public static bool TryGilbert(IEntityManager entMan, string arg, out Entity<GilbertAIComponent> gilbert)
    {
        gilbert = default;
        if (!NetEntity.TryParse(arg, out var net) || !entMan.TryGetEntity(net, out var uid)
            || !entMan.TryGetComponent<GilbertAIComponent>(uid, out var comp))
            return false;
        gilbert = (uid.Value, comp);
        return true;
    }

    public static IEnumerable<CompletionOption> Gilberts(IEntityManager entMan)
    {
        var query = entMan.EntityQueryEnumerator<GilbertAIComponent, MetaDataComponent>();
        while (query.MoveNext(out _, out _, out var meta))
            yield return new CompletionOption(meta.NetEntity.ToString(), meta.EntityName);
    }
}

/// <summary>
/// <c>gilbertgoto &lt;gilbert&gt; &lt;x&gt; &lt;y&gt; [mapId|gridUid]</c>, <c>gilbertgoto &lt;gilbert&gt; here</c>,
/// <c>gilbertgoto &lt;gilbert&gt; clear</c>: walk there with the normal navigation and hold / patrol the point; threats
/// on the way are dealt with first, then he resumes the order.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class GilbertGotoCommand : LocalizedEntityCommands
{
    [Dependency] private readonly GilbertAISystem _ai = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly MapSystem _map = default!;

    public override string Command => "gilbertgoto";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 2 or > 4)
        {
            shell.WriteError(Loc.GetString("cmd-gilbertgoto-usage"));
            return;
        }

        if (!GilbertCommandUtil.TryGilbert(EntityManager, args[0], out var gilbert))
        {
            shell.WriteError(Loc.GetString("cmd-gilbertgoto-not-gilbert", ("uid", args[0])));
            return;
        }

        var name = EntityManager.ToPrettyString(gilbert);
        EntityCoordinates coords;
        switch (args[1].ToLowerInvariant())
        {
            case "clear" when args.Length == 2:
                _ai.SetGotoOrder(gilbert, null);
                shell.WriteLine(Loc.GetString("cmd-gilbertgoto-cleared", ("gilbert", name)));
                return;
            case "here" when args.Length == 2:
                if (shell.Player?.AttachedEntity is not { } admin)
                {
                    shell.WriteError(Loc.GetString("cmd-gilbertgoto-no-player"));
                    return;
                }
                coords = EntityManager.GetComponent<TransformComponent>(admin).Coordinates;
                break;
            default:
                if (args.Length < 3)
                {
                    shell.WriteError(Loc.GetString("cmd-gilbertgoto-usage"));
                    return;
                }
                if (!TryNum(shell, args[1], out var x) || !TryNum(shell, args[2], out var y))
                    return;

                var gx = EntityManager.GetComponent<TransformComponent>(gilbert);
                if (args.Length == 3)
                {
                    coords = _transform.ToCoordinates(new MapCoordinates(x, y, gx.MapID));
                }
                else if (NetEntity.TryParse(args[3], out var net) && EntityManager.TryGetEntity(net, out var gridUid)
                         && gridUid is { } grid && EntityManager.HasComponent<MapGridComponent>(grid))
                {
                    coords = new EntityCoordinates(grid, x, y); // grid-local
                }
                else if (int.TryParse(args[3], out var mapInt) && _map.MapExists(new MapId(mapInt)))
                {
                    coords = _transform.ToCoordinates(new MapCoordinates(x, y, new MapId(mapInt)));
                }
                else
                {
                    shell.WriteError(Loc.GetString("cmd-gilbertgoto-bad-map", ("value", args[3])));
                    return;
                }
                break;
        }

        if (_transform.GetMapId(coords) != EntityManager.GetComponent<TransformComponent>(gilbert).MapID)
        {
            shell.WriteError(Loc.GetString("cmd-gilbertgoto-other-map"));
            return;
        }

        _ai.SetGotoOrder(gilbert, coords);
        var mc = _transform.ToMapCoordinates(coords);
        shell.WriteLine(Loc.GetString("cmd-gilbertgoto-set", ("gilbert", name),
            ("coords", $"{mc.X:0.##}, {mc.Y:0.##} (map {mc.MapId})")));
    }

    private bool TryNum(IConsoleShell shell, string s, out float v)
    {
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return true;
        shell.WriteError(Loc.GetString("cmd-gilbertgoto-bad-number", ("value", s)));
        return false;
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(GilbertCommandUtil.Gilberts(EntityManager), Loc.GetString("cmd-gilbertgoto-hint-gilbert")),
            2 => CompletionResult.FromHintOptions(new[] { "here", "clear" }, Loc.GetString("cmd-gilbertgoto-hint-x")),
            3 => CompletionResult.FromHint(Loc.GetString("cmd-gilbertgoto-hint-y")),
            4 => CompletionResult.FromHintOptions(CompletionHelper.MapIds(EntityManager), Loc.GetString("cmd-gilbertgoto-hint-map")),
            _ => CompletionResult.Empty,
        };
    }
}

/// <summary>
/// <c>gilberttarget &lt;gilbert&gt; &lt;targetUid|playerName&gt;</c> / <c>gilberttarget &lt;gilbert&gt; clear</c>: he
/// always knows where the target is, chases it and fights to kill (no warning / arrest); the order ends with its death.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class GilbertTargetCommand : LocalizedEntityCommands
{
    [Dependency] private readonly GilbertAISystem _ai = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public override string Command => "gilberttarget";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError(Loc.GetString("cmd-gilberttarget-usage"));
            return;
        }

        if (!GilbertCommandUtil.TryGilbert(EntityManager, args[0], out var gilbert))
        {
            shell.WriteError(Loc.GetString("cmd-gilbertgoto-not-gilbert", ("uid", args[0])));
            return;
        }

        var name = EntityManager.ToPrettyString(gilbert);
        if (args[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _ai.SetHuntTarget(gilbert, null);
            shell.WriteLine(Loc.GetString("cmd-gilberttarget-cleared", ("gilbert", name)));
            return;
        }

        EntityUid target;
        if (NetEntity.TryParse(args[1], out var net) && EntityManager.TryGetEntity(net, out var t) && EntityManager.EntityExists(t))
            target = t.Value;
        else if (_players.TryGetSessionByUsername(args[1], out var session) && session.AttachedEntity is { } body)
            target = body;
        else
        {
            shell.WriteError(Loc.GetString("cmd-gilberttarget-bad-target", ("value", args[1])));
            return;
        }

        if (target == gilbert.Owner)
        {
            shell.WriteError(Loc.GetString("cmd-gilberttarget-self"));
            return;
        }

        if (!EntityManager.TryGetComponent<MobStateComponent>(target, out var state) || state.CurrentState == MobState.Dead)
        {
            shell.WriteError(Loc.GetString("cmd-gilberttarget-not-mob", ("target", EntityManager.ToPrettyString(target))));
            return;
        }

        _ai.SetHuntTarget(gilbert, target);
        shell.WriteLine(Loc.GetString("cmd-gilberttarget-set", ("gilbert", name), ("target", EntityManager.ToPrettyString(target))));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(GilbertCommandUtil.Gilberts(EntityManager), Loc.GetString("cmd-gilbertgoto-hint-gilbert")),
            2 => CompletionResult.FromHintOptions(CompletionHelper.SessionNames(players: _players).Append(new CompletionOption("clear")),
                Loc.GetString("cmd-gilberttarget-hint-target")),
            _ => CompletionResult.Empty,
        };
    }
}
