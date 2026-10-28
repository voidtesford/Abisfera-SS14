using System.Numerics;
using Vector3 = System.Numerics.Vector3;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._CorvaxGoob.Gilbert.Finisher;

/// <summary>
/// Keyframed cutscene played on two humanoids (attacker = Gilbert, victim = anyone).
/// All tracks are written in the "forward frame": +X points from the attacker towards the victim, +Y is up on
/// screen, rotations are degrees, counter-clockwise positive when +X points right (east).
/// Body / part offsets are in pixels (32 px = 1 tile); <see cref="GilbertFinisherActorTracks.Move"/> is in tiles.
/// The same YAML drives the server (world movement, sounds), the client (sprite layers, shake, flash)
/// and the offline preview renderer, so the preview matches the game.
/// </summary>
[Prototype("gilbertFinisher")]
public sealed partial class GilbertFinisherPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Total length, seconds.</summary>
    [DataField]
    public float Length = 6.8f;

    /// <summary>Where the victim is pulled to at t=0, tiles in front of the attacker.</summary>
    [DataField]
    public float VictimStart = 0.85f;

    /// <summary>
    /// Holster beat: keys / events at animation time -Intro..0 are only played when the attacker holds an item when
    /// the cutscene starts (otherwise playback starts at 0), so the rest of the timeline is shifted by it.
    /// </summary>
    [DataField]
    public float Intro;

    /// <summary>Free floor needed in front of the attacker (tiles) to play the full motion without clamping.</summary>
    [DataField]
    public float RequiredRoom = 3.9f;

    /// <summary>
    /// Server: the victim's mob state is held at what it was when the cutscene started (no crit / death from the
    /// damage beats) until an event with <see cref="GilbertFinisherEvent.Kill"/> or the end of the cutscene.
    /// </summary>
    [DataField]
    public bool HoldAlive;

    [DataField]
    public GilbertFinisherActorTracks Attacker = new();

    [DataField]
    public GilbertFinisherActorTracks Victim = new();

    /// <summary>Sorted by time. Index in this list seeds the deterministic blood bursts.</summary>
    [DataField]
    public List<GilbertFinisherEvent> Events = new();
}

[DataDefinition]
public sealed partial class GilbertFinisherActorTracks
{
    /// <summary>World movement along the forward axis, tiles, relative to the attacker's start position.</summary>
    [DataField]
    public List<GilbertFinisherKey> Move = new();

    [DataField]
    public Dictionary<GilbertFinisherPart, GilbertFinisherPartTrack> Parts = new();

    /// <summary>Held items are drawn slung on the back (their <see cref="HolsterStates"/>) instead of in the hands.</summary>
    [DataField]
    public bool HolsterHeldItems;

    /// <summary>RSI states tried, in order, for a holstered in-hand item.</summary>
    [DataField]
    public List<string> HolsterStates = new() { "equipped-BACKPACK", "equipped-SUITSTORAGE" };

    /// <summary>
    /// Animation time at which held items move from the hands to the back (the holster swing ends); before it they
    /// are drawn in the hands as their own layers (<see cref="GilbertFinisherPart.HeldNear"/> / HeldFar, carried by
    /// the hands). Null: slung on the back from the start.
    /// </summary>
    [DataField]
    public float? HolsterAt;

    /// <summary>Animation time from which the holstered items are back in the hands (drawn by the hands again).</summary>
    [DataField]
    public float? UnholsterAt;

    /// <summary>Extra sprites carried by a part for a while (e.g. a baton drawn from the belt). Purely visual.</summary>
    [DataField]
    public List<GilbertFinisherProp> Props = new();

    /// <summary>
    /// Intervals drawn with a fixed screen direction (e.g. <c>North</c>: back to the camera). The keys inside are
    /// written for that view: x = screen right (mirrored like the side view), y = screen up.
    /// </summary>
    [DataField]
    public List<GilbertFinisherView> Views = new();

    /// <summary>Procedural walk cycle on top of the keys, phase-locked to the distance travelled along Move.</summary>
    [DataField]
    public GilbertFinisherWalk? Walk;

    /// <summary>Breathing / weight shifts while standing (fades out while walking).</summary>
    [DataField]
    public GilbertFinisherIdle? Idle;

    /// <summary>Head look-at towards the partner.</summary>
    [DataField]
    public GilbertFinisherLook? Look;

    /// <summary>
    /// Part keys with the default (InOut) ease are joined by a monotone cubic spline instead of stopping at every
    /// key, so the motion flows through them. Explicit In / Out / Linear / Step keys keep their sharp timing.
    /// </summary>
    [DataField]
    public bool Smooth;

