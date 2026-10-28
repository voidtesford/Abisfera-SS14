using System.Linq;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._CorvaxGoob.Gilbert.Finisher;

/// <summary>
/// Debug/admin trigger for the finisher cutscene (t265u): <c>gilbertfinisher &lt;attacker&gt; &lt;victim&gt; [variant] [keepdown]</c>,
/// or <c>gilbertfinisher stop &lt;uid&gt;</c>. Works with any two humanoids, not only Gilbert.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class GilbertFinisherCommand : LocalizedEntityCommands
{
    [Dependency] private readonly GilbertFinisherSystem _finisher = default!;

    public override string Command => "gilbertfinisher";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 2 && args[0] == "stop")
        {
            if (!TryParse(args[1], out var stopUid))
            {
                shell.WriteError(Loc.GetString("cmd-gilbertfinisher-bad-uid", ("uid", args[1])));
                return;
            }

            _finisher.Stop(stopUid);
            shell.WriteLine(Loc.GetString("cmd-gilbertfinisher-stopped", ("uid", args[1])));
            return;
        }

        if (args.Length is < 2 or > 4)
        {
            shell.WriteError(Loc.GetString("cmd-gilbertfinisher-usage"));
            return;
        }

        if (!TryParse(args[0], out var attacker))
        {
            shell.WriteError(Loc.GetString("cmd-gilbertfinisher-bad-uid", ("uid", args[0])));
            return;
        }

        if (!TryParse(args[1], out var victim))
        {
            shell.WriteError(Loc.GetString("cmd-gilbertfinisher-bad-uid", ("uid", args[1])));
            return;
        }

        // Optional variant (1 / 2 / 3 or a prototype id) and keepdown, in any order.
        var keepDown = true;
        var finisher = GilbertFinisherSystem.DefaultFinisher;
        for (var i = 2; i < args.Length; i++)
        {
            if (bool.TryParse(args[i], out var kd))
                keepDown = kd;
            else if (_finisher.ResolveVariant(args[i]) is { } id)
                finisher = id;
            else
            {
                shell.WriteError(Loc.GetString("cmd-gilbertfinisher-bad-variant", ("variant", args[i])));
                return;
            }
        }

        if (!_finisher.TryStart(attacker, victim, out var error, finisher, keepDown))
        {
            shell.WriteError(Loc.GetString(error));
            return;
        }

        shell.WriteLine(Loc.GetString("cmd-gilbertfinisher-started",
            ("attacker", EntityManager.ToPrettyString(attacker)), ("victim", EntityManager.ToPrettyString(victim))));
    }

    private bool TryParse(string arg, out EntityUid uid)
    {
        uid = EntityUid.Invalid;
        return NetEntity.TryParse(arg, out var net)
               && EntityManager.TryGetEntity(net, out var ent)
               && EntityManager.EntityExists(ent.Value)
               && (uid = ent.Value) != EntityUid.Invalid;
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint(Loc.GetString("cmd-gilbertfinisher-hint-attacker")),
            2 => CompletionResult.FromHint(Loc.GetString("cmd-gilbertfinisher-hint-victim")),
            3 => CompletionResult.FromHintOptions(GilbertFinisherSystem.Variants.Select((_, i) => (i + 1).ToString())
                .Concat(GilbertFinisherSystem.Variants).Concat(new[] { "true", "false" }), Loc.GetString("cmd-gilbertfinisher-hint-variant")),
            4 => CompletionResult.FromHintOptions(new[] { "true", "false" }, Loc.GetString("cmd-gilbertfinisher-hint-keepdown")),
            _ => CompletionResult.Empty,
        };
    }
}
