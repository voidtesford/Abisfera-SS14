using System.Numerics;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.ProcAnim;
using Robust.Client.GameObjects;
using Robust.Shared.Timing;

namespace Content.Client._CorvaxGoob.Gilbert;

/// <summary>
/// v13: lifts Gilbert's sprite while <see cref="GilbertLevitatingComponent"/> is on him (eases up, bobs, eases
/// down) and puts it back exactly where it was when the component goes away.
/// </summary>
public sealed class GilbertLevitationVisualsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    /// <summary>The sprite offset before the lift, per levitating entity.</summary>
    private readonly Dictionary<EntityUid, Vector2> _base = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertLevitatingComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnShutdown(Entity<GilbertLevitatingComponent> ent, ref ComponentShutdown args)
    {
        if (_base.Remove(ent, out var offset) && TryComp<SpriteComponent>(ent, out var sprite))
            _sprite.SetOffset((ent, sprite), offset);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<GilbertLevitatingComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var comp, out var sprite))
        {
            // A cutscene poses the puppet itself.
            if (HasComp<ProcAnimComponent>(uid))
                continue;

            if (!_base.TryGetValue(uid, out var offset))
                _base[uid] = offset = sprite.Offset;

            _sprite.SetOffset((uid, sprite), offset + new Vector2(0f, GilbertLevitatingComponent.Lift(comp, now)));
        }
    }
}
