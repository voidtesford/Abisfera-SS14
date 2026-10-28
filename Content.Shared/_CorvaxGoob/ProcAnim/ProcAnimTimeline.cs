using System.Numerics;
using Vector3 = System.Numerics.Vector3;
using Robust.Shared.Maths;

namespace Content.Shared._CorvaxGoob.ProcAnim;

/// <summary>
/// Pure, deterministic evaluation of a <see cref="ProcAnimScenePrototype"/>: easing, keys, the procedural layers
/// (walk, drag, daze, idle), joint limits, two-bone IK, look-at, the limb chain, views, props, hit-stop time warp,
/// blood droplets. Shared by the server, the client and (ported 1:1) the Python preview renderer.
/// </summary>
public static class ProcAnimTimeline
{
    private const float BackC1 = 1.70158f;
    private const float BackC2 = BackC1 * 1.525f;
    private const float BackC3 = BackC1 + 1f;
    private const float ElasticC4 = 2f * MathF.PI / 3f;

    /// <summary>Hit-stop ease: fraction of the segment the value stays frozen before snapping in.</summary>
    public const float HitStopHold = 0.35f;

    public static float Ease(ProcAnimEase ease, float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return ease switch
        {
            ProcAnimEase.Linear => u,
            ProcAnimEase.In => u * u * u,
            ProcAnimEase.Out => 1f - MathF.Pow(1f - u, 3f),
            ProcAnimEase.Step => u >= 1f ? 1f : 0f,
            ProcAnimEase.BackIn => BackC3 * u * u * u - BackC1 * u * u,
            ProcAnimEase.BackOut => 1f + BackC3 * MathF.Pow(u - 1f, 3f) + BackC1 * MathF.Pow(u - 1f, 2f),
            ProcAnimEase.BackInOut => u < 0.5f
                ? MathF.Pow(2f * u, 2f) * ((BackC2 + 1f) * 2f * u - BackC2) / 2f
                : (MathF.Pow(2f * u - 2f, 2f) * ((BackC2 + 1f) * (2f * u - 2f) + BackC2) + 2f) / 2f,
            ProcAnimEase.ElasticIn => u <= 0f ? 0f : u >= 1f ? 1f
                : -MathF.Pow(2f, 10f * u - 10f) * MathF.Sin((10f * u - 10.75f) * ElasticC4),
            ProcAnimEase.ElasticOut => u <= 0f ? 0f : u >= 1f ? 1f
                : MathF.Pow(2f, -10f * u) * MathF.Sin((10f * u - 0.75f) * ElasticC4) + 1f,
            ProcAnimEase.HitStop => u < HitStopHold ? 0f : 1f - MathF.Pow(1f - (u - HitStopHold) / (1f - HitStopHold), 3f),
            _ => u < 0.5f ? 4f * u * u * u : 1f - MathF.Pow(-2f * u + 2f, 3f) / 2f,
        };
    }

    /// <summary>Monotone (harmonic-mean) slope at key <paramref name="j"/>: 0 at the ends, at extrema and next to eased segments.</summary>
    private static float Tangent(List<ProcAnimKey> keys, int j, int c)
    {
        if (j <= 0 || j >= keys.Count - 1)
            return 0f;
        if (keys[j].Ease != ProcAnimEase.InOut || keys[j + 1].Ease != ProcAnimEase.InOut)
            return 0f;
        var h0 = keys[j].Time - keys[j - 1].Time;
        var h1 = keys[j + 1].Time - keys[j].Time;
        if (h0 <= 0f || h1 <= 0f)
            return 0f;
        var d0 = (Comp(keys[j], c) - Comp(keys[j - 1], c)) / h0;
        var d1 = (Comp(keys[j + 1], c) - Comp(keys[j], c)) / h1;
        if (d0 * d1 <= 0f)
            return 0f;
        return 2f * d0 * d1 / (d0 + d1);
    }

    private static float Comp(ProcAnimKey k, int c) => c switch { 0 => k.X, 1 => k.Y, _ => k.Rot };

    /// <summary>Keyed value at <paramref name="t"/>; <paramref name="smooth"/>: InOut segments use the monotone spline.</summary>
    public static ProcAnimPose Evaluate(List<ProcAnimKey> keys, float t, bool smooth)
    {
        if (!smooth)
            return Evaluate(keys, t);
        if (keys.Count == 0)
            return default;
        if (t <= keys[0].Time)
            return new ProcAnimPose(keys[0].X, keys[0].Y, keys[0].Rot);

        for (var i = 1; i < keys.Count; i++)
        {
            var b = keys[i];
            if (t > b.Time)
                continue;
            if (b.Ease != ProcAnimEase.InOut)
                return Evaluate(keys, t);

            var a = keys[i - 1];
            var span = b.Time - a.Time;
            if (span <= 0f)
                return new ProcAnimPose(b.X, b.Y, b.Rot);
            var u = (t - a.Time) / span;
            var u2 = u * u;
            var u3 = u2 * u;
            var h00 = 2f * u3 - 3f * u2 + 1f;
            var h10 = u3 - 2f * u2 + u;
            var h01 = -2f * u3 + 3f * u2;
            var h11 = u3 - u2;
            float H(int c) => h00 * Comp(a, c) + h10 * span * Tangent(keys, i - 1, c) + h01 * Comp(b, c) + h11 * span * Tangent(keys, i, c);
            return new ProcAnimPose(H(0), H(1), H(2));
        }

        var last = keys[^1];
        return new ProcAnimPose(last.X, last.Y, last.Rot);
    }

