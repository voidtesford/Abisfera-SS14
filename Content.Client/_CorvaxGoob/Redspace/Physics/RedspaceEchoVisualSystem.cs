using Content.Shared._CorvaxGoob.Redspace.Physics;
using Robust.Client.GameObjects;

namespace Content.Client._CorvaxGoob.Redspace.Physics;

/// <summary>Draws a redspace echo as a translucent copy of the sprite of whoever it echoes.</summary>
public sealed class RedspaceEchoVisualSystem : EntitySystem
{
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private static readonly Color EchoColor = new(0.75f, 0.55f, 0.6f, 0.35f);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceEchoComponent, AfterAutoHandleStateEvent>(OnState);
    }

    private void OnState(Entity<RedspaceEchoComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (ent.Comp.Source is not { } source
            || !TryComp<SpriteComponent>(source, out var sourceSprite)
            || !TryComp<SpriteComponent>(ent, out var sprite))
            return;

        _sprite.CopySprite((source, sourceSprite), (ent, sprite));
        _sprite.SetColor((ent, sprite), EchoColor);
        _sprite.SetVisible((ent, sprite), true);
    }
}
