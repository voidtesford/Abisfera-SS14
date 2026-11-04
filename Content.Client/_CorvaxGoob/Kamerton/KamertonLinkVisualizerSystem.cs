using Content.Shared._CorvaxGoob.Kamerton;
using Robust.Client.GameObjects;

namespace Content.Client._CorvaxGoob.Kamerton;

/// <summary>
/// Draws the couplings between linked Kamerton reactor parts. The server sends world-space link flags
/// (N=1, S=2, E=4, W=8); they are turned into the sprite's own frame so rotated parts (heat sinks) join up too.
/// </summary>
public sealed class KamertonLinkVisualizerSystem : VisualizerSystem<KamertonLinkVisualsComponent>
{
    private static readonly Direction[] Dirs = { Direction.North, Direction.South, Direction.East, Direction.West };

    protected override void OnAppearanceChange(EntityUid uid, KamertonLinkVisualsComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite is not { } sprite
            || !AppearanceSystem.TryGetData<int>(uid, KamertonReactorVisuals.Links, out var flags, args.Component)
            || !SpriteSystem.LayerMapTryGet((uid, sprite), KamertonReactorVisualLayers.Link, out var layer, false))
            return;

        var rot = sprite.NoRotation ? Angle.Zero : Transform(uid).LocalRotation;
        var local = 0;
        for (var i = 0; i < Dirs.Length; i++)
        {
            if ((flags & (1 << i)) == 0)
                continue;

            var dir = (Dirs[i].ToAngle() - rot).GetCardinalDir();
            local |= dir switch
            {
                Direction.North => 1,
                Direction.South => 2,
                Direction.East => 4,
                _ => 8,
            };
        }

        SpriteSystem.LayerSetRsiState((uid, sprite), layer, $"{component.StatePrefix}{local}");
        SpriteSystem.LayerSetVisible((uid, sprite), layer, local != 0);
    }
}
