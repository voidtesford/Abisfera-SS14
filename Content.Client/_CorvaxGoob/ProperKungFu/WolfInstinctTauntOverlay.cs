using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Client._CorvaxGoob.ProperKungFu;

/// <summary>
/// Wolf Instinct: a small trembling red-white "test me" above the head of everyone in the counter stance.
/// Lives exactly as long as the networked <see cref="WolfInstinctStanceComponent"/>, so everyone in view sees it.
/// </summary>
public sealed class WolfInstinctTauntSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlay = default!;
    [Dependency] private readonly IResourceCache _cache = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;

    public override void Initialize()
    {
        base.Initialize();
        _overlay.AddOverlay(new WolfInstinctTauntOverlay(EntityManager, _cache, _timing, _random, _transform, _cfg, _ui));
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlay.RemoveOverlay<WolfInstinctTauntOverlay>();
    }
}

public sealed class WolfInstinctTauntOverlay : Overlay
{
    private static readonly Color Fill = Color.FromHex("#FF2828");
    private static readonly Color Edge = Color.FromHex("#FFF4EC");

    /// <summary>
    /// How far above the entity's center the text sits, in tiles.
    /// </summary>
    private const float HeadOffset = 0.95f;

    /// <summary>
    /// Max per-letter shake, in screen pixels (before UI scale).
    /// </summary>
    private const float Shake = 1.6f;

    private readonly IEntityManager _entMan;
    private readonly IGameTiming _timing;
    private readonly IRobustRandom _random;
    private readonly SharedTransformSystem _transform;
    private readonly IConfigurationManager _cfg;
    private readonly IUserInterfaceManager _ui;
    private readonly Font _font;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public WolfInstinctTauntOverlay(IEntityManager entMan, IResourceCache cache, IGameTiming timing, IRobustRandom random, SharedTransformSystem transform, IConfigurationManager cfg, IUserInterfaceManager ui)
    {
        _cfg = cfg;
        _ui = ui;
        _entMan = entMan;
        _timing = timing;
        _random = random;
        _transform = transform;
        _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 10);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.ViewportControl == null)
            return;

        var handle = args.ScreenHandle;
        var matrix = args.ViewportControl.GetWorldToScreenMatrix();
        var bounds = args.WorldBounds.Enlarged(2);
        var scale = _cfg.GetCVar(CVars.DisplayUIScale);
        if (scale == 0f)
            scale = _ui.DefaultUIScale;
        string? text = null;

        var query = _entMan.AllEntityQueryEnumerator<WolfInstinctStanceComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var stance, out var xform))
        {
            if (stance.EndTime <= _timing.CurTime || xform.MapID != args.MapId)
                continue;

            var world = _transform.GetWorldPosition(xform);
            if (!bounds.Contains(world))
                continue;

            text ??= Loc.GetString("proper-kung-fu-wolf-instinct-taunt");
            var anchor = Vector2.Transform(world + new Vector2(0f, HeadOffset), matrix);
            var size = handle.GetDimensions(_font, text, scale);
            var pen = anchor - size / 2f;

            // Every letter trembles on its own; a white edge keeps the red readable on any floor.
            foreach (var rune in text.EnumerateRunes())
            {
                var letter = rune.ToString();
                var jitter = new Vector2(_random.NextFloat(-Shake, Shake), _random.NextFloat(-Shake, Shake)) * scale;
                var pos = pen + jitter;
                foreach (var edge in Edges)
                {
                    handle.DrawString(_font, pos + edge * scale, letter, scale, Edge);
                }

                handle.DrawString(_font, pos, letter, scale, Fill);
                pen.X += handle.GetDimensions(_font, letter, scale).X;
            }
        }
    }

    private static readonly Vector2[] Edges =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };
}
