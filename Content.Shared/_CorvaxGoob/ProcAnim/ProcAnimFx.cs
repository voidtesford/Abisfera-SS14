using System.Numerics;
using Vector3 = System.Numerics.Vector3;

namespace Content.Shared._CorvaxGoob.ProcAnim;

/// <summary>v12 hit effect / aftermath kinds (<see cref="ProcAnimEffect"/>).</summary>
public enum ProcAnimEffectType : byte
{
    /// <summary>Directional blood spray: a short mist plus droplets that fly along <see cref="ProcAnimEffect.Dir"/> and
    /// leave elongated stains on the floor (world-anchored at the hit, they stay for <see cref="ProcAnimEffect.Duration"/>).</summary>
    Spatter,
    /// <summary>Impact recoil / squash of the hit part: a fast push along the hit direction with a damped return (pose layer).</summary>
    Recoil,
    /// <summary>Bruise / blood mark on the part that stays (follows the bone, fades in) until the scene ends.</summary>
    Bruise,
    /// <summary>Blood drips falling from the point for <see cref="ProcAnimEffect.Duration"/> (follows the bone).</summary>
    Drips,
    /// <summary>A blood pool spreading on the floor under the point (world-anchored, grows for the duration then stays).</summary>
    Pool,
    /// <summary>Dust puffs and grit kicked up on the floor (falls, landings).</summary>
    Dust,
    /// <summary>Small local shake of the whole actor in real time (also during the hit-stop).</summary>
    Shake,
    /// <summary>Concussion sway / stagger: decaying wobble of the body, head and knees (pose layer).</summary>
    Stagger,
}

/// <summary>
/// One hit effect of a <see cref="ProcAnimEvent"/> on its target (client-side visuals, deterministic, mirrored in the
/// offline preview). Positions are rest-pose px on <see cref="Part"/> in the forward frame, like <see cref="ProcAnimBlood"/>.
/// </summary>
[DataDefinition]
public sealed partial class ProcAnimEffect
{
    [DataField(required: true)]
    public ProcAnimEffectType Type;

    /// <summary>Bone the effect starts from (Recoil: the bone that is pushed).</summary>
    [DataField]
    public ProcAnimBone Part = ProcAnimBone.Head;

    [DataField]
    public Vector2 At = new(0, 9);

    /// <summary>Hit direction, degrees in the forward frame (0 = +x, away from the first actor; 90 = up).</summary>
    [DataField]
    public float Dir;

    /// <summary>Spatter / Dust: full cone width, degrees.</summary>
    [DataField]
    public float Spread = 50f;

    /// <summary>Particles (Spatter, Bruise, Drips, Dust); -1 = the type's default.</summary>
    [DataField]
    public int Count = -1;

    [DataField]
    public float Strength = 1f;

    /// <summary>Seconds; -1 = the type's default (<see cref="ProcAnimFx.DurationOf"/>).</summary>
    [DataField]
    public float Duration = -1f;

    /// <summary>Bruise / Pool size multiplier.</summary>
    [DataField]
    public float Size = 1f;

    /// <summary>Floor height, px relative to the target entity origin (Spatter, Drips, Pool, Dust).</summary>
    [DataField]
    public float Floor = -14f;

    /// <summary>Overrides the colour (blood types default to the target's blood reagent colour).</summary>
    [DataField]
    public Color? Color;
}

