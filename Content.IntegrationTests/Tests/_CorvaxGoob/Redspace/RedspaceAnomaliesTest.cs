using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._CorvaxGoob.Redspace.Anomalies;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Redspace;
using Content.Server._CorvaxGoob.Redspace.Anomalies;
using Content.Server._CorvaxGoob.Redspace.Commands;
using Content.Server._CorvaxGoob.Redspace.Physics;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.Redspace.Anomalies;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Atmos.Rotting;
using Content.Shared.Damage;
using Content.Shared.Fluids.Components;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Movement.Components;
using Content.Server.Power.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Client.GameObjects;
using Robust.Server.Console;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Content.Shared.Physics;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Redspace;

/// <summary>
/// Redspace anomaly events, body effects and in-world visuals. Every event is started through
/// <see cref="RedspaceAnomalyEventSystem.TryTriggerEvent"/> and stepped with <see cref="RedspaceAnomalyEventSystem.StepEvents"/>
/// inside one server block; random events are off so nothing else interferes.
/// </summary>
[TestFixture]
[TestOf(typeof(RedspaceAnomalyEventSystem))]
[NonParallelizable]
public sealed class RedspaceAnomaliesTest
{
    private const string Mob = "MobHuman";
    private const string Immune = "MobShogkkOperative";
    private const string Collar = "ClothingNeckRedspaceSuppressorCollar";
    private const string Beacon = "RedspaceSuppressorBeacon";
    private const string Cell = "RedspaceSuperEnergyCell";
    private const string Crystal = "MaterialBSCrystal1";
    private const string Item = "PowerCellMedium";

    /// <summary>Everything random off (map effects, organism, random events, body effects); events are started by hand.</summary>
    private static Dictionary<string, string> Quiet(params (string Key, string Value)[] extra)
    {
        var dict = new Dictionary<string, string>
        {
            ["inertiaEnabled"] = "false", ["currentEnabled"] = "false", ["wavesEnabled"] = "false",
            ["weightlessEnabled"] = "false", ["blinkEnabled"] = "false", ["anomaliesEnabled"] = "false",
            ["echoEnabled"] = "false", ["hallucinationsEnabled"] = "false", ["dischargesEnabled"] = "false",
            ["lightFlickerEnabled"] = "false", ["radioStaticEnabled"] = "false", ["ambientEnabled"] = "false",
            ["jitterChance"] = "0", ["vomitChancePerMinute"] = "0", ["organismEnabled"] = "false",
            ["anomalyEventsEnabled"] = "false", ["mutationsEnabled"] = "false", ["privateHallucinationsEnabled"] = "false",
            ["itemDecayEnabled"] = "false", ["visualsEnabled"] = "false",
        };
        foreach (var (key, value) in extra)
            dict[key] = value;
        return dict;
    }

    private static readonly RedspaceAnomalyEvent[] AllEvents = Enum.GetValues<RedspaceAnomalyEvent>();

    private sealed class CaptureShell(IConsoleHost host) : IConsoleShell
    {
        public readonly List<string> Lines = new();
        public readonly List<string> Errors = new();
        public IConsoleHost ConsoleHost { get; } = host;
        public bool IsLocal => true;
        public bool IsServer => true;
        public ICommonSession? Player => null;
        public void ExecuteCommand(string command) => ConsoleHost.ExecuteCommand(command);
        public void RemoteExecuteCommand(string command) { }
        public void WriteLine(string text) => Lines.Add(text);
        public void WriteLine(FormattedMessage message) => Lines.Add(message.ToString());
        public void WriteError(string text) => Errors.Add(text);
        public void Clear() { }
    }

    private static CaptureShell Run(IServerConsoleHost host, string command, string args)
    {
        var shell = new CaptureShell(host);
        var argv = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        host.AvailableCommands[command].Execute(shell, args, argv);
        return shell;
    }

    private sealed class Ctx
    {
        public required TestPair Pair;
        public required TestMapData Map;
        public required IEntityManager EntMan;
        public required RedspaceMapSystem Redspace;
        public required RedspaceAnomalyEventSystem Events;
        public required SharedTransformSystem Xforms;
        public EntityUid Player;

        public RedspaceSettings Settings => EntMan.GetComponent<RedspaceMapComponent>(Map.MapUid).Settings;
        public MapCoordinates At(float x, float y) => new(new Vector2(x, y), Map.MapId);
        public Vector2 Pos(EntityUid uid) => Xforms.GetWorldPosition(uid);
        public FixedPoint2 Damage(EntityUid uid) => EntMan.GetComponent<DamageableComponent>(uid).TotalDamage;

        public void Step(float seconds, float dt = 0.1f)
        {
            for (var t = 0f; t < seconds - 0.0001f; t += dt)
                Events.StepEvents(dt);
        }
    }