    /// <summary>Secondary motion: head / arms trail the body, forearms / shins trail the upper segments.</summary>
    [DataField]
    public GilbertFinisherDrag? Drag;

    /// <summary>Concussion: weight-keyed drunken sway, lolling head, dangling arms, buckling knees, dizzy stars.</summary>
    [DataField]
    public GilbertFinisherDaze? Daze;

    /// <summary>
    /// Thrown section of <see cref="Move"/>: the server shortens the displacement after <c>start</c> (uniformly) so
    /// the actor never lands in a wall or off the floor.
    /// </summary>
    [DataField]
    public GilbertFinisherFlight? Flight;

    /// <summary>Parts that stop being drawn at some point (torn off).</summary>
    [DataField]
    public List<GilbertFinisherHide> Hide = new();
}

[DataDefinition]
public sealed partial class GilbertFinisherView
{
    [DataField(required: true)] public float Start;
    [DataField(required: true)] public float End;
    /// <summary>Screen direction the sprite is drawn with (North = back to the camera).</summary>
    [DataField] public Direction Facing = Direction.North;

    /// <summary>
    /// Instead of <see cref="Facing"/>: the actor is drawn side-on but facing the other way (e.g. the victim lying
    /// with its back to the attacker). Keys inside are authored in the attacker's conventions (+ = towards the
    /// attacker's facing), joint limits are skipped like for any view.
    /// </summary>
    [DataField] public bool Turned;
}

[DataDefinition]
public sealed partial class GilbertFinisherDrag
{
    /// <summary>How far behind (seconds) the dragged parts follow.</summary>
    [DataField] public float Lag = 0.08f;
    [DataField] public float Head = 0.5f;
    [DataField] public float Arms = 0.4f;
    /// <summary>Forearms behind the upper arms.</summary>
    [DataField] public float Low = 0.35f;
    [DataField] public float Legs;
    /// <summary>Shins behind the thighs.</summary>
    [DataField] public float LegsLow;
    /// <summary>Hands behind the forearms.</summary>
    [DataField] public float Hands = 0.3f;
    /// <summary>Clamp, degrees.</summary>
    [DataField] public float Max = 25f;
}

[DataDefinition]
public sealed partial class GilbertFinisherDaze
{
    /// <summary>Sway cycle, seconds.</summary>
    [DataField] public float Period = 1.4f;
    /// <summary>Body roll, degrees.</summary>
    [DataField] public float Sway = 6f;
    /// <summary>Body side-to-side shift, px.</summary>
    [DataField] public float Shift = 1f;
    /// <summary>Knee-buckle dip, px.</summary>
    [DataField] public float Dip = 1f;
    /// <summary>Head loll (phase-lagged behind the body), degrees.</summary>
    [DataField] public float Loll = 12f;
    [DataField] public float Arms = 10f;
    [DataField] public float Knees = 6f;
    /// <summary>Intensity over time (key X, 0..1); empty = <see cref="Amount"/>.</summary>
    [DataField] public List<GilbertFinisherKey> Weight = new();
    [DataField] public float Amount = 1f;
    [DataField] public GilbertFinisherStars? Stars;
}

[DataDefinition]
public sealed partial class GilbertFinisherStars
{
    [DataField] public GilbertFinisherPart From = GilbertFinisherPart.Head;
    /// <summary>Orbit centre, rest-pose px on <see cref="From"/>.</summary>
    [DataField] public Vector2 At = new(0, 13);
    [DataField] public int Count = 3;
    /// <summary>Orbit ellipse radii, px.</summary>
    [DataField] public Vector2 Radius = new(6, 2);
    [DataField] public float Period = 0.9f;
    [DataField] public Color Color = Color.FromHex("#ffe066");
    /// <summary>Shown while the daze weight is above this.</summary>
    [DataField] public float Threshold = 0.5f;
}

[DataDefinition]
public sealed partial class GilbertFinisherFlight
{
    [DataField] public float Start;
    /// <summary>Time the planned landing spot is reached (the displacement at this time is what gets checked).</summary>
    [DataField] public float End;
}