    public static ProcAnimPose Evaluate(List<ProcAnimKey> keys, float t)
    {
        if (keys.Count == 0)
            return default;

        if (t <= keys[0].Time)
            return new ProcAnimPose(keys[0].X, keys[0].Y, keys[0].Rot);

        for (var i = 1; i < keys.Count; i++)
        {
            var b = keys[i];
            if (t > b.Time)
                continue;

            var a = keys[i - 1];
            var span = b.Time - a.Time;
            var u = span <= 0f ? 1f : Ease(b.Ease, (t - a.Time) / span);
            return new ProcAnimPose(
                a.X + (b.X - a.X) * u,
                a.Y + (b.Y - a.Y) * u,
                a.Rot + (b.Rot - a.Rot) * u);
        }

        var last = keys[^1];
        return new ProcAnimPose(last.X, last.Y, last.Rot);
    }

    public static ProcAnimActor Tracks(ProcAnimScenePrototype proto, int actor) => proto.Actors[actor];

    /// <summary>
    /// Final offset (pixels, forward frame) of a part that rotates about its pivot and is translated by the pose:
    /// translation + pivot - R * pivot.
    /// </summary>
    public static Vector2 PivotOffset(Vector2 pivot, ProcAnimPose pose)
    {
        var rad = pose.Rot * MathF.PI / 180f;
        var (sin, cos) = MathF.SinCos(rad);
        var rotated = new Vector2(pivot.X * cos - pivot.Y * sin, pivot.X * sin + pivot.Y * cos);
        return new Vector2(pose.X, pose.Y) + pivot - rotated;
    }

    /// <summary>
    /// World direction of the forward frame's +Y for a cardinal <paramref name="forward"/>: screen-up (world +Y) for the
    /// horizontal side views, so poses and moves keyed "up" stay up whichever way the attacker faces.
    /// </summary>
    public static Vector2 Side(Vector2 forward)
    {
        return Side(forward, Angle.Zero);
    }

    /// <summary>
    /// World direction of the forward frame's +Y for a scene laid out in the screen frame <paramref name="view"/>
    /// (world rotation of the screen axes, see <see cref="ProcAnimComponent.View"/>): screen-up for side views,
    /// otherwise the left-hand perpendicular of <paramref name="forward"/>.
    /// </summary>
    public static Vector2 Side(Vector2 forward, Angle view)
    {
        var up = view.RotateVec(new Vector2(0, 1));
        return MathF.Abs(Vector2.Dot(forward, up)) < 0.5f ? up : new Vector2(-forward.Y, forward.X);
    }

    // ---- hit-stop time warp ----

    /// <summary>Length including every hit-stop: how long the cutscene really plays.</summary>
    public static float TotalLength(ProcAnimScenePrototype proto)
    {
        var total = proto.Length;
        foreach (var ev in proto.Events)
            if (ev.HitStop > 0f)
                total += ev.HitStop;
        return total;
    }

    /// <summary>Animation time at real (wall) time <paramref name="real"/>: frozen during hit-stops, clamped to Length.</summary>
    public static float AnimTime(ProcAnimScenePrototype proto, float real)
    {
        if (real < 0f)
            return real; // intro (holster beat): no hit-stops
        var acc = 0f;
        foreach (var ev in proto.Events)
        {
            if (ev.HitStop <= 0f)
                continue;
            if (real < ev.Time + acc)
                break;
            if (real < ev.Time + acc + ev.HitStop)
                return ev.Time;
            acc += ev.HitStop;
        }

        return MathF.Min(real - acc, proto.Length);
    }

    /// <summary>Real time at which animation time <paramref name="anim"/> is first reached (events fire there).</summary>
    public static float RealTime(ProcAnimScenePrototype proto, float anim)
    {
        var t = anim;
        if (anim < 0f)
            return anim;
        foreach (var ev in proto.Events)
            if (ev.HitStop > 0f && ev.Time < anim)
                t += ev.HitStop;
        return t;
    }

    // ---- pose solver ----

    public const int PartCount = 16;

    public static float Facing(ProcAnimScenePrototype proto, int actor) => proto.Actors[actor].Facing;

    /// <summary>Helper for <see cref="Solve"/>: the same pivots for every other actor.</summary>
    public static Func<int, IReadOnlyDictionary<ProcAnimBone, Vector2>?> Same(IReadOnlyDictionary<ProcAnimBone, Vector2>? pivots)
        => _ => pivots;

    private static ProcAnimBone? Parent(ProcAnimBone part) => part switch
    {
        ProcAnimBone.ArmNearLow => ProcAnimBone.ArmNear,
        ProcAnimBone.ArmFarLow => ProcAnimBone.ArmFar,
        ProcAnimBone.LegNearLow => ProcAnimBone.LegNear,
        ProcAnimBone.LegFarLow => ProcAnimBone.LegFar,
        ProcAnimBone.ArmNearHand => ProcAnimBone.ArmNearLow,
        ProcAnimBone.ArmFarHand => ProcAnimBone.ArmFarLow,
        ProcAnimBone.LegNearFoot => ProcAnimBone.LegNearLow,
        ProcAnimBone.LegFarFoot => ProcAnimBone.LegFarLow,
        ProcAnimBone.HeldNear => ProcAnimBone.ArmNearHand,
        ProcAnimBone.HeldFar => ProcAnimBone.ArmFarHand,
        _ => null,
    };

