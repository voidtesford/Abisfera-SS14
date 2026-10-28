using System.Linq;
using System.Numerics;

namespace Content.Client._CorvaxGoob.Gilbert.Finisher;

/// <summary>Animated groups of the finisher puppet, in draw order (back to front).</summary>
/// <remarks>Limbs are two rigid segments: upper arm / forearm + hand (+ gloves), thigh / shin + shoe. The hand / foot groups stay empty.</remarks>
public enum GilbertPuppetGroup : byte
{
    ArmFar,
    ArmFarLow,
    ArmFarHand,
    /// <summary>Item in the far hand (carried by the hand until holstered).</summary>
    HeldFar,
    LegFar,
    LegFarLow,
    LegFarFoot,
    Torso,
    LegNear,
    LegNearLow,
    LegNearFoot,
    TorsoTop,
    /// <summary>Held items drawn holstered on the back (their on-back state).</summary>
    Slung,
    Head,
    ArmNear,
    ArmNearLow,
    ArmNearHand,
    HeldNear,
}

/// <summary>One visible sprite layer resolved to its displayed frame (RGBA, layer colour applied) on a C x C canvas.</summary>
public readonly record struct GilbertPuppetInput(string? Key, byte[] Pixels);

public sealed class GilbertPuppetResult
{
    public required int Size;
    /// <summary>Composited RGBA image per group (index = (int) group), C x C, y down.</summary>
    public required byte[][] Images;
    /// <summary>Mask-derived joint pivots in pixels, centred on the sprite origin, y up.</summary>
    public required Dictionary<GilbertPuppetGroup, Vector2> Pivots;
    public bool SynthesizedFarArm;
    public int FarArmShift;
    /// <summary>Per limb (ArmNear / ArmFar / LegNear / LegFar): rows of the joint, middle (elbow / knee), tip (wrist; legs: = End, no foot segment) cuts and the end.</summary>
    public Dictionary<GilbertPuppetGroup, (int Joint, int Mid, int Tip, int End)> Segments = new();

    public bool IsEmpty(GilbertPuppetGroup group)
    {
        var img = Images[(int) group];
        for (var i = 3; i < img.Length; i += 4)
            if (img[i] != 0)
                return false;
        return true;
    }
}

/// <summary>
/// Splits a side-facing humanoid into per-limb images so clothing moves with its limbs: whole-limb layers (body parts,
/// held items, head gear) go to their group; one-piece clothing is cut per pixel with the body-part masks; the far arm
/// (mostly hidden behind the torso in side view) is replaced by a darkened copy of the near arm so it reads as a solid
/// limb when swung out. Pure function, a 1:1 port of the preview reference <c>gilbert/finisher/puppet.py</c>.
/// </summary>
public static class GilbertFinisherPuppet
{
    public const int GroupCount = 18;
    public const float FarDarken = 0.82f;
    public const int NearestRadius = 3;

    private enum Kind : byte { Part, Whole, Gloves, Shoes, Split }

    private static readonly HashSet<string> HeadKeys = new() { "Head", "Snout", "Eyes", "HeadSide", "HeadTop", "Face", "Hair", "FacialHair" };
    private static readonly HashSet<string> HeadSlots = new() { "head", "mask", "maskalt", "eyes", "ears" };
    private static readonly HashSet<string> TopSlots = new() { "belt", "back", "neck", "id", "pocket1", "pocket2", "suitstorage" };

    private static readonly GilbertPuppetGroup[] SplitAllowed =
    {
        GilbertPuppetGroup.ArmNear, GilbertPuppetGroup.Head, GilbertPuppetGroup.LegNear,
        GilbertPuppetGroup.Torso, GilbertPuppetGroup.LegFar, GilbertPuppetGroup.ArmFar,
    };
    private static readonly GilbertPuppetGroup[] Arms = { GilbertPuppetGroup.ArmNear, GilbertPuppetGroup.ArmFar };
    private static readonly GilbertPuppetGroup[] Legs = { GilbertPuppetGroup.LegNear, GilbertPuppetGroup.LegFar };

