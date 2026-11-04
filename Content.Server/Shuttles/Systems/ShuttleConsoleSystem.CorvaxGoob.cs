using Content.Server._CorvaxGoob.Shuttles;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;

namespace Content.Server.Shuttles.Systems;

// CorvaxGoob: ftldisk disks with an exact target put the shuttle console at the given point and facing.
public sealed partial class ShuttleConsoleSystem
{
    [Dependency] private readonly FTLDiskExactTargetSystem _exactTarget = default!;
    [Dependency] private readonly ItemSlotsSystem _corvaxItemSlots = default!;

    public bool TryExactDiskArrival(EntityUid slotConsole, EntityUid shuttleConsole, MapId targetMap,
        out EntityCoordinates coordinates, out Angle angle)
    {
        coordinates = default;
        angle = default;
        if (!_mapSystem.TryGetMap(targetMap, out var mapUid))
            return false;

        if (!_corvaxItemSlots.TryGetSlot(slotConsole, SharedShuttleConsoleComponent.DiskSlotName, out var slot))
            return false;

        return _exactTarget.TryGetArrival(shuttleConsole, slot.Item, mapUid.Value, out coordinates, out angle);
    }
}
