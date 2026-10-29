using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// v13 admin trigger for Gilbert's rage abilities: <c>gilbertability &lt;gilbert&gt; leap|cone|levitate [target]</c>.
/// Ignores cooldowns, but he must be in his rage phase (<c>gilbertberserk</c> first).
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class GilbertAbilityCommand : LocalizedEntityCommands
{
    [Dependency] private readonly GilbertRageAbilitiesSystem _abilities = default!;
    [Dependency] private readonly GilbertBerserkSystem _berserk = default!;

    /// <summary>"rage" starts his second phase (transition first, about 7 s), the rest are the abilities.</summary>
    public static readonly string[] Abilities = { "leap", "cone", "levitate", "rage" };

    public override string Command => "gilbertability";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 2 or > 3 || Array.IndexOf(Abilities, args[1]) < 0)
        {
            shell.WriteError(Loc.GetString("cmd-gilbertability-usage"));
            return;
        }

        if (!TryParse(args[0], out var gilbert) || !EntityManager.TryGetComponent<GilbertRageAbilitiesComponent>(gilbert, out var comp))
        {
            shell.WriteError(Loc.GetString("cmd-gilbertability-not-gilbert", ("uid", args[0])));
            return;
        }

        if (args[1] == "rage")
        {
            if (!EntityManager.TryGetComponent<Content.Shared._CorvaxGoob.Gilbert.GilbertBerserkComponent>(gilbert, out var berserk)
                || berserk.Phase != Content.Shared._CorvaxGoob.Gilbert.GilbertBerserkPhase.None)
            {
                shell.WriteError(Loc.GetString("cmd-gilbertability-rage-failed"));
                return;
            }

            _berserk.StartBerserk((gilbert, berserk));
            shell.WriteLine(Loc.GetString("cmd-gilbertability-rage-started"));
            return;
        }

        EntityUid? target = null;
        if (args.Length == 3)
        {
            if (!TryParse(args[2], out var t))
            {
                shell.WriteError(Loc.GetString("cmd-gilbertability-bad-uid", ("uid", args[2])));
                return;
            }
            target = t;
        }

        if (!_abilities.InRage(gilbert))
        {
            shell.WriteError(Loc.GetString("cmd-gilbertability-not-rage"));
            return;
        }

        if (args[1] == "leap" && target == null)
        {
            shell.WriteError(Loc.GetString("cmd-gilbertability-need-target"));
            return;
        }

        var ok = args[1] switch
        {
            "leap" => _abilities.TryLeap((gilbert, comp), target!.Value, true),
            "cone" => _abilities.TryCone((gilbert, comp), target, true),
            _ => _abilities.TryLevitate((gilbert, comp), true),
        };

        if (!ok)
        {
            shell.WriteError(Loc.GetString("cmd-gilbertability-failed", ("ability", args[1])));
            return;
        }

        shell.WriteLine(Loc.GetString("cmd-gilbertability-started", ("ability", args[1]),
            ("gilbert", EntityManager.ToPrettyString(gilbert))));
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
            1 => CompletionResult.FromHint(Loc.GetString("cmd-gilbertability-hint-gilbert")),
            2 => CompletionResult.FromHintOptions(Abilities, Loc.GetString("cmd-gilbertability-hint-ability")),
            3 => CompletionResult.FromHint(Loc.GetString("cmd-gilbertability-hint-target")),
            _ => CompletionResult.Empty,
        };
    }
}