/// <summary>v12 face overlay config of an actor (<see cref="ProcAnimActor.Face"/>), rest-pose px on the Head bone.</summary>
[DataDefinition]
public sealed partial class ProcAnimFace
{
    /// <summary>Side view eye centre when facing +x (mirrored with the facing).</summary>
    [DataField] public Vector2 Eye = new(2.5f, 9.5f);
    /// <summary>Side view mouth centre when facing +x.</summary>
    [DataField] public Vector2 Mouth = new(3.5f, 7.5f);
    /// <summary>Front (South) view eye centres.</summary>
    [DataField] public List<Vector2> FrontEyes = new() { new(-1.5f, 9.5f), new(0.5f, 9.5f) };
    [DataField] public Vector2 FrontMouth = new(-0.5f, 7.5f);
    /// <summary>Closed lids / squint lines.</summary>
    [DataField] public Color Lid = Color.FromHex("#6b3f33");
    [DataField] public Color Brow = Color.FromHex("#2b1c16");
    [DataField] public Color MouthColor = Color.FromHex("#3a0d0d");
    [DataField] public Color DeadColor = Color.FromHex("#1c1c1c");
    /// <summary>Glowing eyes (visor): angry flares them in <see cref="GlowColor"/> instead of drawing a brow.</summary>
    [DataField] public bool Glow;
    [DataField] public Color GlowColor = Color.FromHex("#ff3020");
    /// <summary>No face overlay at all.</summary>
    [DataField] public bool Disabled;
}

/// <summary>Pure, deterministic maths of the v12 hit effects and face layers (client overlay + preview mirror).</summary>
public static class ProcAnimFx
{
    public static readonly string[] Faces = { "neutral", "pain", "dazed", "angry", "dead" };
    public static readonly string[] Eyes = { "open", "closed", "narrowed" };

    /// <summary>A pixel quad: centre (forward-frame px), size (px), colour with alpha.</summary>
    public readonly record struct Dot(Vector2 Position, Vector2 Size, Color Color);

    public static int CountOf(ProcAnimEffect fx) => fx.Count >= 0 ? fx.Count : fx.Type switch
    {
        ProcAnimEffectType.Spatter => 14,
        ProcAnimEffectType.Bruise => 4,
        ProcAnimEffectType.Drips => 6,
        ProcAnimEffectType.Dust => 10,
        _ => 0,
    };

    public static float DurationOf(ProcAnimEffect fx) => fx.Duration >= 0f ? fx.Duration : fx.Type switch
    {
        ProcAnimEffectType.Spatter => 8f,
        ProcAnimEffectType.Recoil => 0.35f,
        ProcAnimEffectType.Bruise => 0.6f,
        ProcAnimEffectType.Drips => 3f,
        ProcAnimEffectType.Pool => 4f,
        ProcAnimEffectType.Dust => 0.8f,
        ProcAnimEffectType.Shake => 0.3f,
        ProcAnimEffectType.Stagger => 2.5f,
        _ => 0f,
    };

    /// <summary>World-anchored at the hit (drawn as a burst that outlives the pose); else attached to the bone.</summary>
    public static bool Anchored(ProcAnimEffectType type) =>
        type is ProcAnimEffectType.Spatter or ProcAnimEffectType.Pool or ProcAnimEffectType.Dust;

    /// <summary>Particle effects (drawn); Recoil / Stagger / Shake only move the actor.</summary>
    public static bool Drawn(ProcAnimEffectType type) =>
        type is ProcAnimEffectType.Spatter or ProcAnimEffectType.Pool or ProcAnimEffectType.Dust
            or ProcAnimEffectType.Bruise or ProcAnimEffectType.Drips;

    /// <summary>Seconds an anchored burst stays drawn.</summary>
    public static float Life(ProcAnimEffect fx) => fx.Type switch
    {
        ProcAnimEffectType.Pool => DurationOf(fx) + 60f,
        _ => DurationOf(fx) + 0.05f,
    };

    /// <summary>Uses the blood colour unless <see cref="ProcAnimEffect.Color"/> is set.</summary>
    public static bool Bloody(ProcAnimEffectType type) =>
        type is ProcAnimEffectType.Spatter or ProcAnimEffectType.Drips or ProcAnimEffectType.Pool;

    public static Color ColorOf(ProcAnimEffect fx, Color blood) => fx.Color ?? fx.Type switch
    {
        ProcAnimEffectType.Bruise => Color.FromHex("#5a1f2b"),
        ProcAnimEffectType.Dust => Color.FromHex("#8e877b"),
        _ => blood,
    };

