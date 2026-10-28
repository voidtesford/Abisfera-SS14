using System.Linq;
using System.Numerics;
using Content.Client.Inventory;
using Content.Shared._CorvaxGoob.Gilbert.Finisher;
using Content.Shared.Humanoid;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared.ContentPack;
using Robust.Shared.Graphics;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using Color = Robust.Shared.Maths.Color;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._CorvaxGoob.Gilbert.Finisher;

/// <summary>
/// Runtime "puppet" for side-facing humanoids: the visible layers are composited into one texture per limb group (see
/// <see cref="GilbertFinisherPuppet"/>), the originals are hidden for the duration of the cutscene and the group layers
/// are animated about mask-derived joint pivots with whole-pixel offsets. Layers that cannot be baked (animated,
/// shader-driven, scaled, raw textures) stay as they are and follow the group under most of their pixels.
/// </summary>
public sealed partial class GilbertFinisherVisualsSystem
{
    [Dependency] private readonly IClyde _clyde = default!;
    [Dependency] private readonly IResourceManager _res = default!;

    public const string PuppetKeyPrefix = "gilbert-finisher-";

    /// <summary>
    /// Baked puppet segments: sRGB like every RSI texture (the pixels are copied straight from the RSI frames), only
    /// point sampled. A plain <c>new TextureLoadParameters { .. }</c> leaves <see cref="TextureLoadParameters.Srgb"/>
    /// false, and then the GPU reads the sRGB bytes as linear: every colour comes out washed towards grey, so the dark
    /// clothing turns into a pale, see-through-looking ghost during the finisher.
    /// </summary>
    public static readonly TextureLoadParameters PuppetTextureParameters = new()
    {
        Srgb = true,
        SampleParameters = TextureSampleParameters.Default with { Filter = false },
    };

    private readonly Dictionary<(ResPath, string), Image<Rgba32>?> _pngCache = new();

    private sealed class PuppetData
    {
        public int Signature;
        public required GilbertPuppetResult Result;
        public readonly List<(GilbertPuppetGroup Group, string Key, SpriteComponent.Layer Layer, OwnedTexture Texture)> Layers = new();
        public readonly Dictionary<SpriteComponent.Layer, (RSI? Rsi, string? State, bool WantsVisible)> Hidden = new();
        public readonly Dictionary<SpriteComponent.Layer, GilbertPuppetGroup> Leftovers = new();
    }

    private static GilbertPuppetGroup GroupOf(GilbertFinisherPart part) => part switch
    {
        GilbertFinisherPart.Head => GilbertPuppetGroup.Head,
        GilbertFinisherPart.ArmNear => GilbertPuppetGroup.ArmNear,
        GilbertFinisherPart.ArmFar => GilbertPuppetGroup.ArmFar,
        GilbertFinisherPart.LegNear => GilbertPuppetGroup.LegNear,
        GilbertFinisherPart.LegFar => GilbertPuppetGroup.LegFar,
        GilbertFinisherPart.ArmNearLow => GilbertPuppetGroup.ArmNearLow,
        GilbertFinisherPart.ArmFarLow => GilbertPuppetGroup.ArmFarLow,
        GilbertFinisherPart.LegNearLow => GilbertPuppetGroup.LegNearLow,
        GilbertFinisherPart.LegFarLow => GilbertPuppetGroup.LegFarLow,
        GilbertFinisherPart.ArmNearHand => GilbertPuppetGroup.ArmNearHand,
        GilbertFinisherPart.ArmFarHand => GilbertPuppetGroup.ArmFarHand,
        GilbertFinisherPart.LegNearFoot => GilbertPuppetGroup.LegNearFoot,
        GilbertFinisherPart.LegFarFoot => GilbertPuppetGroup.LegFarFoot,
        GilbertFinisherPart.HeldNear => GilbertPuppetGroup.HeldNear,
        GilbertFinisherPart.HeldFar => GilbertPuppetGroup.HeldFar,
        _ => GilbertPuppetGroup.Torso,
    };