    public static Vector2 Rotate(Vector2 v, float deg)
    {
        var (sin, cos) = MathF.SinCos(deg * MathF.PI / 180f);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    /// <summary>
    /// Full pose of one actor at animation time <paramref name="t"/>: keys + procedural walk cycle, idle breathing and
    /// head look-at, chained through the two-segment limbs. Result index = (int) part; X/Y are the final layer offsets
    /// (px, forward frame, relative to the sprite origin, pivot rotation included), Rot the final layer rotation (Low
    /// parts include their parent's); Body is the sprite offset / rotation.
    /// </summary>
    /// <param name="pivots">Pivot per part (forward frame), e.g. mask-derived puppet joints; null = the YAML pivots.</param>
    /// <param name="otherPivots">Pivots of another actor by index (its bones drive the look-at / IK targets); null = YAML.</param>
    /// <param name="ik">False: skip the IK effectors (and look-at is controlled by <paramref name="look"/>).</param>
    public static ProcAnimPose[] Solve(ProcAnimScenePrototype proto, int actor, float t,
        IReadOnlyDictionary<ProcAnimBone, Vector2>? pivots = null, bool look = true,
        Func<int, IReadOnlyDictionary<ProcAnimBone, Vector2>?>? otherPivots = null, bool ik = true,
        IReadOnlyDictionary<ProcAnimBone, ProcAnimPose>? overlay = null)
    {
        var tr = Tracks(proto, actor);
        var f = tr.Facing;
        var pose = new Vector3[PartCount];
        var pivot = new Vector2[PartCount];
        for (var i = 0; i < PartCount; i++)
        {
            var part = (ProcAnimBone) i;
            if (tr.Parts.TryGetValue(part, out var track))
            {
                var p = Evaluate(track.Keys, t, tr.Smooth);
                pose[i] = new Vector3(p.X, p.Y, p.Rot);
                pivot[i] = track.Pivot;
            }

            // Additive layer (e.g. the stow reach), in the actor's facing convention like its keys.
            if (overlay != null && overlay.TryGetValue(part, out var add))
                pose[i] += new Vector3(f * add.X, add.Y, f * add.Rot);

            if (part != ProcAnimBone.Body && pivots != null && pivots.TryGetValue(part, out var pv))
                pivot[i] = pv;
        }

        ref var body = ref pose[(int) ProcAnimBone.Body];
        ref var head = ref pose[(int) ProcAnimBone.Head];
        ref var armN = ref pose[(int) ProcAnimBone.ArmNear];
        ref var armF = ref pose[(int) ProcAnimBone.ArmFar];
        ref var legN = ref pose[(int) ProcAnimBone.LegNear];
        ref var legF = ref pose[(int) ProcAnimBone.LegFar];
        ref var armNL = ref pose[(int) ProcAnimBone.ArmNearLow];
        ref var armFL = ref pose[(int) ProcAnimBone.ArmFarLow];
        ref var legNL = ref pose[(int) ProcAnimBone.LegNearLow];
        ref var legFL = ref pose[(int) ProcAnimBone.LegFarLow];
        ref var armNH = ref pose[(int) ProcAnimBone.ArmNearHand];
        ref var armFH = ref pose[(int) ProcAnimBone.ArmFarHand];

        var w = 0f;
        if (tr.Walk is { } walk && tr.Move.Count > 0)
        {
            const float h = 0.03f;
            var t0 = t - h;
            var v = (Evaluate(tr.Move, t + h).X - Evaluate(tr.Move, t0).X) / (t + h - t0);
            var d = Evaluate(tr.Move, t).X;
            w = MathF.Min(MathF.Abs(v) / walk.FullSpeed, 1f);
            if (walk.Weight.Count > 0)
                w *= MathF.Max(0f, Evaluate(walk.Weight, t).X);
            var ph = MathF.PI * d / walk.Stride;
            var (sn, cs) = MathF.SinCos(ph);
            var dirv = v * f >= 0 ? 1f : -1f;
            legN.Z += f * w * walk.LegSwing * sn;
            legF.Z -= f * w * walk.LegSwing * sn;
            legNL.Z -= f * w * walk.Knee * MathF.Max(0f, cs * dirv);
            legFL.Z -= f * w * walk.Knee * MathF.Max(0f, -cs * dirv);
            armN.Z -= f * w * walk.ArmSwing * sn;
            armF.Z += f * w * walk.ArmSwing * sn;
            armNL.Z += f * w * walk.Elbow * (1f + 0.5f * MathF.Max(0f, -sn));
            armFL.Z += f * w * walk.Elbow * (1f + 0.5f * MathF.Max(0f, sn));
            armN.X -= f * w * walk.Shoulder * sn;
            armF.X += f * w * walk.Shoulder * sn;
            body.Y += w * walk.Bob * (MathF.Abs(cs) - 1f);
            body.Z += -f * w * walk.Lean + w * walk.Sway * MathF.Sin(2f * ph);
            head.Z += f * w * walk.Nod * MathF.Cos(2f * ph);
        }

        if (tr.Drag is { } dr)
        {
            var t0 = t - dr.Lag;
            float Delta(ProcAnimBone p) => tr.Parts.TryGetValue(p, out var k)
                ? Evaluate(k.Keys, t0, tr.Smooth).Rot - Evaluate(k.Keys, t, tr.Smooth).Rot
                : 0f;
            float Cl(float v) => Math.Clamp(v, -dr.Max, dr.Max);
            var bd = Delta(ProcAnimBone.Body);
            head.Z += Cl(dr.Head * bd);
            armN.Z += Cl(dr.Arms * bd);
            armF.Z += Cl(dr.Arms * bd);
            legN.Z += Cl(dr.Legs * bd);
            legF.Z += Cl(dr.Legs * bd);
            armNL.Z += Cl(dr.Low * Delta(ProcAnimBone.ArmNear));
            armFL.Z += Cl(dr.Low * Delta(ProcAnimBone.ArmFar));
            legNL.Z += Cl(dr.LegsLow * Delta(ProcAnimBone.LegNear));
            legFL.Z += Cl(dr.LegsLow * Delta(ProcAnimBone.LegFar));
            armNH.Z += Cl(dr.Hands * Delta(ProcAnimBone.ArmNearLow));
            armFH.Z += Cl(dr.Hands * Delta(ProcAnimBone.ArmFarLow));
        }

        if (tr.Daze is { } dz && DazeWeight(tr, t) is var dw && dw > 0f)
        {
            var a = 2f * MathF.PI * t / dz.Period;
            var s1 = MathF.Sin(a) + 0.35f * MathF.Sin(2.3f * a + 1f);
            body.Z += dw * dz.Sway * s1;
            body.X += dw * dz.Shift * MathF.Sin(a + 0.5f);
            var dip = 0.5f + 0.5f * MathF.Sin(2f * a + 0.7f);
            body.Y -= dw * dz.Dip * dip;
            head.Z += dw * dz.Loll * (MathF.Sin(a - 1.1f) + 0.3f * MathF.Sin(3.1f * a));
            var am = dw * dz.Arms;
            armN.Z += am * MathF.Sin(a - 0.7f);
            armF.Z += am * 0.8f * MathF.Sin(a - 1.2f);
            armNL.Z += am * 0.6f * MathF.Sin(a - 1.3f);
            armFL.Z += am * 0.5f * MathF.Sin(a - 1.8f);
            armNH.Z += am * 0.5f * MathF.Sin(a - 2.0f);
            armFH.Z += am * 0.4f * MathF.Sin(a - 2.4f);
            var kn = dw * dz.Knees * dip;
            legN.Z -= f * kn;
            legNL.Z += f * kn * 1.6f;
            legF.Z -= f * kn * 0.7f;
            legFL.Z += f * kn * 1.2f;
        }

        if (tr.Idle is { } idle)
        {
            var k = 1f - w;
            var per = idle.Period;
            var b = MathF.Sin(2f * MathF.PI * t / per);
            body.Z += k * idle.WeightShift * MathF.Sin(2f * MathF.PI * t / (per * 1.7f) + 1f);
            head.Z += k * f * idle.HeadNod * MathF.Sin(2f * MathF.PI * t / per - 0.6f);
            var br = idle.Breath * k * b;
            head.Y += br;
            armN.Y += br;
            armF.Y += br;
            var sw = idle.ArmSway * k * b;
            armN.Z += f * sw;
            armF.Z -= f * sw * 0.7f;
            armNL.Z += f * sw * 0.8f;
            armFL.Z += f * sw * 0.8f;
            armNH.Z += f * sw * 0.5f;
            armFH.Z += f * sw * 0.5f;
        }

        // v12 hit effects: impact recoil of the hit part and the concussion stagger.
        ProcAnimFx.ApplyPose(proto, actor, t, pose, f);

        var sideView = ViewAt(tr, t) == null;
        var limits = tr.Joints ?? ProcAnimJointLimits.Default;
        ApplyJointLimits(pose, f, sideView, limits);
        var outPose = Chain(pose, pivot);

        if (ik && tr.Ik.Count > 0)
        {
            var any = false;
            foreach (var track in tr.Ik)
                any |= SolveIk(proto, actor, tr, track, t, pose, pivot, outPose, otherPivots);
            if (any)
            {
                ApplyJointLimits(pose, f, sideView, limits);
                outPose = Chain(pose, pivot);
            }
        }

        if (look && tr.Look is { } lk)
        {
            var wt = lk.Keys.Count > 0 ? Evaluate(lk.Keys, t).X : lk.Weight;
            if (wt > 0f)
            {
                var own = Point(outPose, ProcAnimBone.Head, lk.At);
                var me = Evaluate(tr.Move, t);
                var other = lk.ActorIndex;
                if (other < 0 || other >= proto.Actors.Count || other == actor)
                    return outPose;
                var otherPose = Solve(proto, other, t, otherPivots?.Invoke(other), false, ik: false);
                var tp = Point(otherPose, lk.Target, lk.TargetAt);
                var om = Evaluate(Tracks(proto, other).Move, t);
                var dx = (om.X * 32f + tp.X) - (me.X * 32f + own.X);
                var dy = (om.Y * 32f + tp.Y) - (me.Y * 32f + own.Y);
                wt *= Math.Clamp(f * dx / 8f, 0f, 1f); // partner behind: stop looking
                var desired = MathF.Atan2(dy, MathF.Max(f * dx, 1e-3f)) * 180f / MathF.PI;
                desired = Math.Clamp(desired, -lk.Max, lk.Max);
                var cur = f * (outPose[(int) ProcAnimBone.Body].Rot + outPose[(int) ProcAnimBone.Head].Rot);
                var rel = Math.Clamp(f * head.Z + wt * (desired - cur), -lk.Neck, lk.Neck);
                head.Z = f * rel;
                outPose = Chain(pose, pivot);
            }
        }

        return outPose;
    }

    // Joint limits (degrees, relative to the parent segment, in the actor's forward frame: + = swung towards its facing).
    /// <summary>The shin only folds back behind the thigh (no hyperextension, no forward bend).</summary>
    public const float KneeMin = -150f, KneeMax = 0f;
    /// <summary>The forearm only folds forward / up.</summary>
    public const float ElbowMin = -5f, ElbowMax = 150f;
    public const float HipMin = -60f, HipMax = 125f;
    /// <summary>
    /// v10 (t279u): the shoulder swings from slightly behind the body to overhead (forward); there is no wrist joint any
    /// more, the hand is rigid with the forearm (like the shoe with the shin).
    /// </summary>
    public const float ShoulderMin = -80f, ShoulderMax = 190f;
    /// <summary>Back-to-camera views have no facing in the screen plane: knees / elbows only get a range.</summary>
    public const float BackViewBend = 100f;

    /// <summary>
    /// Clamps the keyed + procedural (walk, drag, daze, idle) rotations to the joint limits. Arms and legs have two
    /// segments (upper arm, forearm + hand; thigh, shin + shoe), so the hand and foot parts stay at 0.
    /// </summary>
    /// <summary>v12 soft joint limit zone, degrees: the last few degrees before a limit ease into it (C1).</summary>
    public const float SoftLimit = 4f;

    /// <summary>
    /// Clamp with a soft knee: identity inside <c>[lo + k, hi - k]</c>, a quadratic ease that reaches the limit at
    /// <c>k</c> past it with zero slope, so limbs slow into a limit instead of stopping dead (no popping), and exact
    /// limits further out.
    /// </summary>
    public static float SoftClamp(float x, float lo, float hi, float k = SoftLimit)
    {
        if (lo > hi)
            (lo, hi) = (hi, lo);
        k = MathF.Min(k, (hi - lo) * 0.5f);
        if (k <= 0f)
            return Math.Clamp(x, lo, hi);
        if (x > hi - k)
            return x >= hi + k ? hi : hi - (hi + k - x) * (hi + k - x) / (4f * k);
        if (x < lo + k)
            return x <= lo - k ? lo : lo + (x - lo + k) * (x - lo + k) / (4f * k);
        return x;
    }

    public static void ApplyJointLimits(Vector3[] pose, float f, bool sideView, ProcAnimJointLimits? limits = null)
    {
        var l = limits ?? ProcAnimJointLimits.Default;
        foreach (var p in new[] { ProcAnimBone.LegNearLow, ProcAnimBone.LegFarLow, ProcAnimBone.ArmNearLow, ProcAnimBone.ArmFarLow })
        {
            var leg = p is ProcAnimBone.LegNearLow or ProcAnimBone.LegFarLow;
            var range = leg ? l.Knee : l.Elbow;
            ref var v = ref pose[(int) p];
            v.Z = sideView
                ? f * SoftClamp(f * v.Z, range.X, range.Y)
                : SoftClamp(v.Z, -l.BackViewBend, l.BackViewBend);
        }

        if (sideView)
        {
            pose[(int) ProcAnimBone.LegNear].Z = f * SoftClamp(f * pose[(int) ProcAnimBone.LegNear].Z, l.Hip.X, l.Hip.Y);
            pose[(int) ProcAnimBone.LegFar].Z = f * SoftClamp(f * pose[(int) ProcAnimBone.LegFar].Z, l.Hip.X, l.Hip.Y);
        }

        if (sideView)
        {
            pose[(int) ProcAnimBone.ArmNear].Z = f * SoftClamp(f * pose[(int) ProcAnimBone.ArmNear].Z, l.Shoulder.X, l.Shoulder.Y);
            pose[(int) ProcAnimBone.ArmFar].Z = f * SoftClamp(f * pose[(int) ProcAnimBone.ArmFar].Z, l.Shoulder.X, l.Shoulder.Y);
        }

        // Rigid forearm + hand: no wrist rotation or offset, whatever the keys / secondary motion say.
        pose[(int) ProcAnimBone.ArmNearHand] = Vector3.Zero;
        pose[(int) ProcAnimBone.ArmFarHand] = Vector3.Zero;
        pose[(int) ProcAnimBone.LegNearFoot].Z = 0f;
        pose[(int) ProcAnimBone.LegFarFoot].Z = 0f;
    }

    // ---- two-bone IK ----

    private static (ProcAnimBone Low, ProcAnimBone Tip) IkChain(ProcAnimBone limb) => limb switch
    {
        ProcAnimBone.ArmNear => (ProcAnimBone.ArmNearLow, ProcAnimBone.ArmNearHand),
        ProcAnimBone.ArmFar => (ProcAnimBone.ArmFarLow, ProcAnimBone.ArmFarHand),
        ProcAnimBone.LegNear => (ProcAnimBone.LegNearLow, ProcAnimBone.LegNearFoot),
        _ => (ProcAnimBone.LegFarLow, ProcAnimBone.LegFarFoot),
    };

    /// <summary>IK blend weight of <paramref name="track"/> at <paramref name="t"/> (0..1).</summary>
    public static float IkWeight(ProcAnimIkTrack track, float t)
    {
        var w = Math.Clamp(track.Weights.Count > 0 ? Evaluate(track.Weights, t).X : track.Weight, 0f, 1f);
        // v12: smoothstep, so blending the IK in / out starts and ends with zero speed (no limb pop).
        return w * w * (3f - 2f * w);
    }

    /// <summary>
    /// IK target of <paramref name="track"/> at <paramref name="t"/>: forward-frame px relative to the sprite origin of
    /// <paramref name="actor"/> (like <see cref="Point"/>).
    /// </summary>
    public static Vector2 IkTarget(ProcAnimScenePrototype proto, int actor, ProcAnimIkTrack track, float t,
        ProcAnimPose[] ownPose, Func<int, IReadOnlyDictionary<ProcAnimBone, Vector2>?>? otherPivots = null)
    {
        var k = Evaluate(track.Keys, t);
        var off = new Vector2(k.X, k.Y);
        if (track.TargetActor < 0)
            return off;
        if (track.TargetActor == actor)
            return Point(ownPose, track.TargetBone, track.At) + off;

        var other = track.TargetActor;
        var otherPose = Solve(proto, other, t, otherPivots?.Invoke(other), false, ik: false);
        var tp = Point(otherPose, track.TargetBone, track.At);
        var me = Evaluate(Tracks(proto, actor).Move, t);
        var om = Evaluate(Tracks(proto, other).Move, t);
        return new Vector2(om.X * 32f + tp.X - me.X * 32f, om.Y * 32f + tp.Y - me.Y * 32f) + off;
    }

    private static float Atan2Deg(Vector2 v) => MathF.Atan2(v.Y, v.X) * 180f / MathF.PI;

    private static float Wrap(float deg)
    {
        deg %= 360f;
        if (deg > 180f)
            deg -= 360f;
        else if (deg <= -180f)
            deg += 360f;
        return deg;
    }

    /// <summary>
    /// Two-bone IK in the body-local frame of the chain: rotates the upper / lower segment (pose Z) so the tip reaches
    /// the target, picks the bend allowed by the joint limits, blends by the track weight. Returns false when inactive.
    /// </summary>
    private static bool SolveIk(ProcAnimScenePrototype proto, int actor, ProcAnimActor tr, ProcAnimIkTrack track, float t,
        Vector3[] pose, Vector2[] pivot, ProcAnimPose[] outPose,
        Func<int, IReadOnlyDictionary<ProcAnimBone, Vector2>?>? otherPivots)
    {
        if (!track.Valid)
            return false;
        var w = IkWeight(track, t);
        if (w <= 0f)
            return false;

        var upper = track.Limb;
        var (low, tipPart) = IkChain(upper);
        var target = IkTarget(proto, actor, track, t, outPose, otherPivots);
        var b = outPose[(int) ProcAnimBone.Body];
        var local = Rotate(target - new Vector2(b.X, b.Y), -b.Rot);

        var u = pose[(int) upper];
        var l = pose[(int) low];
        var s = pivot[(int) upper] + new Vector2(u.X, u.Y);
        var v1 = pivot[(int) low] - pivot[(int) upper] + new Vector2(l.X, l.Y);
        var v2 = (track.Tip ?? pivot[(int) tipPart]) - pivot[(int) low];
        var l1 = v1.Length();
        var l2 = v2.Length();
        if (l1 < 1e-3f || l2 < 1e-3f)
            return false;

        var d = local - s;
        var dist = Math.Clamp(d.Length(), MathF.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
        var cosA = Math.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
        var alpha = MathF.Acos(cosA) * 180f / MathF.PI;
        var dirD = Atan2Deg(d);

        (float Up, float Low) Bend(float sign)
        {
            var thU = dirD + sign * alpha - Atan2Deg(v1);
            var elbow = s + Rotate(v1, thU);
            var thL = Atan2Deg(local - elbow) - Atan2Deg(v2) - thU;
            return (Wrap(thU), Wrap(thL));
        }

        var plus = Bend(1f);
        var minus = Bend(-1f);
        var leg = upper is ProcAnimBone.LegNear or ProcAnimBone.LegFar;
        var f = tr.Facing;
        var pick = track.Bend switch
        {
            ProcAnimIkBend.Positive => plus.Low >= minus.Low ? plus : minus,
            ProcAnimIkBend.Negative => plus.Low <= minus.Low ? plus : minus,
            // Elbows fold towards the facing (f * low >= 0), knees away from it.
            _ => (leg ? f * plus.Low <= f * minus.Low : f * plus.Low >= f * minus.Low) ? plus : minus,
        };

        // Shortest-way blend from the keyed pose.
        pose[(int) upper].Z = u.Z + w * Wrap(pick.Up - u.Z);
        pose[(int) low].Z = l.Z + w * Wrap(pick.Low - l.Z);
        return true;
    }

    // ---- expressions (stub layer) ----

    /// <summary>Facial expression / eye state of <paramref name="tr"/> at <paramref name="t"/> (step keys).</summary>
    public static ProcAnimExpression ExpressionAt(ProcAnimActor tr, float t)
    {
        string? face = null, eyes = null;
        foreach (var k in tr.Expressions)
        {
            if (k.Time > t)
                break;
            face = k.Face ?? face;
            eyes = k.Eyes ?? eyes;
        }

        return new ProcAnimExpression(face, eyes);
    }

    /// <summary>Screen direction forced at <paramref name="t"/> by <see cref="ProcAnimActor.Views"/>, if any.</summary>
    public static Direction? ViewAt(ProcAnimActor tr, float t)
    {
        return ViewInfoAt(tr, t)?.Facing;
    }

    /// <summary>The <see cref="ProcAnimActor.Views"/> entry active at <paramref name="t"/>, if any.</summary>
    public static ProcAnimView? ViewInfoAt(ProcAnimActor tr, float t)
    {
        foreach (var v in tr.Views)
            if (t >= v.Start && t < v.End)
                return v;
        return null;
    }

    /// <summary>
    /// Screen direction the actor is drawn with at <paramref name="t"/>, if forced. <paramref name="facingRight"/>:
    /// the actor's normal side-on facing (towards screen right); a <see cref="ProcAnimView.Turned"/> view flips it.
    /// </summary>
    public static Direction? ViewDirection(ProcAnimActor tr, float t, bool facingRight)
    {
        if (ViewInfoAt(tr, t) is not { } v)
            return null;
        if (!v.Turned)
            return v.Facing;
        return facingRight ? Direction.West : Direction.East;
    }

    /// <summary>True while held items are drawn in the hands (before <see cref="ProcAnimActor.HolsterAt"/>).</summary>
    public static bool HeldInHands(ProcAnimActor tr, float t)
    {
        return !tr.HolsterHeldItems
               || tr.HolsterAt is { } at && t < at
               || tr.UnholsterAt is { } un && t >= un;
    }

    public static bool PropVisible(ProcAnimActor tr, ProcAnimProp prop, float t)
    {
        return t >= prop.Start && t < prop.End && !IsHidden(tr, prop.Part, t);
    }

    /// <summary>Layer offset / rotation of a prop (forward frame, like the part poses) given its part's pose.</summary>
    public static ProcAnimPose PropPose(ProcAnimProp prop, ProcAnimPose part, float t)
    {
        var k = Evaluate(prop.Keys, t);
        var (sin, cos) = MathF.SinCos(part.Rot * MathF.PI / 180f);
        return new ProcAnimPose(part.X + cos * k.X - sin * k.Y, part.Y + sin * k.X + cos * k.Y, part.Rot + k.Rot);
    }

    /// <summary>True when <paramref name="part"/> (or the limb segment it hangs from) is hidden at <paramref name="t"/>.</summary>
    public static bool IsHidden(ProcAnimActor tr, ProcAnimBone part, float t)
    {
        foreach (var h in tr.Hide)
        {
            if (t < h.Time)
                continue;
            for (var p = (ProcAnimBone?) part; p is { } q; p = Parent(q))
                if (h.Parts.Contains(q))
                    return true;
        }

        return false;
    }

    public static float DazeWeight(ProcAnimActor tr, float t)
    {
        if (tr.Daze is not { } dz)
            return 0f;
        return dz.Weight.Count > 0 ? MathF.Max(0f, Evaluate(dz.Weight, t).X) : dz.Amount;
    }

    public readonly record struct Star(Vector2 Position, int Size, float Alpha, bool Front);

    /// <summary>Dizzy stars orbiting the stars centre (px offsets, forward frame); empty while not dazed enough.</summary>
    public static List<Star> Stars(ProcAnimActor tr, float t)
    {
        var res = new List<Star>();
        if (tr.Daze?.Stars is not { } st)
            return res;
        var w = DazeWeight(tr, t);
        if (w <= st.Threshold)
            return res;

        var alpha = MathF.Min(1f, (w - st.Threshold) / MathF.Max(1e-3f, (1f - st.Threshold) * 0.5f));
        for (var i = 0; i < st.Count; i++)
        {
            var a = 2f * MathF.PI * (t / st.Period + (float) i / st.Count);
            var (sin, cos) = MathF.SinCos(a);
            var front = sin < 0f;
            var tw = MathF.Sin(2f * MathF.PI * t * 3.1f + i * 2.1f);
            res.Add(new Star(new Vector2(st.Radius.X * cos, st.Radius.Y * sin), front && tw > -0.2f ? 2 : 1,
                alpha * (front ? 1f : 0.55f), front));
        }

        return res;
    }

    private static ProcAnimPose[] Chain(Vector3[] pose, Vector2[] pivot)
    {
        var res = new ProcAnimPose[PartCount];
        for (var i = 0; i < PartCount; i++)
        {
            var part = (ProcAnimBone) i;
            var off = PivotOffset(pivot[i], new ProcAnimPose(pose[i].X, pose[i].Y, pose[i].Z));
            if (Parent(part) is not { } parent)
            {
                res[i] = new ProcAnimPose(off.X, off.Y, pose[i].Z);
                continue;
            }

            var up = res[(int) parent]; // parents come first in the enum
            var r = Rotate(off, up.Rot);
            res[i] = new ProcAnimPose(up.X + r.X, up.Y + r.Y, up.Rot + pose[i].Z);
        }

        return res;
    }

    /// <summary>Forward-frame px (relative to the entity position) of the rest-pose point <paramref name="at"/> on a part.</summary>
    public static Vector2 Point(ProcAnimPose[] pose, ProcAnimBone part, Vector2 at)
    {
        var b = pose[(int) ProcAnimBone.Body];
        if (part == ProcAnimBone.Body)
            return new Vector2(b.X, b.Y) + Rotate(at, b.Rot);

        var p = pose[(int) part];
        var q = Rotate(new Vector2(p.X, p.Y) + Rotate(at, p.Rot), b.Rot);
        return new Vector2(b.X, b.Y) + q;
    }

    // ---- blood ----

    public static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }
    }

