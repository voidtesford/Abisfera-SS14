using Content.Server._CorvaxGoob.Gilbert;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>Shared helpers for the Gilbert tests.</summary>
public static class GilbertTestUtil
{
    /// <summary>
    /// Spawns Gilbert and skips his ~2.2 s spawn sequence (v5), so the AI / ability tests get a fighting-ready
    /// Gilbert on the first tick. The sequence itself is covered by GilbertV5Test.SpawnSequenceTest.
    /// </summary>
    public static EntityUid SpawnActive(IEntityManager entMan, string proto, EntityCoordinates coords)
    {
        var uid = entMan.SpawnEntity(proto, coords);
        entMan.System<GilbertSystem>().FinishSpawnSequence(uid);
        return uid;
    }
}