    private static GilbertFinisherPart? PartOf(GilbertPuppetGroup group) => group switch
    {
        GilbertPuppetGroup.Head => GilbertFinisherPart.Head,
        GilbertPuppetGroup.ArmNear => GilbertFinisherPart.ArmNear,
        GilbertPuppetGroup.ArmFar => GilbertFinisherPart.ArmFar,
        GilbertPuppetGroup.LegNear => GilbertFinisherPart.LegNear,
        GilbertPuppetGroup.LegFar => GilbertFinisherPart.LegFar,
        GilbertPuppetGroup.ArmNearLow => GilbertFinisherPart.ArmNearLow,
        GilbertPuppetGroup.ArmFarLow => GilbertFinisherPart.ArmFarLow,
        GilbertPuppetGroup.LegNearLow => GilbertFinisherPart.LegNearLow,
        GilbertPuppetGroup.LegFarLow => GilbertFinisherPart.LegFarLow,
        GilbertPuppetGroup.ArmNearHand => GilbertFinisherPart.ArmNearHand,
        GilbertPuppetGroup.ArmFarHand => GilbertFinisherPart.ArmFarHand,
        GilbertPuppetGroup.LegNearFoot => GilbertFinisherPart.LegNearFoot,
        GilbertPuppetGroup.LegFarFoot => GilbertFinisherPart.LegFarFoot,
        GilbertPuppetGroup.HeldNear => GilbertFinisherPart.HeldNear,
        GilbertPuppetGroup.HeldFar => GilbertFinisherPart.HeldFar,
        _ => null,
    };

    /// <summary>Mask pivots are measured on screen; converted to the forward frame (x mirrored when facing left).</summary>
    private static Dictionary<GilbertFinisherPart, Vector2> ForwardPivots(PuppetData puppet, float sx)
    {
        var res = new Dictionary<GilbertFinisherPart, Vector2>();
        foreach (var (group, p) in puppet.Result.Pivots)
            if (PartOf(group) is { } part)
                res[part] = new Vector2(sx * p.X, p.Y);
        return res;
    }

    /// <summary>Base 4-dir RSI direction the sprite is currently drawn with.</summary>
    private RsiDirection DisplayDir(Entity<SpriteComponent> ent, RsiDirectionType type = RsiDirectionType.Dir4)
    {
        if (ent.Comp.EnableDirectionOverride)
            return ent.Comp.DirectionOverride.Convert(type);

        var angle = (_transform.GetWorldRotation(ent.Owner) + _eye.CurrentEye.Rotation).Reduced().FlipPositive();
        return SpriteComponent.Layer.GetDirection(type, angle);
    }

    private RsiDirection LayerDir(Entity<SpriteComponent> ent, SpriteComponent.Layer layer, RSI.State state)
    {
        var dir = state.RsiDirections == RsiDirectionType.Dir1 ? RsiDirection.South : DisplayDir(ent, state.RsiDirections);
        if (ent.Comp.EnableDirectionOverride)
            dir = ent.Comp.DirectionOverride.Convert(state.RsiDirections);
        return dir.OffsetRsiDir(layer.DirOffset);
    }

    private int PuppetSignature(Entity<SpriteComponent> ent, PlayState state, RsiDirection dir)
    {
        var hc = new HashCode();
        hc.Add(dir);
        var puppet = state.Puppet;
        foreach (var l in ent.Comp.AllLayers)
        {
            if (l is not SpriteComponent.Layer layer)
                continue;
            if (puppet != null && puppet.Layers.Any(p => p.Layer == layer))
                continue;
            if (state.Props.Any(p => p.Layer == layer))
                continue;

            // Visibility of shader / unshaded layers (blinking eyes, glows) never changes the bake.
            var plain = layer.ShaderPrototype == null && layer.Shader == null;
            var hidden = puppet != null && puppet.Hidden.ContainsKey(layer);
            hc.Add(plain && (layer.Visible || hidden));
            hc.Add(layer.ActualRsi?.Path);
            hc.Add(layer.ActualState?.StateId.Name);
            hc.Add(layer.Color);
            hc.Add(state.Layers.TryGetValue(layer, out var orig) ? orig.Offset : layer.Offset);
        }

        return hc.ToHashCode();
    }