    /// <summary>Deterministic [0, 1) random number for droplet <paramref name="i"/> of event <paramref name="e"/>.</summary>
    public static float Rnd(int e, int i, int k)
    {
        unchecked
        {
            return (float) (Hash((uint) (e * 7919 + i * 131 + k + 1)) / 4294967296.0);
        }
    }

    public readonly record struct Droplet(Vector2 Position, int Size, float Shade, float Alpha);

    /// <summary>
    /// Droplets of a burst <paramref name="age"/> seconds after it started; positions are forward-frame px relative to
    /// the target entity. Empty once the burst is over.
    /// </summary>
    public static List<Droplet> Droplets(ProcAnimBlood bl, int e, Vector2 origin, float age)
    {
        var res = new List<Droplet>();
        if (age > bl.Life)
            return res;

        var alpha = age < bl.Life * 0.55f ? 1f : MathF.Max(0f, 1f - (age - bl.Life * 0.55f) / (bl.Life * 0.45f));
        for (var i = 0; i < bl.Count; i++)
        {
            var a = (bl.Angle.X + (bl.Angle.Y - bl.Angle.X) * Rnd(e, i, 0)) * MathF.PI / 180f;
            var s = bl.Speed.X + (bl.Speed.Y - bl.Speed.X) * Rnd(e, i, 1);
            var size = Rnd(e, i, 2) < bl.BigChance ? 2 : 1;
            var shade = 0.7f + 0.4f * Rnd(e, i, 3);
            var floor = bl.Floor + (Rnd(e, i, 4) - 0.5f) * bl.FloorJitter;
            var tt = age - Rnd(e, i, 5) * bl.Stagger;
            if (tt < 0f)
                continue;

            var (sin, cos) = MathF.SinCos(a);
            var vx = s * cos;
            var vy = s * sin;
            var disc = vy * vy + 2f * bl.Gravity * (origin.Y - floor);
            var land = disc >= 0f && bl.Gravity > 0f ? (vy + MathF.Sqrt(disc)) / bl.Gravity : float.MaxValue;
            if (tt >= land)
                res.Add(new Droplet(new Vector2(origin.X + vx * land, floor), size + 1, shade * 0.8f, alpha));
            else
                res.Add(new Droplet(new Vector2(origin.X + vx * tt, origin.Y + vy * tt - 0.5f * bl.Gravity * tt * tt), size, shade, alpha));
        }

        return res;
    }