[DataDefinition]
public sealed partial class GilbertFinisherWalk
{
    /// <summary>Tiles per step (half a cycle).</summary>
    [DataField] public float Stride = 0.45f;
    /// <summary>Move speed (tiles/s) at which the cycle reaches full amplitude.</summary>
    [DataField] public float FullSpeed = 0.7f;
    [DataField] public float LegSwing = 24f;
    [DataField] public float Knee = 30f;
    [DataField] public float ArmSwing = 18f;
    [DataField] public float Elbow = 12f;
    [DataField] public float Shoulder = 0.8f;
    [DataField] public float Bob = 1f;
    [DataField] public float Lean = 4f;
    [DataField] public float Sway = 1f;
    [DataField] public float Nod = 1.5f;
    /// <summary>Optional amplitude multiplier over time (X), e.g. to blend a stagger in or out.</summary>
    [DataField] public List<GilbertFinisherKey> Weight = new();
}

[DataDefinition]
public sealed partial class GilbertFinisherIdle
{
    [DataField] public float Period = 2.6f;
    [DataField] public float Breath = 0.5f;
    [DataField] public float WeightShift = 1f;
    [DataField] public float HeadNod = 1.5f;
    [DataField] public float ArmSway = 2f;
}

[DataDefinition]
public sealed partial class GilbertFinisherLook
{
    /// <summary>Own eye point on the head (px, rest pose, forward frame).</summary>
    [DataField] public Vector2 At = new(0, 9);
    [DataField] public GilbertFinisherPart Target = GilbertFinisherPart.Head;
    /// <summary>Point looked at on the partner's <see cref="Target"/> part.</summary>
    [DataField] public Vector2 TargetAt = new(0, 9);
    /// <summary>Max absolute head pitch, degrees.</summary>
    [DataField] public float Max = 30f;
    /// <summary>Max head rotation relative to the body, degrees.</summary>
    [DataField] public float Neck = 40f;
    /// <summary>Look weight over time (X); constant <see cref="Weight"/> when empty.</summary>
    [DataField] public List<GilbertFinisherKey> Keys = new();
    [DataField] public float Weight = 1f;
}

[DataDefinition]
public sealed partial class GilbertFinisherPartTrack
{
    /// <summary>Rotation pivot in pixels relative to the sprite centre (forward frame): neck, shoulder, hip...</summary>
    [DataField]
    public Vector2 Pivot;

    [DataField]
    public List<GilbertFinisherKey> Keys = new();
}

[DataDefinition]
public sealed partial class GilbertFinisherKey
{
    [DataField("t")]
    public float Time;

    [DataField]
    public float X;

    [DataField]
    public float Y;

    [DataField]
    public float Rot;

    /// <summary>Easing of the segment that ends at this key.</summary>
    [DataField]
    public GilbertFinisherEase Ease = GilbertFinisherEase.InOut;
}

[DataDefinition]
public sealed partial class GilbertFinisherEvent
{
    [DataField("t")]
    public float Time;

    /// <summary>Who the sound / flash happens on.</summary>
    [DataField]
    public GilbertFinisherRole Target = GilbertFinisherRole.Victim;

    [DataField]
    public SoundSpecifier? Sound;

    /// <summary>Camera kick strength for nearby viewers (0 = none).</summary>
    [DataField]
    public float Shake;

    /// <summary>Brief white-red tint on the target.</summary>
    [DataField]
    public bool Flash;

    /// <summary>Human readable beat name (preview captions).</summary>
    [DataField]
    public string? Beat;

    /// <summary>Freezes the animation for this long at the event (impact hit-stop); adds to the total length.</summary>
    [DataField]
    public float HitStop;

    [DataField]
    public GilbertFinisherBlood? Blood;

    /// <summary>Server-side floor decal left at the event (e.g. a blood splatter).</summary>
    [DataField]
    public GilbertFinisherDecal? Decal;

    /// <summary>
    /// Server: damage dealt to the victim at this beat, each entry to one body part. Applied with resistances ignored
    /// (armour and the damage modifier sets do nothing), see GilbertFinisherSystem.
    /// </summary>
    [DataField]
    public List<GilbertFinisherHit> Damage = new();

    /// <summary>Server: victim body parts torn off at this beat (Shitmed amputation: the limb drops as an item).</summary>
    [DataField]
    public List<TargetBodyPart> Amputate = new();

    /// <summary>
    /// Server: after the damage / amputation of this beat, releases <see cref="GilbertFinisherPrototype.HoldAlive"/>
    /// and kills the victim (tops the damage up to the death threshold, then forces the dead state if still alive).
    /// </summary>
    [DataField]
    public bool Kill;

    /// <summary>Preview only: put this beat on the contact sheet.</summary>
    [DataField]
    public bool Sheet;

    /// <summary>Preview only: contact sheet frame delay after the event.</summary>
    [DataField]
    public float SheetDelay;
}

