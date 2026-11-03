using Content.Shared.Movement.Systems;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Controllers;
using Robust.Shared.Physics.Systems;

namespace Content.Shared._CorvaxGoob.Redspace.Physics;

/// <summary>
/// Pushes affected mobs along the redspace gravity current. Runs after the mover so the drift adds
/// on top of walking; only the few entities that have <see cref="RedspaceAffectedComponent"/> are touched.
/// </summary>
public sealed class RedspaceDriftController : VirtualController
{
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        UpdatesAfter.Add(typeof(SharedMoverController));
        base.Initialize();
    }

    public override void UpdateBeforeSolve(bool prediction, float frameTime)
    {
        base.UpdateBeforeSolve(prediction, frameTime);

        var query = EntityQueryEnumerator<RedspaceAffectedComponent, PhysicsComponent>();
        while (query.MoveNext(out var uid, out var affected, out var body))
        {
            if (affected.Drift == default || HasComp<RedspaceImmuneComponent>(uid))
                continue;

            if (prediction && !body.Predict)
                continue;

            _physics.SetLinearVelocity(uid, body.LinearVelocity + affected.Drift * frameTime, body: body);
        }
    }
}