    private static string? SlotOf(string? key) => key?.Split('-')[0];

    private static (Kind Kind, GilbertPuppetGroup Group) Classify(string? key, bool nearRight)
    {
        if (key is "Chest" or "Groin" or "Tail")
            return (Kind.Part, GilbertPuppetGroup.Torso);
        if (key == "Head")
            return (Kind.Part, GilbertPuppetGroup.Head);
        if (key == "back-slung")
            return (Kind.Whole, GilbertPuppetGroup.Slung);
        if (key != null && HeadKeys.Contains(key))
            return (Kind.Whole, GilbertPuppetGroup.Head);

        var n = nearRight ? "R" : "L";
        var f = nearRight ? "L" : "R";
        if (key == n + "Arm" || key == n + "Hand") return (Kind.Part, GilbertPuppetGroup.ArmNear);
        if (key == n + "Leg" || key == n + "Foot") return (Kind.Part, GilbertPuppetGroup.LegNear);
        if (key == f + "Arm" || key == f + "Hand") return (Kind.Part, GilbertPuppetGroup.ArmFar);
        if (key == f + "Leg" || key == f + "Foot") return (Kind.Part, GilbertPuppetGroup.LegFar);

        if (key != null && key.StartsWith("inhand-"))
        {
            var right = key.StartsWith("inhand-right");
            return (Kind.Whole, right == nearRight ? GilbertPuppetGroup.ArmNear : GilbertPuppetGroup.ArmFar);
        }

        var slot = SlotOf(key);
        if (slot != null && HeadSlots.Contains(slot)) return (Kind.Whole, GilbertPuppetGroup.Head);
        if (slot != null && TopSlots.Contains(slot)) return (Kind.Whole, GilbertPuppetGroup.TorsoTop);
        if (slot == "gloves") return (Kind.Gloves, default);
        if (slot == "shoes") return (Kind.Shoes, default);
        return (Kind.Split, default);
    }

    /// <summary>Alpha-over of <paramref name="src"/> onto <paramref name="dst"/>, optionally only where sel is set.</summary>
    private static void Over(byte[] dst, byte[] src, bool[]? sel = null)
    {
        var count = dst.Length / 4;
        for (var p = 0; p < count; p++)
        {
            if (sel != null && !sel[p])
                continue;
            var i = p * 4;
            var a = src[i + 3] / 255f;
            if (a <= 0f)
                continue;
            var da = dst[i + 3] / 255f;
            var oa = a + da * (1f - a);
            for (var c = 0; c < 3; c++)
            {
                var v = (src[i + c] * a + dst[i + c] * da * (1f - a)) / MathF.Max(oa, 1e-6f);
                dst[i + c] = (byte) Math.Clamp(MathF.Round(v), 0f, 255f);
            }
            dst[i + 3] = (byte) Math.Clamp(MathF.Round(oa * 255f), 0f, 255f);
        }
    }

    private static bool Any(bool[] m)
    {
        foreach (var b in m)
            if (b) return true;
        return false;
    }

    private static int Count(bool[] m)
    {
        var n = 0;
        foreach (var b in m)
            if (b) n++;
        return n;
    }

    /// <summary>(minY, maxY, minX, maxX) of a mask or null.</summary>
    private static (int MinY, int MaxY, int MinX, int MaxX)? BBox(bool[] m, int c)
    {
        int minY = int.MaxValue, maxY = -1, minX = int.MaxValue, maxX = -1;
        for (var y = 0; y < c; y++)
        for (var x = 0; x < c; x++)
        {
            if (!m[y * c + x])
                continue;
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
        }

        return maxY < 0 ? null : (minY, maxY, minX, maxX);
    }

