using System.Numerics;
using Content.Client.Camera;
using Content.Client.Inventory;
using Content.Shared._CorvaxGoob.Gilbert.Finisher;
using Content.Shared.Body.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Hands.Components;
using Content.Shared.Humanoid;
using Content.Shared.Rotation;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._CorvaxGoob.Gilbert.Finisher;

/// <summary>
/// Client half of the finisher cutscene: procedurally animates the humanoid sprite (side view: baked per-limb puppet
/// layers, see the Puppet partial; otherwise the original layers for body, head, arms, legs and
/// the clothing that belongs to them) from the networked start time, plus camera shake and a hit flash.
/// Everything is applied on top of cached original values and restored when the component goes away.
/// </summary>
public sealed partial class GilbertFinisherVisualsSystem : SharedGilbertFinisherSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly CameraRecoilSystem _recoil = default!;
    [Dependency] private readonly IOverlayManager _overlays = default!;

    private const float PixelsPerMeter = 32f;
    private const float FlashTime = 0.12f;
    private const float ShakeRange = 10f;
    private static readonly Color FlashColor = new(1f, 0.55f, 0.55f);

    private readonly Dictionary<EntityUid, PlayState> _states = new();

    private sealed class PlayState
    {
        public Vector2 SpriteOffset;
        public Angle SpriteRotation;
        public Color SpriteColor;
        public readonly Dictionary<SpriteComponent.Layer, (Vector2 Offset, Angle Rotation)> Layers = new();
        public int NextEvent;
        public float FlashUntil = -1f;
        public float LastBodyRot;
        public float LastSign = 1f;
        public PuppetData? Puppet;
        public int? FailedPuppetSignature;
        /// <summary>In-hand layers hidden by the holster in the non-puppet fallback.</summary>
        public readonly HashSet<SpriteComponent.Layer> HiddenHands = new();
        /// <summary>Sprite direction override before a <see cref="GilbertFinisherView"/> took it over, while one is active.</summary>
        public (bool Enabled, Direction Dir)? ViewOriginal;
        /// <summary>Prop layers (<see cref="GilbertFinisherActorTracks.Props"/>, by index), built for <see cref="PropsPuppet"/>.</summary>
        public readonly List<(string Key, SpriteComponent.Layer Layer)> Props = new();
        public PuppetData? PropsPuppet;
        public bool PropsBuilt;
    }

    /// <summary>A running blood burst (drawn by <see cref="GilbertFinisherBloodOverlay"/>).</summary>
    internal sealed class BloodBurst
    {
        public required GilbertFinisherBlood Blood;
        public int EventIndex;
        public MapId Map;
        /// <summary>World position of the target entity when the burst started.</summary>
        public Vector2 Anchor;
        /// <summary>Spawn point, forward-frame px relative to <see cref="Anchor"/>.</summary>
        public Vector2 Origin;
        /// <summary>World vector of one forward-frame px along x / y.</summary>
        public Vector2 AxisX, AxisY;
        public Color Color;
        public TimeSpan Start;
    }

    internal readonly List<BloodBurst> Bursts = new();

    /// <summary>A dizzy star for this frame (drawn by <see cref="GilbertFinisherBloodOverlay"/>).</summary>
    internal readonly record struct DizzyStar(MapId Map, Vector2 Position, int Size, Color Color);

    /// <summary>Rebuilt every frame from the dazed actors.</summary>
    internal readonly List<DizzyStar> Stars = new();
    private GilbertFinisherBloodOverlay? _bloodOverlay;

    public override void Initialize()
    {
        base.Initialize();
        _bloodOverlay = new GilbertFinisherBloodOverlay(this, _timing);
        _overlays.AddOverlay(_bloodOverlay);
    }

    private static readonly Enum[] HeadLayers =
    {
        HumanoidVisualLayers.Head, HumanoidVisualLayers.Eyes, HumanoidVisualLayers.Hair, HumanoidVisualLayers.FacialHair,
        HumanoidVisualLayers.Snout, HumanoidVisualLayers.HeadSide, HumanoidVisualLayers.HeadTop, HumanoidVisualLayers.Face,
    };

    private static readonly string[] HeadSlots = { "head", "mask", "eyes", "ears" };

    public override void Shutdown()
    {
        base.Shutdown();
        if (_bloodOverlay != null)
            _overlays.RemoveOverlay(_bloodOverlay);
        Bursts.Clear();
        foreach (var (uid, state) in _states)
            Restore(uid, state, null);
        _states.Clear();
        foreach (var image in _pngCache.Values)
            image?.Dispose();
        _pngCache.Clear();
    }

    protected override void OnFinisherShutdown(Entity<GilbertFinisherComponent> ent)
    {
        if (!_states.Remove(ent, out var state))
            return;

        Restore(ent, state, ent.Comp);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var now = _timing.CurTime;
        Stars.Clear();
        var query = EntityQueryEnumerator<GilbertFinisherComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var comp, out var sprite))
        {
            if (!_proto.TryIndex(comp.Finisher, out var proto))
                continue;

            if (!_states.TryGetValue(uid, out var state))
            {
                state = new PlayState
                {
                    SpriteOffset = sprite.Offset,
                    SpriteRotation = sprite.Rotation,
                    SpriteColor = sprite.Color,
                };
                // Late joiners / mid-cutscene PVS entries must not replay old impacts.
                // Intro (holster) events are skipped when the server skipped the intro.
                var t0 = GilbertFinisherTimeline.AnimTime(proto, (float) (now - comp.StartTime).TotalSeconds);
                while (state.NextEvent < proto.Events.Count
                       && (proto.Events[state.NextEvent].Time < t0 - 0.2f || proto.Events[state.NextEvent].Time < -comp.Intro - 1e-4f))
                    state.NextEvent++;
                _states[uid] = state;
            }

            // Real time drives the effects; the poses use the hit-stop warped animation time.
            // Negative during the intro (the holster beat before StartTime).
            var real = Math.Clamp((float) (now - comp.StartTime).TotalSeconds, -comp.Intro, GilbertFinisherTimeline.TotalLength(proto));
            var t = GilbertFinisherTimeline.AnimTime(proto, real);
            Animate((uid, sprite), comp, proto, state, t);
            Effects((uid, sprite), comp, proto, state, t, real);
        }
    }

    /// <summary>
    /// Screen-space mirror sign: +1 when the attacker → victim direction points right on screen, -1 when left.
    /// Vertical directions fall back to the east mapping (the poses are side-view poses).
    /// </summary>
    private float ScreenSign(GilbertFinisherComponent comp)
    {
        var screenFwd = _eye.CurrentEye.Rotation.RotateVec(comp.Forward);
        return screenFwd.X < -0.3f ? -1f : 1f;
    }

    private void Animate(Entity<SpriteComponent> ent, GilbertFinisherComponent comp, GilbertFinisherPrototype proto,
        PlayState state, float t)
    {
        var tracks = GilbertFinisherTimeline.Tracks(proto, comp.Role);
        var sx = ScreenSign(comp);
        state.LastSign = sx;

        // The victim faces the attacker, so its "near" side is the other one.
        var facingRight = comp.Role == GilbertFinisherRole.Attacker ? sx > 0 : sx < 0;

        // Fixed-direction intervals (e.g. back to the camera for the temple kick).
        // Turned views draw the actor side-on facing the other way (e.g. lying with its back to the attacker).
        var view = GilbertFinisherTimeline.ViewDirection(tracks, t, facingRight);
        if (GilbertFinisherTimeline.ViewInfoAt(tracks, t) is { Turned: true })
            facingRight = !facingRight;
        ApplyView(ent, state, view);
        var inHands = GilbertFinisherTimeline.HeldInHands(tracks, t);

        // Side view (and forced views): per-limb baked layers with clothing split along the body-part masks. Otherwise
        // (vertical facing, rotating sprites, bake failure) the original layers are moved as whole layers.
        var puppet = UpdatePuppet(ent, state, tracks.HolsterHeldItems ? tracks.HolsterStates : null,
            tracks.HolsterAt != null, view != null ? facingRight : null);
        if (puppet == null)
            HolsterFallback(ent, state, tracks.HolsterHeldItems && !inHands);
        else
            SetHeldVisibility(puppet, inHands);

        // Keys + walk cycle + idle + look-at, solved about the mask-derived joints of both puppets.
        var pivots = puppet != null ? ForwardPivots(puppet, sx) : null;
        var otherPivots = comp.Partner is { } partner && _states.TryGetValue(partner, out var other) && other.Puppet != null
            ? ForwardPivots(other.Puppet, sx)
            : null;
        var poses = GilbertFinisherTimeline.Solve(proto, comp.Role, t, pivots, true, otherPivots);

        AddStars(ent, tracks, poses, t, sx);

        var body = poses[(int) GilbertFinisherPart.Body];
        state.LastBodyRot = sx * body.Rot;
        _sprite.SetOffset(ent.AsNullable(), state.SpriteOffset + new Vector2(sx * body.X, body.Y) / PixelsPerMeter);
        _sprite.SetRotation(ent.AsNullable(), state.SpriteRotation + Angle.FromDegrees(sx * body.Rot));

        UpdateProps(ent, state, tracks, puppet, poses, t, sx);

        for (var i = 1; i < GilbertFinisherTimeline.PartCount; i++)
        {
            var part = (GilbertFinisherPart) i;
            var pose = poses[i];
            if (puppet != null)
            {
                AnimatePuppetPart(puppet, state, part, pose, sx, GilbertFinisherTimeline.IsHidden(tracks, part, t));
                continue;
            }

            // Whole original layers can't bend: the lower limb segments are skipped.
            if (part >= GilbertFinisherPart.ArmNearLow)
                continue;

            var offset = new Vector2(sx * pose.X, pose.Y) / PixelsPerMeter;
            var rotation = Angle.FromDegrees(sx * pose.Rot);
            foreach (var layer in PartLayers(ent, part, facingRight))
            {
                if (!state.Layers.TryGetValue(layer, out var orig))
                {
                    orig = (layer.Offset, layer.Rotation);
                    state.Layers[layer] = orig;
                }

                _sprite.LayerSetOffset(layer, orig.Offset + offset);
                _sprite.LayerSetRotation(layer, orig.Rotation + rotation);
            }
        }
    }

    /// <summary>
    /// Prop sprites: one layer per prop, drawn right above its part's puppet layer (on top without a puppet), rebuilt
    /// whenever the puppet is rebaked, posed from the part's pose and mirrored with the actor.
    /// </summary>
    private void UpdateProps(Entity<SpriteComponent> ent, PlayState state, GilbertFinisherActorTracks tracks,
        PuppetData? puppet, GilbertFinisherPose[] poses, float t, float sx)
    {
        if (tracks.Props.Count == 0)
            return;

        if (!state.PropsBuilt || state.PropsPuppet != puppet)
        {
            RemoveProps(ent, state);
            state.PropsBuilt = true;
            state.PropsPuppet = puppet;
            for (var i = 0; i < tracks.Props.Count; i++)
            {
                var prop = tracks.Props[i];
                int? index = null;
                if (puppet != null)
                {
                    var group = GroupOf(prop.Part);
                    foreach (var (g, _, layer, _) in puppet.Layers)
                        if (g == group)
                            index = IndexOfLayer(ent, layer) + 1;
                }

                var key = $"{PuppetKeyPrefix}prop-{i}";
                var added = _sprite.AddBlankLayer(ent, index);
                _sprite.LayerSetRsi(added, prop.Sprite, prop.State);
                _sprite.LayerMapSet(ent.AsNullable(), key, IndexOfLayer(ent, added));
                state.Props.Add((key, added));
            }
        }

        for (var i = 0; i < tracks.Props.Count && i < state.Props.Count; i++)
        {
            var prop = tracks.Props[i];
            var layer = state.Props[i].Layer;
            var visible = GilbertFinisherTimeline.PropVisible(tracks, prop, t);
            _sprite.LayerSetVisible(layer, visible);
            if (!visible)
                continue;
            // The body pose moves the whole sprite already.
            var partPose = prop.Part == GilbertFinisherPart.Body ? default : poses[(int) prop.Part];
            var pose = GilbertFinisherTimeline.PropPose(prop, partPose, t);
            _sprite.LayerSetOffset(layer, new Vector2(MathF.Round(sx * pose.X), MathF.Round(pose.Y)) / PixelsPerMeter);
            _sprite.LayerSetRotation(layer, Angle.FromDegrees(sx * pose.Rot));
            _sprite.LayerSetScale(layer, new Vector2(sx * prop.Scale, prop.Scale));
        }
    }

    private static int IndexOfLayer(Entity<SpriteComponent> ent, SpriteComponent.Layer layer)
    {
        var i = 0;
        foreach (var l in ent.Comp.AllLayers)
        {
            if (l == layer)
                return i;
            i++;
        }

        return -1;
    }

    private void RemoveProps(Entity<SpriteComponent> ent, PlayState state)
    {
        foreach (var (key, _) in state.Props)
            _sprite.RemoveLayer(ent.AsNullable(), key, false);
        state.Props.Clear();
        state.PropsBuilt = false;
        state.PropsPuppet = null;
    }

    /// <summary>Forces the sprite direction while a view is active; puts the previous override back afterwards.</summary>
    private void ApplyView(Entity<SpriteComponent> ent, PlayState state, Direction? view)
    {
        if (view is { } dir)
        {
            state.ViewOriginal ??= (ent.Comp.EnableDirectionOverride, ent.Comp.DirectionOverride);
            ent.Comp.EnableDirectionOverride = true;
            ent.Comp.DirectionOverride = dir;
            return;
        }

        RestoreView(ent.Comp, state);
    }

    private static void RestoreView(SpriteComponent sprite, PlayState state)
    {
        if (state.ViewOriginal is not { } orig)
            return;
        sprite.EnableDirectionOverride = orig.Enabled;
        sprite.DirectionOverride = orig.Dir;
        state.ViewOriginal = null;
    }

    /// <summary>Held items: in the hands until the holster swing ends, then on the back.</summary>
    private void SetHeldVisibility(PuppetData puppet, bool inHands)
    {
        foreach (var (g, _, layer, _) in puppet.Layers)
        {
            if (g is GilbertPuppetGroup.HeldNear or GilbertPuppetGroup.HeldFar)
                _sprite.LayerSetVisible(layer, inHands);
            else if (g == GilbertPuppetGroup.Slung)
                _sprite.LayerSetVisible(layer, !inHands);
        }
    }

    /// <summary>Without a puppet a holstered item can't be re-drawn on the back: just hide it (after the swing).</summary>
    private void HolsterFallback(Entity<SpriteComponent> ent, PlayState state, bool holster)
    {
        if (!holster)
            return;

        foreach (var location in new[] { HandLocation.Left, HandLocation.Right })
        foreach (var layer in HandItemLayers(ent, location))
        {
            if (!layer.Visible)
                continue;
            state.HiddenHands.Add(layer);
            _sprite.LayerSetVisible(layer, false);
        }
    }

    private void Effects(Entity<SpriteComponent> ent, GilbertFinisherComponent comp, GilbertFinisherPrototype proto,
        PlayState state, float t, float real)
    {
        while (state.NextEvent < proto.Events.Count && proto.Events[state.NextEvent].Time <= t)
        {
            var index = state.NextEvent++;
            var ev = proto.Events[index];
            if (ev.Target != comp.Role)
                continue;

            if (ev.Flash)
                state.FlashUntil = real + FlashTime;

            if (ev.Shake > 0f)
                Shake(ent, comp, ev.Shake);

            if (ev.Blood != null)
                StartBlood(ent, comp, proto, ev, index);
        }

        _sprite.SetColor(ent.AsNullable(), real < state.FlashUntil ? state.SpriteColor * FlashColor : state.SpriteColor);
    }

    /// <summary>Dizzy stars orbiting the dazed actor's head, in the same forward-frame -> world mapping as the blood.</summary>
    private void AddStars(EntityUid uid, GilbertFinisherActorTracks tracks, GilbertFinisherPose[] poses, float t, float sx)
    {
        if (tracks.Daze?.Stars is not { } cfg)
            return;
        var stars = GilbertFinisherTimeline.Stars(tracks, t);
        if (stars.Count == 0)
            return;
        var map = _transform.GetMapCoordinates(uid);
        if (map.MapId == MapId.Nullspace)
            return;

        var inv = -_eye.CurrentEye.Rotation;
        var axisX = inv.RotateVec(new Vector2(sx, 0)) / PixelsPerMeter;
        var axisY = inv.RotateVec(new Vector2(0, 1)) / PixelsPerMeter;
        var centre = GilbertFinisherTimeline.Point(poses, cfg.From, cfg.At);
        foreach (var star in stars)
        {
            var p = centre + star.Position;
            var world = map.Position + axisX * MathF.Round(p.X) + axisY * MathF.Round(p.Y);
            var shade = star.Front ? 1f : 0.7f;
            var color = new Color(cfg.Color.R * shade, cfg.Color.G * shade, cfg.Color.B * shade, cfg.Color.A * star.Alpha);
            Stars.Add(new DizzyStar(map.MapId, world, star.Size, color));
        }
    }

    private void StartBlood(EntityUid uid, GilbertFinisherComponent comp, GilbertFinisherPrototype proto,
        GilbertFinisherEvent ev, int index)
    {
        var blood = ev.Blood!;
        var map = _transform.GetMapCoordinates(uid);
        if (map.MapId == MapId.Nullspace)
            return;

        var pivots = _states.TryGetValue(uid, out var state) && state.Puppet != null ? ForwardPivots(state.Puppet, ScreenSign(comp)) : null;
        var pose = GilbertFinisherTimeline.Solve(proto, comp.Role, ev.Time, pivots);
        var origin = GilbertFinisherTimeline.Point(pose, blood.From, blood.At);

        var color = blood.Color ?? BloodColor(uid);
        // Forward-frame px -> world: x along the screen side the attacker faces, y screen-up.
        var inv = -_eye.CurrentEye.Rotation;
        var sx = ScreenSign(comp);
        Bursts.Add(new BloodBurst
        {
            Blood = blood,
            EventIndex = index,
            Map = map.MapId,
            Anchor = map.Position,
            Origin = origin,
            AxisX = inv.RotateVec(new Vector2(sx, 0)) / PixelsPerMeter,
            AxisY = inv.RotateVec(new Vector2(0, 1)) / PixelsPerMeter,
            Color = color,
            Start = _timing.CurTime,
        });
    }

    private Color BloodColor(EntityUid uid)
    {
        var reagent = TryComp<BloodstreamComponent>(uid, out var bloodstream) ? bloodstream.BloodReagent.Id : "Blood";
        return _proto.TryIndex<ReagentPrototype>(reagent, out var proto) ? proto.SubstanceColor : Color.FromHex("#800000");
    }

    private void Shake(EntityUid uid, GilbertFinisherComponent comp, float strength)
    {
        if (_player.LocalEntity is not { } local)
            return;

        var localPos = _transform.GetMapCoordinates(local);
        var pos = _transform.GetMapCoordinates(uid);
        if (localPos.MapId != pos.MapId || (localPos.Position - pos.Position).Length() > ShakeRange)
            return;

        _recoil.KickCamera(local, comp.Forward * 0.35f * strength);
    }

    private IEnumerable<SpriteComponent.Layer> PartLayers(Entity<SpriteComponent> ent, GilbertFinisherPart part, bool facingRight)
    {
        switch (part)
        {
            case GilbertFinisherPart.Head:
                foreach (var key in HeadLayers)
                    if (TryLayer(ent, key, out var l)) yield return l;
                foreach (var slot in HeadSlots)
                    foreach (var l in SlotLayers(ent, slot)) yield return l;
                break;

            case GilbertFinisherPart.ArmNear:
            case GilbertFinisherPart.ArmFar:
            {
                // Facing right on screen the right arm is the near one.
                var right = (part == GilbertFinisherPart.ArmNear) == facingRight;
                if (TryLayer(ent, right ? HumanoidVisualLayers.RArm : HumanoidVisualLayers.LArm, out var arm)) yield return arm;
                if (TryLayer(ent, right ? HumanoidVisualLayers.RHand : HumanoidVisualLayers.LHand, out var hand)) yield return hand;
                foreach (var l in HandItemLayers(ent, right ? HandLocation.Right : HandLocation.Left)) yield return l;
                if (part == GilbertFinisherPart.ArmNear)
                    foreach (var l in SlotLayers(ent, "gloves")) yield return l;
                break;
            }

            case GilbertFinisherPart.LegNear:
            case GilbertFinisherPart.LegFar:
            {
                var right = (part == GilbertFinisherPart.LegNear) == facingRight;
                if (TryLayer(ent, right ? HumanoidVisualLayers.RLeg : HumanoidVisualLayers.LLeg, out var leg)) yield return leg;
                if (TryLayer(ent, right ? HumanoidVisualLayers.RFoot : HumanoidVisualLayers.LFoot, out var foot)) yield return foot;
                if (part == GilbertFinisherPart.LegNear)
                    foreach (var l in SlotLayers(ent, "shoes")) yield return l;
                break;
            }
        }
    }

    private bool TryLayer(Entity<SpriteComponent> ent, Enum key, out SpriteComponent.Layer layer)
    {
        layer = default!;
        if (!_sprite.LayerMapTryGet(ent.AsNullable(), key, out var index, false)
            || !_sprite.TryGetLayer(ent.AsNullable(), index, out var found, false))
            return false;

        layer = found;
        return true;
    }

    private bool TryLayer(Entity<SpriteComponent> ent, string key, out SpriteComponent.Layer layer)
    {
        layer = default!;
        if (!_sprite.LayerMapTryGet(ent.AsNullable(), key, out var index, false)
            || !_sprite.TryGetLayer(ent.AsNullable(), index, out var found, false))
            return false;

        layer = found;
        return true;
    }

    private IEnumerable<SpriteComponent.Layer> SlotLayers(Entity<SpriteComponent> ent, string slot)
    {
        if (!TryComp<InventorySlotsComponent>(ent, out var slots) || !slots.VisualLayerKeys.TryGetValue(slot, out var keys))
            yield break;

        foreach (var key in keys)
            if (TryLayer(ent, key, out var l)) yield return l;
    }

    /// <summary>
    /// In-hand item layers. <c>HandsComponent.RevealedLayers</c> is access-restricted, so this uses the default
    /// in-hand key scheme (<c>inhand-left</c> / <c>inhand-right</c>, plus numbered extra layers).
    /// </summary>
    private IEnumerable<SpriteComponent.Layer> HandItemLayers(Entity<SpriteComponent> ent, HandLocation location)
    {
        var baseKey = $"inhand-{location.ToString().ToLowerInvariant()}";
        if (TryLayer(ent, baseKey, out var main))
            yield return main;

        for (var i = 0; i < 4; i++)
        {
            if (TryLayer(ent, $"{baseKey}-{i}", out var extra))
                yield return extra;
        }
    }

    private void Restore(EntityUid uid, PlayState state, GilbertFinisherComponent? comp)
    {
        if (TerminatingOrDeleted(uid) || !TryComp<SpriteComponent>(uid, out var sprite))
            return;

        RemoveProps((uid, sprite), state);
        TearDownPuppet((uid, sprite), state);
        RestoreView(sprite, state);
        var ent = new Entity<SpriteComponent?>(uid, sprite);
        // Layers can be removed / rebuilt meanwhile (clothing or in-hand changes); only touch the ones still present.
        var alive = new HashSet<ISpriteLayer>(sprite.AllLayers);
        foreach (var layer in state.HiddenHands)
            if (alive.Contains(layer))
                _sprite.LayerSetVisible(layer, true);
        foreach (var (layer, orig) in state.Layers)
        {
            if (!alive.Contains(layer))
                continue;

            _sprite.LayerSetOffset(layer, orig.Offset);
            _sprite.LayerSetRotation(layer, orig.Rotation);
        }

        _sprite.SetOffset(ent, state.SpriteOffset);
        _sprite.SetColor(ent, state.SpriteColor);

        // A victim left lying keeps the on-back angle of the cutscene instead of snapping to the default face-down
        // lying rotation (the server knocks it down right after the component is removed).
        if (comp is { Role: GilbertFinisherRole.Victim, KeepDown: true }
            && MathF.Abs(state.LastBodyRot) > 45f
            && TryComp<RotationVisualsComponent>(uid, out var rotVisuals))
        {
            var lying = Angle.FromDegrees(state.LastBodyRot > 0 ? 90 : 270);
            rotVisuals.HorizontalRotation = lying;
            _sprite.SetRotation(ent, lying);
            return;
        }

        _sprite.SetRotation(ent, state.SpriteRotation);
    }

    /// <summary>For tests: true while this client still holds modified sprite state for the entity.</summary>
    public bool IsAnimating(EntityUid uid) => _states.ContainsKey(uid);
}
