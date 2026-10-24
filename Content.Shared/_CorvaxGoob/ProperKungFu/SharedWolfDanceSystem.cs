using Content.Shared.ActionBlocker;
using Content.Shared.Movement.Events;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Blocks normal movement input while the Wolf Dance moves the entity (server and client).
/// </summary>
public sealed class SharedWolfDanceSystem : EntitySystem
{
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<WolfDancingComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<WolfDancingComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<WolfDancingComponent, UpdateCanMoveEvent>(OnUpdateCanMove);
    }

    private void OnStartup(Entity<WolfDancingComponent> ent, ref ComponentStartup args)
    {
        _blocker.UpdateCanMove(ent);
    }

    private void OnShutdown(Entity<WolfDancingComponent> ent, ref ComponentShutdown args)
    {
        _blocker.UpdateCanMove(ent);
    }

    private void OnUpdateCanMove(EntityUid uid, WolfDancingComponent component, UpdateCanMoveEvent args)
    {
        if (component.LifeStage > ComponentLifeStage.Running)
            return;

        args.Cancel();
    }
}