[DataDefinition]
public sealed partial class GilbertFinisherHit
{
    /// <summary>Single body part the damage goes to.</summary>
    [DataField]
    public TargetBodyPart Part = TargetBodyPart.Head;

    [DataField(required: true)]
    public DamageSpecifier Damage = new();

    /// <summary>Armour / resistances skipped (the finisher always goes through).</summary>
    [DataField]
    public bool IgnoreArmor = true;
}

/// <summary>
/// A sprite drawn on top of one part between <see cref="Start"/> and <see cref="End"/>: its centre sits at the keyed
/// (x, y) of the part's frame (px, the rest-pose sprite frame, forward = +x like the part's keys) rotated by the keyed
/// rot. Mirrored with the actor. A prop handed over to the other actor is simply a second prop on that actor.
/// </summary>
[DataDefinition]
public sealed partial class GilbertFinisherProp
{
    /// <summary>RSI path (under Textures/).</summary>
    [DataField(required: true)]
    public ResPath Sprite;

    [DataField]
    public string State = "icon";

    [DataField]
    public float Start;

    [DataField]
    public float End = float.MaxValue;

    [DataField]
    public GilbertFinisherPart Part = GilbertFinisherPart.Body;

    [DataField]
    public List<GilbertFinisherKey> Keys = new();

    [DataField]
    public float Scale = 1f;
}

/// <summary>From <see cref="Time"/> on, these parts are no longer drawn (e.g. torn-off arms).</summary>
[DataDefinition]
public sealed partial class GilbertFinisherHide
{
    [DataField("t")]
    public float Time;

    [DataField]
    public List<GilbertFinisherPart> Parts = new();
}

/// <summary>Client-side blood burst: deterministic ballistic droplets that land and fade.</summary>
[DataDefinition]
public sealed partial class GilbertFinisherBlood
{
    [DataField] public GilbertFinisherPart From = GilbertFinisherPart.Head;
    /// <summary>Spawn point on <see cref="From"/> (px, rest pose, forward frame).</summary>
    [DataField] public Vector2 At = new(0, 9);
    [DataField] public int Count = 12;
    /// <summary>Min / max launch speed, px/s.</summary>
    [DataField] public Vector2 Speed = new(40, 110);
    /// <summary>Min / max launch angle, degrees, forward frame (0 = away from the attacker, 90 = up).</summary>
    [DataField] public Vector2 Angle = new(30, 150);
    [DataField] public float Gravity = 320f;
    /// <summary>Floor height, px relative to the target entity origin.</summary>
    [DataField] public float Floor = -14f;
    [DataField] public float FloorJitter = 4f;
    [DataField] public float Life = 1.6f;
    [DataField] public float BigChance = 0.35f;
    [DataField] public float Stagger = 0.04f;
    /// <summary>Overrides the target's blood reagent colour.</summary>
    [DataField] public Color? Color;
}

[DataDefinition]
public sealed partial class GilbertFinisherDecal
{
    [DataField(required: true)] public string Id = default!;
    [DataField] public GilbertFinisherPart From = GilbertFinisherPart.Head;
    [DataField] public Vector2 At = new(0, 9);
    /// <summary>Tint; RGB is replaced by the target's blood colour, alpha is kept.</summary>
    [DataField] public Color Color = Color.FromHex("#80000099");
}

[Serializable, NetSerializable]
public enum GilbertFinisherRole : byte
{
    Attacker,
    Victim,
}

/// <summary>Near = the limb on the side facing the camera in a side view (right side when facing east).</summary>
[Serializable, NetSerializable]
public enum GilbertFinisherPart : byte
{
    Body,
    Head,
    ArmNear,
    ArmFar,
    LegNear,
    LegFar,
    /// <summary>Forearm, rotates about the elbow and is carried by the upper arm.</summary>
    ArmNearLow,
    ArmFarLow,
    /// <summary>Shin, rotates about the knee and is carried by the thigh.</summary>
    LegNearLow,
    LegFarLow,
    /// <summary>Hand (+ gloves) grip point: rigid with the forearm since v10 (always 0; the held item still rotates about it).</summary>
    ArmNearHand,
    ArmFarHand,
    /// <summary>Reserved (legs are drawn in two segments, the shoe rides on the shin): held at 0 by ApplyJointLimits.</summary>
    LegNearFoot,
    LegFarFoot,
    /// <summary>Item held in the near / far hand (until <see cref="GilbertFinisherActorTracks.HolsterAt"/>), carried by the hand.</summary>
    HeldNear,
    HeldFar,
}

