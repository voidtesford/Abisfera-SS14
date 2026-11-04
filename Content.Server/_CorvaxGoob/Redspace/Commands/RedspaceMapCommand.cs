using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server._CorvaxGoob.Redspace.Anomalies;
using Content.Server._CorvaxGoob.Redspace.Physics;
using Content.Shared._CorvaxGoob.Redspace.Anomalies;
using Content.Server.Administration;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Redspace.Commands;

/// <summary>
/// redspacemap &lt;mapId&gt; [preset|off] [field=value ...]: makes a map redspace with a preset (calm / normal / storm
/// or a full prototype id), overrides single fields, or switches it off. With only a map id it prints the map's state.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class RedspaceMapCommand : LocalizedEntityCommands
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly RedspaceMapSystem _redspace = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;

    public override string Command => "redspacemap";

    /// <summary>Accepts short names: calm -> RedspaceCalm.</summary>
    public string? ResolvePreset(string name)
    {
        if (_proto.HasIndex<RedspacePresetPrototype>(name))
            return name;

        var full = "Redspace" + char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
        return _proto.HasIndex<RedspacePresetPrototype>(full) ? full : null;
    }

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 1)
        {
            shell.WriteError(Loc.GetString("cmd-redspacemap-usage"));
            shell.WriteLine(Loc.GetString("cmd-redspacemap-presets",
                ("presets", string.Join(", ", _proto.EnumeratePrototypes<RedspacePresetPrototype>().Select(p => p.ID)))));
            return;
        }

        if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            || !_mapSystem.TryGetMap(new MapId(id), out var mapUid))
        {
            shell.WriteError(Loc.GetString("cmd-redspacemap-no-map", ("map", args[0])));
            return;
        }

        if (args.Length == 1)
        {
            if (!EntityManager.TryGetComponent(mapUid, out RedspaceMapComponent? comp))
            {
                shell.WriteLine(Loc.GetString("cmd-redspacemap-state-off", ("map", id)));
                return;
            }

            shell.WriteLine(Loc.GetString("cmd-redspacemap-state", ("map", id), ("preset", comp.Preset.Id),
                ("overrides", comp.Overrides.Count == 0 ? "-" : string.Join(", ", comp.Overrides.Select(o => $"{o.Key}={o.Value}")))));
            return;
        }

        if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            _redspace.SetRedspace(mapUid.Value, null);
            shell.WriteLine(Loc.GetString("cmd-redspacemap-off", ("map", id)));
            return;
        }

        var preset = ResolvePreset(args[1]);
        if (preset == null)
        {
            shell.WriteError(Loc.GetString("cmd-redspacemap-no-preset", ("preset", args[1])));
            return;
        }

        var overrides = new Dictionary<string, string>();
        var probe = new RedspaceSettings();
        foreach (var arg in args.Skip(2))
        {
            var split = arg.Split('=', 2);
            if (split.Length != 2 || !_redspace.TrySetField(probe, split[0], split[1], out var error))
            {
                shell.WriteError(Loc.GetString("cmd-redspacemap-bad-override", ("arg", arg)));
                return;
            }

            overrides[split[0]] = split[1];
        }

        _redspace.SetRedspace(mapUid.Value, preset, overrides);
        shell.WriteLine(Loc.GetString("cmd-redspacemap-on", ("map", id), ("preset", preset)));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHint(Loc.GetString("cmd-redspacemap-hint-map")),
            2 => CompletionResult.FromHintOptions(
                _proto.EnumeratePrototypes<RedspacePresetPrototype>().Select(p => p.ID).Append("off"),
                Loc.GetString("cmd-redspacemap-hint-preset")),
            _ => CompletionResult.FromHintOptions(RedspaceMapSystem.SettingNames.Select(n => n + "="),
                Loc.GetString("cmd-redspacemap-hint-override")),
        };
    }
}

/// <summary>
/// redspacestatus [mapId x y]: whether a position (by default your own) is on redspace and whether something suppresses it.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class RedspaceStatusCommand : LocalizedEntityCommands
{
    [Dependency] private readonly RedspaceMapSystem _redspace = default!;
    [Dependency] private readonly RedspaceAnomalyEventSystem _events = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;

    public override string Command => "redspacestatus";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        MapCoordinates coords;
        EntityUid? who = null;
        if (args.Length == 3
            && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId)
            && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            coords = new MapCoordinates(new Vector2(x, y), new MapId(mapId));
        }
        else if (args.Length == 0 && shell.Player?.AttachedEntity is { } player)
        {
            coords = _transform.GetMapCoordinates(player);
            who = player;
        }
        else
        {
            shell.WriteError(Loc.GetString("cmd-redspacestatus-usage"));
            return;
        }

        var where = $"{coords.MapId} ({coords.Position.X:0.#}, {coords.Position.Y:0.#})";
        if (!_redspace.IsRedspace(coords))
        {
            shell.WriteLine(Loc.GetString("cmd-redspacestatus-not-redspace", ("where", where)));
            return;
        }

        if (_redspace.IsSuppressed(coords, out var by))
        {
            shell.WriteLine(Loc.GetString("cmd-redspacestatus-suppressed", ("where", where),
                ("by", by == null ? "?" : EntityManager.ToPrettyString(by.Value).ToString())));
        }
        else
        {
            shell.WriteLine(Loc.GetString("cmd-redspacestatus-exposed", ("where", where)));
        }

        WriteAnomalies(shell, coords.MapId, who);
    }

    /// <summary>Extra lines: next zone event, running events, and the exposure of the checked player.</summary>
    private void WriteAnomalies(IConsoleShell shell, MapId mapId, EntityUid? who)
    {
        if (_mapSystem.TryGetMap(mapId, out var mapUid))
        {
            var next = _events.NextEventIn(mapUid.Value);
            shell.WriteLine(Loc.GetString("cmd-redspacestatus-next-event",
                ("seconds", next < 0 ? "-" : MathF.Ceiling(next).ToString(CultureInfo.InvariantCulture))));
        }

        var active = _events.ActiveEvents(mapId);
        shell.WriteLine(Loc.GetString("cmd-redspacestatus-active-events", ("count", active.Count),
            ("events", active.Count == 0 ? "-" : string.Join(", ", active.Select(e => $"{e.Kind} {MathF.Ceiling(e.Left)}s")))));

        if (who is not { } uid)
            return;

        if (_redspace.IsImmune(uid))
        {
            shell.WriteLine(Loc.GetString("cmd-redspacestatus-immune"));
            return;
        }

        var exposure = EntityManager.TryGetComponent(uid, out RedspaceAffectedComponent? affected) ? affected.Exposure : 0f;
        var mutations = new List<string>();
        if (EntityManager.TryGetComponent(uid, out RedspaceMutationComponent? mutation))
        {
            if (mutation.Voice)
                mutations.Add("voice");
            if (mutation.Slowdown < 1f)
                mutations.Add("slow");
            if (mutation.ExtraEye)
                mutations.Add("eye");
        }

        shell.WriteLine(Loc.GetString("cmd-redspacestatus-exposure", ("seconds", MathF.Floor(exposure)),
            ("mutations", mutations.Count == 0 ? "-" : string.Join(", ", mutations))));
    }
}