    private static Color Shade(Color c, float k, float a) =>
        new(MathF.Min(1f, c.R * k), MathF.Min(1f, c.G * k), MathF.Min(1f, c.B * k), c.A * Math.Clamp(a, 0f, 1f));

    private static float Smooth(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    /// <summary>
    /// Particles of a drawn effect <paramref name="age"/> s after it started. <paramref name="origin"/>: the effect point
    /// (forward-frame px; for anchored effects relative to the target position at the hit, else to its current one).
    /// <paramref name="seed"/>: event index * 16 + effect index.
    /// </summary>
    public static List<Dot> Particles(ProcAnimEffect fx, int seed, Vector2 origin, float age, Color blood)
    {
        var res = new List<Dot>();
        if (age < 0f)
            return res;
        var color = ColorOf(fx, blood);
        var n = CountOf(fx);
        var dur = DurationOf(fx);
        var dir = fx.Dir * MathF.PI / 180f;
        switch (fx.Type)
        {
            case ProcAnimEffectType.Spatter:
            {
                if (age > dur)
                    break;
                var fade = age < dur - 1f ? 1f : MathF.Max(0f, dur - age);
                // Mist: a fine short-lived spray at the wound along the hit direction.
                if (age < 0.12f)
                {
                    var u = age / 0.12f;
                    for (var i = 0; i < 4; i++)
                    {
                        var a = dir + (ProcAnimTimeline.Rnd(seed, i, 20) - 0.5f) * fx.Spread * 0.6f * MathF.PI / 180f;
                        var r = (2f + 6f * u) * (0.6f + 0.6f * ProcAnimTimeline.Rnd(seed, i, 21)) * fx.Strength;
                        res.Add(new Dot(origin + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r, Vector2.One,
                            Shade(color, 1.25f, 0.55f * (1f - u))));
                    }
                }

                const float g = 380f;
                for (var i = 0; i < n; i++)
                {
                    var a = dir + (ProcAnimTimeline.Rnd(seed, i, 0) - 0.5f) * fx.Spread * MathF.PI / 180f;
                    var s = (50f + 110f * ProcAnimTimeline.Rnd(seed, i, 1)) * fx.Strength;
                    var big = ProcAnimTimeline.Rnd(seed, i, 2) < 0.3f;
                    var floor = fx.Floor + (ProcAnimTimeline.Rnd(seed, i, 4) - 0.5f) * 4f;
                    var tt = age - ProcAnimTimeline.Rnd(seed, i, 5) * 0.03f;
                    if (tt < 0f)
                        continue;
                    var vx = s * MathF.Cos(a);
                    var vy = s * MathF.Sin(a);
                    var disc = vy * vy + 2f * g * (origin.Y - floor);
                    var land = disc >= 0f ? (vy + MathF.Sqrt(disc)) / g : float.MaxValue;
                    if (tt >= land)
                    {
                        // Stain: stretched along the travel, darker as it dries.
                        var len = Math.Clamp(1f + MathF.Abs(vx) / 60f, 1f, 3f) + (big ? 1f : 0f);
                        var dry = 0.85f - 0.25f * Smooth((tt - land) / 3f);
                        res.Add(new Dot(new Vector2(origin.X + vx * land + MathF.Sign(vx) * (len - 1f) * 0.5f, floor),
                            new Vector2(len, 1f), Shade(color, dry * (0.8f + 0.3f * ProcAnimTimeline.Rnd(seed, i, 3)), 0.9f * fade)));
                    }
                    else
                    {
                        var sz = big ? 2f : 1f;
                        res.Add(new Dot(new Vector2(origin.X + vx * tt, origin.Y + vy * tt - 0.5f * g * tt * tt),
                            new Vector2(sz), Shade(color, 0.85f + 0.3f * ProcAnimTimeline.Rnd(seed, i, 3), fade)));
                    }
                }

                break;
            }
            case ProcAnimEffectType.Bruise:
            {
                var a = 0.75f * Smooth(dur > 0f ? age / dur : 1f);
                for (var i = 0; i < n; i++)
                {
                    var off = new Vector2(MathF.Round((ProcAnimTimeline.Rnd(seed, i, 0) - 0.5f) * 3f * fx.Size),
                        MathF.Round((ProcAnimTimeline.Rnd(seed, i, 1) - 0.5f) * 2f * fx.Size));
                    res.Add(new Dot(origin + off, Vector2.One,
                        Shade(color, 0.75f + 0.45f * ProcAnimTimeline.Rnd(seed, i, 2), a * (i == 0 ? 1f : 0.8f))));
                }

                break;
            }
            case ProcAnimEffectType.Drips:
            {
                const float g = 300f;
                for (var i = 0; i < n; i++)
                {
                    var release = 0.1f + dur * (i + 0.6f * ProcAnimTimeline.Rnd(seed, i, 0)) / MathF.Max(1, n);
                    var x = origin.X + MathF.Round((ProcAnimTimeline.Rnd(seed, i, 1) - 0.5f) * 2f);
                    var tt = age - release;
                    if (tt < -0.35f)
                        continue;
                    if (tt < 0f)
                    {
                        // Swelling drop hanging from the wound.
                        var grow = 1f + tt / 0.35f;
                        res.Add(new Dot(new Vector2(x, origin.Y - 0.5f - grow), Vector2.One, Shade(color, 0.9f, 0.6f + 0.4f * grow)));
                        continue;
                    }

                    var fall = origin.Y - fx.Floor;
                    var land = fall > 0f ? MathF.Sqrt(2f * fall / g) : 0f;
                    if (tt < land)
                    {
                        var v = g * tt;
                        res.Add(new Dot(new Vector2(x, origin.Y - 0.5f * g * tt * tt), new Vector2(1f, v > 70f ? 2f : 1f),
                            Shade(color, 0.95f, 1f)));
                    }
                    else if (tt < land + 1.5f)
                        res.Add(new Dot(new Vector2(x, fx.Floor), new Vector2(2f, 1f),
                            Shade(color, 0.7f, 0.85f * (1f - (tt - land) / 1.5f))));
                }

                break;
            }
            case ProcAnimEffectType.Pool:
            {
                var u = dur > 0f ? MathF.Min(1f, age / dur) : 1f;
                var r = fx.Size * 7f * MathF.Sqrt(u) * (1f - 0.15f * (1f - u));
                if (r < 0.75f)
                    break;
                var ry = MathF.Max(1f, r * 0.4f);
                var rows = (int) MathF.Ceiling(ry);
                for (var y = -rows; y <= rows; y++)
                {
                    var q = 1f - (y / (ry + 0.5f)) * (y / (ry + 0.5f));
                    if (q <= 0f)
                        continue;
                    var hw = r * MathF.Sqrt(q) * (0.85f + 0.3f * ProcAnimTimeline.Rnd(seed, y + 50, 0));
                    if (hw < 0.5f)
                        continue;
                    var cx = origin.X + (ProcAnimTimeline.Rnd(seed, y + 50, 1) - 0.5f) * 1.2f;
                    var edge = MathF.Abs(y) == rows;
                    res.Add(new Dot(new Vector2(cx, fx.Floor + y), new Vector2(2f * hw, 1f),
                        Shade(color, edge ? 0.62f : 0.5f, edge ? 0.7f : 0.88f)));
                }

                break;
            }
            case ProcAnimEffectType.Dust:
            {
                if (age > dur)
                    break;
                var spread = fx.Spread * MathF.PI / 360f;
                for (var i = 0; i < n; i++)
                {
                    var tt = age - ProcAnimTimeline.Rnd(seed, i, 3) * 0.06f;
                    if (tt < 0f)
                        continue;
                    var u = tt / dur;
                    var side = ProcAnimTimeline.Rnd(seed, i, 0) < 0.5f ? -1f : 1f;
                    var v = (12f + 40f * ProcAnimTimeline.Rnd(seed, i, 1)) * fx.Strength;
                    var x = origin.X + side * v * dur / 3f * (1f - MathF.Exp(-3f * u));
                    var y = fx.Floor + (2f + 5f * ProcAnimTimeline.Rnd(seed, i, 2)) * MathF.Sin(spread) * 1.4f * (1f - MathF.Exp(-2.5f * u));
                    var sz = 1f + 2f * u;
                    res.Add(new Dot(new Vector2(x, y), new Vector2(sz), Shade(color, 1f, 0.55f * MathF.Pow(1f - u, 1.5f))));
                }

                // Grit: a few dark specks hopping out.
                const float g = 400f;
                for (var i = 0; i < n / 3; i++)
                {
                    var side = ProcAnimTimeline.Rnd(seed, i, 10) < 0.5f ? -1f : 1f;
                    var vx = side * (30f + 50f * ProcAnimTimeline.Rnd(seed, i, 11)) * fx.Strength;
                    var vy = (40f + 50f * ProcAnimTimeline.Rnd(seed, i, 12)) * fx.Strength;
                    var land = 2f * vy / g;
                    var tt = MathF.Min(age, land);
                    res.Add(new Dot(new Vector2(origin.X + vx * tt, fx.Floor + vy * tt - 0.5f * g * tt * tt), Vector2.One,
                        Shade(color, 0.45f, 1f - Smooth((age - dur * 0.6f) / (dur * 0.4f)))));
                }

                break;
            }
        }

        return res;
    }

    /// <summary>Spawn point of an effect on the pose (forward-frame px relative to the entity).</summary>
    public static Vector2 Origin(ProcAnimEffect fx, ProcAnimPose[] pose)
    {
        var p = ProcAnimTimeline.Point(pose, fx.Part, fx.At);
        // Pools / dust lie on the floor under the point.
        return fx.Type is ProcAnimEffectType.Pool or ProcAnimEffectType.Dust ? new Vector2(p.X, fx.Floor) : p;
    }

    /// <summary>Recoil impulse envelope: rises to 1 at ~18 % of the duration, then a damped return with a slight overshoot.</summary>
    public static float RecoilEnvelope(float age, float dur)
    {
        if (age < 0f || dur <= 0f || age >= dur)
            return 0f;
        var tau = dur * 0.18f;
        var u = age / tau;
        var main = u * MathF.Exp(1f - u);
        var over = -0.18f * MathF.Sin(MathF.PI * age / dur) * Smooth(age / tau - 1f);
        return (main + over) * (1f - Smooth((age - dur * 0.8f) / (dur * 0.2f)));
    }

    /// <summary>
    /// Adds the Recoil and Stagger pose layers of the events on <paramref name="actor"/> up to <paramref name="t"/> (anim
    /// time, so they hold during the hit-stop and play out right after it).
    /// </summary>
    public static void ApplyPose(ProcAnimScenePrototype proto, int actor, float t, Vector3[] pose, float f)
    {
        foreach (var ev in proto.Events)
        {
            if (ev.Time > t)
                break;
            if (ev.TargetIndex != actor || ev.Effects.Count == 0)
                continue;
            var age = t - ev.Time;
            foreach (var fx in ev.Effects)
            {
                var dur = DurationOf(fx);
                if (age >= dur)
                    continue;
                if (fx.Type == ProcAnimEffectType.Recoil)
                {
                    var e = RecoilEnvelope(age, dur) * fx.Strength;
                    // Dir is in the forward frame; a limb / head offset lives in the body's (rotated) frame.
                    var dir = fx.Part == ProcAnimBone.Body ? fx.Dir : fx.Dir - pose[(int) ProcAnimBone.Body].Z;
                    var (sin, cos) = MathF.SinCos(dir * MathF.PI / 180f);
                    ref var p = ref pose[(int) fx.Part];
                    p.X += 2f * cos * e;
                    p.Y += 2f * sin * e;
                    p.Z += -10f * cos * e;
                    if (fx.Part == ProcAnimBone.Body)
                        pose[(int) ProcAnimBone.Body].Y -= 0.8f * MathF.Abs(e); // squash down into the hit
                    if (fx.Part == ProcAnimBone.Head)
                        pose[(int) ProcAnimBone.Body].Z += -2.5f * cos * e; // the neck pulls the body a little
                }
                else if (fx.Type == ProcAnimEffectType.Stagger)
                {
                    var env = Smooth(age / 0.15f) * (1f - age / dur) * (1f - age / dur) * fx.Strength;
                    var ph = 2f * MathF.PI * age / 1.1f;
                    pose[(int) ProcAnimBone.Body].Z += 4f * env * MathF.Sin(ph);
                    pose[(int) ProcAnimBone.Body].X += 1.5f * env * MathF.Sin(ph + 0.6f);
                    pose[(int) ProcAnimBone.Head].Z += 8f * env * MathF.Sin(ph - 1f);
                    var buckle = env * MathF.Max(0f, MathF.Sin(2f * ph));
                    pose[(int) ProcAnimBone.LegNear].Z -= f * 6f * buckle;
                    pose[(int) ProcAnimBone.LegNearLow].Z += f * 9f * buckle;
                    pose[(int) ProcAnimBone.LegFar].Z -= f * 3f * buckle;
                    pose[(int) ProcAnimBone.LegFarLow].Z += f * 5f * buckle;
                    pose[(int) ProcAnimBone.ArmNear].Z += 6f * env * MathF.Sin(ph - 1.4f);
                    pose[(int) ProcAnimBone.ArmFar].Z += 5f * env * MathF.Sin(ph - 1.8f);
                }
            }
        }
    }

    /// <summary>Local actor shake (forward-frame px) at <paramref name="real"/> seconds of real time.</summary>
    public static Vector2 Shake(ProcAnimScenePrototype proto, int actor, float real)
    {
        var res = Vector2.Zero;
        foreach (var ev in proto.Events)
        {
            if (ev.TargetIndex != actor || ev.Effects.Count == 0)
                continue;
            var age = real - ProcAnimTimeline.RealTime(proto, ev.Time);
            if (age < 0f)
                continue;
            foreach (var fx in ev.Effects)
            {
                if (fx.Type != ProcAnimEffectType.Shake)
                    continue;
                var dur = DurationOf(fx);
                if (age >= dur)
                    continue;
                var k = 1.5f * fx.Strength * (1f - age / dur) * (1f - age / dur);
                res += new Vector2(k * MathF.Sin(age * 90f), 0.45f * k * MathF.Sin(age * 67f + 1f));
            }
        }

        return res;
    }

    /// <summary>
    /// Expression overlay pixels (forward-frame px relative to the entity) for the face / eyes keys at <paramref name="t"/>.
    /// <paramref name="faceSign"/>: +1 when the face points +x in the forward frame, -1 for -x; <paramref name="front"/>:
    /// drawn facing the camera (South view); null view = no face (e.g. back to the camera).
    /// </summary>
    public static List<Dot> FaceDots(ProcAnimFace cfg, ProcAnimExpression expr, ProcAnimPose[] pose, float faceSign, bool front)
    {
        var res = new List<Dot>();
        if (cfg.Disabled)
            return res;
        var face = expr.Face ?? "neutral";
        var eyes = expr.Eyes ?? "open";
        void Px(Vector2 at, Color c) => res.Add(new Dot(ProcAnimTimeline.Point(pose, ProcAnimBone.Head, at), Vector2.One, c));

        var eyeList = front ? cfg.FrontEyes : new List<Vector2> { new(faceSign * cfg.Eye.X, cfg.Eye.Y) };
        var mouth = front ? cfg.FrontMouth : new Vector2(faceSign * cfg.Mouth.X, cfg.Mouth.Y);
        var u = front ? 1f : faceSign; // "towards the face front" in px
        foreach (var e in eyeList)
        {
            switch (face)
            {
                case "dead":
                    if (cfg.Glow)
                    {
                        Px(e, cfg.DeadColor);
                        Px(e + new Vector2(-u, 0), cfg.DeadColor);
                        break;
                    }
                    Px(e + new Vector2(-u, 1), cfg.DeadColor);
                    Px(e + new Vector2(u, 1), cfg.DeadColor);
                    Px(e, cfg.DeadColor);
                    Px(e + new Vector2(-u, -1), cfg.DeadColor);
                    Px(e + new Vector2(u, -1), cfg.DeadColor);
                    continue;
                case "pain":
                    if (!cfg.Glow)
                    {
                        Px(e + new Vector2(-u, 0), cfg.Lid);
                        Px(e, cfg.Lid);
                        Px(e + new Vector2(0, 1), cfg.Brow);
                        Px(e + new Vector2(-u, 1), cfg.Brow);
                    }
                    continue;
                case "dazed":
                    if (!cfg.Glow)
                    {
                        Px(e + new Vector2(-u, 0), cfg.Lid);
                        Px(e, cfg.Lid);
                    }
                    continue;
                case "angry":
                    if (cfg.Glow)
                    {
                        Px(e, cfg.GlowColor);
                        Px(e + new Vector2(-u, 0), cfg.GlowColor);
                        Px(e + new Vector2(-u, 1), Shade(cfg.GlowColor, 0.6f, 0.8f));
                    }
                    else
                    {
                        Px(e + new Vector2(-u, 2), cfg.Brow);
                        Px(e + new Vector2(0, 1), cfg.Brow);
                    }
                    break;
            }

            if (cfg.Glow)
                continue;
            if (eyes == "closed")
            {
                Px(e + new Vector2(-u, 0), cfg.Lid);
                Px(e, cfg.Lid);
            }
            else if (eyes == "narrowed")
                Px(e + new Vector2(0, 1), cfg.Lid);
        }

        if (!cfg.Glow)
        {
            switch (face)
            {
                case "pain":
                    Px(mouth, cfg.MouthColor);
                    Px(mouth + new Vector2(-u, 0), cfg.MouthColor);
                    break;
                case "dazed":
                case "dead":
                    Px(mouth, cfg.MouthColor);
                    break;
            }
        }

        return res;
    }

    /// <summary>
    /// v12 view-change squash: horizontal sprite scale in [0.3, 1], dipping around every change of the forced view /
    /// facing (<see cref="ProcAnimActor.Views"/> edges), so turning reads as a turn instead of a one-frame flip.
    /// </summary>
    public static float ViewFlipScale(ProcAnimActor tr, float t, float width = 0.07f)
    {
        if (tr.Views.Count == 0 || width <= 0f)
            return 1f;
        var best = float.MaxValue;
        foreach (var v in tr.Views)
        {
            foreach (var edge in new[] { v.Start, v.End })
            {
                // Only edges where the drawn view actually changes.
                var before = ProcAnimTimeline.ViewInfoAt(tr, edge - 1e-3f);
                var after = ProcAnimTimeline.ViewInfoAt(tr, edge + 1e-3f);
                if (Same(before, after))
                    continue;
                best = MathF.Min(best, MathF.Abs(t - edge));
            }
        }

        if (best >= width)
            return 1f;
        return 0.3f + 0.7f * Smooth(best / width);
    }

    private static bool Same(ProcAnimView? a, ProcAnimView? b)
    {
        if (a == null || b == null)
            return a == b;
        return a.Turned == b.Turned && a.Facing == b.Facing;
    }
}
