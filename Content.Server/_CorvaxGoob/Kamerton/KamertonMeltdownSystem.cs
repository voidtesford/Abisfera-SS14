using System.Numerics;
using Content.Shared._CorvaxGoob.Kamerton;
using Content.Shared.Damage;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// A core in meltdown keeps throwing thin red and blue arcs at everything around it within
/// <see cref="KamertonReactorCoreComponent.ArcRange"/>: each arc is a jagged line of short bolt segments (drawn
/// the same way as Gilbert's phase-2 lightning, with Kamerton's own sprites) and a small shock to what it hit.
/// </summary>
public sealed class KamertonMeltdownSystem : EntitySystem
{
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private static readonly EntProtoId[] Segments = { "KamertonArcSegmentRed", "KamertonArcSegmentBlue" };
    private static readonly EntProtoId[] Crawls = { "KamertonArcCrawlRed", "KamertonArcCrawlBlue" };
    private static readonly SoundSpecifier ArcSound = new SoundPathSpecifier("/Audio/Effects/Lightning/lightningshock.ogg");

    private readonly HashSet<Entity<DamageableComponent>> _targets = new();
    private readonly List<EntityUid> _candidates = new();

    /// <summary>Arcs thrown so far, for tests.</summary>
    public int ArcsThrown { get; private set; }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<KamertonReactorCoreComponent>();
        while (query.MoveNext(out var uid, out var core))
        {
            if (!core.Meltdown || core.Collapsed)
            {
                core.ArcAccumulator = 0f;
                continue;
            }

            core.ArcAccumulator += frameTime;
            while (core.ArcAccumulator >= core.ArcInterval)
            {
                core.ArcAccumulator -= core.ArcInterval;
                Arc((uid, core));
            }
        }
    }

    /// <summary>Throws one meltdown arc from the core. Public for tests.</summary>
    public EntityUid? Arc(Entity<KamertonReactorCoreComponent> core)
    {
        var origin = _transform.GetMapCoordinates(core);
        if (origin.MapId == MapId.Nullspace)
            return null;

        _targets.Clear();
        _candidates.Clear();
        _lookup.GetEntitiesInRange(origin, core.Comp.ArcRange, _targets);
        foreach (var t in _targets)
        {
            if (t.Owner == core.Owner || HasComp<KamertonReactorPartComponent>(t) || HasComp<KamertonDroneComponent>(t))
                continue;
            _candidates.Add(t.Owner);
        }

        var color = _random.Next(Segments.Length);
        Vector2 end;
        EntityUid? hit = null;
        if (_candidates.Count > 0)
        {
            hit = _random.Pick(_candidates);
            end = _transform.GetWorldPosition(hit.Value);
        }
        else
        {
            end = origin.Position + _random.NextAngle().ToVec() * _random.NextFloat(2f, core.Comp.ArcRange);
        }

        var start = origin.Position + _random.NextVector2(0.3f);
        DrawArc(origin.MapId, start, end, Segments[color]);
        Spawn(Crawls[color], new MapCoordinates(end, origin.MapId));

        if (hit is { } target)
            _damage.TryChangeDamage(target, core.Comp.ArcDamage, interruptsDoAfters: false, origin: core);

        ArcsThrown++;
        if (ArcsThrown % 4 == 0)
            _audio.PlayPvs(ArcSound, core, AudioParams.Default.WithVolume(-8f).WithVariation(0.2f));

        return hit;
    }

    /// <summary>A jagged thin arc from <paramref name="from"/> to <paramref name="to"/> out of ~1-tile segments.</summary>
    private void DrawArc(MapId map, Vector2 from, Vector2 to, EntProtoId segment)
    {
        var delta = to - from;
        var len = delta.Length();
        if (len < 0.05f)
            return;

        var n = Math.Max(1, (int) MathF.Ceiling(len / 0.9f));
        var normal = new Vector2(-delta.Y, delta.X) / len;
        var prev = from;
        for (var i = 1; i <= n; i++)
        {
            var next = i == n
                ? to
                : from + delta * ((float) i / n) + normal * _random.NextFloat(-0.25f, 0.25f);
            var seg = next - prev;
            var fx = Spawn(segment, new MapCoordinates((prev + next) / 2f, map));
            _transform.SetWorldRotation(fx, new Angle(seg));
            prev = next;
        }
    }
}