    private static List<GilbertPuppetGroup> Label(bool[][] masks, GilbertPuppetGroup[] allowed, int y, int x, int c)
    {
        var hit = new List<GilbertPuppetGroup>();
        foreach (var g in allowed)
            if (masks[(int) g][y * c + x]) hit.Add(g);
        if (hit.Count > 0)
            return hit;

        int? best = null;
        for (var dy = -NearestRadius; dy <= NearestRadius; dy++)
        for (var dx = -NearestRadius; dx <= NearestRadius; dx++)
        {
            int yy = y + dy, xx = x + dx;
            if (yy < 0 || yy >= c || xx < 0 || xx >= c)
                continue;
            var d = dx * dx + dy * dy;
            if (d > NearestRadius * NearestRadius)
                continue;
            var gs = allowed.Where(g => masks[(int) g][yy * c + xx]).ToList();
            if (gs.Count == 0)
                continue;
            if (best == null || d < best)
            {
                best = d;
                hit = gs;
            }
            else if (d == best)
            {
                foreach (var g in gs)
                    if (!hit.Contains(g)) hit.Add(g);
            }
        }

        return hit;
    }

    /// <summary>
    /// Torso pixels hidden under the near arm get the colour of the nearest torso pixel outside the arm in the same row
    /// (else column). Silhouette pixels (opaque with a transparent 4-neighbour) are kept as the outline.
    /// </summary>
    private static void FillUnderArm(byte[] torso, bool[] arm, int c)
    {
        var op = new bool[c * c];
        for (var p = 0; p < op.Length; p++)
            op[p] = torso[p * 4 + 3] > 0;

        bool Boundary(int y, int x)
        {
            if (y - 1 < 0 || !op[(y - 1) * c + x]) return true;
            if (y + 1 >= c || !op[(y + 1) * c + x]) return true;
            if (x - 1 < 0 || !op[y * c + x - 1]) return true;
            if (x + 1 >= c || !op[y * c + x + 1]) return true;
            return false;
        }

        var src = (byte[]) torso.Clone();
        bool Ok(int y, int x) => op[y * c + x] && !arm[y * c + x] && !Boundary(y, x);

        for (var y = 0; y < c; y++)
        for (var x = 0; x < c; x++)
        {
            if (!(arm[y * c + x] && op[y * c + x]) || Boundary(y, x))
                continue;

            int by = -1, bx = -1;
            for (var d = 1; d < c && by < 0; d++)
            {
                if (x - d >= 0 && Ok(y, x - d)) { by = y; bx = x - d; }
                else if (x + d < c && Ok(y, x + d)) { by = y; bx = x + d; }
            }
            for (var d = 1; d < c && by < 0; d++)
            {
                if (y - d >= 0 && Ok(y - d, x)) { by = y - d; bx = x; }
                else if (y + d < c && Ok(y + d, x)) { by = y + d; bx = x; }
            }
            if (by < 0)
                continue;

            Array.Copy(src, (by * c + bx) * 4, torso, (y * c + x) * 4, 4);
        }
    }

    private static Vector2? TopJoint(bool[] m, int c)
    {
        if (BBox(m, c) is not { } b)
            return null;
        var y0 = b.MinY;
        float sum = 0; var n = 0;
        for (var x = 0; x < c; x++)
        {
            var any = m[y0 * c + x] || (y0 + 1 < c && m[(y0 + 1) * c + x]);
            if (!any) continue;
            sum += x; n++;
        }

        return new Vector2(sum / n + 0.5f - c / 2f, c / 2f - (y0 + 1));
    }

