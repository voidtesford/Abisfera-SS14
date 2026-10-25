using System.Numerics;
using Content.Client.Rotation;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Humanoid;
using Content.Shared.Rotation;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Shared.Animations;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using RobustAnimation = Robust.Client.Animations.Animation;

namespace Content.Client._CorvaxGoob.ProperKungFu;

/// <summary>
/// v13 client visuals: the recoloured time stop / crimson / noir circles, the Wolf Gaze cutscene overlay,
/// the jump / crouch / slow fall / launch sprite poses and the Claw Mastery look (red glowing eyes with a light,
/// short eye trails and a pulsing crimson outline), all removed when the mode ends.
/// </summary>
public sealed class ProperKungFuV13VisualsSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlayMan = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly AnimationPlayerSystem _animation = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private ProperKungFuTintOverlay _tint = default!;
    private ProperKungFuGazeOverlay _gaze = default!;
    private ProperKungFuGazeTextOverlay _gazeText = default!;

    private const string PoseKey = "proper-kung-fu-pose";
    private const string EyeGlowKey = "proper-kung-fu-ult-eyes";
    private static readonly ProtoId<ShaderPrototype> OutlineShader = "SelectionOutline";
    private static readonly EntProtoId EyeLightProto = "ProperKungFuUltEyeLight";
    private static readonly EntProtoId EyeStreakProto = "ProperKungFuUltEyeStreak";
    private static readonly Color EyeColor = Color.FromHex("#FF1A1A");

    /// <summary>Client-side bits of the Claw Mastery look per entity.</summary>
    private sealed class UltLook
    {
        public ShaderInstance Outline = default!;
        public EntityUid? Light;
        public Vector2 LastPos;
        public TimeSpan NextStreak;
        public bool EyeLayer;
    }

    private readonly Dictionary<EntityUid, UltLook> _looks = new();
    private readonly List<(EntityUid Uid, TimeSpan End)> _streaks = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<ProperKungFuPoseAnimationEvent>(OnPose);
        SubscribeNetworkEvent<ProperKungFuGazeCinematicEvent>(OnGazeCinematic);
        SubscribeLocalEvent<ProperKungFuComponent, ComponentShutdown>(OnKungFuShutdown);

        _tint = new ProperKungFuTintOverlay();
        _gaze = new ProperKungFuGazeOverlay();
        _gazeText = new ProperKungFuGazeTextOverlay();
        _overlayMan.AddOverlay(_tint);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlayMan.RemoveOverlay(_tint);
        _overlayMan.RemoveOverlay(_gaze);
        _overlayMan.RemoveOverlay(_gazeText);
        foreach (var uid in new List<EntityUid>(_looks.Keys))
        {
            RemoveLook(uid);
        }
    }

    // ---- Wolf Gaze cutscene ----

    private void OnGazeCinematic(ProperKungFuGazeCinematicEvent ev)
    {
        var now = _timing.CurTime;
        _gaze.Start = now;
        _gaze.Duration = ev.Duration;
        _gaze.TextAt = ev.TextAt;
        _gazeText.Start = now;
        _gazeText.TextAt = ev.TextAt;
        _gazeText.Duration = ev.Duration;
        _gazeText.Show = ev.ShowText;
        if (!_overlayMan.HasOverlay<ProperKungFuGazeOverlay>())
            _overlayMan.AddOverlay(_gaze);
        if (!_overlayMan.HasOverlay<ProperKungFuGazeTextOverlay>())
            _overlayMan.AddOverlay(_gazeText);
    }

    // ---- Poses ----

    private void OnPose(ProperKungFuPoseAnimationEvent ev)
    {
        var uid = GetEntity(ev.Target);
        if (!uid.IsValid() || TerminatingOrDeleted(uid) || !HasComp<SpriteComponent>(uid))
            return;

        var player = EnsureComp<AnimationPlayerComponent>(uid);
        if (_animation.HasRunningAnimation(player, PoseKey))
            _animation.Stop((uid, player), PoseKey);

        var rest = CompOrNull<RotationVisualsComponent>(uid)?.VerticalRotation ?? Angle.Zero;
        _animation.Play((uid, player), PoseAnimation(ev.Pose, rest, ev.Duration, ev.Height, ev.Spins), PoseKey);
    }

    public static RobustAnimation PoseAnimation(ProperKungFuPose pose, Angle rest, float duration, float height, float spins)
    {
        var length = MathF.Max(duration, 0.1f);
        var offset = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Offset),
            InterpolationMode = AnimationInterpolationMode.Cubic,
        };
        var rotation = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Rotation),
            InterpolationMode = AnimationInterpolationMode.Linear,
        };
        rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest, 0f));

        switch (pose)
        {
            case ProperKungFuPose.Jump:
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(new Vector2(0f, height), length * 0.45f));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.55f));
                rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest, length));
                break;
            case ProperKungFuPose.Crouch:
                // Low and leaning forward, then back up.
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(new Vector2(0f, -height), length * 0.15f));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(new Vector2(0f, -height), length * 0.65f));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.2f));
                rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(-12), length * 0.15f));
                rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(-12), length * 0.65f));
                rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest, length * 0.2f));
                break;
            case ProperKungFuPose.SlowFall:
            case ProperKungFuPose.Launch:
            {
                // Up fast, then a slow spinning fall (SlowFall), or a quick tumbling arc (Launch).
                var top = pose == ProperKungFuPose.SlowFall ? 0.2f : 0.35f;
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(new Vector2(0f, height), length * top));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(new Vector2(0f, height * 0.6f), length * (0.85f - top) * 0.5f));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(new Vector2(0f, height * 0.2f), length * (0.85f - top) * 0.5f));
                offset.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.15f));
                var steps = Math.Max(1, (int) MathF.Round(spins * 4f));
                for (var i = 1; i <= steps; i++)
                {
                    rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(90 * i), length / steps));
                }

                break;
            }
        }

        return new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length),
            AnimationTracks = { offset, rotation },
        };
    }

    // ---- Claw Mastery look ----

    private void OnKungFuShutdown(Entity<ProperKungFuComponent> ent, ref ComponentShutdown args)
    {
        RemoveLook(ent);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var now = _timing.CurTime;
        var real = _timing.RealTime;
        var pulse = 0.5f + 0.5f * MathF.Sin((float) real.TotalSeconds * 5f);

        var query = EntityQueryEnumerator<ProperKungFuComponent, SpriteComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var kungFu, out var sprite, out var xform))
        {
            if (!kungFu.UltimateActive)
            {
                if (_looks.ContainsKey(uid))
                    RemoveLook(uid);
                continue;
            }

            if (!_looks.TryGetValue(uid, out var look))
            {
                look = AddLook(uid, sprite);
                _looks[uid] = look;
            }

            // Pulsing crimson outline (left alone while something else, like the hover outline, uses PostShader).
            look.Outline.SetParameter("outline_color", Color.FromHex("#C0102A").WithAlpha(0.45f + 0.5f * pulse));
            look.Outline.SetParameter("outline_width", 1.2f + 0.8f * pulse);
            if (sprite.PostShader == null)
                sprite.PostShader = look.Outline;

            // Short red streaks behind the eyes while moving.
            var pos = _transform.GetWorldPosition(xform);
            var moved = (pos - look.LastPos).Length();
            look.LastPos = pos;
            if (moved > 0.02f && moved < 2f && real >= look.NextStreak && xform.MapID != MapId.Nullspace)
            {
                look.NextStreak = real + TimeSpan.FromSeconds(0.04);
                var up = (-_eye.CurrentEye.Rotation).RotateVec(new Vector2(0f, 0.22f));
                var streak = Spawn(EyeStreakProto, new MapCoordinates(pos + up, xform.MapID));
                _streaks.Add((streak, real + TimeSpan.FromSeconds(0.35)));
            }
        }

        for (var i = _streaks.Count - 1; i >= 0; i--)
        {
            if (real < _streaks[i].End)
                continue;

            if (!TerminatingOrDeleted(_streaks[i].Uid))
                QueueDel(_streaks[i].Uid);
            _streaks.RemoveAt(i);
        }

        foreach (var uid in new List<EntityUid>(_looks.Keys))
        {
            if (TerminatingOrDeleted(uid))
                RemoveLook(uid);
        }

        if (_overlayMan.HasOverlay<ProperKungFuGazeOverlay>() && now > _gaze.Start + TimeSpan.FromSeconds(_gaze.Duration + 0.3f))
        {
            _overlayMan.RemoveOverlay(_gaze);
            _overlayMan.RemoveOverlay(_gazeText);
        }
    }

    private UltLook AddLook(EntityUid uid, SpriteComponent sprite)
    {
        var look = new UltLook
        {
            Outline = _proto.Index(OutlineShader).InstanceUnique(),
            LastPos = _transform.GetWorldPosition(uid),
        };

        // Red glowing copy of the eyes layer, drawn unshaded above the real eyes.
        if (_sprite.LayerMapTryGet((uid, sprite), HumanoidVisualLayers.Eyes, out var eyes, false)
            && sprite[eyes] is SpriteComponent.Layer { ActualRsi: { } rsi } eyeLayer
            && eyeLayer.State.Name is { } state)
        {
            var index = _sprite.AddLayer((uid, sprite), new SpriteSpecifier.Rsi(rsi.Path, state), eyes + 1);
            _sprite.LayerMapSet((uid, sprite), EyeGlowKey, index);
            sprite.LayerSetShader(index, "unshaded");
            _sprite.LayerSetColor((uid, sprite), index, EyeColor);
            look.EyeLayer = true;
        }

        look.Light = Spawn(EyeLightProto, new EntityCoordinates(uid, new Vector2(0f, 0.25f)));
        return look;
    }

    private void RemoveLook(EntityUid uid)
    {
        if (!_looks.Remove(uid, out var look))
            return;

        if (look.Light is { } light && !TerminatingOrDeleted(light))
            QueueDel(light);

        if (!TryComp<SpriteComponent>(uid, out var sprite))
            return;

        if (sprite.PostShader == look.Outline)
            sprite.PostShader = null;

        if (look.EyeLayer && _sprite.LayerMapTryGet((uid, sprite), EyeGlowKey, out var index, false))
        {
            _sprite.RemoveLayer((uid, sprite), index);
            _sprite.LayerMapRemove((uid, sprite), EyeGlowKey);
        }
    }

    /// <summary>Whether the Claw Mastery look is applied to the entity (for tests and debugging).</summary>
    public bool HasUltLook(EntityUid uid) => _looks.ContainsKey(uid);
}

