using Content.Shared._CorvaxGoob.Kamerton;
using Robust.Client.GameObjects;

namespace Content.Client._CorvaxGoob.Kamerton;

/// <summary>
/// Joins Kamerton reactor parts into one hull, like the AME shielding. The server sends world-space neighbour
/// flags (N, S, E, W, NE, NW, SE, SW); they are turned into the sprite's own frame (rotated heat sinks and
/// radiators) and each corner layer gets a state from its two side neighbours and the diagonal one, the same
/// rule IconSmooth uses for its corner mode.
/// </summary>
public sealed class KamertonLinkVisualizerSystem : VisualizerSystem<KamertonLinkVisualsComponent>
{
    private static readonly Direction[] Dirs =
    {
        Direction.North, Direction.South, Direction.East, Direction.West,
        Direction.NorthEast, Direction.NorthWest, Direction.SouthEast, Direction.SouthWest,
    };

    protected override void OnAppearanceChange(EntityUid uid, KamertonLinkVisualsComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite is not { } sprite)
            return;

        var ent = (uid, sprite);
        var rot = sprite.NoRotation ? Angle.Zero : Transform(uid).LocalRotation;

        if (AppearanceSystem.TryGetData<int>(uid, KamertonReactorVisuals.Links, out var links, args.Component))
        {
            var local = new HashSet<Direction>();
            for (var i = 0; i < Dirs.Length; i++)
            {
                if ((links & (1 << i)) != 0)
                    local.Add(ToLocal(Dirs[i], rot));
            }

            SetCorner(ent, KamertonReactorVisualLayers.HullNE, component, "ne", local, Direction.North, Direction.NorthEast, Direction.East);
            SetCorner(ent, KamertonReactorVisualLayers.HullSE, component, "se", local, Direction.South, Direction.SouthEast, Direction.East);
            SetCorner(ent, KamertonReactorVisualLayers.HullSW, component, "sw", local, Direction.South, Direction.SouthWest, Direction.West);
            SetCorner(ent, KamertonReactorVisualLayers.HullNW, component, "nw", local, Direction.North, Direction.NorthWest, Direction.West);
        }

        if (AppearanceSystem.TryGetData<int>(uid, KamertonReactorVisuals.HeatLinks, out var heat, args.Component))
        {
            // Bits 0-3: heat pipes to radiators; bits 4-7: joints between chained heat sinks.
            SetHeatLayer(ent, KamertonReactorVisualLayers.HeatLink, component.HeatStatePrefix, heat & 15, rot);
            SetHeatLayer(ent, KamertonReactorVisualLayers.SinkLink, component.SinkStatePrefix, (heat >> 4) & 15, rot);
        }
    }

    private void SetHeatLayer(Entity<SpriteComponent> ent, KamertonReactorVisualLayers key, string prefix, int world, Angle rot)
    {
        if (!SpriteSystem.LayerMapTryGet(ent.AsNullable(), key, out var layer, false))
            return;

        var local = 0;
        for (var i = 0; i < 4; i++)
        {
            if ((world & (1 << i)) == 0)
                continue;

            local |= ToLocal(Dirs[i], rot) switch
            {
                Direction.North => 1,
                Direction.South => 2,
                Direction.East => 4,
                _ => 8,
            };
        }

        SpriteSystem.LayerSetRsiState(ent.AsNullable(), layer, $"{prefix}{local}");
        SpriteSystem.LayerSetVisible(ent.AsNullable(), layer, local != 0);
    }

    private static Direction ToLocal(Direction world, Angle rot)
    {
        return (world.ToAngle() - rot).GetDir();
    }

    /// <summary>Corner state bits: 1 = vertical neighbour, 2 = diagonal, 4 = horizontal neighbour.</summary>
    private void SetCorner(Entity<SpriteComponent> ent, KamertonReactorVisualLayers key, KamertonLinkVisualsComponent comp,
        string corner, HashSet<Direction> local, Direction vertical, Direction diagonal, Direction horizontal)
    {
        if (!SpriteSystem.LayerMapTryGet(ent.AsNullable(), key, out var layer, false))
            return;

        var fill = (local.Contains(vertical) ? 1 : 0) | (local.Contains(diagonal) ? 2 : 0) | (local.Contains(horizontal) ? 4 : 0);
        SpriteSystem.LayerSetRsiState(ent.AsNullable(), layer, $"{comp.HullStatePrefix}{corner}_{fill}");
    }
}
