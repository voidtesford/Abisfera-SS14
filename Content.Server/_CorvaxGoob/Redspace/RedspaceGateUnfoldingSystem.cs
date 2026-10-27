using Content.Server.Administration.Logs;
using Content.Shared._CorvaxGoob.Redspace;
using Content.Shared.Database;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Redspace;

/// <summary>
/// Drives the unfolding stand-in: stamps the start time (the client syncs the one-shot animation to it),
/// plays the stock mechanical sounds at the matching frames and swaps in the real gate at the end.
/// </summary>
public sealed class RedspaceGateUnfoldingSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceGateUnfoldingComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<RedspaceGateUnfoldingComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.Start = _timing.CurTime;
        Dirty(ent);
        _audio.PlayPvs(ent.Comp.StartSound, ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<RedspaceGateUnfoldingComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            var elapsed = now - comp.Start;

            while (comp.RiseSoundsPlayed < comp.RiseSoundTimes.Count && elapsed >= comp.RiseSoundTimes[comp.RiseSoundsPlayed])
            {
                comp.RiseSoundsPlayed++;
                _audio.PlayPvs(comp.RiseSound, uid);
            }

            if (!comp.Connected && elapsed >= comp.ConnectTime)
            {
                comp.Connected = true;
                _audio.PlayPvs(comp.ConnectSound, uid);
                _audio.PlayPvs(comp.ConnectSparksSound, uid);
            }

            if (elapsed < comp.Duration || TerminatingOrDeleted(uid))
                continue;

            var gate = Spawn(comp.Gate, xform.Coordinates);
            _audio.PlayPvs(comp.FinishSound, gate);
            _adminLog.Add(LogType.Construction, LogImpact.Low,
                $"{ToPrettyString(uid):entity} unfolded into {ToPrettyString(gate):entity}");
            QueueDel(uid);
        }
    }
}