/// <summary>
/// Draws every <see cref="ProperKungFuTintZoneComponent"/> on screen: grey for the time stop, crimson and noir for
/// Wolf Judgment, fading in and out.
/// </summary>
public sealed class ProperKungFuTintOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> Shader = "ProperKungFuTint";

    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool RequestScreenTexture => true;

    private readonly ShaderInstance _shader;

    public ProperKungFuTintOverlay()
    {
        IoCManager.InjectDependencies(this);
        _shader = _prototypeManager.Index(Shader).InstanceUnique();
    }

    public static float Strength(ProperKungFuTintZoneComponent zone, TimeSpan now)
    {
        var since = (float) (now - zone.Start).TotalSeconds;
        var left = (float) (zone.End - now).TotalSeconds;
        if (since < 0f || left <= 0f)
            return 0f;

        var fadeIn = zone.FadeIn > 0f ? Math.Clamp(since / zone.FadeIn, 0f, 1f) : 1f;
        var fadeOut = zone.FadeOut > 0f ? Math.Clamp(left / zone.FadeOut, 0f, 1f) : 1f;
        return MathF.Min(fadeIn, fadeOut);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        var query = _entityManager.EntityQueryEnumerator<ProperKungFuTintZoneComponent>();
        return query.MoveNext(out _, out _);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (ScreenTexture == null)
            return;

        var xformSys = _entityManager.System<SharedTransformSystem>();
        var now = _timing.CurTime;
        var handle = args.WorldHandle;
        var query = _entityManager.EntityQueryEnumerator<ProperKungFuTintZoneComponent, TransformComponent>();
        while (query.MoveNext(out _, out var zone, out var xform))
        {
            if (xform.MapID != args.MapId)
                continue;

            var strength = Strength(zone, now);
            if (strength <= 0f || zone.Mode == ProperKungFuTintMode.None)
                continue;

            var center = xformSys.GetWorldPosition(xform);
            var box = new Box2(center - new Vector2(zone.Radius), center + new Vector2(zone.Radius));
            if (!box.Intersects(args.WorldAABB))
                continue;

            var local = args.Viewport.WorldToLocal(center);
            var edge = args.Viewport.WorldToLocal(center + new Vector2(zone.Radius, 0f));
            // The circle grows out from the centre while it fades in.
            var since = (float) (now - zone.Start).TotalSeconds;
            var grow = zone.FadeIn > 0f ? Math.Clamp(since / zone.FadeIn, 0f, 1f) : 1f;
            grow = 1f - (1f - grow) * (1f - grow);
            var radius = (edge - local).Length() * (0.1f + 0.9f * grow);
            local.Y = args.Viewport.Size.Y - local.Y;

            _shader.SetParameter("SCREEN_TEXTURE", ScreenTexture);
            _shader.SetParameter("Center", local);
            _shader.SetParameter("Radius", radius);
            _shader.SetParameter("Strength", strength);
            _shader.SetParameter("Mode", (float) (int) zone.Mode);
            handle.UseShader(_shader);
            handle.DrawRect(box.Intersect(args.WorldAABB), Color.White);
            handle.UseShader(null);
        }
    }
}

