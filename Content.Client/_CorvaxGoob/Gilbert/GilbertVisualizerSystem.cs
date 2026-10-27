using Content.Shared._CorvaxGoob.Gilbert;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._CorvaxGoob.Gilbert;

/// <summary>
/// Spawn sequence visuals (red energy silhouette assembling, then a fading red tint) and glowing
/// (unshaded, flickering, with a soft halo) red eyes for Gilbert.
/// </summary>
public sealed class GilbertVisualizerSystem : VisualizerSystem<GilbertComponent>
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;

    public static readonly ProtoId<ShaderPrototype> SilhouetteShader = "GilbertSilhouette";

    private ShaderInstance _silhouette = default!;

    /// <summary>The post-shader drawing the assembling red-energy silhouette (one instance for all Gilberts).</summary>
    public ShaderInstance SilhouetteInstance => _silhouette;

    private static readonly SpriteSpecifier.Rsi EyesSprite =
        new(new ResPath("_CorvaxGoob/Mobs/Gilbert/eyes.rsi"), "glow");

    private static readonly SpriteSpecifier.Rsi HaloSprite =
        new(new ResPath("_CorvaxGoob/Mobs/Gilbert/eyes.rsi"), "halo");

    private static readonly Color SpawnTint = Color.FromHex("#ff2020");

    public override void Initialize()
    {
        base.Initialize();
        _silhouette = _proto.Index(SilhouetteShader).InstanceUnique();
        SubscribeLocalEvent<GilbertComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<GilbertComponent, BeforePostShaderRenderEvent>(OnShaderRender);
    }

    /// <summary>How far the silhouette has assembled, 0..1.</summary>
    public float SilhouetteProgress(GilbertComponent comp)
    {
        var duration = comp.AssembleDuration.TotalSeconds;
        if (duration <= 0)
            return 1f;
        return (float) Math.Clamp((_timing.CurTime - comp.SpawnTime).TotalSeconds / duration, 0, 1);
    }

    private void OnShaderRender(EntityUid uid, GilbertComponent comp, BeforePostShaderRenderEvent args)
    {
        if (args.Sprite.PostShader != _silhouette)
            return;

        // Noise cells move with the entity (see the stealth shader for this reference trick).
        var parent = Transform(uid).ParentUid;
        if (!parent.IsValid())
            return;

        var reference = args.Viewport.WorldToLocal(_xform.GetWorldPosition(parent));
        reference.X = -reference.X;
        _silhouette.SetParameter("reference", reference);
        _silhouette.SetParameter("progress", SilhouetteProgress(comp));
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        // After the burst: the red tint fades back to his normal look until he becomes active.
        var query = EntityQueryEnumerator<GilbertComponent, SpriteComponent, AppearanceComponent>();
        while (query.MoveNext(out var uid, out var comp, out var sprite, out var appearance))
        {
            if (comp.Active || !AppearanceSystem.TryGetData<bool>(uid, GilbertVisuals.SpawnTint, out var tint, appearance) || !tint)
                continue;

            var span = (comp.ActivateDelay - comp.AssembleDuration).TotalSeconds;
            var k = span <= 0 ? 1f : (float) Math.Clamp((_timing.CurTime - comp.SpawnTime - comp.AssembleDuration).TotalSeconds / span, 0, 1);
            SpriteSystem.SetColor((uid, sprite), Color.InterpolateBetween(SpawnTint, Color.White, k));
        }
    }

    private void OnStartup(Entity<GilbertComponent> ent, ref ComponentStartup args)
    {
        if (!TryComp<SpriteComponent>(ent, out var sprite))
            return;

        EnsureEyesLayer((ent, sprite));
    }

    /// <summary>
    /// Adds the halo and then the eyes on top of every other layer (clothing layers sit at their slot's
    /// map position, before these), so the eyes show over the helmet and gas mask. Both are unshaded and
    /// directional, so they sit on the eyes for every facing and glow in the dark.
    /// </summary>
    private int EnsureEyesLayer(Entity<SpriteComponent> sprite)
    {
        if (!SpriteSystem.LayerMapTryGet(sprite.AsNullable(), GilbertVisualLayers.EyesHalo, out var halo, false))
        {
            halo = SpriteSystem.AddLayer(sprite.AsNullable(), HaloSprite);
            SpriteSystem.LayerMapSet(sprite.AsNullable(), GilbertVisualLayers.EyesHalo, halo);
            sprite.Comp.LayerSetShader(halo, "unshaded");
        }

        if (SpriteSystem.LayerMapTryGet(sprite.AsNullable(), GilbertVisualLayers.Eyes, out var index, false))
            return index;

        index = SpriteSystem.AddLayer(sprite.AsNullable(), EyesSprite);
        SpriteSystem.LayerMapSet(sprite.AsNullable(), GilbertVisualLayers.Eyes, index);
        sprite.Comp.LayerSetShader(index, "unshaded");
        return index;
    }

    protected override void OnAppearanceChange(EntityUid uid, GilbertComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        Entity<SpriteComponent> sprite = (uid, args.Sprite);
        var tint = AppearanceSystem.TryGetData<bool>(uid, GilbertVisuals.SpawnTint, out var tinted, args.Component) && tinted;
        SpriteSystem.SetColor(sprite.AsNullable(), tint ? SpawnTint : Color.White);

        var silhouette = AppearanceSystem.TryGetData<bool>(uid, GilbertVisuals.Silhouette, out var assembling, args.Component) && assembling;
        if (silhouette)
            args.Sprite.PostShader = _silhouette;
        else if (args.Sprite.PostShader == _silhouette)
            args.Sprite.PostShader = null;

        var glow = !AppearanceSystem.TryGetData<bool>(uid, GilbertVisuals.EyesGlow, out var eyes, args.Component) || eyes;
        var index = EnsureEyesLayer(sprite);
        SpriteSystem.LayerSetVisible(sprite.AsNullable(), index, glow);
        if (SpriteSystem.LayerMapTryGet(sprite.AsNullable(), GilbertVisualLayers.EyesHalo, out var halo, false))
            SpriteSystem.LayerSetVisible(sprite.AsNullable(), halo, glow);
    }
}
