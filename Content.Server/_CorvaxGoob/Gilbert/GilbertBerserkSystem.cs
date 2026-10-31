using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server.Chat.Systems;
using Content.Shared.Chat;
using Content.Server.Hands.Systems;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Jittering;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;
using Content.Shared.Traits.Assorted;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Server.Audio;
using Robust.Shared.Audio.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Raised on Gilbert whenever his second phase moves on (transition, rage, dying, dead), so the AI can let go of
/// whatever it was doing.
/// </summary>
[ByRefEvent]
public readonly record struct GilbertBerserkPhaseChangedEvent(GilbertBerserkPhase Phase);

/// <summary>
/// Gilbert's second phase «Берсерк» (v6).
/// <list type="bullet">
/// <item>Trigger: the hit that would take him to 50 HP or lower (450 of 500 damage) is cut down so he lands at
/// most at 450, and the phase starts. From then on nothing hurts or heals him.</item>
/// <item>Transition (6.9 s, the length of the growl-into-roar clip): «Садист впадает в бешенство»; he drops everything, stops, shakes and screams while
/// discharges build up over his body; at 8 s they burst out 10 tiles (streams along the floor, crackles in the
/// air) and every living mob in range takes 20 Shock.</item>
/// <item>Rage (5 min): the AI hunts everything alive with his fists only (+15 Shock, +100 Structural per punch,
/// run speed ×1.5, smashing through whatever is in the way).</item>
/// <item>Dying (4 s): stops, says «Прости! Прости!», shakes, discharges intensify; falls, explodes in energy (20 Shock in 10
/// tiles again) and dies. He can't be revived (<see cref="UnrevivableComponent"/>, damage stays locked).</item>
/// </list>
/// </summary>
public sealed class GilbertBerserkSystem : SharedGilbertBerserkSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _speed = default!;
    [Dependency] private readonly PointLightSystem _light = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly SharedJitteringSystem _jitter = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly GilbertSpawnSequenceSystem _sequence = default!;
    [Dependency] private readonly GilbertRageAbilitiesSystem _rage = default!;

    /// <summary>Brighter eye light while berserk (the client adds the flame flicker on top).</summary>
    public const float BerserkLightRadius = 3.2f;
    public const float BerserkLightEnergy = 2.4f;

    /// <summary>Burst streams advance one tile every this many seconds.</summary>
    public static readonly TimeSpan StreamStepTime = TimeSpan.FromSeconds(0.05);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertBerserkComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<GilbertBerserkComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<GilbertBerserkComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<GilbertBerserkComponent, GetMeleeDamageEvent>(OnGetMeleeDamage);
        SubscribeLocalEvent<GilbertBerserkComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
    }

    #region Trigger

    protected override void OnFirstPhaseDamage(Entity<GilbertBerserkComponent> ent, ref BeforeDamageChangedEvent args)
    {
        if (!_sequence.IsActive(ent) || !_mobState.IsAlive(ent) || !TryComp<DamageableComponent>(ent, out var damageable))
            return;

        // Only the harmful part counts, after his own innate resistances (cold / radiation immunity).
        var harmful = new DamageSpecifier();
        foreach (var (type, value) in args.Damage.DamageDict)
        {
            if (value > 0)
                harmful.DamageDict[type] = value;
        }

        if (harmful.Empty)
            return;

        var effective = harmful;
        if (damageable.DamageModifierSetId is { } setId && _proto.TryIndex<DamageModifierSetPrototype>(setId, out var set))
            effective = DamageSpecifier.ApplyModifierSet(harmful, set);

        var incoming = effective.GetTotal();
        if (incoming <= 0)
            return;

        var total = damageable.TotalDamage;
        var trigger = FixedPoint2.New(ent.Comp.TriggerDamage);
        if (total + incoming < trigger)
            return;

        // The hit is cut down to exactly what takes him to 50 HP. His own modifiers are already applied above, so
        // this last bit goes in without resistances (armour can't leave him a few HP above the line).
        args.Cancelled = true;
        var room = trigger - total;
        if (room > 0)
        {
            var scale = Math.Clamp(room.Float() / incoming.Float(), 0f, 1f);
            ent.Comp.AllowDamage = true;
            try
            {
                _damageable.TryChangeDamage(ent, effective * scale, ignoreResistances: true, origin: args.Origin,
                    targetPart: args.TargetPart, canMiss: false);
            }
            finally
            {
                ent.Comp.AllowDamage = false;
            }
        }

        StartBerserk(ent);
    }

    /// <summary>Backstop for damage that went around the before-damage check (set directly etc).</summary>
    private void OnDamageChanged(Entity<GilbertBerserkComponent> ent, ref DamageChangedEvent args)
    {
        if (ent.Comp.Phase != GilbertBerserkPhase.None || ent.Comp.AllowDamage || !_sequence.IsActive(ent)
            || !_mobState.IsAlive(ent) || args.Damageable.TotalDamage < ent.Comp.TriggerDamage)
            return;

        StartBerserk(ent);
    }

    /// <summary>Starts the second phase right away (also used by tests and admin tooling).</summary>
    public void StartBerserk(Entity<GilbertBerserkComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.Phase != GilbertBerserkPhase.None || !_mobState.IsAlive(ent))
            return;

        var now = _timing.CurTime;
        SetPhase(ent, GilbertBerserkPhase.Transition, now);
        comp.BurstDone = false;
        comp.Fallen = false;
        comp.NextArc = now;
        comp.NextCrackle = TimeSpan.MaxValue;
        comp.StreamStep = 0;
        comp.StreamAngles.Clear();

        // Body parts: damage aimed straight at a part is blocked as well.
        foreach (var (part, _) in _body.GetBodyChildren(ent))
            EnsureComp<GilbertBerserkPartComponent>(part);

        // Everything in his hands goes on the floor: from now on it's fists only.
        foreach (var hand in _hands.EnumerateHands(ent.Owner))
            _hands.TryDrop(ent.Owner, hand, checkActionBlocker: false);

        _jitter.AddJitter(ent, 6f, 8f);
        _light.SetRadius(ent, BerserkLightRadius);
        _light.SetEnergy(ent, BerserkLightEnergy);
        // An emote ("*Gilbert flies into a rage!"), not a spoken line: he is past words now.
        _chat.TrySendInGameICMessage(ent, Loc.GetString("gilbert-berserk-emote"), InGameICChatType.Emote,
            ChatTransmitRange.Normal, checkRadioPrefix: false, ignoreActionBlocker: true);
        RaisePhaseChanged(ent);
        // v8: the growl that grows into a roar, played once from the very start of the transition (no loop).
        // Tracked as loud audio so it is cut off at the skull-crack shout if it is ever longer than the transition.
        TrackLoud(comp, _audio.PlayPvs(comp.TransitionSound, ent));
    }

    #endregion

    #region Phases

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<GilbertBerserkComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            Entity<GilbertBerserkComponent> ent = (uid, comp);
            switch (comp.Phase)
            {
                case GilbertBerserkPhase.Transition:
                    UpdateTransition(ent, now);
                    break;
                case GilbertBerserkPhase.Rage:
                    if (!comp.RageMusicStarted && now - comp.PhaseStart >= comp.RageMusicDelay)
                        StartRageMusic(ent);
                    if (now - comp.PhaseStart >= comp.RageDuration)
                        StartDying(ent);
                    break;
                case GilbertBerserkPhase.Dying:
                    UpdateDying(ent, now);
                    break;
            }
        }
    }

    private void UpdateTransition(Entity<GilbertBerserkComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        var t = now - comp.PhaseStart;

        // Charging: discharges over his body, more and more often until the peak.
        var charge = (float) Math.Clamp(t.TotalSeconds / Math.Max(comp.BurstAt.TotalSeconds, 0.01), 0, 1);
        if (now >= comp.NextArc)
        {
            var interval = comp.BurstDone ? 0.12f : MathHelper.Lerp(0.35f, 0.035f, charge);
            comp.NextArc = now + TimeSpan.FromSeconds(interval);
            SpawnBodyArcs(ent, comp.BurstDone ? 1 : 1 + (int) (charge * 2f));
            if (!comp.BurstDone)
                _jitter.AddJitter(ent, MathHelper.Lerp(6f, 22f, charge), MathHelper.Lerp(8f, 10f, charge));
        }

        if (!comp.BurstDone && t >= comp.BurstAt)
            Burst(ent, now);

        UpdateStreams(ent, now);

        // Chaotic crackles in the air all over the burst radius, from the burst to the end of the transition.
        if (comp.BurstDone && now >= comp.NextCrackle)
        {
            comp.NextCrackle = now + TimeSpan.FromSeconds(_random.NextFloat(0.03f, 0.07f));
            SpawnCrackle(ent);
        }

        if (t >= comp.TransitionDuration)
            StartRage(ent, now);
    }

    private void Burst(Entity<GilbertBerserkComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        comp.BurstDone = true;
        comp.NextCrackle = now;
        comp.StreamStep = 1;
        comp.NextStreamStep = now;
        comp.StreamAngles.Clear();
        var baseAngle = _random.NextFloat(0f, MathF.Tau);
        for (var i = 0; i < comp.StreamCount; i++)
        {
            var jitter = _random.NextFloat(-0.12f, 0.12f);
            comp.StreamAngles.Add(new Angle(baseAngle + MathF.Tau * i / comp.StreamCount + jitter));
        }

        RemComp<JitteringComponent>(ent);
        _jitter.AddJitter(ent, 8f, 6f);

        var coords = _transform.GetMapCoordinates(ent);
        if (coords.MapId != MapId.Nullspace)
            Spawn(comp.BurstRingPrototype, coords);
        TrackLoud(comp, _audio.PlayPvs(comp.BurstSound, ent));
        DamageAround(ent, coords);
    }

    /// <summary>The burst's energy streams run out along the floor one tile per step, up to the burst radius.</summary>
    private void UpdateStreams(Entity<GilbertBerserkComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.StreamStep <= 0 || comp.StreamStep > (int) MathF.Ceiling(comp.BurstRadius) || now < comp.NextStreamStep)
            return;

        var center = _transform.GetMapCoordinates(ent);
        if (center.MapId == MapId.Nullspace)
            return;

        var step = comp.StreamStep;
        foreach (var angle in comp.StreamAngles)
        {
            var dir = angle.ToVec();
            var wobble = new Vector2(-dir.Y, dir.X) * _random.NextFloat(-0.25f, 0.25f);
            var fx = Spawn(comp.StreamPrototype, center.Offset(dir * (step - 0.5f) + wobble));
            // The streak art points east; turn it outwards.
            _transform.SetWorldRotation(fx, angle);
        }

        comp.StreamStep++;
        comp.NextStreamStep = now + StreamStepTime;
    }

    private void StartRage(Entity<GilbertBerserkComponent> ent, TimeSpan now)
    {
        RemComp<JitteringComponent>(ent);
        ent.Comp.StreamStep = 0;
        SetPhase(ent, GilbertBerserkPhase.Rage, now);
        _speed.RefreshMovementSpeedModifiers(ent);
        // v8: longer reach for the rage (fists and the smashing of whatever is in the way).
        if (ent.Comp.BaseMeleeRange == null && TryComp<MeleeWeaponComponent>(ent, out var fist))
        {
            ent.Comp.BaseMeleeRange = fist.Range;
            fist.Range *= ent.Comp.MeleeRangeMultiplier;
            Dirty(ent.Owner, fist);
        }
        RaisePhaseChanged(ent);
        SkullShout(ent);
    }

    /// <summary>
    /// End of the transition: the screams are cut off and he shouts «Я ТЕБЕ ЧЕРЕП ПРОЛОМЛЮ!!!» (voice and chat).
    /// </summary>
    private void SkullShout(Entity<GilbertBerserkComponent> ent)
    {
        StopLoudAudio(ent.Comp);
        _audio.PlayPvs(ent.Comp.SkullShoutSound, ent);
        _chat.TrySendInGameICMessage(ent, Loc.GetString("gilbert-berserk-skull-shout"), InGameICChatType.Speak,
            ChatTransmitRange.Normal, checkRadioPrefix: false, ignoreActionBlocker: true);
    }

    private void StopLoudAudio(GilbertBerserkComponent comp)
    {
        foreach (var audio in comp.LoudAudio)
        {
            if (!TerminatingOrDeleted(audio))
                _audio.Stop(audio);
        }

        comp.LoudAudio.Clear();
    }

    #region Rage soundtrack (v8)

    private void StartRageMusic(Entity<GilbertBerserkComponent> ent)
    {
        var comp = ent.Comp;
        comp.RageMusicStarted = true;
        if (comp.Phase != GilbertBerserkPhase.Rage || !_mobState.IsAlive(ent))
            return;

        comp.RageMusic = _audio.PlayPvs(comp.RageMusicSound, ent)?.Entity;
    }

    private void StopRageMusic(GilbertBerserkComponent comp)
    {
        comp.RageMusicStarted = true;
        if (comp.RageMusic is { } audio && !TerminatingOrDeleted(audio))
            _audio.Stop(audio);
        comp.RageMusic = null;
    }

    private void OnShutdown(Entity<GilbertBerserkComponent> ent, ref ComponentShutdown args)
    {
        StopRageMusic(ent.Comp);
    }

    /// <summary>The playing berserk soundtrack, if any (tests).</summary>
    public EntityUid? GetRageMusic(GilbertBerserkComponent comp)
    {
        return comp.RageMusic is { } audio && !TerminatingOrDeleted(audio) ? audio : null;
    }

    #endregion

    private void TrackLoud(GilbertBerserkComponent comp, (EntityUid Entity, AudioComponent Component)? audio)
    {
        comp.LoudAudio.RemoveAll(e => TerminatingOrDeleted(e));
        if (audio != null)
            comp.LoudAudio.Add(audio.Value.Entity);
    }

    /// <summary>Time's up: he stops, screams and shakes harder and harder, falls and explodes.</summary>
    public void StartDying(Entity<GilbertBerserkComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.Phase is GilbertBerserkPhase.None or GilbertBerserkPhase.Dying or GilbertBerserkPhase.Dead)
            return;

        var now = _timing.CurTime;
        StopRageMusic(comp);
        RemComp<JitteringComponent>(ent);
        comp.DeathLineSaid = false;
        comp.NextArc = now;
        comp.Fallen = false;
        SetPhase(ent, GilbertBerserkPhase.Dying, now);
        _speed.RefreshMovementSpeedModifiers(ent);
        _jitter.AddJitter(ent, 10f, 9f);
        RaisePhaseChanged(ent);
    }

    private void UpdateDying(Entity<GilbertBerserkComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        var t = now - comp.PhaseStart;
        var k = (float) Math.Clamp(t.TotalSeconds / Math.Max(comp.DyingDuration.TotalSeconds, 0.01), 0, 1);

        // v9: no zombie wails any more, just his own «Прости! Прости!», once.
        if (!comp.DeathLineSaid && t >= comp.DeathLineDelay)
        {
            comp.DeathLineSaid = true;
            TrackLoud(comp, _audio.PlayPvs(comp.DeathLineSound, ent));
            _chat.TrySendInGameICMessage(ent, Loc.GetString("gilbert-berserk-death-line"), InGameICChatType.Speak,
                ChatTransmitRange.Normal, checkRadioPrefix: false, ignoreActionBlocker: true);
        }

        if (now >= comp.NextArc)
        {
            comp.NextArc = now + TimeSpan.FromSeconds(MathHelper.Lerp(0.2f, 0.03f, k));
            SpawnBodyArcs(ent, 1 + (int) (k * 3f));
            _jitter.AddJitter(ent, MathHelper.Lerp(10f, 30f, k), 10f);
        }

        if (!comp.Fallen && t >= comp.FallAt)
        {
            comp.Fallen = true;
            _standing.Down(ent, dropHeldItems: true);
        }

        if (t >= comp.DyingDuration)
            Explode(ent);
    }

    /// <summary>The energy explosion that kills him: 20 Shock around, dead for good.</summary>
    private void Explode(Entity<GilbertBerserkComponent> ent)
    {
        var comp = ent.Comp;
        var now = _timing.CurTime;
        var coords = _transform.GetMapCoordinates(ent);
        if (coords.MapId != MapId.Nullspace)
            Spawn(comp.ExplosionPrototype, coords);
        _audio.PlayPvs(comp.ExplosionSound, ent);
        _audio.PlayPvs(comp.BurstSound, ent);
        DamageAround(ent, coords);

        RemComp<JitteringComponent>(ent);
        SetPhase(ent, GilbertBerserkPhase.Dead, now);
        Kill(ent);
        RaisePhaseChanged(ent);
    }

    /// <summary>Takes him to his dead threshold (so the health readout agrees) and makes it final.</summary>
    private void Kill(Entity<GilbertBerserkComponent> ent)
    {
        EnsureComp<UnrevivableComponent>(ent);

        if (_mobState.IsDead(ent))
            return;

        if (TryComp<DamageableComponent>(ent, out var damageable)
            && _thresholds.TryGetThresholdForState(ent, MobState.Dead, out var dead))
        {
            var needed = dead.Value - damageable.TotalDamage + 1;
            if (needed > 0)
            {
                ent.Comp.AllowDamage = true;
                try
                {
                    _damageable.TryChangeDamage(ent, new DamageSpecifier { DamageDict = { ["Shock"] = needed } },
                        ignoreResistances: true, interruptsDoAfters: false, canMiss: false);
                }
                finally
                {
                    ent.Comp.AllowDamage = false;
                }
            }
        }

        if (!_mobState.IsDead(ent))
            _mobState.ChangeMobState(ent, MobState.Dead);
    }

    private void OnMobStateChanged(Entity<GilbertBerserkComponent> ent, ref MobStateChangedEvent args)
    {
        var comp = ent.Comp;
        if (args.NewMobState == MobState.Dead)
            StopRageMusic(comp);

        if (comp.Phase == GilbertBerserkPhase.Dead)
        {
            // Exploded: nothing brings him back.
            if (args.NewMobState != MobState.Dead)
                _mobState.ChangeMobState(ent, MobState.Dead);
            return;
        }

        // Killed some other way mid-phase (admin smite etc): that's final too.
        if (comp.Phase != GilbertBerserkPhase.None && args.NewMobState == MobState.Dead)
        {
            RemComp<JitteringComponent>(ent);
            SetPhase(ent, GilbertBerserkPhase.Dead, _timing.CurTime);
            EnsureComp<UnrevivableComponent>(ent);
            RaisePhaseChanged(ent);
        }
    }

    private void RaisePhaseChanged(Entity<GilbertBerserkComponent> ent)
    {
        var ev = new GilbertBerserkPhaseChangedEvent(ent.Comp.Phase);
        RaiseLocalEvent(ent, ref ev);
    }

    #endregion

    #region Combat

    private void OnGetMeleeDamage(Entity<GilbertBerserkComponent> ent, ref GetMeleeDamageEvent args)
    {
        // His own fists (he is his own melee weapon) while raging.
        if (ent.Comp.Phase != GilbertBerserkPhase.Rage || args.Weapon != ent.Owner || args.User != ent.Owner)
            return;

        args.Damage += ent.Comp.PunchBonus;
    }

    private void OnRefreshSpeed(Entity<GilbertBerserkComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (ent.Comp.Phase == GilbertBerserkPhase.Rage)
            args.ModifySpeed(1f, ent.Comp.SprintModifier);
    }

    /// <summary>The burst / explosion: <see cref="GilbertBerserkComponent.BurstDamage"/> to every living mob in range but him.</summary>
    private void DamageAround(Entity<GilbertBerserkComponent> ent, MapCoordinates coords)
    {
        if (coords.MapId == MapId.Nullspace)
            return;

        foreach (var target in _lookup.GetEntitiesInRange<MobStateComponent>(coords, ent.Comp.BurstRadius))
        {
            if (!_rage.IsRageCollateral(ent.Owner, target.Owner))
                continue; // v13.4: not the pAI / DT-2 in someone's pocket

            _damageable.TryChangeDamage(target, ent.Comp.BurstDamage, ignoreResistances: true, origin: ent,
                canMiss: false);
        }
    }

    #endregion

    #region Effects

    private void SpawnBodyArcs(Entity<GilbertBerserkComponent> ent, int count)
    {
        var coords = _transform.GetMapCoordinates(ent);
        if (coords.MapId == MapId.Nullspace)
            return;

        for (var i = 0; i < count; i++)
        {
            var offset = new Vector2(_random.NextFloat(-0.35f, 0.35f), _random.NextFloat(-0.45f, 0.45f));
            var arc = Spawn(ent.Comp.ArcPrototype, coords.Offset(offset));
            _transform.SetLocalRotation(arc, _random.NextAngle());
        }
    }

    private void SpawnCrackle(Entity<GilbertBerserkComponent> ent)
    {
        var coords = _transform.GetMapCoordinates(ent);
        if (coords.MapId == MapId.Nullspace)
            return;

        // Uniform over the disc of the burst radius.
        var r = ent.Comp.BurstRadius * MathF.Sqrt(_random.NextFloat());
        var fx = Spawn(ent.Comp.CracklePrototype, coords.Offset(_random.NextAngle().ToVec() * r));
        _transform.SetLocalRotation(fx, _random.NextAngle());
        if (_random.Prob(0.12f))
            _audio.PlayPvs(ent.Comp.CrackleSound, fx);
    }

    #endregion
}
