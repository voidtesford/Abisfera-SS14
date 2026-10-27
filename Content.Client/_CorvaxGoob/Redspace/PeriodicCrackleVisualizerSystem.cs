using System.Linq;
using Content.Client.Items.Systems;
using Content.Shared._CorvaxGoob.Redspace;
using Content.Shared.Hands;
using Content.Shared.Item;
using Robust.Client.GameObjects;

namespace Content.Client._CorvaxGoob.Redspace;

/// <summary>
/// Shows the crackle overlay layer while the server says the entity is crackling and restarts its
/// animation from the first frame each time, so every crackle plays the full arc.
/// While held, the same toggle adds <see cref="PeriodicCrackleComponent.InhandVisuals"/> to the holder's
/// in-hand sprite (fresh layers, so they also start from frame 0).
/// </summary>
public sealed class PeriodicCrackleVisualizerSystem : VisualizerSystem<PeriodicCrackleComponent>
{
    [Dependency] private readonly SharedItemSystem _item = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PeriodicCrackleComponent, GetInhandVisualsEvent>(OnGetHeldVisuals,
            after: [typeof(ItemSystem)]);
    }

    protected override void OnAppearanceChange(EntityUid uid, PeriodicCrackleComponent component, ref AppearanceChangeEvent args)
    {
        AppearanceSystem.TryGetData<bool>(uid, PeriodicCrackleVisuals.Crackling, out var crackling, args.Component);

        if (args.Sprite != null
            && SpriteSystem.LayerMapTryGet((uid, args.Sprite), PeriodicCrackleLayers.Crackle, out var layer, false))
        {
            SpriteSystem.LayerSetVisible((uid, args.Sprite), layer, crackling);
            if (crackling)
                SpriteSystem.LayerSetAnimationTime((uid, args.Sprite), layer, 0f);
        }

        // Re-request the holder's in-hand layers (no-op when not in a container).
        if (component.InhandVisuals.Count > 0)
            _item.VisualsChanged(uid);
    }

    private void OnGetHeldVisuals(EntityUid uid, PeriodicCrackleComponent component, GetInhandVisualsEvent args)
    {
        if (!component.InhandVisuals.TryGetValue(args.Location, out var layers)
            || !AppearanceSystem.TryGetData<bool>(uid, PeriodicCrackleVisuals.Crackling, out var crackling)
            || !crackling)
            return;

        var defaultKey = $"inhand-{args.Location.ToString().ToLowerInvariant()}-crackle";
        var i = 0;
        foreach (var layer in layers)
        {
            var key = layer.MapKeys?.FirstOrDefault();
            if (key == null)
            {
                key = i == 0 ? defaultKey : $"{defaultKey}-{i}";
                i++;
            }

            args.Layers.Add((key, layer));
        }
    }
}