    /// <summary>Keeps the puppet in sync with the sprite; null when this sprite cannot be puppeted (use the fallback).</summary>
    /// <param name="holster">RSI states to draw held items with on the back, or null to keep them in the hands.</param>
    /// <param name="keepInHand">With <paramref name="holster"/>: also bake the in-hand frames (shown until the holster swing ends).</param>
    /// <param name="forcedNearRight">A forced (e.g. back) view: puppet the vertical direction too, with this near side.</param>
    private PuppetData? UpdatePuppet(Entity<SpriteComponent> ent, PlayState state, List<string>? holster, bool keepInHand,
        bool? forcedNearRight)
    {
        if (!ent.Comp.NoRotation)
        {
            TearDownPuppet(ent, state);
            return null;
        }

        var dir = DisplayDir(ent);
        var sig = PuppetSignature(ent, state, dir);
        if (state.Puppet is { } current && current.Signature == sig)
        {
            RehideOriginals(ent, current);
            return current;
        }

        if (state.Puppet == null && state.FailedPuppetSignature == sig)
            return null;

        TearDownPuppet(ent, state);
        RestoreLayerTransforms(ent, state);

        bool? nearRight = dir is RsiDirection.East or RsiDirection.West ? dir == RsiDirection.East : forcedNearRight;
        if (nearRight is { } near)
        {
            try
            {
                state.Puppet = BuildPuppet(ent, state, near, holster, keepInHand);
            }
            catch (Exception e)
            {
                Log.Warning($"Finisher puppet failed for {ToPrettyString(ent)}: {e}");
                TearDownPuppet(ent, state);
            }
        }

        if (state.Puppet == null)
        {
            state.FailedPuppetSignature = sig;
            return null;
        }

        // Signature after the bake (puppet layers excluded, hidden layers count as visible: unchanged).
        state.Puppet.Signature = PuppetSignature(ent, state, dir);
        return state.Puppet;
    }

    private void RehideOriginals(Entity<SpriteComponent> ent, PuppetData puppet)
    {
        foreach (var (layer, info) in puppet.Hidden.ToList())
        {
            if (!layer.Visible)
                continue;
            // Something (re)enabled the layer while it was baked: honour that on teardown.
            puppet.Hidden[layer] = info with { WantsVisible = true };
            _sprite.LayerSetVisible(layer, false);
        }
    }

    private void RestoreLayerTransforms(Entity<SpriteComponent> ent, PlayState state)
    {
        var alive = new HashSet<ISpriteLayer>(ent.Comp.AllLayers);
        foreach (var (layer, orig) in state.Layers)
        {
            if (!alive.Contains(layer))
                continue;
            _sprite.LayerSetOffset(layer, orig.Offset);
            _sprite.LayerSetRotation(layer, orig.Rotation);
        }

        state.Layers.Clear();
    }

    private void TearDownPuppet(Entity<SpriteComponent> ent, PlayState state)
    {
        if (state.Puppet is not { } puppet)
            return;

        state.Puppet = null;
        foreach (var (_, key, _, texture) in puppet.Layers)
        {
            _sprite.RemoveLayer(ent.AsNullable(), key, false);
            texture.Dispose();
        }

        var alive = new HashSet<ISpriteLayer>(ent.Comp.AllLayers);
        foreach (var (layer, info) in puppet.Hidden)
        {
            if (!alive.Contains(layer))
                continue;
            if (info.WantsVisible || (layer.ActualRsi == info.Rsi && layer.ActualState?.StateId.Name == info.State))
                _sprite.LayerSetVisible(layer, true);
        }

        // Body layers the humanoid hid meanwhile (limbs torn off during the cutscene) stay hidden.
        if (TryComp<HumanoidAppearanceComponent>(ent, out var humanoid))
        {
            foreach (var key in humanoid.HiddenLayers.Keys.Concat(humanoid.PermanentlyHidden))
            {
                if (_sprite.LayerMapTryGet(ent.AsNullable(), key, out var index, false))
                    _sprite.LayerSetVisible(ent.AsNullable(), index, false);
            }
        }

        foreach (var layer in puppet.Leftovers.Keys)
        {
            if (!state.Layers.Remove(layer, out var orig) || !alive.Contains(layer))
                continue;
            _sprite.LayerSetOffset(layer, orig.Offset);
            _sprite.LayerSetRotation(layer, orig.Rotation);
        }
    }

    private Image<Rgba32>? LoadPng(RSI rsi, RSI.State st)
    {
        var key = (rsi.Path, st.StateId.Name ?? string.Empty);
        if (_pngCache.TryGetValue(key, out var cached))
            return cached;

        Image<Rgba32>? image = null;
        try
        {
            using var stream = _res.ContentFileRead(rsi.Path / $"{key.Item2}.png");
            image = Image.Load<Rgba32>(stream);
        }
        catch (Exception e)
        {
            Log.Debug($"Finisher puppet: cannot read {rsi.Path}/{key.Item2}.png: {e.Message}");
        }

        _pngCache[key] = image;
        return image;
    }