/// <summary>
/// The Wolf Gaze cutscene for both people in it: slow zoom and a dark heartbeat vignette for the tension,
/// then a short grey freeze-frame.
/// </summary>
public sealed class ProperKungFuGazeOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> Shader = "ProperKungFuGaze";

    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool RequestScreenTexture => true;

    private readonly ShaderInstance _shader;

    public TimeSpan Start;
    public float Duration = 3.7f;
    public float TextAt = 3f;

    public ProperKungFuGazeOverlay()
    {
        IoCManager.InjectDependencies(this);
        _shader = _prototypeManager.Index(Shader).InstanceUnique();
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (!_entityManager.TryGetComponent(_playerManager.LocalEntity, out EyeComponent? eyeComp))
            return false;

        return args.Viewport.Eye == eyeComp.Eye;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (ScreenTexture == null)
            return;

        var t = (float) (_timing.CurTime - Start).TotalSeconds;
        float strength, zoom, grey;
        if (t < TextAt)
        {
            var k = Math.Clamp(t / MathF.Max(0.1f, TextAt), 0f, 1f);
            strength = MathF.Min(1f, t / 0.4f);
            zoom = 1f + 0.18f * k * k;
            grey = 0f;
        }
        else
        {
            var k = Math.Clamp((t - TextAt) / MathF.Max(0.1f, Duration - TextAt), 0f, 1f);
            strength = 1f - k;
            zoom = 1f + 0.18f * (1f - k);
            grey = t < Duration ? 1f : 0f;
        }

        var beat = (t % 0.75f) / 0.75f;
        var pulse = MathF.Max(0f, 1f - beat * 4f);

        _shader.SetParameter("SCREEN_TEXTURE", ScreenTexture);
        _shader.SetParameter("Strength", Math.Clamp(strength, 0f, 1f));
        _shader.SetParameter("Zoom", zoom);
        _shader.SetParameter("Pulse", pulse);
        _shader.SetParameter("Grey", grey);
        var handle = args.WorldHandle;
        handle.UseShader(_shader);
        handle.DrawRect(args.WorldBounds, Color.White);
        handle.UseShader(null);
    }
}

