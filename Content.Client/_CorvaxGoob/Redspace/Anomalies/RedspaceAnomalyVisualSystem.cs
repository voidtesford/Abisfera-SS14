using Content.Shared._CorvaxGoob.Redspace.Anomalies;
using Robust.Client.GameObjects;
using System.Linq;
using System.Numerics;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._CorvaxGoob.Redspace.Anomalies;

/// <summary>
/// Sprites of redspace anomaly entities that depend on someone else: the doppelganger copies its source and gets a
/// glitch layer, a mutated mob gets an extra eye layer. No shaders, no tint of the screen.
/// </summary>
public sealed class RedspaceAnomalyVisualSystem : EntitySystem
{
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private const string EyeKey = "redspace-mutation-eye";
    private const string GlitchKey = "redspace-doppelganger-glitch";
    private static readonly ResPath DoppelRsi = new("/Textures/_CorvaxGoob/Effects/redspace_doppelganger.rsi");
    private static readonly ResPath EyeRsi = new("/Textures/_CorvaxGoob/Effects/redspace_mutation_eye.rsi");

    /// <summary>Washed-out, slightly cold copy: recognisable, but clearly wrong. No red.</summary>
    private static readonly Color DoppelColor = new(0.8f, 0.82f, 0.88f, 0.82f);

    /// <summary>How far (in tiles) the copy twitches and how often.</summary>
    private const float JitterAmplitude = 1f / 32f;
    private static readonly TimeSpan JitterPeriod = TimeSpan.FromSeconds(0.12);

    private readonly HashSet<EntityUid> _withEye = new();
    private readonly List<EntityUid> _remove = new();

    /// <summary>Doppelgangers still waiting for their source's sprite (not streamed in yet, or sprite not built yet).</summary>
    private readonly HashSet<EntityUid> _pending = new();
    private readonly Dictionary<EntityUid, EntityUid> _dressedFrom = new();
    private readonly List<EntityUid> _done = new();
    private TimeSpan _nextJitter;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceDoppelgangerComponent, ComponentStartup>(OnDoppelStartup);
        SubscribeLocalEvent<RedspaceDoppelgangerComponent, AfterAutoHandleStateEvent>(OnDoppelState);
        SubscribeLocalEvent<RedspaceDoppelgangerComponent, ComponentShutdown>(OnDoppelShutdown);
    }

    private void OnDoppelStartup(Entity<RedspaceDoppelgangerComponent> ent, ref ComponentStartup args)
    {
        _pending.Add(ent);
    }

    private void OnDoppelState(Entity<RedspaceDoppelgangerComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (ent.Comp.Source is { } source && _dressedFrom.TryGetValue(ent, out var dressed) && dressed == source)
            return;

        _pending.Add(ent);
    }

    private void OnDoppelShutdown(Entity<RedspaceDoppelgangerComponent> ent, ref ComponentShutdown args)
    {
        _pending.Remove(ent);
        _dressedFrom.Remove(ent);
    }

    /// <summary>
    /// Copies the source's current look (body, hair, clothes, held items) onto the doppelganger and adds the glitch
    /// overlay. False while the source is not known on this client yet; the glitch silhouette stays visible meanwhile.
    /// </summary>
    private bool TryDress(EntityUid uid, RedspaceDoppelgangerComponent comp)
    {
        if (comp.Source is not { } source
            || TerminatingOrDeleted(source)
            || !TryComp<SpriteComponent>(source, out var sourceSprite)
            || !sourceSprite.AllLayers.Any()
            || !TryComp<SpriteComponent>(uid, out var sprite))
            return false;

        _sprite.CopySprite((source, sourceSprite), (uid, sprite));
        _sprite.SetColor((uid, sprite), DoppelColor);
        var layer = _sprite.AddRsiLayer((uid, sprite), "glitch", DoppelRsi);
        _sprite.LayerMapSet((uid, sprite), GlitchKey, layer);
        _sprite.LayerSetColor((uid, sprite), layer, new Color(1f, 1f, 1f, 0.75f));
        _sprite.SetVisible((uid, sprite), true);
        _dressedFrom[uid] = source;
        return true;
    }

    public bool IsDressed(EntityUid uid) => _dressedFrom.ContainsKey(uid);

    private void UpdateDoppelgangers()
    {
        _done.Clear();
        foreach (var uid in _pending)
        {
            if (TerminatingOrDeleted(uid) || !TryComp<RedspaceDoppelgangerComponent>(uid, out var comp))
            {
                _done.Add(uid);
                continue;
            }

            if (TryDress(uid, comp))
                _done.Add(uid);
        }

        foreach (var uid in _done)
            _pending.Remove(uid);

        // A small in-world twitch of the copy itself (sprite offset only, nothing on the screen).
        if (_timing.RealTime < _nextJitter)
            return;

        _nextJitter = _timing.RealTime + JitterPeriod;
        var query = EntityQueryEnumerator<RedspaceDoppelgangerComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out _, out var sprite))
        {
            var twitch = _random.Prob(0.35f)
                ? new Vector2(_random.NextFloat(-1f, 1f), _random.NextFloat(-1f, 1f)) * JitterAmplitude
                : Vector2.Zero;
            _sprite.SetOffset((uid, sprite), twitch);
        }
    }

    /// <summary>Extra eye layers are synced by polling, the mutation component's own events belong to the shared system.</summary>
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        UpdateDoppelgangers();

        var query = EntityQueryEnumerator<RedspaceMutationComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var mutation, out var sprite))
        {
            if (!mutation.ExtraEye || _withEye.Contains(uid))
                continue;

            var index = _sprite.AddRsiLayer((uid, sprite), "eye", EyeRsi);
            _sprite.LayerMapSet((uid, sprite), EyeKey, index);
            _withEye.Add(uid);
        }

        _remove.Clear();
        foreach (var uid in _withEye)
        {
            if (TryComp<RedspaceMutationComponent>(uid, out var mutation) && mutation.ExtraEye && !TerminatingOrDeleted(uid))
                continue;
            _remove.Add(uid);
        }

        foreach (var uid in _remove)
        {
            _withEye.Remove(uid);
            if (TryComp<SpriteComponent>(uid, out var sprite) && !TerminatingOrDeleted(uid))
                _sprite.RemoveLayer((uid, sprite), EyeKey, false);
        }
    }

    public bool HasEyeLayer(EntityUid uid)
    {
        return TryComp<SpriteComponent>(uid, out var sprite) && _sprite.LayerMapTryGet((uid, sprite), EyeKey, out _, false);
    }
}