    /// <param name="layers">Visible layers in draw order, each a C x C RGBA canvas.</param>
    /// <param name="size">Canvas size C.</param>
    /// <param name="nearRight">True when the right side of the body faces the viewer (sprite facing east).</param>
    public static GilbertPuppetResult Build(IReadOnlyList<GilbertPuppetInput> layers, int size, bool nearRight)
    {
        var c = size;
        var px = c * c;
        var kinds = layers.Select(l => Classify(l.Key, nearRight)).ToArray();
        var masks = new bool[GroupCount][];
        for (var g = 0; g < GroupCount; g++) masks[g] = new bool[px];
        var hands = new[] { new bool[px], new bool[px] };    // ArmNear, ArmFar
        var armOnly = new[] { new bool[px], new bool[px] };
        var legOnly = new[] { new bool[px], new bool[px] };  // LegNear, LegFar
        var feet = new[] { new bool[px], new bool[px] };

        for (var i = 0; i < layers.Count; i++)
        {
            var (kind, g) = kinds[i];
            if (kind != Kind.Part)
                continue;
            var key = layers[i].Key ?? string.Empty;
            var img = layers[i].Pixels;
            var arm = g == GilbertPuppetGroup.ArmNear ? 0 : g == GilbertPuppetGroup.ArmFar ? 1 : -1;
            var leg = g == GilbertPuppetGroup.LegNear ? 0 : g == GilbertPuppetGroup.LegFar ? 1 : -1;
            for (var p = 0; p < px; p++)
            {
                if (img[p * 4 + 3] == 0) continue;
                masks[(int) g][p] = true;
                if (arm >= 0) (key.Contains("Hand") ? hands : armOnly)[arm][p] = true;
                if (leg >= 0) (key.Contains("Leg") ? legOnly : feet)[leg][p] = true;
            }
        }

        var nearN = Count(masks[(int) GilbertPuppetGroup.ArmNear]);
        var farN = Count(masks[(int) GilbertPuppetGroup.ArmFar]);
        var synth = nearN > 0 && farN < 0.6f * nearN;
        var shift = 0;
        if (synth && BBox(hands[1], c) is { } bf && BBox(hands[0], c) is { } bn)
            shift = nearRight ? bf.MaxX - bn.MaxX : bf.MinX - bn.MinX;

        var labelCache = new Dictionary<(GilbertPuppetGroup[], int), List<GilbertPuppetGroup>>();
        List<GilbertPuppetGroup> LabelOf(GilbertPuppetGroup[] allowed, GilbertPuppetGroup fallback, int p)
        {
            if (labelCache.TryGetValue((allowed, p), out var cached))
                return cached;
            var gs = Label(masks, allowed, p / c, p % c, c);
            if (gs.Count == 0) gs = new List<GilbertPuppetGroup> { fallback };
            labelCache[(allowed, p)] = gs;
            return gs;
        }

        var outImg = new byte[GroupCount][];
        for (var g = 0; g < GroupCount; g++) outImg[g] = new byte[px * 4];
        var nearBare = new byte[px * 4];
        // In-hand items get their own layer carried by the hand: ArmNear, ArmFar.
        var held = new[] { new byte[px * 4], new byte[px * 4] };
        // Glove pixels always ride on the hand segment.
        var glove = new[] { new bool[px], new bool[px] };

        for (var i = 0; i < layers.Count; i++)
        {
            var (kind, g) = kinds[i];
            var img = layers[i].Pixels;
            if (kind == Kind.Whole && g is GilbertPuppetGroup.ArmNear or GilbertPuppetGroup.ArmFar)
            {
                Over(held[g == GilbertPuppetGroup.ArmNear ? 0 : 1], img);
                continue;
            }

            if (kind is Kind.Part or Kind.Whole)
            {
                if (synth && g == GilbertPuppetGroup.ArmFar && kind == Kind.Part)
                    continue;
                Over(outImg[(int) g], img);
                if (g == GilbertPuppetGroup.ArmNear && kind == Kind.Part)
                    Over(nearBare, img);
                continue;
            }

            var (allowed, fallback) = kind switch
            {
                Kind.Gloves => (Arms, GilbertPuppetGroup.ArmNear),
                Kind.Shoes => (Legs, GilbertPuppetGroup.LegNear),
                _ => (SplitAllowed, GilbertPuppetGroup.Torso),
            };

            var sel = new bool[GroupCount][];
            for (var p = 0; p < px; p++)
            {
                if (img[p * 4 + 3] == 0) continue;
                foreach (var grp in LabelOf(allowed, fallback, p))
                {
                    if (synth && grp == GilbertPuppetGroup.ArmFar) continue;
                    (sel[(int) grp] ??= new bool[px])[p] = true;
                }
            }

            for (var grp = 0; grp < GroupCount; grp++)
            {
                if (sel[grp] == null) continue;
                Over(outImg[grp], img, sel[grp]);
                if (grp == (int) GilbertPuppetGroup.ArmNear)
                    Over(nearBare, img, sel[grp]);
                if (kind == Kind.Gloves && grp is (int) GilbertPuppetGroup.ArmNear or (int) GilbertPuppetGroup.ArmFar)
                {
                    var gm = glove[grp == (int) GilbertPuppetGroup.ArmNear ? 0 : 1];
                    for (var p = 0; p < px; p++)
                        gm[p] |= sel[grp][p];
                }
            }
        }

        FillUnderArm(outImg[(int) GilbertPuppetGroup.Torso], masks[(int) GilbertPuppetGroup.ArmNear], c);

        if (synth)
        {
            var sh = new byte[px * 4];
            for (var y = 0; y < c; y++)
            for (var x = 0; x < c; x++)
            {
                var sx = x - shift;
                if (sx < 0 || sx >= c) continue;
                var s = (y * c + sx) * 4;
                var d = (y * c + x) * 4;
                for (var k = 0; k < 3; k++)
                    sh[d + k] = (byte) Math.Clamp(MathF.Round(nearBare[s + k] * FarDarken), 0f, 255f);
                sh[d + 3] = nearBare[s + 3];
            }

            var far = new byte[px * 4];
            Over(far, sh);
            Over(far, outImg[(int) GilbertPuppetGroup.ArmFar]);
            outImg[(int) GilbertPuppetGroup.ArmFar] = far;
            glove[1] = Shift(glove[0], shift, c);
        }

        var piv = new Dictionary<GilbertPuppetGroup, Vector2>();
        var j = TopJoint(Any(armOnly[0]) ? armOnly[0] : masks[(int) GilbertPuppetGroup.ArmNear], c);
        if (j is { } jn)
        {
            piv[GilbertPuppetGroup.ArmNear] = jn;
            if (synth) piv[GilbertPuppetGroup.ArmFar] = jn + new Vector2(shift, 0);
        }
        if (!synth && TopJoint(Any(armOnly[1]) ? armOnly[1] : masks[(int) GilbertPuppetGroup.ArmFar], c) is { } jf)
            piv[GilbertPuppetGroup.ArmFar] = jf;

        for (var l = 0; l < 2; l++)
        {
            var g = Legs[l];
            if (TopJoint(Any(legOnly[l]) ? legOnly[l] : masks[(int) g], c) is { } jl)
                piv[g] = jl;
        }

        if (BBox(masks[(int) GilbertPuppetGroup.Head], c) is { } hb)
        {
            var y1 = hb.MaxY;
            float sum = 0; var n = 0;
            for (var x = 0; x < c; x++)
                if (masks[(int) GilbertPuppetGroup.Head][y1 * c + x]) { sum += x; n++; }
            piv[GilbertPuppetGroup.Head] = new Vector2(sum / n + 0.5f - c / 2f, c / 2f - (y1 + 1));
        }

        // Arms and legs have two segments (v10: upper arm, forearm + hand; thigh, shin + foot), cut along rows taken from
        // the masks: the elbow at 2/5 of shoulder -> finger tips, the knee half-way hip -> sole; the wrist (top of the hand mask) is kept
        // as the hand pivot (grip reference). Each lower segment keeps one overlap row
        // above its cut so no seam opens while bending; glove pixels always ride on the hand.
        var segments = new Dictionary<GilbertPuppetGroup, (int Joint, int Mid, int Tip, int End)>();
        foreach (var g in new[] { GilbertPuppetGroup.ArmNear, GilbertPuppetGroup.ArmFar, GilbertPuppetGroup.LegNear, GilbertPuppetGroup.LegFar })
        {
            bool[] jm, mm, tm;
            bool[]? gl = null;
            if (g is GilbertPuppetGroup.ArmNear or GilbertPuppetGroup.ArmFar)
            {
                var a = g == GilbertPuppetGroup.ArmFar && !synth ? 1 : 0;
                var sdx = g == GilbertPuppetGroup.ArmFar && synth ? shift : 0;
                var srcMask = masks[(int) (a == 0 ? GilbertPuppetGroup.ArmNear : GilbertPuppetGroup.ArmFar)];
                jm = Shift(Any(armOnly[a]) ? armOnly[a] : srcMask, sdx, c);
                mm = Shift(srcMask, sdx, c);
                tm = Shift(hands[a], sdx, c);
                gl = glove[g == GilbertPuppetGroup.ArmNear ? 0 : 1];
            }
            else
            {
                var l = g == GilbertPuppetGroup.LegNear ? 0 : 1;
                jm = Any(legOnly[l]) ? legOnly[l] : masks[(int) g];
                mm = masks[(int) g];
                tm = feet[l];
            }

            if (BBox(jm, c) is not { } jb || BBox(mm, c) is not { } mb || !piv.ContainsKey(g))
                continue;
            int y0 = jb.MinY, y1 = mb.MaxY;
            var len = y1 - y0 + 1;
            if (len < 6)
                continue;

            if (g is GilbertPuppetGroup.LegNear or GilbertPuppetGroup.LegFar)
            {
                // Legs keep two segments: at 32 px the shoe fills the rows below the knee, so a separate foot would
                // leave a 1-2 px shin and read as a broken leg. Knee half-way between the hip and the sole.
                var k = y0 + (len + 1) / 2;
                if (RowMean(mm, k, c) is not { } xk)
                    continue;
                var leg = outImg[(int) g];
                var shin = new byte[px * 4];
                Array.Copy(leg, (k - 1) * c * 4, shin, (k - 1) * c * 4, (c - k + 1) * c * 4);
                Array.Clear(leg, k * c * 4, (c - k) * c * 4);
                outImg[(int) LowOf(g)] = shin;
                piv[LowOf(g)] = new Vector2(xk + 0.5f - c / 2f, c / 2f - k);
                piv[TipOf(g)] = new Vector2(xk + 0.5f - c / 2f, c / 2f - (y1 + 1)); // sole (no foot segment)
                segments[g] = (y0 + 1, k, y1 + 1, y1 + 1);
                continue;
            }

            var w = BBox(tm, c) is { } tb && tb.MinY >= y0 + 3 && tb.MinY <= y1
                ? tb.MinY
                : y0 + (int) MathF.Round(0.7f * len, MidpointRounding.ToEven);
            // v10: elbow at 2/5 of shoulder -> finger tips (upper arm : forearm + hand ~ 30 : 44 cm), the forearm
            // carries the whole hand.
            var e = y0 + 1 + 2 * (len - 1) / 5;
            if (RowMean(mm, e, c) is not { } xe || (RowMean(jm, w - 1, c) ?? RowMean(mm, w, c)) is not { } xw)
                continue;

            var img = outImg[(int) g];
            var up = new byte[px * 4];
            var low = new byte[px * 4];
            var tip = new byte[px * 4];
            // v10 (t279u): the hand (and glove) is part of the forearm, one rigid segment like the shin with its shoe;
            // the hand group stays empty (no wrist joint). The elbow cut is unchanged, so the arm keeps its full length.
            for (var p = 0; p < px; p++)
            {
                var y = p / c;
                var isGlove = gl != null && gl[p];
                if (isGlove || y >= e - 1)
                    Array.Copy(img, p * 4, low, p * 4, 4);
                if (!isGlove && y < e)
                    Array.Copy(img, p * 4, up, p * 4, 4);
            }

            outImg[(int) g] = up;
            outImg[(int) LowOf(g)] = low;
            outImg[(int) TipOf(g)] = tip;
            piv[LowOf(g)] = new Vector2(xe + 0.5f - c / 2f, c / 2f - e);
            piv[TipOf(g)] = new Vector2(xw + 0.5f - c / 2f, c / 2f - w);
            segments[g] = (y0 + 1, e, w, y1 + 1);
        }

        // Held items: their own layer, rotating about the middle of the hand that holds them.
        for (var h = 0; h < 2; h++)
        {
            if (!AnyAlpha(held[h]))
                continue;
            var g = h == 0 ? GilbertPuppetGroup.HeldNear : GilbertPuppetGroup.HeldFar;
            outImg[(int) g] = held[h];
            var a = h == 1 && !synth ? 1 : 0;
            var sdx = h == 1 && synth ? shift : 0;
            if (BBox(Shift(hands[a], sdx, c), c) is { } hb2)
                piv[g] = new Vector2((hb2.MinX + hb2.MaxX + 1) / 2f - c / 2f, c / 2f - (hb2.MinY + hb2.MaxY + 1) / 2f);
            else if (piv.TryGetValue(h == 0 ? GilbertPuppetGroup.ArmNearHand : GilbertPuppetGroup.ArmFarHand, out var hp))
                piv[g] = hp;
        }

        return new GilbertPuppetResult
        {
            Size = c,
            Images = outImg,
            Pivots = piv,
            SynthesizedFarArm = synth,
            FarArmShift = shift,
            Segments = segments,
        };
    }

