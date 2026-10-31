namespace Content.Shared._CorvaxGoob.ProcAnim;

/// <summary>
/// Server: raised on the target of a timeline event after it was applied (sound, damage, amputation, kill...).
/// Hook for scene-specific gameplay on top of the built-in event types.
/// </summary>
[ByRefEvent]
public readonly record struct ProcAnimEventFiredEvent(string Scene, int Index, ProcAnimEvent Event, EntityUid Lead);

/// <summary>Server: raised on every actor when its scene ends (<paramref name="Completed"/>: played to the end).</summary>
[ByRefEvent]
public readonly record struct ProcAnimEndedEvent(string Scene, int Actor, bool Completed);

/// <summary>
/// Client: raised on an actor when its keyed facial expression / eye state (<see cref="ProcAnimActor.Expressions"/>)
/// changes. Stub hook for future face / eye layers; nothing in the library draws them.
/// </summary>
[ByRefEvent]
public readonly record struct ProcAnimExpressionChangedEvent(ProcAnimExpression Old, ProcAnimExpression New);
