using System.Linq;
using Content.Server._CorvaxGoob.Redspace.Anomalies;
using Content.Server.Administration;
using Content.Shared._CorvaxGoob.Redspace.Anomalies;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._CorvaxGoob.Redspace.Commands;

/// <summary>
/// redspaceevent &lt;type&gt; [target]: starts a redspace zone event next to a player (yourself by default).
/// Protection rules still apply: suppressed, immune or dead targets are refused with the reason.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class RedspaceEventCommand : LocalizedEntityCommands
{
    [Dependency] private readonly RedspaceAnomalyEventSystem _events = default!;

    public override string Command => "redspaceevent";

    public static readonly Dictionary<string, RedspaceAnomalyEvent> Types = new()
    {
        ["rift"] = RedspaceAnomalyEvent.Rift,
        ["timeloop"] = RedspaceAnomalyEvent.TimeLoop,
        ["doppelganger"] = RedspaceAnomalyEvent.Doppelganger,
        ["bloodrain"] = RedspaceAnomalyEvent.BloodRain,
        ["mirror"] = RedspaceAnomalyEvent.MirrorCorridor,
        ["whispers"] = RedspaceAnomalyEvent.Whispers,
        ["gravityknot"] = RedspaceAnomalyEvent.GravityKnot,
    };

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1 || args.Length > 2 || !Types.TryGetValue(args[0].ToLowerInvariant(), out var kind))
        {
            shell.WriteError(Loc.GetString("cmd-redspaceevent-usage", ("types", string.Join(", ", Types.Keys))));
            return;
        }

        EntityUid target;
        if (args.Length == 2)
        {
            if (!NetEntity.TryParse(args[1], out var net) || !EntityManager.TryGetEntity(net, out var found))
            {
                shell.WriteError(Loc.GetString("cmd-redspaceevent-bad-target", ("target", args[1])));
                return;
            }
            target = found.Value;
        }
        else if (shell.Player?.AttachedEntity is { } self)
        {
            target = self;
        }
        else
        {
            shell.WriteError(Loc.GetString("cmd-redspaceevent-usage", ("types", string.Join(", ", Types.Keys))));
            return;
        }

        if (!_events.TryTriggerEvent(kind, target, out var spawned, out var reason))
        {
            shell.WriteError(Loc.GetString("cmd-redspaceevent-failed", ("type", args[0]), ("reason", Loc.GetString(reason))));
            return;
        }

        shell.WriteLine(Loc.GetString("cmd-redspaceevent-started", ("type", args[0]),
            ("target", EntityManager.ToPrettyString(target).ToString()),
            ("spawned", spawned == null ? "-" : EntityManager.ToPrettyString(spawned.Value).ToString())));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(Types.Keys, Loc.GetString("cmd-redspaceevent-hint-type")),
            2 => CompletionResult.FromHint(Loc.GetString("cmd-redspaceevent-hint-target")),
            _ => CompletionResult.Empty,
        };
    }
}
