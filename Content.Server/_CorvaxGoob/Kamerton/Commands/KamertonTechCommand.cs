using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._CorvaxGoob.Kamerton.Commands;

/// <summary>
/// kamertontech open|close|status: opens or closes the Kamerton research for events. The state is global and kept
/// between rounds and restarts, see <see cref="KamertonTechLockSystem"/>.
/// </summary>
[AdminCommand(AdminFlags.Fun)] // event tooling, same flag as ftldisk
public sealed class KamertonTechCommand : LocalizedEntityCommands
{
    [Dependency] private readonly KamertonTechLockSystem _lock = default!;

    public override string Command => "kamertontech";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Loc.GetString("cmd-kamertontech-help"));
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "open":
            case "открыть":
                _lock.SetOpen(true);
                shell.WriteLine(Loc.GetString("cmd-kamertontech-opened"));
                break;
            case "close":
            case "закрыть":
                _lock.SetOpen(false);
                shell.WriteLine(Loc.GetString("cmd-kamertontech-closed"));
                break;
            case "status":
            case "статус":
                shell.WriteLine(Loc.GetString(_lock.IsOpen ? "cmd-kamertontech-status-open" : "cmd-kamertontech-status-closed",
                    ("path", KamertonTechLockSystem.StatePath.ToString())));
                break;
            default:
                shell.WriteError(Loc.GetString("cmd-kamertontech-help"));
                break;
        }
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            return CompletionResult.FromHintOptions(new[]
            {
                new CompletionOption("open", Loc.GetString("cmd-kamertontech-hint-open")),
                new CompletionOption("close", Loc.GetString("cmd-kamertontech-hint-close")),
                new CompletionOption("status", Loc.GetString("cmd-kamertontech-hint-status")),
            }, Loc.GetString("cmd-kamertontech-hint"));
        }

        return CompletionResult.Empty;
    }
}