    /// <summary>Displayed frame of a layer as raw RGBA (w x h), or null.</summary>
    private (byte[] Pixels, int W, int H)? LayerFrame(Entity<SpriteComponent> ent, SpriteComponent.Layer layer,
        RSI.State? stateOverride = null)
    {
        if (layer.ActualRsi is not { } rsi || (stateOverride ?? layer.ActualState) is not { } st || LoadPng(rsi, st) is not { } png)
            return null;

        var dir = LayerDir(ent, layer, st);
        var w = st.Size.X;
        var h = st.Size.Y;
        var frames = Math.Max(1, st.DelayCount);
        var dirIndex = st.RsiDirections == RsiDirectionType.Dir1 ? 0 : (int) dir;
        var idx = dirIndex * frames + Math.Clamp(layer.AnimationFrame, 0, frames - 1);
        var cols = Math.Max(1, png.Width / w);
        var fx = idx % cols * w;
        var fy = idx / cols * h;
        if (fx + w > png.Width || fy + h > png.Height)
            return null;

        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = png[fx + x, fy + y];
            var i = (y * w + x) * 4;
            pixels[i] = p.R; pixels[i + 1] = p.G; pixels[i + 2] = p.B; pixels[i + 3] = p.A;
        }

        return (pixels, w, h);
    }

    private static byte[] Place((byte[] Pixels, int W, int H) frame, Vector2 offset, Color color, int c)
    {
        var dst = new byte[c * c * 4];
        var x0 = (c - frame.W) / 2 + (int) MathF.Round(offset.X * PixelsPerMeter);
        var y0 = (c - frame.H) / 2 - (int) MathF.Round(offset.Y * PixelsPerMeter);
        for (var y = 0; y < frame.H; y++)
        for (var x = 0; x < frame.W; x++)
        {
            int dx = x0 + x, dy = y0 + y;
            if (dx < 0 || dy < 0 || dx >= c || dy >= c)
                continue;
            var s = (y * frame.W + x) * 4;
            var d = (dy * c + dx) * 4;
            dst[d] = (byte) Math.Clamp(MathF.Round(frame.Pixels[s] * color.R), 0, 255);
            dst[d + 1] = (byte) Math.Clamp(MathF.Round(frame.Pixels[s + 1] * color.G), 0, 255);
            dst[d + 2] = (byte) Math.Clamp(MathF.Round(frame.Pixels[s + 2] * color.B), 0, 255);
            dst[d + 3] = (byte) Math.Clamp(MathF.Round(frame.Pixels[s + 3] * color.A), 0, 255);
        }

        return dst;
    }

    /// <summary>CPU version of the displacement shader: out(x, y) = src(x + R - 128, y + G - 128), alpha x map alpha.</summary>
    private static byte[] Displace(byte[] src, byte[] map, int c)
    {
        var dst = new byte[src.Length];
        for (var y = 0; y < c; y++)
        for (var x = 0; x < c; x++)
        {
            var m = (y * c + x) * 4;
            if (map[m + 3] == 0)
                continue;
            var sx = x + map[m] - 128;
            var sy = y + map[m + 1] - 128;
            if (sx < 0 || sy < 0 || sx >= c || sy >= c)
                continue;
            var s = (sy * c + sx) * 4;
            dst[m] = src[s];
            dst[m + 1] = src[s + 1];
            dst[m + 2] = src[s + 2];
            dst[m + 3] = (byte) MathF.Round(src[s + 3] * map[m + 3] / 255f);
        }

        return dst;
    }

    private bool TryIndex(Entity<SpriteComponent> ent, object key, out int index)
    {
        index = -1;
        return key switch
        {
            Enum e => _sprite.LayerMapTryGet(ent.AsNullable(), e, out index, false),
            string s => _sprite.LayerMapTryGet(ent.AsNullable(), s, out index, false),
            _ => false,
        };
    }

    private PuppetData? BuildPuppet(Entity<SpriteComponent> ent, PlayState state, bool nearRight, List<string>? holster,
        bool keepInHand)
    {
        var sprite = ent.Comp;
        var all = sprite.AllLayers.OfType<SpriteComponent.Layer>().ToList();

        // index -> classification key
        var keys = new Dictionary<int, string>();
        foreach (var v in Enum.GetValues<HumanoidVisualLayers>())
            if (_sprite.LayerMapTryGet(ent.AsNullable(), v, out var idx, false))
                keys.TryAdd(idx, v.ToString());
        if (TryComp<InventorySlotsComponent>(ent, out var slots))
        {
            foreach (var (slot, slotKeys) in slots.VisualLayerKeys)
            foreach (var key in slotKeys)
                if (_sprite.LayerMapTryGet(ent.AsNullable(), key, out var idx, false))
                    keys.TryAdd(idx, $"{slot}-{key}");
        }
        foreach (var side in new[] { "inhand-left", "inhand-right" })
        {
            if (_sprite.LayerMapTryGet(ent.AsNullable(), side, out var idx, false))
                keys.TryAdd(idx, side);
            for (var i = 0; i < 4; i++)
                if (_sprite.LayerMapTryGet(ent.AsNullable(), $"{side}-{i}", out idx, false))
                    keys.TryAdd(idx, $"{side}-{i}");
        }

        // displacement target index -> map layer
        var displacement = new Dictionary<int, SpriteComponent.Layer>();
        foreach (var layer in all)
            if (layer.CopyToShaderParameters is { } copy && TryIndex(ent, copy.LayerKey, out var target))
                displacement[target] = layer;

        var baked = new List<(int Index, SpriteComponent.Layer Layer, (byte[], int, int) Frame, string? Key)>();
        var leftovers = new List<(SpriteComponent.Layer Layer, (byte[], int, int)? Frame)>();
        var holstered = new List<SpriteComponent.Layer>();
        var maxSize = 32;
        var maxOff = 0f;
        for (var i = 0; i < all.Count; i++)
        {
            var layer = all[i];
            if (!layer.Visible || layer.CopyToShaderParameters != null)
                continue;
            if (state.Props.Any(p => p.Layer == layer))
                continue;
            if (layer.ActualState == null && layer.Texture == null)
                continue;

            // Holstered held item: baked from the item's on-back state (the Slung group, over the torso), else just
            // hidden; with keepInHand also from its in-hand frame (the Held groups, until the holster swing ends).
            if (holster != null && keys.TryGetValue(i, out var handKey) && handKey.StartsWith("inhand-"))
            {
                holstered.Add(layer);
                if (layer.ActualRsi is not { } handRsi)
                    continue;
                if (keepInHand && layer.ActualState is { IsAnimated: false } && LayerFrame(ent, layer) is { } handFrame)
                {
                    maxSize = Math.Max(maxSize, Math.Max(handFrame.W, handFrame.H));
                    baked.Add((i, layer, handFrame, handKey));
                }

                foreach (var stateName in holster)
                {
                    if (!handRsi.TryGetState(stateName, out var backState)
                        || LayerFrame(ent, layer, backState) is not { } backFrame)
                        continue;
                    maxSize = Math.Max(maxSize, Math.Max(backFrame.W, backFrame.H));
                    baked.Add((i, layer, backFrame, "back-slung"));
                    break;
                }

                continue;
            }

            var plain = layer.ShaderPrototype == null && layer.Shader == null || displacement.ContainsKey(i);
            var eligible = plain
                           && layer.ActualState is { IsAnimated: false }
                           && layer.Scale == Vector2.One
                           && layer.Rotation == Angle.Zero
                           && layer.RenderingStrategy == LayerRenderingStrategy.UseSpriteStrategy;
            var frame = LayerFrame(ent, layer);
            if (frame is { } f)
            {
                maxSize = Math.Max(maxSize, Math.Max(f.W, f.H));
                maxOff = Math.Max(maxOff, Math.Max(MathF.Abs(layer.Offset.X), MathF.Abs(layer.Offset.Y)) * PixelsPerMeter);
            }

            if (eligible && frame is { } ef)
                baked.Add((i, layer, ef, keys.GetValueOrDefault(i)));
            else
                leftovers.Add((layer, frame));
        }

        if (!baked.Any(b => b.Key == nameof(HumanoidVisualLayers.Chest)))
            return null;

        var c = maxSize + 2 * (int) MathF.Ceiling(maxOff);
        c += c % 2;

        var inputs = new List<GilbertPuppetInput>();
        foreach (var (index, layer, frame, bakeKey) in baked)
        {
            var pixels = Place(frame, layer.Offset, layer.Color, c);
            if (displacement.TryGetValue(index, out var mapLayer) && LayerFrame(ent, mapLayer) is { } mapFrame)
                pixels = Displace(pixels, Place(mapFrame, mapLayer.Offset, Color.White, c), c);
            inputs.Add(new GilbertPuppetInput(bakeKey, pixels));
        }

        var result = GilbertFinisherPuppet.Build(inputs, c, nearRight);
        var puppet = new PuppetData { Result = result };

        var insertAt = 0;
        for (var g = 0; g < GilbertFinisherPuppet.GroupCount; g++)
        {
            var group = (GilbertPuppetGroup) g;
            if (result.IsEmpty(group))
                continue;

            var src = result.Images[g];
            using var image = new Image<Rgba32>(c, c);
            for (var y = 0; y < c; y++)
            for (var x = 0; x < c; x++)
            {
                var i = (y * c + x) * 4;
                image[x, y] = new Rgba32(src[i], src[i + 1], src[i + 2], src[i + 3]);
            }

            var key = PuppetKeyPrefix + group;
            var texture = _clyde.LoadTextureFromImage(image, key, PuppetTextureParameters);
            var index = insertAt++;
            var layer = _sprite.AddBlankLayer(ent, index);
            _sprite.LayerSetTexture(layer, texture);
            _sprite.LayerMapSet(ent.AsNullable(), key, index);
            puppet.Layers.Add((group, key, layer, texture));
        }

        foreach (var layer in baked.Select(b => b.Layer).Concat(holstered).Distinct())
        {
            puppet.Hidden[layer] = (layer.ActualRsi, layer.ActualState?.StateId.Name, false);
            _sprite.LayerSetVisible(layer, false);
        }

        foreach (var (layer, frame) in leftovers)
        {
            var group = frame is { } f
                ? GilbertFinisherPuppet.MajorityGroup(result, Place(f, layer.Offset, Color.White, c))
                : GilbertPuppetGroup.Torso;
            if (group is not (GilbertPuppetGroup.Torso or GilbertPuppetGroup.TorsoTop))
                puppet.Leftovers[layer] = group;
        }

        return puppet;
    }

    /// <summary>Applies one solved part pose to its puppet group (baked layer + leftovers), offsets snapped to whole pixels.</summary>
    private void AnimatePuppetPart(PuppetData puppet, PlayState state, GilbertFinisherPart part,
        GilbertFinisherPose pose, float sx, bool hidden = false)
    {
        var group = GroupOf(part);
        var offset = new Vector2(MathF.Round(sx * pose.X), MathF.Round(pose.Y)) / PixelsPerMeter;
        var rotation = Angle.FromDegrees(sx * pose.Rot);

        foreach (var (g, _, layer, _) in puppet.Layers)
        {
            if (g != group)
                continue;
            // Torn-off limb: the baked segment stops being drawn (the real body part is gone by then too).
            if (hidden)
                _sprite.LayerSetVisible(layer, false);
            _sprite.LayerSetOffset(layer, offset);
            _sprite.LayerSetRotation(layer, rotation);
        }

        foreach (var (layer, g) in puppet.Leftovers)
        {
            if (g != group)
                continue;
            if (!state.Layers.TryGetValue(layer, out var orig))
            {
                orig = (layer.Offset, layer.Rotation);
                state.Layers[layer] = orig;
            }

            _sprite.LayerSetOffset(layer, offset + rotation.RotateVec(orig.Offset));
            _sprite.LayerSetRotation(layer, orig.Rotation + rotation);
        }
    }

    /// <summary>For tests: the puppet layer of a group, if the entity is currently puppeted.</summary>
    public SpriteComponent.Layer? GetPuppetLayer(EntityUid uid, GilbertPuppetGroup group)
    {
        if (!_states.TryGetValue(uid, out var state) || state.Puppet is not { } puppet)
            return null;
        foreach (var (g, _, layer, _) in puppet.Layers)
            if (g == group)
                return layer;
        return null;
    }

    /// <summary>For tests / debugging: the current bake of a puppeted entity.</summary>
    public GilbertPuppetResult? GetPuppetResult(EntityUid uid)
    {
        return _states.TryGetValue(uid, out var state) ? state.Puppet?.Result : null;
    }

    /// <summary>For tests: original layers currently hidden by the puppet.</summary>
    public IReadOnlyCollection<SpriteComponent.Layer> GetPuppetHiddenLayers(EntityUid uid)
    {
        return _states.TryGetValue(uid, out var state) && state.Puppet is { } puppet
            ? puppet.Hidden.Keys.ToList()
            : Array.Empty<SpriteComponent.Layer>();
    }
}
