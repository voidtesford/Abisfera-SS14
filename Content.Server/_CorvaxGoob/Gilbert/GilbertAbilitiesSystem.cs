using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.Standing;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server.Chat.Systems;
using Content.Server.Fluids.EntitySystems;
using Content.Server.Popups;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Chat;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Gilbert's Scream, Dash and Shove.
/// </summary>
public sealed class GilbertAbilitiesSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly PuddleSystem _puddle = default!;
    [Dependency] private readonly SharedBloodstreamSystem _bloodstream = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly GilbertSpawnSequenceSystem _sequence = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertAbilitiesComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<GilbertAbilitiesComponent, AttackedEvent>(OnAttacked);
        SubscribeLocalEvent<GilbertAbilitiesComponent, MeleeHitEvent>(OnPunch);
    }

    #region Effects

    private void SpawnEffect(EntProtoId proto, MapCoordinates coords, Angle? rotation = null)
    {
        var fx = Spawn(proto, coords);
        if (rotation != null)
            _transform.SetWorldRotation(fx, rotation.Value);
    }

    /// <summary>Bare-handed punch (he is his own weapon): a small red spark on everything it hits.</summary>
    private void OnPunch(Entity<GilbertAbilitiesComponent> ent, ref MeleeHitEvent args)
    {
        if (args.Weapon != ent.Owner || args.User != ent.Owner || !args.IsHit)
            return;

        // Berserk (v6): a small energy explosion throwing a cascade of energy every way instead.
        TryComp<GilbertBerserkComponent>(ent, out var berserk);
        var raging = berserk is { Phase: GilbertBerserkPhase.Rage };

        // Berserk (v6): his own heavy energy hit sound instead of the normal punch.
        if (raging)
            args.HitSoundOverride = berserk!.PunchSound;

        foreach (var hit in args.HitEntities)
        {
            if (hit == ent.Owner || TerminatingOrDeleted(hit))
                continue;

            var coords = _transform.GetMapCoordinates(hit);
            if (!raging)
            {
                SpawnEffect(ent.Comp.PunchSparkEffect, coords);
                continue;
            }

            SpawnEffect(berserk!.PunchPrototype, coords, _random.NextAngle());
            var shards = _random.Next(4, 7);
            var start = _random.NextFloat(0f, MathF.Tau);
            for (var i = 0; i < shards; i++)
            {
                var angle = new Angle(start + MathF.Tau * i / shards + _random.NextFloat(-0.3f, 0.3f));
                var offset = angle.ToVec() * _random.NextFloat(0.35f, 0.8f);
                // The shard art flies east; turn it outwards.
                SpawnEffect(berserk.PunchShardPrototype, coords.Offset(offset), angle);
            }
        }
    }

    #endregion

    #region Scream

    private void OnDamageChanged(Entity<GilbertAbilitiesComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta == null || _mobState.IsDead(ent))
            return;

        var amount = FixedPoint2.Zero;
        foreach (var value in args.DamageDelta.DamageDict.Values)
        {
            if (value > 0)
                amount += value;
        }
        if (amount <= 0)
            return;

        var now = _timing.CurTime;
        var recent = ent.Comp.RecentDamage;
        recent.Add((now, amount));
        recent.RemoveAll(d => d.Time < now - ent.Comp.ScreamWindow);

        var total = FixedPoint2.Zero;
        foreach (var (_, dmg) in recent)
            total += dmg;

        if (total < ent.Comp.ScreamDamageThreshold || InBerserk(ent))
            return;

        recent.Clear();
        Scream(ent);
    }

    /// <summary>
    /// Wordless howl: a white shockwave goes out and everyone it reaches is stunned and throws up blood.
    /// Only an emote is shown in chat, never speech.
    /// </summary>
    public void Scream(Entity<GilbertAbilitiesComponent> ent)
    {
        var (uid, comp) = ent;

        // No self-stun: he keeps moving and fighting while he howls.
        _audio.PlayPvs(comp.ScreamSound, uid, AudioParams.Default.WithVolume(6f).WithMaxDistance(15f));
        _chat.TrySendInGameICMessage(uid, Loc.GetString("gilbert-scream-emote"), InGameICChatType.Emote,
            ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false, ignoreActionBlocker: true);

        var coords = Transform(uid).Coordinates;
        var mapCoords = _transform.ToMapCoordinates(coords);
        Spawn(comp.ScreamEffect, mapCoords);
        // Red energy crackling inside the white wave, plus sparks thrown out around him.
        SpawnEffect(comp.ScreamCrackleEffect, mapCoords);
        for (var i = 0; i < comp.ScreamSparks; i++)
        {
            var angle = MathHelper.TwoPi * i / comp.ScreamSparks;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 1.5f;
            SpawnEffect(comp.PunchSparkEffect, mapCoords.Offset(offset));
        }

        foreach (var victim in _lookup.GetEntitiesInRange<MobStateComponent>(coords, comp.ScreamRange))
        {
            if (victim.Owner == uid || _mobState.IsDead(victim, victim.Comp))
                continue;

            if (!_interaction.InRangeUnobstructed(uid, victim.Owner, comp.ScreamRange + 0.5f))
                continue;

            _stun.TryParalyze(victim, comp.ScreamStun, true);
            VomitBlood(victim, comp.ScreamBloodVomit);
        }
    }

    /// <summary>
    /// Makes the victim throw up their own blood: it is taken from their bloodstream and spilled on the floor.
    /// </summary>
    private void VomitBlood(EntityUid victim, FixedPoint2 amount)
    {
        if (!TryComp<BloodstreamComponent>(victim, out var blood))
            return;

        _bloodstream.TryModifyBloodLevel((victim, blood), -amount);

        var solution = new Solution();
        solution.AddReagent(new ReagentId(blood.BloodReagent, _bloodstream.GetEntityBloodData(victim)), amount);
        _puddle.TrySpillAt(victim, solution, out _, sound: false);

        _audio.PlayPvs(new SoundCollectionSpecifier("Vomit"), victim);
        _popup.PopupEntity(Loc.GetString("gilbert-scream-vomit-blood", ("person", Identity.Entity(victim, EntityManager))),
            victim, PopupType.MediumCaution);
    }

    #endregion

    #region Dash

    /// <summary>In his second phase (v6) he has no abilities at all, only fists.</summary>
    private bool InBerserk(EntityUid uid)
    {
        return TryComp<GilbertBerserkComponent>(uid, out var berserk) && berserk.Phase != GilbertBerserkPhase.None;
    }

    public bool CanDash(Entity<GilbertAbilitiesComponent?> ent)
    {
        return Resolve(ent, ref ent.Comp, false) && _timing.CurTime >= ent.Comp.NextDash && !_mobState.IsIncapacitated(ent)
            && _sequence.IsActive(ent) && !InBerserk(ent);
    }

    /// <summary>
    /// Dashes up to <see cref="GilbertAbilitiesComponent.DashDistance"/> tiles towards <paramref name="target"/>
    /// (stopping in front of walls) and stuns the target if he reaches them.
    /// </summary>
    public bool TryDash(Entity<GilbertAbilitiesComponent?> ent, EntityUid target)
    {
        if (!Resolve(ent, ref ent.Comp, false) || !CanDash(ent))
            return false;

        var uid = ent.Owner;
        var comp = ent.Comp;
        var xform = Transform(uid);
        var from = _transform.GetMapCoordinates(uid, xform);
        var to = _transform.GetMapCoordinates(target);
        if (from.MapId != to.MapId)
            return false;

        var delta = to.Position - from.Position;
        var dist = delta.Length();
        if (dist < 0.1f)
            return false;

        var dir = delta / dist;
        // Stop just in front of the target, never further than the dash distance.
        var length = MathF.Min(comp.DashDistance, MathF.Max(0f, dist - 0.8f));

        var ray = new CollisionRay(from.Position, dir, (int) CollisionGroup.Impassable);
        foreach (var hit in _physics.IntersectRay(from.MapId, ray, length, uid))
        {
            if (hit.HitEntity == target)
                continue;
            length = MathF.Min(length, MathF.Max(0f, hit.Distance - 0.45f));
            break;
        }

        comp.NextDash = _timing.CurTime + comp.DashCooldown;

        var angle = dir.ToWorldAngle();
        // Burst where he leaves, a red afterimage trail along the way, and another burst where he lands.
        SpawnEffect(comp.DashBurstEffect, from);
        if (length > 0.05f)
        {
            var spacing = MathF.Max(0.1f, comp.DashTrailSpacing);
            for (var d = spacing; d < length; d += spacing)
                SpawnEffect(comp.DashTrailEffect, new MapCoordinates(from.Position + dir * d, from.MapId), angle);

            _transform.SetWorldPosition((uid, xform), from.Position + dir * length);
        }
        SpawnEffect(comp.DashBurstEffect, new MapCoordinates(from.Position + dir * length, from.MapId));

        _transform.SetWorldRotation(uid, angle);
        _audio.PlayPvs(comp.DashSound, uid);

        var after = _transform.GetWorldPosition(xform);
        if ((to.Position - after).Length() > comp.DashHitRange)
            return true;

        _stun.TryStun(target, comp.DashStun, true);
        _stun.TryKnockdown(target, comp.DashStun, true);
        _popup.PopupEntity(Loc.GetString("gilbert-dash-hit", ("target", Identity.Entity(target, EntityManager))),
            target, PopupType.MediumCaution);
        return true;
    }

    #endregion

    #region Shove

    private void OnAttacked(Entity<GilbertAbilitiesComponent> ent, ref AttackedEvent args)
    {
        var user = args.User;
        if (user == ent.Owner || !HasComp<MobStateComponent>(user) || _mobState.IsIncapacitated(ent) || !_sequence.IsActive(ent))
            return;

        // Berserk: no shoves or other tricks, only his fists.
        if (InBerserk(ent))
            return;

        // Only melee: the attacker has to be right next to him.
        if (!_interaction.InRangeUnobstructed(ent.Owner, user, 2f))
            return;

        TryShove(ent, user);
    }

    /// <summary>
    /// Counter-attack: hits the attacker with his punch, stuns and knocks them down, and they drop what they hold.
    /// </summary>
    public bool TryShove(Entity<GilbertAbilitiesComponent> ent, EntityUid target)
    {
        var (uid, comp) = ent;
        var now = _timing.CurTime;
        if (now < comp.NextShove)
            return false;

        comp.NextShove = now + comp.ShoveCooldown;

        if (TryComp<MeleeWeaponComponent>(uid, out var melee))
            _damageable.TryChangeDamage(target, melee.Damage, origin: uid, canMiss: false);

        SpawnEffect(comp.ShoveImpactEffect, _transform.GetMapCoordinates(target));

        _stun.TryKnockdown(target, comp.ShoveStun, true, DropHeldItemsBehavior.AlwaysDrop);
        _stun.TryStun(target, comp.ShoveStun, true);

        _audio.PlayPvs(new SoundCollectionSpecifier("Punch"), target);
        _audio.PlayPvs(comp.ShoveSound, uid);
        _chat.TrySendInGameICMessage(uid, Loc.GetString("gilbert-line-shove-1"), InGameICChatType.Speak,
            ChatTransmitRange.Normal, hideLog: true, checkRadioPrefix: false, ignoreActionBlocker: true);
        return true;
    }

    #endregion
}
