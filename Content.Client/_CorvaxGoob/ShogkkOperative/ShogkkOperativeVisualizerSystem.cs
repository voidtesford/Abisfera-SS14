using Content.Shared._CorvaxGoob.ShogkkOperative;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;

namespace Content.Client._CorvaxGoob.ShogkkOperative;

/// <summary>
/// The only effect a SHOGKK operative has: glowing (unshaded, flickering) yellowish eyes with a faint halo, drawn over
/// the helmet and gas mask like Gilbert's red ones. They go dark when he dies.
/// </summary>
public sealed class ShogkkOperativeVisualizerSystem : VisualizerSystem<ShogkkOperativeComponent>
{
    private static readonly ResPath EyesRsi = new("_CorvaxGoob/Mobs/ShogkkOperative/eyes.rsi");
    private static readonly SpriteSpecifier.Rsi EyesSprite = new(EyesRsi, "glow");
    private static readonly SpriteSpecifier.Rsi HaloSprite = new(EyesRsi, "halo");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShogkkOperativeComponent, ComponentStartup>(OnStartup);
    }

    private void OnStartup(Entity<ShogkkOperativeComponent> ent, ref ComponentStartup args)
    {
        if (TryComp<SpriteComponent>(ent, out var sprite))
            EnsureEyesLayers((ent, sprite));
    }

    private void EnsureEyesLayers(Entity<SpriteComponent> sprite)
    {
        if (!SpriteSystem.LayerMapTryGet(sprite.AsNullable(), ShogkkOperativeVisualLayers.EyesHalo, out _, false))
        {
            var halo = SpriteSystem.AddLayer(sprite.AsNullable(), HaloSprite);
            SpriteSystem.LayerMapSet(sprite.AsNullable(), ShogkkOperativeVisualLayers.EyesHalo, halo);
            sprite.Comp.LayerSetShader(halo, "unshaded");
        }

        if (!SpriteSystem.LayerMapTryGet(sprite.AsNullable(), ShogkkOperativeVisualLayers.Eyes, out _, false))
        {
            var eyes = SpriteSystem.AddLayer(sprite.AsNullable(), EyesSprite);
            SpriteSystem.LayerMapSet(sprite.AsNullable(), ShogkkOperativeVisualLayers.Eyes, eyes);
            sprite.Comp.LayerSetShader(eyes, "unshaded");
        }
    }

    protected override void OnAppearanceChange(EntityUid uid, ShogkkOperativeComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        EnsureEyesLayers((uid, args.Sprite));
        var glow = !AppearanceSystem.TryGetData<bool>(uid, ShogkkOperativeVisuals.EyesGlow, out var on, args.Component) || on;
        Entity<SpriteComponent> sprite = (uid, args.Sprite);
        if (SpriteSystem.LayerMapTryGet(sprite.AsNullable(), ShogkkOperativeVisualLayers.Eyes, out var eyes, false))
            SpriteSystem.LayerSetVisible(sprite.AsNullable(), eyes, glow);
        if (SpriteSystem.LayerMapTryGet(sprite.AsNullable(), ShogkkOperativeVisualLayers.EyesHalo, out var halo, false))
            SpriteSystem.LayerSetVisible(sprite.AsNullable(), halo, glow);
    }
}
