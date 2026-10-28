using System.Linq;
using Content.Server.Administration;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ProcAnim;

/// <summary>
/// Admin / debug trigger for any ProcAnim scene: <c>procanim play &lt;scene&gt; &lt;actor uids...&gt; [keepdown]</c>
/// (uids in the scene's role order), <c>procanim stop [uid]</c> (no uid: every playing scene).
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class ProcAnimCommand : LocalizedEntityCommands
{
    [Dependency] private readonly ProcAnimSystem _procAnim = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public override string Command => "procanim";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length >= 1 && args[0] == "stop")
        {
            if (args.Length == 1)
            {
                shell.WriteLine(Loc.GetString("cmd-procanim-stopped-all", ("count", _procAnim.StopAll())));
                return;
            }

            for (var i = 1; i < args.Length; i++)
            {
                if (!TryParse(args[i], out var stopUid))
                {
                    shell.WriteError(Loc.GetString("cmd-procanim-bad-uid", ("uid", args[i])));
                    return;
                }

                _procAnim.Stop(stopUid);
            }

            shell.WriteLine(Loc.GetString("cmd-procanim-stopped"));
            return;
        }

        if (args.Length < 3 || args[0] != "play")
        {
            shell.WriteError(Loc.GetString("cmd-procanim-usage"));
            return;
        }

        var scene = args[1];
        var keepDown = true;
        var actors = new List<EntityUid>();
        for (var i = 2; i < args.Length; i++)
        {
            if (i == args.Length - 1 && bool.TryParse(args[i], out var kd))
            {
                keepDown = kd;
                continue;
            }

            if (!TryParse(args[i], out var uid))
            {
                shell.WriteError(Loc.GetString("cmd-procanim-bad-uid", ("uid", args[i])));
                return;
            }

            actors.Add(uid);
        }

        // Authoring problems are only warnings here: the scene still plays, so it can be looked at.
        if (_proto.TryIndex<ProcAnimScenePrototype>(scene, out var checkedScene))
        {
            foreach (var problem in ProcAnimValidation.Validate(checkedScene))
                shell.WriteError(problem);
        }

        if (!_procAnim.TryPlay(scene, actors, out var error, keepDown))
        {
            var expected = _proto.TryIndex<ProcAnimScenePrototype>(scene, out var proto)
                ? string.Join(' ', proto.Actors.Select(a => a.Role))
                : string.Empty;
            shell.WriteError(Loc.GetString(error, ("roles", expected)));
            return;
        }

        shell.WriteLine(Loc.GetString("cmd-procanim-started", ("scene", scene),
            ("actors", string.Join(", ", actors.Select(a => EntityManager.ToPrettyString(a).ToString())))));
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
        if (args.Length == 1)
            return CompletionResult.FromHintOptions(new[] { "play", "stop" }, Loc.GetString("cmd-procanim-hint-mode"));
        if (args[0] != "play")
            return CompletionResult.FromHint(Loc.GetString("cmd-procanim-hint-uid"));
        if (args.Length == 2)
            return CompletionResult.FromHintOptions(
                _proto.EnumeratePrototypes<ProcAnimScenePrototype>().Select(p => p.ID).OrderBy(x => x),
                Loc.GetString("cmd-procanim-hint-scene"));

        var role = _proto.TryIndex<ProcAnimScenePrototype>(args[1], out var scene) && args.Length - 3 < scene.Actors.Count
            ? scene.Actors[args.Length - 3].Role
            : "keepdown";
        return CompletionResult.FromHint(Loc.GetString("cmd-procanim-hint-actor", ("role", role)));
    }
}