[Serializable, NetSerializable]
public enum GilbertFinisherEase : byte
{
    Linear,
    In,
    Out,
    InOut,
    /// <summary>Jumps to this key's value at its time.</summary>
    Step,
}

/// <summary>Evaluated pose of one part: translation in pixels, rotation in degrees (forward frame).</summary>
public readonly record struct GilbertFinisherPose(float X, float Y, float Rot);

public static class GilbertFinisherTimeline
{
    public static float Ease(GilbertFinisherEase ease, float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return ease switch
        {
            GilbertFinisherEase.Linear => u,
            GilbertFinisherEase.In => u * u * u,
            GilbertFinisherEase.Out => 1f - MathF.Pow(1f - u, 3f),
            GilbertFinisherEase.Step => u >= 1f ? 1f : 0f,
            _ => u < 0.5f ? 4f * u * u * u : 1f - MathF.Pow(-2f * u + 2f, 3f) / 2f,
        };
    }

    /// <summary>Monotone (harmonic-mean) slope at key <paramref name="j"/>: 0 at the ends, at extrema and next to eased segments.</summary>
    private static float Tangent(List<GilbertFinisherKey> keys, int j, int c)
    {
        if (j <= 0 || j >= keys.Count - 1)
            return 0f;
        if (keys[j].Ease != GilbertFinisherEase.InOut || keys[j + 1].Ease != GilbertFinisherEase.InOut)
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

    private static float Comp(GilbertFinisherKey k, int c) => c switch { 0 => k.X, 1 => k.Y, _ => k.Rot };

    /// <summary>Keyed value at <paramref name="t"/>; <paramref name="smooth"/>: InOut segments use the monotone spline.</summary>
    public static GilbertFinisherPose Evaluate(List<GilbertFinisherKey> keys, float t, bool smooth)
    {
        if (!smooth)
            return Evaluate(keys, t);
        if (keys.Count == 0)
            return default;
        if (t <= keys[0].Time)
            return new GilbertFinisherPose(keys[0].X, keys[0].Y, keys[0].Rot);

        for (var i = 1; i < keys.Count; i++)
        {
            var b = keys[i];
            if (t > b.Time)
                continue;
            if (b.Ease != GilbertFinisherEase.InOut)
                return Evaluate(keys, t);

            var a = keys[i - 1];
            var span = b.Time - a.Time;
            if (span <= 0f)
                return new GilbertFinisherPose(b.X, b.Y, b.Rot);
            var u = (t - a.Time) / span;
            var u2 = u * u;
            var u3 = u2 * u;
            var h00 = 2f * u3 - 3f * u2 + 1f;
            var h10 = u3 - 2f * u2 + u;
            var h01 = -2f * u3 + 3f * u2;
            var h11 = u3 - u2;
            float H(int c) => h00 * Comp(a, c) + h10 * span * Tangent(keys, i - 1, c) + h01 * Comp(b, c) + h11 * span * Tangent(keys, i, c);
            return new GilbertFinisherPose(H(0), H(1), H(2));
        }

        var last = keys[^1];
        return new GilbertFinisherPose(last.X, last.Y, last.Rot);
    }

    public static GilbertFinisherPose Evaluate(List<GilbertFinisherKey> keys, float t)
    {
        if (keys.Count == 0)
            return default;

        if (t <= keys[0].Time)
            return new GilbertFinisherPose(keys[0].X, keys[0].Y, keys[0].Rot);

        for (var i = 1; i < keys.Count; i++)
        {
            var b = keys[i];
            if (t > b.Time)
                continue;

            var a = keys[i - 1];
            var span = b.Time - a.Time;
            var u = span <= 0f ? 1f : Ease(b.Ease, (t - a.Time) / span);
            return new GilbertFinisherPose(
                a.X + (b.X - a.X) * u,
                a.Y + (b.Y - a.Y) * u,
                a.Rot + (b.Rot - a.Rot) * u);
        }

        var last = keys[^1];
        return new GilbertFinisherPose(last.X, last.Y, last.Rot);
    }

    public static GilbertFinisherActorTracks Tracks(GilbertFinisherPrototype proto, GilbertFinisherRole role)
    {
        return role == GilbertFinisherRole.Attacker ? proto.Attacker : proto.Victim;
    }

    /// <summary>
    /// Final offset (pixels, forward frame) of a part that rotates about its pivot and is translated by the pose:
    /// translation + pivot - R * pivot.
    /// </summary>
    public static Vector2 PivotOffset(Vector2 pivot, GilbertFinisherPose pose)
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
        return MathF.Abs(forward.X) > 0.5f ? new Vector2(0, 1) : new Vector2(-forward.Y, forward.X);
    }

