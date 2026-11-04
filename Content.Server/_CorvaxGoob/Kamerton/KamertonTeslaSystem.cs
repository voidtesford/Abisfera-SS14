using System.Numerics;
using Content.Server.Beam;
using Content.Server.Singularity.Events;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Drives the Kamerton tesla: tentacle bursts, offspring and immunity to singularities.
/// Its ordinary arcs come from <c>LightningArcShooter</c> with the scarlet lightning.
/// </summary>
public sealed class KamertonTeslaSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly BeamSystem _beam = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    private static readonly SoundSpecifier BurstSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningbolt.ogg");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<KamertonTeslaComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<KamertonTeslaComponent, EventHorizonAttemptConsumeEntityEvent>(OnAttemptConsume);
    }

    private void OnMapInit(Entity<KamertonTeslaComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.BurstTimer = _random.NextFloat(ent.Comp.BurstMinInterval, ent.Comp.BurstMaxInterval);
        ent.Comp.SpawnTimer = ent.Comp.SpawnInterval;
    }

    /// <summary>No singularity can swallow the Kamerton tesla.</summary>
    private void OnAttemptConsume(Entity<KamertonTeslaComponent> ent, ref EventHorizonAttemptConsumeEntityEvent args)
    {
        args.Cancelled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<KamertonTeslaComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var ent = (uid, comp);

            comp.SpawnTimer -= frameTime;
            if (comp.SpawnTimer <= 0f)
            {
                comp.SpawnTimer += comp.SpawnInterval;
                SpawnOffspring(ent);
            }

            comp.BurstTimer -= frameTime;
            if (comp.BurstTimer <= 0f)
            {
                comp.BurstTimer += _random.NextFloat(comp.BurstMinInterval, comp.BurstMaxInterval);
                Burst(ent);
            }

            GrowTentacles(ent, frameTime);
        }
    }

    /// <summary>Spawns one tesla ball or one singularity 8-15 tiles away.</summary>
    public EntityUid SpawnOffspring(Entity<KamertonTeslaComponent> ent)
    {
        var comp = ent.Comp;
        var pos = _transform.GetMapCoordinates(ent);
        var dist = _random.NextFloat(comp.SpawnMinRange, comp.SpawnMaxRange);
        var offset = _random.NextAngle().ToVec() * dist;
        var proto = _random.Prob(comp.SingularityChance) ? comp.SingularityPrototype : comp.TeslaPrototype;
        return Spawn(proto, new MapCoordinates(pos.Position + offset, pos.MapId));
    }

    /// <summary>
    /// Throws a ring of 8-12 tentacles. Each tentacle is a chain of short beams along a curling, noisy path;
    /// the segments light up one after another so the tentacle grows outward. Returns the segment count.
    /// </summary>
    public int Burst(Entity<KamertonTeslaComponent> ent)
    {
        var comp = ent.Comp;
        var center = _transform.GetMapCoordinates(ent);
        var count = _random.Next(comp.MinTentacles, comp.MaxTentacles + 1);
        var steps = Math.Max(1, (int) MathF.Ceiling(comp.TentacleRange / comp.SegmentLength));
        var stepDelay = comp.GrowTime / steps;

        if (comp.BurstController is { } old && !TerminatingOrDeleted(old))
            QueueDel(old);
        comp.BurstController = Spawn("VirtualBeamEntityController", center);
        _audio.PlayPvs(BurstSound, ent);

        var segments = 0;
        for (var t = 0; t < count; t++)
        {
            var angle = MathF.Tau * t / count + _random.NextFloat(-0.25f, 0.25f);
            var turn = _random.NextFloat(-comp.Curl, comp.Curl);
            var phase = _random.NextFloat(0f, MathF.Tau);
            var point = center.Position;
            var prev = Spawn(comp.NodePrototype, center);

            for (var s = 1; s <= steps; s++)
            {
                // A tentacle curls one way, sways like a whip and twitches.
                turn = Math.Clamp(turn + _random.NextFloat(-comp.Curl, comp.Curl) * 0.5f, -comp.Curl, comp.Curl);
                angle += turn * 0.3f + MathF.Sin(phase + s * 0.7f) * 0.15f;
                var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                point += dir * comp.SegmentLength;
                var jitter = new Vector2(-dir.Y, dir.X) * _random.NextFloat(-0.4f, 0.4f);

                var next = Spawn(comp.NodePrototype, new MapCoordinates(point + jitter, center.MapId));
                comp.Pending.Add(new KamertonTentacleSegment(prev, next, s * stepDelay));
                prev = next;
                segments++;
            }
        }

        return segments;
    }

    private void GrowTentacles(Entity<KamertonTeslaComponent> ent, float frameTime)
    {
        var comp = ent.Comp;
        if (comp.Pending.Count == 0)
            return;

        for (var i = comp.Pending.Count - 1; i >= 0; i--)
        {
            var seg = comp.Pending[i];
            seg.Delay -= frameTime;
            if (seg.Delay > 0f)
            {
                comp.Pending[i] = seg;
                continue;
            }

            comp.Pending.RemoveAt(i);
            if (TerminatingOrDeleted(seg.From) || TerminatingOrDeleted(seg.To))
                continue;

            var state = $"lightning_{_random.Next(1, 13)}";
            _beam.TryCreateBeam(seg.From, seg.To, comp.TentaclePrototype, state, controller: comp.BurstController);
        }
    }
}
