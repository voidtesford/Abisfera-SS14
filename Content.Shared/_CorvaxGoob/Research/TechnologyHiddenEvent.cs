using Content.Shared.Research.Prototypes;

namespace Content.Shared._CorvaxGoob.Research;

/// <summary>
/// Broadcast before a technology's recipes are put into a random pool (e.g. a technology disk).
/// Setting <see cref="Hidden"/> keeps the technology out of it. The server uses this to keep
/// event-locked technologies such as KamertonTech out of reach while they are closed.
/// </summary>
[ByRefEvent]
public record struct TechnologyHiddenEvent(TechnologyPrototype Technology, bool Hidden = false);