    // ---- hit-stop time warp ----

    /// <summary>Length including every hit-stop: how long the cutscene really plays.</summary>
    public static float TotalLength(GilbertFinisherPrototype proto)
    {
        var total = proto.Length;
        foreach (var ev in proto.Events)
            if (ev.HitStop > 0f)
                total += ev.HitStop;
        return total;
    }

    /// <summary>Animation time at real (wall) time <paramref name="real"/>: frozen during hit-stops, clamped to Length.</summary>
    public static float AnimTime(GilbertFinisherPrototype proto, float real)
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
    public static float RealTime(GilbertFinisherPrototype proto, float anim)
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

    public static float Facing(GilbertFinisherRole role) => role == GilbertFinisherRole.Attacker ? 1f : -1f;

    private static GilbertFinisherPart? Parent(GilbertFinisherPart part) => part switch
    {
        GilbertFinisherPart.ArmNearLow => GilbertFinisherPart.ArmNear,
        GilbertFinisherPart.ArmFarLow => GilbertFinisherPart.ArmFar,
        GilbertFinisherPart.LegNearLow => GilbertFinisherPart.LegNear,
        GilbertFinisherPart.LegFarLow => GilbertFinisherPart.LegFar,
        GilbertFinisherPart.ArmNearHand => GilbertFinisherPart.ArmNearLow,
        GilbertFinisherPart.ArmFarHand => GilbertFinisherPart.ArmFarLow,
        GilbertFinisherPart.LegNearFoot => GilbertFinisherPart.LegNearLow,
        GilbertFinisherPart.LegFarFoot => GilbertFinisherPart.LegFarLow,
        GilbertFinisherPart.HeldNear => GilbertFinisherPart.ArmNearHand,
        GilbertFinisherPart.HeldFar => GilbertFinisherPart.ArmFarHand,
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
    /// <param name="otherPivots">Same for the partner (its head position drives the look-at).</param>
    public static GilbertFinisherPose[] Solve(GilbertFinisherPrototype proto, GilbertFinisherRole role, float t,
        IReadOnlyDictionary<GilbertFinisherPart, Vector2>? pivots = null, bool look = true,
        IReadOnlyDictionary<GilbertFinisherPart, Vector2>? otherPivots = null)
    {
        var tr = Tracks(proto, role);
        var f = Facing(role);
        var pose = new Vector3[PartCount];
        var pivot = new Vector2[PartCount];
        for (var i = 0; i < PartCount; i++)
        {
            var part = (GilbertFinisherPart) i;
            if (tr.Parts.TryGetValue(part, out var track))
            {
                var p = Evaluate(track.Keys, t, tr.Smooth);
                pose[i] = new Vector3(p.X, p.Y, p.Rot);
                pivot[i] = track.Pivot;
            }

            if (part != GilbertFinisherPart.Body && pivots != null && pivots.TryGetValue(part, out var pv))
                pivot[i] = pv;
        }

        ref var body = ref pose[(int) GilbertFinisherPart.Body];
        ref var head = ref pose[(int) GilbertFinisherPart.Head];
        ref var armN = ref pose[(int) GilbertFinisherPart.ArmNear];
        ref var armF = ref pose[(int) GilbertFinisherPart.ArmFar];
        ref var legN = ref pose[(int) GilbertFinisherPart.LegNear];
        ref var legF = ref pose[(int) GilbertFinisherPart.LegFar];
        ref var armNL = ref pose[(int) GilbertFinisherPart.ArmNearLow];
        ref var armFL = ref pose[(int) GilbertFinisherPart.ArmFarLow];
        ref var legNL = ref pose[(int) GilbertFinisherPart.LegNearLow];
        ref var legFL = ref pose[(int) GilbertFinisherPart.LegFarLow];
        ref var armNH = ref pose[(int) GilbertFinisherPart.ArmNearHand];
        ref var armFH = ref pose[(int) GilbertFinisherPart.ArmFarHand];

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
            float Delta(GilbertFinisherPart p) => tr.Parts.TryGetValue(p, out var k)
                ? Evaluate(k.Keys, t0, tr.Smooth).Rot - Evaluate(k.Keys, t, tr.Smooth).Rot
                : 0f;
            float Cl(float v) => Math.Clamp(v, -dr.Max, dr.Max);
            var bd = Delta(GilbertFinisherPart.Body);
            head.Z += Cl(dr.Head * bd);
            armN.Z += Cl(dr.Arms * bd);
            armF.Z += Cl(dr.Arms * bd);
            legN.Z += Cl(dr.Legs * bd);
            legF.Z += Cl(dr.Legs * bd);
            armNL.Z += Cl(dr.Low * Delta(GilbertFinisherPart.ArmNear));
            armFL.Z += Cl(dr.Low * Delta(GilbertFinisherPart.ArmFar));
            legNL.Z += Cl(dr.LegsLow * Delta(GilbertFinisherPart.LegNear));
            legFL.Z += Cl(dr.LegsLow * Delta(GilbertFinisherPart.LegFar));
            armNH.Z += Cl(dr.Hands * Delta(GilbertFinisherPart.ArmNearLow));
            armFH.Z += Cl(dr.Hands * Delta(GilbertFinisherPart.ArmFarLow));
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

        ApplyJointLimits(pose, f, ViewAt(tr, t) == null);
        var outPose = Chain(pose, pivot);

        if (look && tr.Look is { } lk)
        {
            var wt = lk.Keys.Count > 0 ? Evaluate(lk.Keys, t).X : lk.Weight;
            if (wt > 0f)
            {
                var own = Point(outPose, GilbertFinisherPart.Head, lk.At);
                var me = Evaluate(tr.Move, t);
                var other = role == GilbertFinisherRole.Attacker ? GilbertFinisherRole.Victim : GilbertFinisherRole.Attacker;
                var otherPose = Solve(proto, other, t, otherPivots, false);
                var tp = Point(otherPose, lk.Target, lk.TargetAt);
                var om = Evaluate(Tracks(proto, other).Move, t);
                var dx = (om.X * 32f + tp.X) - (me.X * 32f + own.X);
                var dy = (om.Y * 32f + tp.Y) - (me.Y * 32f + own.Y);
                wt *= Math.Clamp(f * dx / 8f, 0f, 1f); // partner behind: stop looking
                var desired = MathF.Atan2(dy, MathF.Max(f * dx, 1e-3f)) * 180f / MathF.PI;
                desired = Math.Clamp(desired, -lk.Max, lk.Max);
                var cur = f * (outPose[(int) GilbertFinisherPart.Body].Rot + outPose[(int) GilbertFinisherPart.Head].Rot);
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
    public static void ApplyJointLimits(Vector3[] pose, float f, bool sideView)
    {
        foreach (var p in new[] { GilbertFinisherPart.LegNearLow, GilbertFinisherPart.LegFarLow, GilbertFinisherPart.ArmNearLow, GilbertFinisherPart.ArmFarLow })
        {
            var leg = p is GilbertFinisherPart.LegNearLow or GilbertFinisherPart.LegFarLow;
            ref var v = ref pose[(int) p];
            v.Z = sideView
                ? f * Math.Clamp(f * v.Z, leg ? KneeMin : ElbowMin, leg ? KneeMax : ElbowMax)
                : Math.Clamp(v.Z, -BackViewBend, BackViewBend);
        }

        if (sideView)
        {
            pose[(int) GilbertFinisherPart.LegNear].Z = f * Math.Clamp(f * pose[(int) GilbertFinisherPart.LegNear].Z, HipMin, HipMax);
            pose[(int) GilbertFinisherPart.LegFar].Z = f * Math.Clamp(f * pose[(int) GilbertFinisherPart.LegFar].Z, HipMin, HipMax);
        }

        if (sideView)
        {
            pose[(int) GilbertFinisherPart.ArmNear].Z = f * Math.Clamp(f * pose[(int) GilbertFinisherPart.ArmNear].Z, ShoulderMin, ShoulderMax);
            pose[(int) GilbertFinisherPart.ArmFar].Z = f * Math.Clamp(f * pose[(int) GilbertFinisherPart.ArmFar].Z, ShoulderMin, ShoulderMax);
        }

        // Rigid forearm + hand: no wrist rotation or offset, whatever the keys / secondary motion say.
        pose[(int) GilbertFinisherPart.ArmNearHand] = Vector3.Zero;
        pose[(int) GilbertFinisherPart.ArmFarHand] = Vector3.Zero;
        pose[(int) GilbertFinisherPart.LegNearFoot].Z = 0f;
        pose[(int) GilbertFinisherPart.LegFarFoot].Z = 0f;
    }

    /// <summary>Screen direction forced at <paramref name="t"/> by <see cref="GilbertFinisherActorTracks.Views"/>, if any.</summary>
    public static Direction? ViewAt(GilbertFinisherActorTracks tr, float t)
    {
        return ViewInfoAt(tr, t)?.Facing;
    }

    /// <summary>The <see cref="GilbertFinisherActorTracks.Views"/> entry active at <paramref name="t"/>, if any.</summary>
    public static GilbertFinisherView? ViewInfoAt(GilbertFinisherActorTracks tr, float t)
    {
        foreach (var v in tr.Views)
            if (t >= v.Start && t < v.End)
                return v;
        return null;
    }

    /// <summary>
    /// Screen direction the actor is drawn with at <paramref name="t"/>, if forced. <paramref name="facingRight"/>:
    /// the actor's normal side-on facing (towards screen right); a <see cref="GilbertFinisherView.Turned"/> view flips it.
    /// </summary>
    public static Direction? ViewDirection(GilbertFinisherActorTracks tr, float t, bool facingRight)
    {
        if (ViewInfoAt(tr, t) is not { } v)
            return null;
        if (!v.Turned)
            return v.Facing;
        return facingRight ? Direction.West : Direction.East;
    }

    /// <summary>True while held items are drawn in the hands (before <see cref="GilbertFinisherActorTracks.HolsterAt"/>).</summary>
    public static bool HeldInHands(GilbertFinisherActorTracks tr, float t)
    {
        return !tr.HolsterHeldItems
               || tr.HolsterAt is { } at && t < at
               || tr.UnholsterAt is { } un && t >= un;
    }

    public static bool PropVisible(GilbertFinisherActorTracks tr, GilbertFinisherProp prop, float t)
    {
        return t >= prop.Start && t < prop.End && !IsHidden(tr, prop.Part, t);
    }

    /// <summary>Layer offset / rotation of a prop (forward frame, like the part poses) given its part's pose.</summary>
    public static GilbertFinisherPose PropPose(GilbertFinisherProp prop, GilbertFinisherPose part, float t)
    {
        var k = Evaluate(prop.Keys, t);
        var (sin, cos) = MathF.SinCos(part.Rot * MathF.PI / 180f);
        return new GilbertFinisherPose(part.X + cos * k.X - sin * k.Y, part.Y + sin * k.X + cos * k.Y, part.Rot + k.Rot);
    }

    /// <summary>True when <paramref name="part"/> (or the limb segment it hangs from) is hidden at <paramref name="t"/>.</summary>
    public static bool IsHidden(GilbertFinisherActorTracks tr, GilbertFinisherPart part, float t)
    {
        foreach (var h in tr.Hide)
        {
            if (t < h.Time)
                continue;
            for (var p = (GilbertFinisherPart?) part; p is { } q; p = Parent(q))
                if (h.Parts.Contains(q))
                    return true;
        }

        return false;
    }

    public static float DazeWeight(GilbertFinisherActorTracks tr, float t)
    {
        if (tr.Daze is not { } dz)
            return 0f;
        return dz.Weight.Count > 0 ? MathF.Max(0f, Evaluate(dz.Weight, t).X) : dz.Amount;
    }

    public readonly record struct Star(Vector2 Position, int Size, float Alpha, bool Front);

    /// <summary>Dizzy stars orbiting the stars centre (px offsets, forward frame); empty while not dazed enough.</summary>
    public static List<Star> Stars(GilbertFinisherActorTracks tr, float t)
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

    private static GilbertFinisherPose[] Chain(Vector3[] pose, Vector2[] pivot)
    {
        var res = new GilbertFinisherPose[PartCount];
        for (var i = 0; i < PartCount; i++)
        {
            var part = (GilbertFinisherPart) i;
            var off = PivotOffset(pivot[i], new GilbertFinisherPose(pose[i].X, pose[i].Y, pose[i].Z));
            if (Parent(part) is not { } parent)
            {
                res[i] = new GilbertFinisherPose(off.X, off.Y, pose[i].Z);
                continue;
            }

            var up = res[(int) parent]; // parents come first in the enum
            var r = Rotate(off, up.Rot);
            res[i] = new GilbertFinisherPose(up.X + r.X, up.Y + r.Y, up.Rot + pose[i].Z);
        }

        return res;
    }

    /// <summary>Forward-frame px (relative to the entity position) of the rest-pose point <paramref name="at"/> on a part.</summary>
    public static Vector2 Point(GilbertFinisherPose[] pose, GilbertFinisherPart part, Vector2 at)
    {
        var b = pose[(int) GilbertFinisherPart.Body];
        if (part == GilbertFinisherPart.Body)
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
    public static List<Droplet> Droplets(GilbertFinisherBlood bl, int e, Vector2 origin, float age)
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
}
