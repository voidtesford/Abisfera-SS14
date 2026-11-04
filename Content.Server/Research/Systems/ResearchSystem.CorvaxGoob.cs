using Content.Server._CorvaxGoob.Kamerton;
using Content.Server.Research.Components;
using Content.Shared.Research.Components;

namespace Content.Server.Research.Systems;

// CorvaxGoob: event-locked technologies (KamertonTech) are hidden from consoles and cannot be unlocked while closed.
public sealed partial class ResearchSystem
{
    [Dependency] private readonly KamertonTechLockSystem _techLock = default!;

    public bool IsTechnologyHidden(string technology)
    {
        return _techLock.IsHidden(technology);
    }

    /// <summary>Re-sends the state of every research console, e.g. after a technology was opened or closed.</summary>
    public void RefreshAllConsoles()
    {
        var query = EntityQueryEnumerator<ResearchConsoleComponent>();
        while (query.MoveNext(out var uid, out var console))
        {
            UpdateConsoleInterface(uid, console);
        }
    }
}
