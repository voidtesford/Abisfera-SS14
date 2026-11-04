using Content.Shared.Containers.ItemSlots;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>Charging / draining a Kamerton cell = swapping it for its counterpart prototype in place.</summary>
public sealed class KamertonCellSystem : EntitySystem
{
    [Dependency] private readonly ItemSlotsSystem _slots = default!;

    /// <summary>
    /// Replaces the cell in <paramref name="slotId"/> of <paramref name="holder"/> with its <see cref="KamertonCellComponent.Swap"/> prototype.
    /// Returns the new cell.
    /// </summary>
    public EntityUid? SwapInSlot(EntityUid holder, string slotId, Entity<KamertonCellComponent> cell)
    {
        var coords = Transform(holder).Coordinates;
        var proto = cell.Comp.Swap;
        // Deleting right away frees the slot for the replacement.
        Del(cell);

        var spawned = Spawn(proto, coords);
        // If the slot refuses it, the new cell simply lies next to the holder.
        _slots.TryInsert(holder, slotId, spawned, null, excludeUserAudio: true);
        return spawned;
    }
}