    /// <summary>
    /// Additive pose of the stow pre-beat (<see cref="ProcAnimActor.StowHands"/>) at <paramref name="pre"/> seconds into
    /// it: reach number <c>pre / duration</c>, played on the near arm or mirrored to the far one. Null outside it.
    /// </summary>
    public static Dictionary<ProcAnimBone, ProcAnimPose>? StowOverlay(ProcAnimActor tracks, IReadOnlyList<bool> reachRight,
        float pre, bool facingRight)
    {
        var stow = tracks.StowHands ?? DefaultStow;
        var reach = (int) MathF.Floor(pre / stow.Duration);
        if (pre < 0f || reach < 0 || reach >= reachRight.Count)
            return null;

        var near = reachRight[reach] == facingRight;
        var u = pre - reach * stow.Duration;
        var result = new Dictionary<ProcAnimBone, ProcAnimPose>();
        foreach (var (bone, keys) in stow.Reach)
        {
            var target = near ? bone : bone switch
            {
                ProcAnimBone.ArmNear => ProcAnimBone.ArmFar,
                ProcAnimBone.ArmNearLow => ProcAnimBone.ArmFarLow,
                ProcAnimBone.ArmNearHand => ProcAnimBone.ArmFarHand,
                _ => bone,
            };
            result[target] = Evaluate(keys, u);
        }

        return result;
    }

    private static readonly ProcAnimStow DefaultStow = new();
}