/// <summary>
/// The yellow "THIS POWE-" line, shown only to the one who was caught, from the strike to the end of the freeze-frame.
/// </summary>
public sealed class ProperKungFuGazeTextOverlay : Overlay
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IResourceCache _cache = default!;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    private readonly Font _font;

    public TimeSpan Start;
    public float TextAt = 3f;
    public float Duration = 3.7f;
    public bool Show;

    public ProperKungFuGazeTextOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(_cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 30);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!Show)
            return;

        var t = (float) (_timing.CurTime - Start).TotalSeconds;
        if (t < TextAt || t > Duration + 0.3f)
            return;

        var alpha = Math.Clamp((Duration + 0.3f - t) / 0.3f, 0f, 1f);
        var text = Loc.GetString("proper-kung-fu-wolf-gaze-this-power");
        var handle = args.ScreenHandle;
        var size = handle.GetDimensions(_font, text, 1f);
        var pos = new Vector2(args.ViewportBounds.Center.X - size.X / 2f, args.ViewportBounds.Bottom - args.ViewportBounds.Height * 0.28f);
        handle.DrawString(_font, pos + new Vector2(2f, 2f), text, 1f, Color.Black.WithAlpha(alpha * 0.85f));
        handle.DrawString(_font, pos, text, 1f, Color.FromHex("#FFE030").WithAlpha(alpha));
    }
}
