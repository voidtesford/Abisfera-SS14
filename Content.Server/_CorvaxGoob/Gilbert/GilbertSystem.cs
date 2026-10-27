using System.Linq;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._Starlight.CollectiveMind;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Systems;
using Content.Shared.Bed.Sleep;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Preferences;
using Content.Shared.Temperature.Components;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Body setup, spawn sequence, eyes and sleep immunity for Gilbert.
/// </summary>
public sealed class GilbertSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly PointLightSystem _light = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly WoundSystem _wound = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly SharedHumanoidAppearanceSystem _humanoid = default!;

    [Dependency] private readonly GilbertSpawnSequenceSystem _sequence = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<GilbertComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<GilbertComponent, TryingToSleepEvent>(OnTryingToSleep);
        SubscribeLocalEvent<GilbertComponent, GilbertBerserkPhaseChangedEvent>(OnBerserkPhaseChanged);
        SubscribeLocalEvent<GilbertComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnMapInit(Entity<GilbertComponent> ent, ref MapInitEvent args)
    {
        // Spawn sequence: inactive and invulnerable red energy silhouette until ActivateDelay (see Update).
        ent.Comp.SpawnTime = _timing.CurTime;
        ent.Comp.Active = false;
        Dirty(ent);
        ApplyAppearance(ent);
        _appearance.SetData(ent, GilbertVisuals.Silhouette, true);
        _appearance.SetData(ent, GilbertVisuals.SpawnTint, false);
        _appearance.SetData(ent, GilbertVisuals.EyesGlow, true);
        _light.SetEnabled(ent, false);

        // He's in no hive mind. Also keeps his chat lines away from CollectiveMindUpdateSystem, whose static id
        // table isn't safe when several test servers run in one process.
        RemComp<CollectiveMindComponent>(ent);

        // Immune to cold: no cold slowdown either (cold damage is zeroed by GilbertModifiers).
        RemCompDeferred<TemperatureSpeedComponent>(ent);
    }

    private void ApplyAppearance(Entity<GilbertComponent> ent)
    {
        if (!TryComp<HumanoidAppearanceComponent>(ent, out var humanoid))
            return;

        var appearance = new HumanoidCharacterAppearance(
            ent.Comp.HairStyle,
            ent.Comp.HairColor,
            HairStyles.DefaultFacialHairStyle,
            ent.Comp.HairColor,
            ent.Comp.EyeColor,
            SkinColor.HumanSkinTone(ent.Comp.SkinTone),
            new());

        var profile = HumanoidCharacterProfile.DefaultWithSpecies(humanoid.Species)
            .WithSex(Sex.Male)
            .WithGender(Robust.Shared.Enums.Gender.Male)
            .WithAge(34)
            .WithCharacterAppearance(appearance);

        _humanoid.LoadProfile(ent, profile, humanoid);
    }

    private void OnMobStateChanged(EntityUid uid, GilbertComponent comp, MobStateChangedEvent args)
    {
        var alive = args.NewMobState != MobState.Dead;
        _appearance.SetData(uid, GilbertVisuals.EyesGlow, alive);
        _light.SetEnabled(uid, alive && comp.BurstDone);
        if (!alive)
            StopSoundtrack(comp);
    }

    #region Soundtrack (v8)

    /// <summary>Starts the looped first-phase ambience, unless it already plays or was stopped for good.</summary>
    private void StartSoundtrack(Entity<GilbertComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.SoundtrackFinished || comp.Soundtrack is { } old && !TerminatingOrDeleted(old)
            || TryComp<GilbertBerserkComponent>(ent, out var berserk) && berserk.Phase != GilbertBerserkPhase.None
            || !_mobState.IsAlive(ent))
            return;

        comp.Soundtrack = _audio.PlayPvs(comp.SoundtrackSound, ent)?.Entity;
    }

    /// <summary>Stops the ambience for good: it never comes back (the berserk phase has its own sound).</summary>
    private void StopSoundtrack(GilbertComponent comp)
    {
        comp.SoundtrackFinished = true;
        if (comp.Soundtrack is { } audio && !TerminatingOrDeleted(audio))
            _audio.Stop(audio);
        comp.Soundtrack = null;
    }

    private void OnBerserkPhaseChanged(Entity<GilbertComponent> ent, ref GilbertBerserkPhaseChangedEvent args)
    {
        if (args.Phase != GilbertBerserkPhase.None)
            StopSoundtrack(ent.Comp);
    }

    private void OnShutdown(Entity<GilbertComponent> ent, ref ComponentShutdown args)
    {
        StopSoundtrack(ent.Comp);
    }

    /// <summary>The playing soundtrack audio entity, if any (tests).</summary>
    public EntityUid? GetSoundtrack(GilbertComponent comp)
    {
        return comp.Soundtrack is { } audio && !TerminatingOrDeleted(audio) ? audio : null;
    }

    #endregion

    private void OnTryingToSleep(Entity<GilbertComponent> ent, ref TryingToSleepEvent args)
    {
        args.Cancelled = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<GilbertComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!comp.PartsSetUp)
                SetUpParts(uid, comp);

            if (!comp.Active)
                UpdateSpawnSequence((uid, comp), now);
        }
    }

    /// <summary>
    /// 0 .. <see cref="GilbertComponent.AssembleDuration"/>: the red silhouette assembles (converging streaks,
    /// crackling arcs, the client fades the silhouette in). Then a small energy burst (sound and light, no
    /// damage) and his normal look under a fading red tint, and at <see cref="GilbertComponent.ActivateDelay"/>
    /// he becomes active.
    /// </summary>
    private void UpdateSpawnSequence(Entity<GilbertComponent> ent, TimeSpan now)
    {
        var (uid, comp) = ent;
        if (!comp.SequenceStarted)
        {
            comp.SequenceStarted = true;
            comp.NextArc = comp.SpawnTime + comp.SpawnArcDelay;
            var xform = Transform(uid);
            if (xform.MapUid != null)
                SpawnAttachedTo(comp.ConvergePrototype, xform.Coordinates);
            _audio.PlayPvs(comp.AssembleSound, uid);
        }

        var burstAt = comp.SpawnTime + comp.AssembleDuration;
        if (!comp.BurstDone)
        {
            if (now >= comp.NextArc && now < burstAt)
            {
                comp.NextArc = now + comp.SpawnArcInterval;
                SpawnArc(uid, comp);
            }

            if (now >= burstAt)
                Burst(ent);
        }

        if (comp.BurstDone && now >= comp.SpawnTime + comp.ActivateDelay)
            Activate(ent);
    }

    private void Burst(Entity<GilbertComponent> ent)
    {
        var (uid, comp) = ent;
        comp.BurstDone = true;
        _appearance.SetData(uid, GilbertVisuals.Silhouette, false);
        _appearance.SetData(uid, GilbertVisuals.SpawnTint, true);
        _light.SetEnabled(uid, !_mobState.IsDead(uid));

        var xform = Transform(uid);
        if (xform.MapUid != null)
            SpawnAttachedTo(comp.BurstPrototype, xform.Coordinates);
        _audio.PlayPvs(comp.BurstSound, uid);
        _audio.PlayPvs(comp.BurstBoomSound, uid);
    }

    private void Activate(Entity<GilbertComponent> ent)
    {
        _appearance.SetData(ent, GilbertVisuals.SpawnTint, false);
        _sequence.SetActive(ent, true);
        StartSoundtrack(ent);
    }

    /// <summary>
    /// Skips the rest of the spawn sequence (burst included) and makes him active right away.
    /// Used by admin tooling and tests that need a fighting-ready Gilbert on the first tick.
    /// </summary>
    public void FinishSpawnSequence(Entity<GilbertComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false) || ent.Comp.Active)
            return;

        Entity<GilbertComponent> gilbert = (ent.Owner, ent.Comp);
        ent.Comp.SequenceStarted = true;
        if (!ent.Comp.BurstDone)
            Burst(gilbert);
        Activate(gilbert);
    }

    /// <summary>
    /// Raises the integrity of every body part to <see cref="GilbertComponent.PartIntegrity"/> (torso and groin:
    /// <see cref="GilbertComponent.PartIntegrityOverrides"/>, 500),
    /// scaling the wound severity thresholds by the same factor.
    /// </summary>
    public void SetUpParts(EntityUid uid, GilbertComponent comp)
    {
        if (!TryComp<BodyComponent>(uid, out var body))
            return;

        var parts = _body.GetBodyChildren(uid, body).ToList();
        if (parts.Count == 0)
            return;

        comp.PartsSetUp = true;
        foreach (var (partUid, part) in parts)
        {
            if (!TryComp<WoundableComponent>(partUid, out var woundable) || woundable.IntegrityCap <= 0)
                continue;

            // v8: torso / groin get their own (bigger) integrity.
            var cap = FixedPoint2.New(comp.PartIntegrityOverrides.TryGetValue(part.PartType, out var over) ? over : comp.PartIntegrity);

            var scale = cap.Float() / woundable.IntegrityCap.Float();
            foreach (var key in woundable.Thresholds.Keys.ToList())
                woundable.Thresholds[key] = woundable.Thresholds[key] * scale;

            woundable.IntegrityCap = cap;
            woundable.WoundableIntegrity = cap;
            Dirty(partUid, woundable);
            _wound.UpdateWoundableIntegrity(partUid, woundable);
        }
    }

    /// <summary>
    /// One short white-red arc crackling somewhere on his body (like hit sparks), never a long bolt.
    /// </summary>
    private void SpawnArc(EntityUid uid, GilbertComponent comp)
    {
        var coords = _transform.GetMapCoordinates(uid);
        var offset = _random.NextAngle().ToVec() * _random.NextFloat(0.1f, comp.SpawnArcRadius);
        var arc = Spawn(comp.SpawnArcPrototype, coords.Offset(offset));
        _transform.SetLocalRotation(arc, _random.NextAngle());
    }
}
