using Content.Shared._CorvaxGoob.ShogkkOperative;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;

namespace Content.Client._CorvaxGoob.ShogkkOperative;

/// <summary>
/// The ordinary SHOGKK zombified's eyes: the operatives' glowing eye layers (unshaded, flickering, faint halo) in a
/// dim, pale amber-red. They go dark on death.
/// </summary>
public sealed class ShogkkZombifiedVisualizerSystem : VisualizerSystem<ShogkkZombifiedComponent>
{
    private static readonly ResPath EyesRsi = new("_CorvaxGoob/Mobs/ShogkkZombified/eyes.rsi");
    private static readonly SpriteSpecifier.Rsi EyesSprite = new(EyesRsi, "glow");
    private static readonly SpriteSpecifier.Rsi HaloSprite = new(EyesRsi, "halo");

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShogkkZombifiedComponent, ComponentStartup>(OnStartup);
    }

    private void OnStartup(Entity<ShogkkZombifiedComponent> ent, ref ComponentStartup args)
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

    protected override void OnAppearanceChange(EntityUid uid, ShogkkZombifiedComponent component, ref AppearanceChangeEvent args)
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
