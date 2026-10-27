using Content.Goobstation.Shared.Sprinting;
using Content.Server.Chat.Systems;
using Content.Server.NPC.Components;
using Content.Shared.Explosion.Components;
using Content.Shared.Humanoid;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Hearing: speech (never whispers), loud emotes such as screams, gunshots, explosions and sprinting
/// within <see cref="GilbertAIComponent.HearingRange"/>. A sound makes him turn towards it and go check the spot.
/// </summary>
public sealed partial class GilbertAISystem
{
    private readonly HashSet<EntityUid> _heardExplosions = new();
    private readonly List<EntityUid> _explosionScratch = new();

    private void InitializeHearing()
    {
        SubscribeLocalEvent<EntitySpokeEvent>(OnEntitySpoke);
        SubscribeLocalEvent<HumanoidAppearanceComponent, EmoteEvent>(OnHumanoidEmote);
        SubscribeLocalEvent<GunComponent, GunShotEvent>(OnGunShot);
    }

    private void OnEntitySpoke(EntitySpokeEvent args)
    {
        if (HasComp<GilbertAIComponent>(args.Source))
            return;

        // Whispers are ignored completely (v5): no detection, no investigation, and a captive lying still
        // stays "dormant" when they whisper. Radio speakers relay their messages as whispers too, so he
        // doesn't react to radios (his own included) either. Normal speech, screams and so on still count.
        if (args.IsWhisper)
            return;

        var query = EntityQueryEnumerator<GilbertAIComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            // Speaking wakes a captive lying still on the floor (no more ignoring them).
            MarkActive((uid, comp), args.Source, _timing.CurTime);

            if (InHearingRange(uid, args.Source, comp.HearingRange))
                HearNoise((uid, comp), Transform(args.Source).Coordinates, GilbertNoise.Speech, args.Source);
        }
    }

    private void OnHumanoidEmote(Entity<HumanoidAppearanceComponent> ent, ref EmoteEvent args)
    {
        if (HasComp<GilbertAIComponent>(ent))
            return;

        var query = EntityQueryEnumerator<GilbertAIComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            MarkActive((uid, comp), ent, _timing.CurTime);
            if (comp.HeardEmotes.Contains(args.Emote.ID) && InHearingRange(uid, ent, comp.HearingRange))
                HearNoise((uid, comp), Transform(ent).Coordinates, GilbertNoise.Scream, ent);
        }
    }

    private void OnGunShot(Entity<GunComponent> gun, ref GunShotEvent args)
    {
        // His own shotgun is not news to him.
        if (HasComp<GilbertAIComponent>(args.User))
            return;

        var source = args.User.IsValid() && !TerminatingOrDeleted(args.User) ? args.User : gun.Owner;
        var query = EntityQueryEnumerator<GilbertAIComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (InHearingRange(uid, source, comp.HearingRange))
                HearNoise((uid, comp), Transform(source).Coordinates, GilbertNoise.Gunshot, source);
        }
    }

    private void UpdateHearing(TimeSpan now)
    {
        // Explosions have no broadcast event; every explosion spawns an entity with its visuals, poll those.
        _explosionScratch.Clear();
        var explosions = EntityQueryEnumerator<ExplosionVisualsComponent>();
        while (explosions.MoveNext(out var boom, out var visuals))
        {
            _explosionScratch.Add(boom);
            if (!_heardExplosions.Add(boom))
                continue;

            var query = EntityQueryEnumerator<GilbertAIComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var comp, out var xform))
            {
                var pos = _transform.GetMapCoordinates(xform);
                if (pos.MapId != visuals.Epicenter.MapId
                    || (pos.Position - visuals.Epicenter.Position).Length() > comp.HearingRange)
                    continue;

                HearNoise((uid, comp), _transform.ToCoordinates(visuals.Epicenter), GilbertNoise.Explosion, null);
            }
        }

        if (_heardExplosions.Count > _explosionScratch.Count)
            _heardExplosions.IntersectWith(_explosionScratch);

        // Sprinting (the stamina-draining Goob sprint, not just walking fast) is loud: re-heard periodically.
        var gilberts = EntityQueryEnumerator<GilbertAIComponent, TransformComponent>();
        while (gilberts.MoveNext(out var uid, out var comp, out var xform))
        {
            if (now < comp.NextSprintCheck)
                continue;

            comp.NextSprintCheck = now + comp.SprintHearInterval;
            EntityUid? loudest = null;
            var best = float.MaxValue;
            foreach (var runner in _lookup.GetEntitiesInRange<SprinterComponent>(xform.Coordinates, comp.HearingRange))
            {
                if (!runner.Comp.IsSprinting || runner.Owner == uid || HasComp<GilbertAIComponent>(runner))
                    continue;

                var d = DistanceTo(uid, runner);
                if (d < best)
                {
                    best = d;
                    loudest = runner;
                }
            }

            if (loudest is { } heard)
                HearNoise((uid, comp), Transform(heard).Coordinates, GilbertNoise.Sprint, heard);
        }
    }

    private bool InHearingRange(EntityUid gilbert, EntityUid source, float range)
    {
        var a = _transform.GetMapCoordinates(gilbert);
        var b = _transform.GetMapCoordinates(source);
        return a.MapId == b.MapId && (a.Position - b.Position).Length() <= range;
    }

    /// <summary>
    /// Gilbert heard something at <paramref name="coords"/>: he turns towards it and (when free) goes to check it.
    /// </summary>
    public void HearNoise(Entity<GilbertAIComponent> ent, EntityCoordinates coords, GilbertNoise kind, EntityUid? source)
    {
        var comp = ent.Comp;
        if (source == ent.Owner || HasComp<ActorComponent>(ent) || !_mobState.IsAlive(ent) || !_sequence.IsActive(ent))
            return;

        var now = _timing.CurTime;
        comp.LastNoise = kind;
        comp.LastHeardAt = now;
        comp.NoisePosition = coords;
        comp.NoiseSource = source;
        comp.HeardCount++;
        // v8: a sound gets an answer on the very next tick.
        comp.NextThink = now;

        // v8: raging - no orders, no investigating, just a direction to run in.
        if (InBerserk(ent))
        {
            BerserkHear(ent, coords);
            return;
        }

        // A captive he can see talking: nothing to investigate.
        if (kind is GilbertNoise.Speech or GilbertNoise.Scream && source is { } speaker && IsCuffed(speaker)
            && !IsHostile(ent, speaker) && CanSee(ent, speaker, comp))
            return;

        switch (comp.State)
        {
            case GilbertAIState.Surrender:
                // Busy holding someone at gunpoint.
                return;

            case GilbertAIState.Expel:
                // Watching a loiterer; only real danger moves him.
                if (kind is not (GilbertNoise.Gunshot or GilbertNoise.Explosion))
                    return;
                comp.ExpelTarget = null;
                comp.ExpelPunched = false;
                break;

            case GilbertAIState.Combat:
                // Fighting someone he can see: noise doesn't distract him. Lost sight: the sound tells him where they went.
                if (now - comp.LastSeenTarget < TimeSpan.FromSeconds(0.5))
                    return;
                // v8: anyone talking or screaming nearby is a lead too once he has lost his target.
                if (source == null || source == comp.Target || kind is GilbertNoise.Gunshot or GilbertNoise.Speech or GilbertNoise.Scream)
                {
                    comp.LastKnownPosition = coords;
                    FaceTowards(ent, coords);
                }
                return;

            case GilbertAIState.Heal:
                // Only real danger interrupts patching up.
                if (kind is not (GilbertNoise.Gunshot or GilbertNoise.Explosion))
                    return;
                AbortHealing(ent);
                break;
        }

        FaceTowards(ent, coords);

        if (comp.State == GilbertAIState.Search)
        {
            // Hunting already: the sound becomes the new place to check.
            comp.LastKnownPosition = coords;
            comp.ArrivedAtLastKnown = false;
            comp.SearchLookUntil = TimeSpan.Zero;
            comp.SearchRoomsChecked = 0;
            if (comp.Task == GilbertTask.None)
                RegisterMove(ent, coords, 0.8f);
            return;
        }

        if (comp.Task != GilbertTask.None)
        {
            // v8: fetching loot is not worth missing a voice over; anything else (reload, grenade, C4, cuffs,
            // healing) finishes first and the sound is checked right after (see TryPendingInvestigate).
            if (comp.Task is GilbertTask.Pickup or GilbertTask.Loot)
            {
                var item = comp.TaskItem;
                AbortTask(ent);
                if (item != null)
                    comp.ScavengeIgnore.Remove(item.Value);
            }
            else
            {
                comp.PendingNoise = coords;
                comp.PendingNoiseAt = now;
                return;
            }
        }

        BeginInvestigate(ent, coords);
    }

    /// <summary>v8: a sound heard during a task: once the task is over (and nothing better came up), go check it.</summary>
    private bool TryPendingInvestigate(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.PendingNoise is not { } spot || comp.Task != GilbertTask.None)
            return false;

        comp.PendingNoise = null;
        if (now - comp.PendingNoiseAt > TimeSpan.FromSeconds(20))
            return false;

        BeginInvestigate(ent, spot);
        return true;
    }

    private void BeginInvestigate(Entity<GilbertAIComponent> ent, EntityCoordinates coords)
    {
        var comp = ent.Comp;
        var now = _timing.CurTime;
        SetState(ent, GilbertAIState.Investigate);
        comp.NoisePosition = coords;
        comp.InvestigateArrived = false;
        comp.InvestigateDeadline = now + comp.InvestigateTimeout;
        comp.InvestigateFallbacks = 0;
        comp.PendingNoise = null;
        comp.InvestigationsStarted++;
        RegisterMove(ent, coords, 1f);
    }

    private void UpdateInvestigate(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;

        if (comp.Task != GilbertTask.None)
        {
            UpdateTask(ent, now);
            return;
        }

        if (comp.NoisePosition is not { } spot || now > comp.InvestigateDeadline)
        {
            EnterIdle(ent);
            return;
        }

        if (!comp.InvestigateArrived)
        {
            if (!TryComp<NPCSteeringComponent>(ent, out var steering))
            {
                if (_transform.InRange(xform.Coordinates, spot, 1.2f))
                    ArriveInvestigate(ent, now);
                else
                    RegisterMove(ent, spot, 1f);
                return;
            }

            switch (steering.Status)
            {
                case SteeringStatus.InRange:
                    ArriveInvestigate(ent, now);
                    return;

                case SteeringStatus.NoPath:
                    _steering.Unregister(ent);
                    // Gunfire or an explosion behind a wall / a door he can't open: make a way.
                    if (comp.LastNoise is GilbertNoise.Gunshot or GilbertNoise.Explosion or GilbertNoise.Scream
                        && TryStartBreach(ent, xform, spot, now))
                        return;

                    // v8: the exact tile is unreachable (inside a locker, behind a table, a closed room): walk to the
                    // nearest reachable point around it instead of "arriving" where he stands.
                    if (TryInvestigateFallback(ent, xform, spot))
                        return;

                    ArriveInvestigate(ent, now);
                    return;
            }

            return;
        }

        // Look around the spot, a quarter turn at a time.
        if (now >= comp.NextLookTurn)
        {
            comp.NextLookTurn = now + TimeSpan.FromSeconds(0.4);
            _transform.SetWorldRotation(ent, _transform.GetWorldRotation(xform) + Angle.FromDegrees(90));
        }

        if (now >= comp.InvestigateLookUntil)
            EnterIdle(ent);
    }

    private static readonly float[] FallbackRadii = { 1.5f, 3f, 5f };

    private bool TryInvestigateFallback(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityCoordinates spot)
    {
        var comp = ent.Comp;
        if (comp.InvestigateFallbacks >= FallbackRadii.Length * 2)
            return false;

        var radius = FallbackRadii[comp.InvestigateFallbacks / 2];
        var spotPos = _transform.ToMapCoordinates(spot).Position;
        var toMe = _transform.GetWorldPosition(xform) - spotPos;
        var dir = toMe.LengthSquared() > 0.01f ? Vector2.Normalize(toMe) : Vector2.UnitY;
        // Even tries: the side facing him; odd tries: a quarter turn.
        if (comp.InvestigateFallbacks % 2 == 1)
            dir = new Vector2(-dir.Y, dir.X);
        comp.InvestigateFallbacks++;
        RegisterMove(ent, spot.Offset(dir * radius), 1f);
        return true;
    }

    private void ArriveInvestigate(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        _steering.Unregister(ent);
        comp.InvestigateArrived = true;
        comp.InvestigateLookUntil = now + comp.InvestigateLookTime;
        comp.NextLookTurn = now + TimeSpan.FromSeconds(0.35);
    }

    private void FaceTowards(EntityUid uid, EntityCoordinates coords)
    {
        var delta = _transform.ToMapCoordinates(coords).Position - _transform.GetWorldPosition(uid);
        if (delta.LengthSquared() > 0.01f)
            _transform.SetWorldRotation(uid, delta.ToWorldAngle());
    }
}