    public static GilbertPuppetGroup LowOf(GilbertPuppetGroup limb) => limb switch
    {
        GilbertPuppetGroup.ArmNear => GilbertPuppetGroup.ArmNearLow,
        GilbertPuppetGroup.ArmFar => GilbertPuppetGroup.ArmFarLow,
        GilbertPuppetGroup.LegNear => GilbertPuppetGroup.LegNearLow,
        _ => GilbertPuppetGroup.LegFarLow,
    };

    public static GilbertPuppetGroup TipOf(GilbertPuppetGroup limb) => limb switch
    {
        GilbertPuppetGroup.ArmNear => GilbertPuppetGroup.ArmNearHand,
        GilbertPuppetGroup.ArmFar => GilbertPuppetGroup.ArmFarHand,
        GilbertPuppetGroup.LegNear => GilbertPuppetGroup.LegNearFoot,
        _ => GilbertPuppetGroup.LegFarFoot,
    };

    private static bool[] Shift(bool[] m, int d, int c)
    {
        if (d == 0)
            return m;
        var r = new bool[m.Length];
        for (var y = 0; y < c; y++)
        for (var x = 0; x < c; x++)
        {
            var sx = x - d;
            if (sx >= 0 && sx < c)
                r[y * c + x] = m[y * c + sx];
        }

        return r;
    }

    private static float? RowMean(bool[] m, int row, int c)
    {
        if (row < 0 || row >= c)
            return null;
        float sum = 0; var n = 0;
        for (var x = 0; x < c; x++)
            if (m[row * c + x]) { sum += x; n++; }
        return n == 0 ? null : sum / n;
    }

    private static bool AnyAlpha(byte[] img)
    {
        for (var i = 3; i < img.Length; i += 4)
            if (img[i] != 0)
                return true;
        return false;
    }

    /// <summary>Index of the group with the most opaque pixels under <paramref name="pixels"/>; Torso when none.</summary>
    public static GilbertPuppetGroup MajorityGroup(GilbertPuppetResult result, byte[] pixels)
    {
        var best = GilbertPuppetGroup.Torso;
        var bestN = 0;
        for (var g = 0; g < GroupCount; g++)
        {
            var img = result.Images[g];
            var n = 0;
            for (var i = 3; i < pixels.Length && i < img.Length; i += 4)
                if (pixels[i] > 0 && img[i] > 0) n++;
            if (n > bestN) { bestN = n; best = (GilbertPuppetGroup) g; }
        }

        return best;
    }
}
