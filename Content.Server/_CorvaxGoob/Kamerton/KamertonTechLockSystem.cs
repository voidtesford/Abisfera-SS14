using Content.Server.Research.Systems;
using Content.Shared._CorvaxGoob.Research;
using Content.Shared.Research.Prototypes;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Global, persistent switch for the Kamerton research, toggled by the <c>kamertontech</c> admin command for events.
/// Closed (the default): KamertonTech is left out of every research console state, cannot be unlocked
/// and its recipes do not show up on random technology disks. Open: it behaves like any other technology.
/// The state lives in the server's user data as <see cref="StatePath"/>, so it survives rounds and restarts.
/// </summary>
public sealed class KamertonTechLockSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _res = default!;
    [Dependency] private readonly ResearchSystem _research = default!;

    public static readonly ProtoId<TechnologyPrototype> Technology = "KamertonTech";

    /// <summary>File in the server data folder (next to server_config.toml on a local server).</summary>
    public static readonly ResPath StatePath = new("/kamerton_tech.yml");

    public bool IsOpen { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TechnologyHiddenEvent>(OnTechnologyHidden);
        Reload();
    }

    private void OnTechnologyHidden(ref TechnologyHiddenEvent args)
    {
        if (IsHidden(args.Technology.ID))
            args.Hidden = true;
    }

    /// <summary>Whether a technology must be hidden from consoles and kept from being unlocked.</summary>
    public bool IsHidden(string technology)
    {
        return !IsOpen && technology == Technology.Id;
    }

    /// <summary>Opens or closes the Kamerton research, saves the state and refreshes every research console.</summary>
    public void SetOpen(bool open)
    {
        IsOpen = open;
        Save();
        _research.RefreshAllConsoles();
    }

    /// <summary>Reads the state back from disk; a missing or unreadable file means closed.</summary>
    public void Reload()
    {
        IsOpen = false;
        try
        {
            if (!_res.UserData.Exists(StatePath))
                return;

            foreach (var line in _res.UserData.ReadAllText(StatePath).Split('\n'))
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2 && parts[0].Trim() == "open")
                    IsOpen = parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Could not read {StatePath}, Kamerton research stays closed: {e}");
            IsOpen = false;
        }
    }

    private void Save()
    {
        try
        {
            _res.UserData.WriteAllText(StatePath,
                "# Kamerton research for events, changed with the kamertontech command.\n" +
                $"open: {(IsOpen ? "true" : "false")}\n");
        }
        catch (Exception e)
        {
            Log.Error($"Could not save {StatePath}: {e}");
        }
    }
}
