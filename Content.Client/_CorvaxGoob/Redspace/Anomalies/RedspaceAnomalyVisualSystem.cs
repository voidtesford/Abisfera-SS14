using Content.Shared._CorvaxGoob.Redspace.Anomalies;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;

namespace Content.Client._CorvaxGoob.Redspace.Anomalies;

/// <summary>
/// Sprites of redspace anomaly entities that depend on someone else: the doppelganger copies its source and gets a
/// glitch layer, a mutated mob gets an extra eye layer. No shaders, no tint of the screen.
/// </summary>
public sealed class RedspaceAnomalyVisualSystem : EntitySystem
{
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private const string EyeKey = "redspace-mutation-eye";
    private static readonly ResPath DoppelRsi = new("/Textures/_CorvaxGoob/Effects/redspace_doppelganger.rsi");
    private static readonly ResPath EyeRsi = new("/Textures/_CorvaxGoob/Effects/redspace_mutation_eye.rsi");
    private static readonly Color DoppelColor = new(0.35f, 0.33f, 0.38f, 0.9f);

    private readonly HashSet<EntityUid> _withEye = new();
    private readonly List<EntityUid> _remove = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceDoppelgangerComponent, AfterAutoHandleStateEvent>(OnDoppelState);
    }

    private void OnDoppelState(Entity<RedspaceDoppelgangerComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (ent.Comp.Source is not { } source
            || !TryComp<SpriteComponent>(source, out var sourceSprite)
            || !TryComp<SpriteComponent>(ent, out var sprite))
            return;

        _sprite.CopySprite((source, sourceSprite), (ent, sprite));
        _sprite.SetColor((ent, sprite), DoppelColor);
        var layer = _sprite.AddRsiLayer((ent, sprite), "glitch", DoppelRsi);
        _sprite.LayerSetColor((ent, sprite), layer, new Color(1f, 1f, 1f, 0.9f));
        _sprite.SetVisible((ent, sprite), true);
    }

    /// <summary>Extra eye layers are synced by polling, the mutation component's own events belong to the shared system.</summary>
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

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
