using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.Shuttles;

/// <summary>
/// Added to a coordinates disk by <c>ftldisk &lt;map&gt; &lt;x&gt; &lt;y&gt; &lt;direction&gt;</c>. A shuttle that jumps to
/// <see cref="Map"/> with this disk in its console arrives so that the console sits exactly at <see cref="Position"/>,
/// facing <see cref="Direction"/>; the rest of the shuttle ends up wherever that puts it.
/// </summary>
[RegisterComponent]
public sealed partial class FTLDiskExactTargetComponent : Component
{
    [DataField]
    public EntityUid Map;

    [DataField]
    public Vector2 Position;

    [DataField]
    public Direction Direction = Direction.South;
}

public sealed class FTLDiskExactTargetSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>Parses north/south/east/west, their first letters and Russian names.</summary>
    public static bool TryParseDirection(string text, out Direction direction)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "north": case "n": case "север": case "с": case "вверх":
                direction = Direction.North; return true;
            case "south": case "s": case "юг": case "ю": case "вниз":
                direction = Direction.South; return true;
            case "east": case "e": case "восток": case "в": case "вправо":
                direction = Direction.East; return true;
            case "west": case "w": case "запад": case "з": case "влево":
                direction = Direction.West; return true;
        }

        direction = Direction.Invalid;
        return false;
    }

    public void SetTarget(EntityUid disk, EntityUid map, Vector2 position, Direction direction)
    {
        var comp = EnsureComp<FTLDiskExactTargetComponent>(disk);
        comp.Map = map;
        comp.Position = position;
        comp.Direction = direction;
    }

    /// <summary>
    /// Where the shuttle grid has to arrive (grid origin on the map and its rotation) so that
    /// <paramref name="console"/> ends up at the disk's position, facing the disk's direction.
    /// </summary>
    public (EntityCoordinates Coordinates, Angle Angle) GetArrival(EntityUid console, FTLDiskExactTargetComponent target)
    {
        var xform = Transform(console);
        // Console rotation and position relative to its grid.
        var localRot = xform.LocalRotation;
        var localPos = xform.LocalPosition;
        var gridAngle = (target.Direction.ToAngle() - localRot).Reduced();
        var origin = target.Position - gridAngle.RotateVec(localPos);
        return (new EntityCoordinates(target.Map, origin), gridAngle);
    }

    /// <summary>
    /// If the disk in the console points at <paramref name="targetMap"/> with an exact target, returns the arrival.
    /// </summary>
    public bool TryGetArrival(EntityUid console, EntityUid? disk, EntityUid targetMap,
        out EntityCoordinates coordinates, out Angle angle)
    {
        coordinates = default;
        angle = default;
        if (disk == null || !TryComp<FTLDiskExactTargetComponent>(disk, out var target) || target.Map != targetMap)
            return false;

        // The console has to sit on the grid that jumps.
        if (Transform(console).GridUid == null)
            return false;

        (coordinates, angle) = GetArrival(console, target);
        return true;
    }
}