    /// <summary>A floored, gravity-on test map in redspace with the client's player standing at the origin.</summary>
    private static async Task<Ctx> Setup(TestPair pair, params (string, string)[] overrides)
    {
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var ctx = new Ctx
        {
            Pair = pair,
            Map = map,
            EntMan = server.EntMan,
            Redspace = server.System<RedspaceMapSystem>(),
            Events = server.System<RedspaceAnomalyEventSystem>(),
            Xforms = server.System<SharedTransformSystem>(),
        };

        await server.WaitPost(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = -12; x <= 12; x++)
            for (var y = -12; y <= 12; y++)
                tiles.Add((new Vector2i(x, y), map.Tile.Tile));
            mapSys.SetTiles(map.Grid.Owner, map.Grid.Comp, tiles);
            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);

            ctx.Redspace.SetRedspace(map.MapUid, "RedspaceNormal", Quiet(overrides));
            ctx.Player = server.EntMan.SpawnEntity(Mob, ctx.At(0.5f, 0.5f));
            server.PlayerMan.SetAttachedEntity(pair.Player!, ctx.Player);
        });
        await pair.RunTicksSync(5);
        return ctx;
    }

    private static EntityUid WearCollar(Ctx ctx, EntityUid wearer)
    {
        var server = ctx.Pair.Server;
        var collarUid = ctx.EntMan.SpawnEntity(Collar, ctx.Xforms.GetMapCoordinates(wearer));
        var collar = new Entity<RedspaceCollarComponent>(collarUid, ctx.EntMan.GetComponent<RedspaceCollarComponent>(collarUid));
        var crystal = ctx.EntMan.SpawnEntity(Crystal, ctx.Xforms.GetMapCoordinates(wearer));
        Assert.That(server.System<RedspaceSuppressionSystem>().TryInsertCrystal(collar, crystal, null), Is.True);
        Assert.That(server.System<InventorySystem>().TryEquip(wearer, collarUid, "neck", silent: true, force: true), Is.True);
        ctx.Redspace.Step(1f);
        return collarUid;
    }

    private static EntityUid PlaceBeacon(Ctx ctx)
    {
        var sup = ctx.Pair.Server.System<RedspaceSuppressionSystem>();
        var uid = ctx.EntMan.SpawnEntity(Beacon, ctx.Map.GridCoords);
        var beacon = new Entity<RedspaceBeaconComponent>(uid, ctx.EntMan.GetComponent<RedspaceBeaconComponent>(uid));
        Assert.That(sup.TryInsertCell(beacon, ctx.EntMan.SpawnEntity(Cell, ctx.Map.MapCoords), null), Is.True);
        ctx.Redspace.Step(1f);
        Assert.That(sup.BeaconActive(beacon), Is.True);
        return uid;
    }

    private static EntityUid Loose(Ctx ctx, float x, float y)
    {
        var uid = ctx.EntMan.SpawnEntity(Item, ctx.At(x, y));
        Assert.That(ctx.EntMan.GetComponent<PhysicsComponent>(uid).BodyType, Is.Not.EqualTo(Robust.Shared.Physics.BodyType.Static));
        return uid;
    }

    private static int Count<T>(IEntityManager entMan) where T : IComponent
    {
        var n = 0;
        var query = entMan.AllEntityQueryEnumerator<T>();
        while (query.MoveNext(out _, out _))
            n++;
        return n;
    }

    private static void Prime(Ctx ctx, RedspaceAnomalyEvent kind)
    {
        // Whispers need something said by someone else.
        if (kind == RedspaceAnomalyEvent.Whispers)
        {
            var other = ctx.EntMan.SpawnEntity(Mob, ctx.At(30f, 0f));
            ctx.Events.RecordSpeech(other, "тест шёпота");
        }
    }

    #region Protection, per event

    /// <summary>Each event starts on a bare player, but not on a collar wearer, not next to a fuelled beacon, and never on an immune SHOGKK.</summary>
    [Test]
    [TestCaseSource(nameof(AllEvents))]
    public async Task EventIsBlockedByCollarBeaconAndImmunity(RedspaceAnomalyEvent kind)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair);
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            Prime(ctx, kind);

            // Immune SHOGKK: refused outright.
            var op = ctx.EntMan.SpawnEntity(Immune, ctx.At(-3f, -3f));
            Assert.That(ctx.Events.TryTriggerEvent(kind, op, out var none, out var reason), Is.False, $"{kind}: immune");
            Assert.That(reason, Is.EqualTo("redspace-event-fail-immune"));
            Assert.That(none, Is.Null);

            // Collar on: refused.
            var collar = WearCollar(ctx, ctx.Player);
            Assert.That(ctx.Events.TryTriggerEvent(kind, ctx.Player, out _, out reason), Is.False, $"{kind}: collar");
            Assert.That(reason, Is.EqualTo("redspace-event-fail-suppressed"));
            ctx.EntMan.DeleteEntity(collar);
            ctx.Redspace.Step(1f);

            // Fuelled beacon nearby: refused.
            var beacon = PlaceBeacon(ctx);
            Assert.That(ctx.Events.TryTriggerEvent(kind, ctx.Player, out _, out reason), Is.False, $"{kind}: beacon");
            Assert.That(reason, Is.EqualTo("redspace-event-fail-suppressed"));
            ctx.EntMan.DeleteEntity(beacon);
            ctx.Redspace.Step(1f);

            // Bare player: starts.
            Assert.That(ctx.Events.TryTriggerEvent(kind, ctx.Player, out _, out reason), Is.True, $"{kind}: bare player ({reason})");
        });
    }

    #endregion

    #region Effects, one test per event

    [Test]
    public async Task RiftPullsSwallowsAndThrows()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair, ("riftThrowMin", "6"), ("riftThrowMax", "10"));
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.Rift, ctx.Player, out var rift, out _), Is.True);
            Assert.That(ctx.EntMan.GetComponent<MetaDataComponent>(rift!.Value).EntityPrototype!.ID, Is.EqualTo(RedspaceAnomalyEventSystem.RiftPrototype));
            var center = ctx.Pos(rift.Value);
            Assert.That(Vector2.Distance(center, ctx.Pos(ctx.Player)), Is.InRange(1.5f, 4.5f), "rift opens a few tiles away");
            Assert.That(ctx.Events.ActiveEvents(ctx.Map.MapId), Is.Not.Empty);

            // Pull: a loose item 3 tiles out gets velocity towards the rift.
            var pulled = Loose(ctx, center.X + 3f, center.Y);
            ctx.Step(0.1f);
            Assert.That(ctx.EntMan.GetComponent<PhysicsComponent>(pulled).LinearVelocity.X, Is.LessThan(0f), "pulled in");

            // Swallow: an item at the centre is thrown far, a mob at the centre is thrown and hurt.
            var item = Loose(ctx, center.X + 0.1f, center.Y);
            var mob = ctx.EntMan.SpawnEntity(Mob, ctx.At(center.X, center.Y + 0.1f));
            var exits = Count<TimedDespawnComponent>(ctx.EntMan);
            ctx.Step(0.1f);
            Assert.That(Vector2.Distance(ctx.Pos(item), center), Is.GreaterThan(5f), "item thrown away");
            Assert.That(Vector2.Distance(ctx.Pos(mob), center), Is.GreaterThan(5f), "mob thrown away");
            Assert.That(ctx.Damage(mob), Is.GreaterThan(FixedPoint2.Zero), "mob hurt");
            Assert.That(Count<TimedDespawnComponent>(ctx.EntMan), Is.GreaterThan(exits), "exit flash spawned");

            // Protected/immune things in reach stay put.
            var op = ctx.EntMan.SpawnEntity(Immune, ctx.At(center.X, center.Y - 0.1f));
            var wearer = ctx.EntMan.SpawnEntity(Mob, ctx.At(center.X - 0.1f, center.Y));
            WearCollar(ctx, wearer);
            var opPos = ctx.Pos(op);
            var wearerPos = ctx.Pos(wearer);
            ctx.Step(0.3f);
            Assert.That(ctx.Pos(op), Is.EqualTo(opPos), "immune not swallowed");
            Assert.That(ctx.Pos(wearer), Is.EqualTo(wearerPos), "collar wearer not swallowed");
            Assert.That(ctx.Damage(op), Is.EqualTo(FixedPoint2.Zero));

            // Ends on time.
            ctx.Step(ctx.Settings.RiftDurationMax + 1f, 1f);
            Assert.That(ctx.EntMan.Deleted(rift.Value) || ctx.EntMan.IsQueuedForDeletion(rift.Value), Is.True, "rift closes");
        });
    }

    [Test]
    public async Task TimeLoopSnapsBackPositionAndHealth()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair);
        var server = pair.Server;
        var damage = server.System<DamageableSystem>();

        await server.WaitAssertion(() =>
        {
            var start = ctx.Pos(ctx.Player);
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.TimeLoop, ctx.Player, out _, out _), Is.True);
            Assert.That(ctx.EntMan.HasComponent<RedspaceTimeLoopComponent>(ctx.Player), Is.True);
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.TimeLoop, ctx.Player, out _, out var reason), Is.False, "one loop at a time");
            Assert.That(reason, Is.EqualTo("redspace-event-fail-busy"));

            ctx.Xforms.SetMapCoordinates(ctx.Player, ctx.At(6.5f, 3.5f));
            var hit = new DamageSpecifier();
            hit.DamageDict["Blunt"] = FixedPoint2.New(20);
            damage.TryChangeDamage(ctx.Player, hit);
            Assert.That(ctx.Damage(ctx.Player), Is.EqualTo(FixedPoint2.New(20)));

            ctx.Step(4.5f);
            Assert.That(Vector2.Distance(ctx.Pos(ctx.Player), start), Is.GreaterThan(5f), "not yet");
            ctx.Step(0.6f);
            Assert.That(Vector2.Distance(ctx.Pos(ctx.Player), start), Is.LessThan(0.01f), "back where it started");
            Assert.That(ctx.Damage(ctx.Player), Is.EqualTo(FixedPoint2.Zero), "health restored");
            Assert.That(ctx.EntMan.HasComponent<RedspaceTimeLoopComponent>(ctx.Player), Is.False);

            // A collar put on during the loop breaks it.
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.TimeLoop, ctx.Player, out _, out _), Is.True);
            ctx.Xforms.SetMapCoordinates(ctx.Player, ctx.At(6.5f, 3.5f));
            WearCollar(ctx, ctx.Player);
            ctx.Step(5.5f);
            Assert.That(Vector2.Distance(ctx.Pos(ctx.Player), new Vector2(6.5f, 3.5f)), Is.LessThan(0.01f), "collar breaks the loop");
        });
    }

    [Test]
    public async Task DoppelgangerHitsAndVanishes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair, ("doppelgangerHitCooldown", "0.2"));
        var server = pair.Server;
        var damage = server.System<DamageableSystem>();

        await server.WaitAssertion(() =>
        {
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.Doppelganger, ctx.Player, out var dopp, out _), Is.True);
            var d = dopp!.Value;
            Assert.That(ctx.EntMan.GetComponent<RedspaceDoppelgangerComponent>(d).Source, Is.EqualTo(ctx.Player));
            Assert.That(ctx.EntMan.HasComponent<RedspaceDoppelgangerStateComponent>(d), Is.True);

            // Walk into it: it hits.
            ctx.Xforms.SetWorldPosition(ctx.Player, ctx.Pos(d));
            ctx.Step(0.3f);
            Assert.That(ctx.Damage(ctx.Player), Is.GreaterThan(FixedPoint2.Zero), "doppelganger hits");

            // Damage it: gone.
            var hit = new DamageSpecifier();
            hit.DamageDict["Blunt"] = FixedPoint2.New(5);
            damage.TryChangeDamage(d, hit);
            Assert.That(ctx.EntMan.Deleted(d) || ctx.EntMan.IsQueuedForDeletion(d), Is.True, "vanishes when hit");

            // A second one runs out after its lifetime.
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.Doppelganger, ctx.Player, out var second, out _), Is.True);
            ctx.Step(ctx.Settings.DoppelgangerLifetime - 1f, 1f);
            Assert.That(ctx.EntMan.IsQueuedForDeletion(second!.Value) || ctx.EntMan.Deleted(second.Value), Is.False, "still there");
            ctx.Step(2f, 1f);
            Assert.That(ctx.EntMan.Deleted(second.Value) || ctx.EntMan.IsQueuedForDeletion(second.Value), Is.True, "vanishes after a minute");
        });
    }

    /// <summary>
    /// Regression: the doppelganger was invisible in game (its sprite started hidden and the client never got the
    /// state event that dressed it) and bullets flew through it (soft fixture).
    /// </summary>
    [Test]
    public async Task DoppelgangerIsVisibleToClientsAndHittable()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair, ("doppelgangerHitCooldown", "1000"));
        var server = pair.Server;
        var client = pair.Client;
        EntityUid dopp = default;
        NetEntity netDopp = default;

        await server.WaitAssertion(() =>
        {
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.Doppelganger, ctx.Player, out var spawned, out _), Is.True);
            dopp = spawned!.Value;
            netDopp = ctx.EntMan.GetNetEntity(dopp);

            var fixtures = ctx.EntMan.GetComponent<FixturesComponent>(dopp);
            Assert.That(fixtures.Fixtures.Values.Any(f => f.Hard && (f.CollisionLayer & (int) CollisionGroup.BulletImpassable) != 0),
                "hard fixture that bullets collide with");
            Assert.That(ctx.EntMan.GetComponent<PhysicsComponent>(dopp).CanCollide, Is.True);
        });

        await pair.RunTicksSync(20);

        await client.WaitAssertion(() =>
        {
            // The prototype itself is visible with a layer, so it can never be an invisible attacker.
            var proto = client.ProtoMan.Index<EntityPrototype>(RedspaceAnomalyEventSystem.DoppelgangerPrototype);
            Assert.That(proto.TryGetComponent<SpriteComponent>(out var protoSprite, client.EntMan.ComponentFactory), Is.True);
            Assert.That(protoSprite!.Visible, Is.True, "prototype sprite visible");

            var clientDopp = client.EntMan.GetEntity(netDopp);
            Assert.That(client.EntMan.EntityExists(clientDopp), Is.True, "doppelganger reaches the client");
            var sprite = client.EntMan.GetComponent<SpriteComponent>(clientDopp);
            Assert.That(sprite.Visible, Is.True, "client sprite visible");
            Assert.That(sprite.Color.A, Is.GreaterThan(0.5f), "not drawn transparent");
            Assert.That(sprite.Color.R, Is.LessThanOrEqualTo(sprite.Color.B), "no red tint");

            // It wears the player's look: all of the player's layers plus the glitch overlay.
            var clientPlayer = client.EntMan.GetEntity(server.EntMan.GetNetEntity(ctx.Player));
            var playerLayers = client.EntMan.GetComponent<SpriteComponent>(clientPlayer).AllLayers.Count();
            var visibleLayers = sprite.AllLayers.Count(l => l.Visible);
            Assert.That(client.System<RedspaceAnomalyVisualSystem>().IsDressed(clientDopp), Is.True, "copied the player's sprite");
            Assert.That(sprite.AllLayers.Count(), Is.EqualTo(playerLayers + 1), "player layers + glitch");
            Assert.That(visibleLayers, Is.GreaterThan(1), "has visible layers");
        });

        Vector2 origin = default, dir = default;
        await server.WaitAssertion(() =>
        {
            // Hitscan: a bullet ray finds it.
            var at = ctx.Pos(dopp);
            var away = at - ctx.Pos(ctx.Player);
            dir = Vector2.Normalize(new Vector2(-away.Y, away.X));
            origin = at - dir * 2f;
            var physics = server.System<SharedPhysicsSystem>();
            var hits = physics.IntersectRay(ctx.Map.MapId,
                new CollisionRay(origin, dir, (int) CollisionGroup.BulletImpassable), 4f, returnOnFirstHit: true).ToList();
            Assert.That(hits.Select(h => h.HitEntity), Does.Contain(dopp), "hitscan ray hits it");

            // A real bullet flies at it.
            var bullet = ctx.EntMan.SpawnEntity("BulletPistol", new MapCoordinates(origin, ctx.Map.MapId));
            physics.SetLinearVelocity(bullet, dir * 15f);
        });

        await pair.RunTicksSync(30);

        await server.WaitAssertion(() =>
        {
            Assert.That(ctx.EntMan.Deleted(dopp) || ctx.EntMan.IsQueuedForDeletion(dopp), Is.True, "shot doppelganger vanishes");
        });

        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
        {
            Assert.That(client.EntMan.TryGetEntity(netDopp, out var gone) && client.EntMan.EntityExists(gone), Is.False,
                "gone on the client too");
        });
    }

    [Test]
    public async Task BloodRainSpillsAndHurts()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair, ("bloodRainDamage", "1"));
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.BloodRain, ctx.Player, out var rain, out var reason), Is.True, reason);
            var center = ctx.Pos(rain!.Value);
            // An immune SHOGKK and a collar wearer (far enough that the collar does not cover the player) in the rain.
            var op = ctx.EntMan.SpawnEntity(Immune, ctx.At(center.X + 0.5f, center.Y));
            var away = ctx.Pos(ctx.Player) - center;
            away = away.LengthSquared() < 0.01f ? new Vector2(-2.5f, 0f) : -Vector2.Normalize(away) * 2.5f;
            var wearer = ctx.EntMan.SpawnEntity(Mob, ctx.At(center.X + away.X, center.Y + away.Y));
            WearCollar(ctx, wearer);
            var op0 = ctx.Damage(op);

            ctx.Step(3f);
            Assert.That(ctx.Damage(ctx.Player), Is.GreaterThan(FixedPoint2.Zero), "rain hurts");
            Assert.That(Count<PuddleComponent>(ctx.EntMan), Is.GreaterThan(0), "blood puddles");
            Assert.That(ctx.Damage(op), Is.EqualTo(op0), "immune untouched");
            Assert.That(ctx.Damage(wearer), Is.EqualTo(FixedPoint2.Zero), "collar wearer untouched");
            Assert.That(rain, Is.Not.Null);
        });
    }

    [Test]
    public async Task MirrorCorridorInvertsMovement()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair);
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var op = ctx.EntMan.SpawnEntity(Immune, ctx.At(0.5f, 0.6f));
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.MirrorCorridor, ctx.Player, out var mirror, out _), Is.True);
            ctx.Step(0.1f);
            Assert.That(ctx.EntMan.HasComponent<RedspaceMirroredComponent>(ctx.Player), Is.True, "inside the corridor");
            Assert.That(ctx.EntMan.GetComponent<MovementSpeedModifierComponent>(ctx.Player).WalkSpeedModifier, Is.LessThan(0f), "input inverted");
            Assert.That(ctx.EntMan.HasComponent<RedspaceMirroredComponent>(op), Is.False, "immune not mirrored");

            // Walk out: back to normal.
            ctx.Xforms.SetWorldPosition(ctx.Player, ctx.Pos(mirror!.Value) + new Vector2(9f, 9f));
            ctx.Step(0.1f);
            Assert.That(ctx.EntMan.HasComponent<RedspaceMirroredComponent>(ctx.Player), Is.False, "left the corridor");
            Assert.That(ctx.EntMan.GetComponent<MovementSpeedModifierComponent>(ctx.Player).WalkSpeedModifier, Is.GreaterThan(0f));

            // Back in, but with a collar on: not mirrored.
            ctx.Xforms.SetWorldPosition(ctx.Player, ctx.Pos(mirror.Value));
            WearCollar(ctx, ctx.Player);
            ctx.Step(0.1f);
            Assert.That(ctx.EntMan.HasComponent<RedspaceMirroredComponent>(ctx.Player), Is.False, "collar protects");
        });
    }

    [Test]
    public async Task WhispersReplayOthersSpeech()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair);
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            // Nothing heard yet (and own words don't count).
            ctx.Events.RecordSpeech(ctx.Player, "это я сам");
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.Whispers, ctx.Player, out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("redspace-event-fail-no-speech"));

            var other = ctx.EntMan.SpawnEntity(Mob, ctx.At(40f, 0f));
            ctx.Events.RecordSpeech(other, "кто-нибудь слышит?");
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.Whispers, ctx.Player, out _, out _), Is.True);
            Assert.That(ctx.Events.LastWhisper, Is.Not.Null);
            Assert.That(ctx.Events.LastWhisper!.Value.Target, Is.EqualTo(ctx.Player));
            Assert.That(ctx.Events.LastWhisper!.Value.Source, Is.EqualTo(other));
            Assert.That(ctx.Events.LastWhisper!.Value.Message, Is.EqualTo("кто-нибудь слышит?"));

            // Needs a real player to whisper to.
            var npc = ctx.EntMan.SpawnEntity(Mob, ctx.At(-3f, 0f));
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.Whispers, npc, out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("redspace-event-fail-not-player"));
        });
    }

    [Test]
    public async Task GravityKnotMakesThingsOrbit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair);
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            Assert.That(ctx.Events.TryTriggerEvent(RedspaceAnomalyEvent.GravityKnot, ctx.Player, out var knot, out _), Is.True);
            var center = ctx.Pos(knot!.Value);
            var item = Loose(ctx, center.X + 2f, center.Y);
            var op = ctx.EntMan.SpawnEntity(Immune, ctx.At(center.X - 2f, center.Y));
            ctx.Step(0.5f);

            var vel = ctx.EntMan.GetComponent<PhysicsComponent>(item).LinearVelocity;
            Assert.That(vel.Y, Is.GreaterThan(0.5f), "sideways: orbiting");
            Assert.That(vel.X, Is.LessThan(0f), "and drawn in");
            Assert.That(ctx.EntMan.GetComponent<PhysicsComponent>(op).LinearVelocity.Length(), Is.LessThan(0.01f), "immune not moved");
        });
    }

    #endregion

    #region Body effects

    [Test]
    public async Task MutationsFromExposureAndProtectionCures()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair, ("mutationsEnabled", "true"), ("mutationExposure", "5"), ("mutationChancePerMinute", "100000"));
        var server = pair.Server;
        var client = pair.Client;

        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < 3; i++)
                ctx.Redspace.Step(1f);
            Assert.That(ctx.EntMan.HasComponent<RedspaceMutationComponent>(ctx.Player), Is.False, "not before the exposure threshold");

            for (var i = 0; i < 6; i++)
                ctx.Redspace.Step(1f);
            Assert.That(ctx.EntMan.TryGetComponent<RedspaceMutationComponent>(ctx.Player, out var mut), Is.True, "mutated");
            Assert.That(mut!.Voice || mut.Slowdown < 1f || mut.ExtraEye, Is.True);

            // Each kind on demand.
            var s = ctx.Settings;
            ctx.Events.TryMutate(ctx.Player, s, 0);
            ctx.Events.TryMutate(ctx.Player, s, 1);
            ctx.Events.TryMutate(ctx.Player, s, 2);
            Assert.That(mut.Voice && mut.ExtraEye && mut.Slowdown < 1f, Is.True);
            Assert.That(ctx.Events.Distort("красный космос"), Is.Not.EqualTo("красный космос"), "distorted voice");
            Assert.That(ctx.EntMan.GetComponent<MovementSpeedModifierComponent>(ctx.Player).WalkSpeedModifier,
                Is.EqualTo(s.MutationSlowdown).Within(0.01f), "slowed");

            var op = ctx.EntMan.SpawnEntity(Immune, ctx.At(-2f, 0f));
            Assert.That(ctx.Events.TryMutate(op, s), Is.False, "immune");
        });
        await pair.RunTicksSync(5);

        // The extra eye shows on the client.
        await client.WaitAssertion(() =>
        {
            var cPlayer = client.EntMan.GetEntity(server.EntMan.GetNetEntity(ctx.Player));
            Assert.That(client.EntMan.GetComponent<RedspaceMutationComponent>(cPlayer).ExtraEye, Is.True);
            Assert.That(client.System<RedspaceAnomalyVisualSystem>().HasEyeLayer(cPlayer), Is.True, "extra-eye overlay");
        });

        // Protection removes everything at once.
        await server.WaitAssertion(() => WearCollar(ctx, ctx.Player));
        await pair.RunTicksSync(5);
        await server.WaitAssertion(() =>
            Assert.That(ctx.EntMan.HasComponent<RedspaceMutationComponent>(ctx.Player), Is.False, "collar cures"));
        await client.WaitAssertion(() =>
        {
            var cPlayer = client.EntMan.GetEntity(server.EntMan.GetNetEntity(ctx.Player));
            Assert.That(client.System<RedspaceAnomalyVisualSystem>().HasEyeLayer(cPlayer), Is.False, "overlay gone");
        });
    }

    [Test]
    public async Task PrivateHallucinationReachesOnlyTheClient()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair);
        var server = pair.Server;
        var client = pair.Client;
        var before = 0;
        await client.WaitPost(() => before = client.System<RedspaceAmbientVisualsSystem>().ReceivedHallucinations);

        await server.WaitAssertion(() =>
        {
            var session = server.PlayerMan.Sessions.First();
            Assert.That(ctx.Events.TrySendHallucination(ctx.Player, session, ctx.Map.MapId, new List<RedspaceSuppressor>()), Is.True);
        });
        await pair.RunTicksSync(5);

        await client.WaitAssertion(() =>
            Assert.That(client.System<RedspaceAmbientVisualsSystem>().ReceivedHallucinations, Is.GreaterThan(before), "fake enemy shown"));

        // Random private hallucinations come from exposure; the collar stops them.
        await server.WaitAssertion(() =>
        {
            ctx.Redspace.SetRedspace(ctx.Map.MapUid, "RedspaceNormal", Quiet(("privateHallucinationsEnabled", "true"),
                ("privateHallucinationIntervalMin", "1"), ("privateHallucinationIntervalMax", "1")));
            WearCollar(ctx, ctx.Player);
        });
        await pair.RunTicksSync(3);
        await client.WaitPost(() => before = client.System<RedspaceAmbientVisualsSystem>().ReceivedHallucinations);
        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < 5; i++)
                ctx.Redspace.Step(1f);
        });
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
            Assert.That(client.System<RedspaceAmbientVisualsSystem>().ReceivedHallucinations, Is.EqualTo(before), "collar: none"));
    }

    [Test]
    public async Task ItemDecayRotsDrainsAndJams()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair, ("foodRotMultiplier", "5"), ("batteryDrainPerMinute", "0.5"), ("gunJamChancePerMinute", "100000"), ("gunJamDuration", "3"));
        var server = pair.Server;
        var hands = server.System<SharedHandsSystem>();

        await server.WaitAssertion(() =>
        {
            var cell = ctx.EntMan.SpawnEntity(Item, ctx.Map.MapCoords);
            var gun = ctx.EntMan.SpawnEntity("WeaponPistolDebug", ctx.Map.MapCoords);
            var meat = ctx.EntMan.SpawnEntity("FoodMeat", ctx.Map.MapCoords);
            Assert.That(hands.TryPickupAnyHand(ctx.Player, cell), Is.True);
            Assert.That(hands.TryPickupAnyHand(ctx.Player, gun), Is.True);
            ctx.EntMan.GetComponent<InventoryComponent>(ctx.Player);
            Assert.That(server.System<InventorySystem>().TryEquip(ctx.Player, meat, "pocket1", silent: true, force: true)
                || hands.TryPickupAnyHand(ctx.Player, meat), Is.True);

            var battery = ctx.EntMan.GetComponent<BatteryComponent>(cell);
            var charge0 = battery.CurrentCharge;
            var rot0 = ctx.EntMan.GetComponent<PerishableComponent>(meat).RotAccumulator;

            ctx.Events.ApplyItemDecay(ctx.Player, ctx.Settings, 10f);
            Assert.That(battery.CurrentCharge, Is.LessThan(charge0), "battery drained");
            Assert.That(ctx.EntMan.GetComponent<PerishableComponent>(meat).RotAccumulator - rot0,
                Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(39)), "food rots 5x");
            Assert.That(ctx.EntMan.HasComponent<RedspaceJammedComponent>(gun), Is.True, "gun jammed");

            var attempt = new AttemptShootEvent(ctx.Player, null);
            ctx.EntMan.EventBus.RaiseLocalEvent(gun, ref attempt);
            Assert.That(attempt.Cancelled, Is.True, "jammed gun does not fire");

            ctx.Step(3.2f);
            Assert.That(ctx.EntMan.HasComponent<RedspaceJammedComponent>(gun), Is.False, "clears");

            // An immune SHOGKK's gear is not decayed by the map step (it is never exposed).
            var op = ctx.EntMan.SpawnEntity(Immune, ctx.At(-3f, 0f));
            Assert.That(ctx.EntMan.HasComponent<RedspaceAffectedComponent>(op), Is.False);
        });
    }

    #endregion

    #region Visuals and commands

    [Test]
    public async Task VisualSpawnersLoad()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair, ("visualsEnabled", "true"));
        var client = pair.Client;
        var server = pair.Server;

        var protos = new[]
        {
            RedspaceAmbientVisualsSystem.DebrisProto, RedspaceAmbientVisualsSystem.PatchProto, RedspaceAmbientVisualsSystem.SilhouetteProto,
            RedspaceAmbientVisualsSystem.ArcProto, RedspaceAmbientVisualsSystem.ArcGlowProto, RedspaceAmbientVisualsSystem.FootprintProto,
            RedspaceAmbientVisualsSystem.HallucinationProto,
            RedspaceAnomalyEventSystem.RiftPrototype, RedspaceAnomalyEventSystem.GravityKnotPrototype, RedspaceAnomalyEventSystem.BloodRainPrototype,
            RedspaceAnomalyEventSystem.MirrorPrototype, RedspaceAnomalyEventSystem.DoppelgangerPrototype, RedspaceAnomalyEventSystem.RiftExitPrototype,
            RedspaceAnomalyEventSystem.TimeLoopMarkerPrototype,
        };

        await client.WaitAssertion(() =>
        {
            foreach (var id in protos)
            {
                var proto = client.ProtoMan.Index<EntityPrototype>(id);
                Assert.That(proto.TryGetComponent<SpriteComponent>(out _, client.EntMan.ComponentFactory), Is.True, id);
            }

            // Every sprite layer resolves to a real RSI state.
            foreach (var id in protos)
            {
                var uid = client.EntMan.SpawnEntity(id, MapCoordinates.Nullspace);
                var sprite = client.EntMan.GetComponent<SpriteComponent>(uid);
                foreach (var layer in sprite.AllLayers)
                {
                    if (!layer.RsiState.IsValid)
                        continue;
                    var rsi = layer.ActualRsi ?? sprite.BaseRSI;
                    Assert.That(rsi, Is.Not.Null, id);
                    Assert.That(rsi!.TryGetState(layer.RsiState, out _), Is.True, $"{id}: {layer.RsiState.Name}");
                }
                client.EntMan.DeleteEntity(uid);
            }

            var visuals = client.System<RedspaceAmbientVisualsSystem>();
            var cPlayer = client.EntMan.GetEntity(server.EntMan.GetNetEntity(ctx.Player));
            var center = client.System<SharedTransformSystem>().GetMapCoordinates(cPlayer);
            var spawned = visuals.SpawnAllAround(center, 6f);
            Assert.That(spawned, Is.GreaterThanOrEqualTo(5), "debris, patch, silhouette, arc, footprints");
            Assert.That(visuals.ActiveVisuals, Is.GreaterThanOrEqualTo(5));
            Assert.That(visuals.SpawnedDebris, Is.GreaterThan(0));
            Assert.That(visuals.SpawnedPatches, Is.GreaterThan(0));
            Assert.That(visuals.SpawnedSilhouettes, Is.GreaterThan(0));
            Assert.That(visuals.SpawnedArcs, Is.GreaterThan(0));
            Assert.That(visuals.SpawnedFootprints, Is.GreaterThan(0));
        });

        // The ambient spawner runs on its own while the local player is exposed.
        await pair.RunTicksSync(5);
        await server.WaitAssertion(() => ctx.Redspace.Step(1f));
        var debris0 = 0;
        await client.WaitPost(() => debris0 = client.System<RedspaceAmbientVisualsSystem>().SpawnedDebris);
        await pair.RunTicksSync(240);
        await client.WaitAssertion(() =>
            Assert.That(client.System<RedspaceAmbientVisualsSystem>().SpawnedDebris, Is.GreaterThan(debris0), "debris drifts on its own"));
    }

    [Test]
    public async Task EventAndStatusCommands()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var ctx = await Setup(pair, ("anomalyEventsEnabled", "true"), ("anomalyEventIntervalMin", "500"), ("anomalyEventIntervalMax", "500"));
        var server = pair.Server;
        var host = server.ResolveDependency<IServerConsoleHost>();

        await server.WaitAssertion(() =>
        {
            Assert.That(RedspaceEventCommand.Types.Keys,
                Is.EquivalentTo(new[] { "rift", "timeloop", "doppelganger", "bloodrain", "mirror", "whispers", "gravityknot" }));

            var net = ctx.EntMan.GetNetEntity(ctx.Player);
            var ok = Run(host, "redspaceevent", $"gravityknot {net}");
            Assert.That(ok.Errors, Is.Empty, string.Join("\n", ok.Errors));
            Assert.That(ok.Lines, Has.Count.EqualTo(1));

            var bad = Run(host, "redspaceevent", $"nonsense {net}");
            Assert.That(bad.Errors, Is.Not.Empty);
            var failed = Run(host, "redspaceevent", $"whispers {net}");
            Assert.That(failed.Errors, Is.Not.Empty, "no speech to replay");

            ctx.Redspace.Step(1f);
            var status = Run(host, "redspacestatus", $"{(int) ctx.Map.MapId} 0.5 0.5");
            var text = string.Join("\n", status.Lines);
            TestContext.Out.WriteLine(text);
            Assert.That(status.Lines.Count, Is.GreaterThanOrEqualTo(3), text);
            Assert.That(text, Does.Contain("GravityKnot"), text);
            Assert.That(ctx.Events.ActiveEvents(ctx.Map.MapId), Has.Count.EqualTo(1));
            Assert.That(ctx.Events.NextEventIn(ctx.Map.MapUid), Is.GreaterThan(0f));
        });
    }

    #endregion
}
