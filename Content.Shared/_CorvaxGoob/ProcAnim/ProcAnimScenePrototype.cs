using System.Numerics;
using Vector3 = System.Numerics.Vector3;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.Popups;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._CorvaxGoob.ProcAnim;

/// <summary>
/// A procedural cutscene ("ProcAnim scene"): any number of humanoid actors (by role, e.g. <c>attacker</c> / <c>victim</c>)
/// with keyframed tracks, plus a time-sorted list of gameplay / effect events. Generic (not tied to any NPC); see
/// <c>Resources/Prototypes/_CorvaxGoob/ProcAnim/README.md</c> for the authoring guide.
/// All tracks are written in the scene's shared "forward frame": +X points from the first actor towards the second (the
/// direction the server picks when the scene starts), +Y is up on screen, rotations are degrees, counter-clockwise
/// positive when +X points right (east). Bone offsets are in pixels (32 px = 1 tile); <see cref="ProcAnimActor.Move"/>
/// is in tiles relative to the first actor's start position.
/// The same YAML drives the server (world movement, damage, sounds), the client (sprite rig, shake, flash, blood)
/// and the offline preview renderer (<c>Tools/_CorvaxGoob/procanim/preview.py</c>), so the preview matches the game.
/// </summary>
[Prototype("procAnimScene")]
public sealed partial class ProcAnimScenePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Animation length, seconds (hit-stops come on top, see <see cref="ProcAnimTimeline.TotalLength"/>).</summary>
    [DataField]
    public float Length = 6.8f;

    /// <summary>
    /// Holster beat: keys / events at animation time -Intro..0 are only played when an actor with
    /// <see cref="ProcAnimActor.HolsterHeldItems"/> holds an item when the scene starts (otherwise playback starts at 0).
    /// </summary>
    [DataField]
    public float Intro;

    /// <summary>Free floor needed in front of the first actor (tiles) to play the full motion without clamping.</summary>
    [DataField]
    public float RequiredRoom = 3.9f;

    /// <summary>
    /// v13: when the start spot has no screen-horizontal room (a 1-tile vertical corridor), the actors are first moved
    /// to the nearest free floor within this many tiles (in line of sight of both, not into walls) where the scene fits
    /// side-on, so the world positions match the drawn side view (no snap at the end). 0 = never move them.
    /// </summary>
    [DataField]
    public float StageRadius = 3f;

    [DataField("actors")]
    private List<ProcAnimActor> _actors = new();

    [DataField("events")]
    private List<ProcAnimEvent> _events = new();

    private bool _resolved;

    /// <summary>The actors in role order; index 0 leads (its start position is the origin, it "deals" the damage).</summary>
    public IReadOnlyList<ProcAnimActor> Actors
    {
        get
        {
            Resolve();
            return _actors;
        }
    }

    /// <summary>Sorted by time. Index in this list seeds the deterministic blood bursts.</summary>
    public IReadOnlyList<ProcAnimEvent> Events
    {
        get
        {
            Resolve();
            return _events;
        }
    }

    private readonly List<ProcAnimItem> _items = new();

    /// <summary>Every actor's <see cref="ProcAnimActor.Items"/>, flattened (<see cref="ProcAnimItem.Index"/>).</summary>
    public IReadOnlyList<ProcAnimItem> Items
    {
        get
        {
            Resolve();
            return _items;
        }
    }

    /// <summary><c>id</c> (this actor's first, then anyone's) or <c>role.id</c> → index in <see cref="Items"/>, -1 if none.</summary>
    public int FindItem(string reference, int self)
    {
        var dot = reference.IndexOf('.');
        var actor = dot > 0 ? ActorIndex(reference[..dot]) : self;
        var id = dot > 0 ? reference[(dot + 1)..] : reference;
        var any = -1;
        foreach (var item in _items)
        {
            if (item.Id != id)
                continue;
            if (item.Actor == actor)
                return item.Index;
            if (dot < 0 && any < 0)
                any = item.Index;
        }

        return any;
    }

    /// <summary>Index of the actor with <paramref name="role"/>, -1 when there is none.</summary>
    public int ActorIndex(string? role)
    {
        if (role == null)
            return -1;
        for (var i = 0; i < _actors.Count; i++)
            if (string.Equals(_actors[i].Role, role, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>The actor with <paramref name="role"/>; throws when there is none.</summary>
    public ProcAnimActor Actor(string role)
    {
        var i = ActorIndex(role);
        if (i < 0)
            throw new KeyNotFoundException($"procAnimScene {ID} has no actor '{role}'");
        return Actors[i];
    }

    /// <summary>Default event / look target: the second actor, or the only one.</summary>
    public int DefaultTarget(int self = 0) => _actors.Count > 1 ? (self == 1 ? 0 : 1) : 0;

    /// <summary>
    /// Resolves the role names (event targets, look / IK references) to actor indices and folds the
    /// <see cref="ProcAnimEvent.Holster"/> / <see cref="ProcAnimEvent.Unholster"/> events into the actor tracks. Idempotent.
    /// </summary>
    private void Resolve()
    {
        if (_resolved)
            return;

        for (var i = 0; i < _actors.Count; i++)
        {
            var actor = _actors[i];
            actor.Index = i;
            if (actor.Look is { } look)
                look.ActorIndex = look.Actor != null ? ActorIndex(look.Actor) : DefaultTarget(i);
            foreach (var ik in actor.Ik)
                ik.Resolve(this, i);
        }

        _items.Clear();
        for (var i = 0; i < _actors.Count; i++)
        {
            foreach (var item in _actors[i].Items)
            {
                item.Actor = i;
                item.Index = _items.Count;
                _items.Add(item);
            }
        }

        for (var i = 0; i < _actors.Count; i++)
        {
            foreach (var prop in _actors[i].Props)
            {
                prop.ItemIndex = prop.Item != null ? FindItem(prop.Item, i) : -1;
                if (prop.ItemIndex >= 0)
                    _items[prop.ItemIndex].HasProp = true;
            }
        }

        foreach (var ev in _events)
        {
            ev.TargetIndex = ev.Target != null ? ActorIndex(ev.Target) : DefaultTarget();
            if (ev.TargetIndex < 0 || ev.TargetIndex >= _actors.Count)
                continue;
            var tr = _actors[ev.TargetIndex];
            if (ev.Holster && tr.HolsterAt == null)
            {
                tr.HolsterHeldItems = true;
                tr.HolsterAt = ev.Time;
            }

            if (ev.Unholster && tr.UnholsterAt == null)
                tr.UnholsterAt = ev.Time;
        }

        _resolved = true;
    }
}

[DataDefinition]
public sealed partial class ProcAnimActor
{
    /// <summary>Role name (<c>attacker</c>, <c>victim</c>, ...): the order of the entities given to the scene.</summary>
    [DataField(required: true)]
    public string Role = "actor";

    /// <summary>+1: faces along the forward frame's +X (towards the second actor); -1: faces back (towards the first).</summary>
    [DataField]
    public float Facing = 1f;

    /// <summary>Server: forced to stand up when the scene starts.</summary>
    [DataField]
    public bool StandUp;

    /// <summary>
    /// Server: the mob state is held at what it was when the scene started (no crit / death from the damage beats) until
    /// an event with <see cref="ProcAnimEvent.Kill"/> on this actor or the end of the scene.
    /// </summary>
    [DataField]
    public bool HoldAlive;

    /// <summary>Server: an actor already in crit is held there (no death from the beats before the final blow).</summary>
    [DataField]
    public bool HoldCrit;

    /// <summary>Server: knocked down at the end (unless the caller asked otherwise, see ProcAnimSystem.TryPlay keepDown).</summary>
    [DataField]
    public bool KnockDown;

    /// <summary>
    /// Server: damage dealt to this actor when the scene plays to its end (after the final blow), ignoring armour and
    /// resistances, not aimed at a body part (split over every part, then topped up). Null / empty: nothing.
    /// </summary>
    [DataField]
    public DamageSpecifier? FinalDamage;

    /// <summary>Per-actor joint limit overrides (default: the humanoid rig's).</summary>
    [DataField]
    public ProcAnimJointLimits? Joints;

    /// <summary>Two-bone IK effectors for the hands / feet (on top of the keys; see <see cref="ProcAnimIkTrack"/>).</summary>
    [DataField]
    public List<ProcAnimIkTrack> Ik = new();

    /// <summary>
    /// Facial expression / eye state keys (step keys; raised as <c>ProcAnimExpressionChangedEvent</c> on the client and
    /// drawn as pixel face layers over the head, see <see cref="Face"/>). Faces: neutral, pain, dazed, angry, dead;
    /// eyes: open, closed, narrowed.
    /// </summary>
    [DataField]
    public List<ProcAnimExpressionKey> Expressions = new();

    /// <summary>v12 face layer anchors / colours (rest-pose px on the Head bone).</summary>
    [DataField]
    public ProcAnimFace Face = new();

    /// <summary>Index in <see cref="ProcAnimScenePrototype.Actors"/> (resolved on load).</summary>
    [ViewVariables]
    public int Index { get; internal set; }

    /// <summary>World movement along the forward axis, tiles, relative to the first actor's start position.</summary>
    [DataField]
    public List<ProcAnimKey> Move = new();

    [DataField]
    public Dictionary<ProcAnimBone, ProcAnimBoneTrack> Parts = new();

    /// <summary>Held items are drawn slung on the back (their <see cref="HolsterStates"/>) instead of in the hands.</summary>
    [DataField]
    public bool HolsterHeldItems;

    /// <summary>RSI states tried, in order, for a holstered in-hand item.</summary>
    [DataField]
    public List<string> HolsterStates = new() { "equipped-BACKPACK", "equipped-SUITSTORAGE" };

    /// <summary>
    /// Animation time at which held items move from the hands to the back (the holster swing ends); before it they
    /// are drawn in the hands as their own layers (<see cref="ProcAnimBone.HeldNear"/> / HeldFar, carried by
    /// the hands). Null: slung on the back from the start.
    /// </summary>
    [DataField]
    public float? HolsterAt;

    /// <summary>Animation time from which the holstered items are back in the hands (drawn by the hands again).</summary>
    [DataField]
    public float? UnholsterAt;

    /// <summary>Extra sprites carried by a part for a while (e.g. a baton drawn from the belt). Purely visual.</summary>
    [DataField]
    public List<ProcAnimProp> Props = new();

    /// <summary>
    /// Intervals drawn with a fixed screen direction (e.g. <c>North</c>: back to the camera). The keys inside are
    /// written for that view: x = screen right (mirrored like the side view), y = screen up.
    /// </summary>
    [DataField]
    public List<ProcAnimView> Views = new();

    /// <summary>Procedural walk cycle on top of the keys, phase-locked to the distance travelled along Move.</summary>
    [DataField]
    public ProcAnimWalk? Walk;

    /// <summary>Breathing / weight shifts while standing (fades out while walking).</summary>
    [DataField]
    public ProcAnimIdle? Idle;

    /// <summary>Head look-at towards the partner.</summary>
    [DataField]
    public ProcAnimLook? Look;

    /// <summary>
    /// Part keys with the default (InOut) ease are joined by a monotone cubic spline instead of stopping at every
    /// key, so the motion flows through them. Explicit In / Out / Linear / Step keys keep their sharp timing.
    /// </summary>
    [DataField]
    public bool Smooth;

    /// <summary>Secondary motion: head / arms trail the body, forearms / shins trail the upper segments.</summary>
    [DataField]
    public ProcAnimDrag? Drag;

    /// <summary>Concussion: weight-keyed drunken sway, lolling head, dangling arms, buckling knees, dizzy stars.</summary>
    [DataField]
    public ProcAnimDaze? Daze;

    /// <summary>
    /// Thrown section of <see cref="Move"/>: the server shortens the displacement after <c>start</c> (uniformly) so
    /// the actor never lands in a wall or off the floor.
    /// </summary>
    [DataField]
    public ProcAnimFlight? Flight;

    /// <summary>Parts that stop being drawn at some point (torn off).</summary>
    [DataField]
    public List<ProcAnimHide> Hide = new();

    /// <summary>
    /// Server: before the scene (a pre-beat ahead of the intro) every item in this actor's hands that isn't
    /// <see cref="ProcAnimStow.Keep"/>-matched or one of its <see cref="Items"/> is really put away (storage / slots,
    /// dropped only as a last resort), one quick reach per item. Null: the hands are left alone.
    /// </summary>
    [DataField]
    public ProcAnimStow? StowHands;

    /// <summary>
    /// Real items this actor uses (e.g. the baton from its belt): found on the actor when the scene starts, taken into
    /// a hand at <see cref="ProcAnimItem.Draw"/> and put back where they came from at <see cref="ProcAnimItem.Return"/>.
    /// Props with <see cref="ProcAnimProp.Item"/> are drawn with the bound item's own sprite.
    /// </summary>
    [DataField]
    public List<ProcAnimItem> Items = new();
}

/// <summary>Matches an item entity by prototype (or a parent of it), tag or component name; any non-empty list matching is enough.</summary>
[DataDefinition]
public sealed partial class ProcAnimItemMatch
{
    [DataField] public List<EntProtoId> Prototypes = new();
    [DataField] public List<ProtoId<Content.Shared.Tag.TagPrototype>> Tags = new();
    /// <summary>Component names, e.g. <c>Gun</c>.</summary>
    [DataField] public List<string> Components = new();
}

/// <summary>One stow rule: items matching <see cref="Match"/> go to <see cref="Slots"/> (tried in order).</summary>
[DataDefinition]
public sealed partial class ProcAnimStowRule
{
    [DataField(required: true)] public ProcAnimItemMatch Match = new();

    /// <summary>
    /// Inventory slots tried in order: the storage worn there (belt, bag...) if any, else the slot itself when empty and
    /// the item fits it (e.g. <c>suitstorage</c>).
    /// </summary>
    [DataField(required: true)] public List<string> Slots = new();
}

/// <summary><see cref="ProcAnimActor.StowHands"/>: the pre-beat that empties the hands with the real items.</summary>
[DataDefinition]
public sealed partial class ProcAnimStow
{
    /// <summary>Items left in the hands (e.g. the gun the intro holster beat slings).</summary>
    [DataField] public List<ProcAnimItemMatch> Keep = new();

    /// <summary>First matching rule wins; otherwise <see cref="Slots"/>.</summary>
    [DataField] public List<ProcAnimStowRule> Rules = new();

    [DataField] public List<string> Slots = new() { "back", "belt", "pocket1", "pocket2" };

    /// <summary>Length of one item's reach (seconds of the pre-beat per item).</summary>
    [DataField] public float Duration = 0.32f;

    /// <summary>Time inside the reach at which the item actually leaves the hand.</summary>
    [DataField] public float Grab = 0.15f;

    /// <summary>When the scene ends, the stowed items still on the actor go back into its free hands (toggles back on).</summary>
    [DataField] public bool Restore = true;

    [DataField] public SoundSpecifier? Sound = new SoundCollectionSpecifier("storageRustle");

    /// <summary>
    /// Additive keys of one reach (times 0..<see cref="Duration"/>), written for the near arm: played on the arm of the
    /// hand the item is in (mirrored to the far bones for the far hand). Empty: a short reach to the hip.
    /// </summary>
    [DataField] public Dictionary<ProcAnimBone, List<ProcAnimKey>> Keys = new();

    public static readonly Dictionary<ProcAnimBone, List<ProcAnimKey>> DefaultKeys = new()
    {
        [ProcAnimBone.Body] = new() { K(0f, 0f), K(0.15f, 3f, ProcAnimEase.Out), K(0.32f, 0f) },
        [ProcAnimBone.ArmNear] = new() { K(0f, 0f), K(0.15f, -30f, ProcAnimEase.Out), K(0.32f, 0f) },
        [ProcAnimBone.ArmNearLow] = new() { K(0f, 0f), K(0.15f, 38f, ProcAnimEase.Out), K(0.32f, 0f) },
        [ProcAnimBone.ArmNearHand] = new() { K(0f, 0f), K(0.15f, 20f, ProcAnimEase.Out), K(0.32f, 0f) },
    };

    private static ProcAnimKey K(float t, float rot, ProcAnimEase ease = ProcAnimEase.InOut)
        => new() { Time = t, Rot = rot, Ease = ease };

    public IReadOnlyDictionary<ProcAnimBone, List<ProcAnimKey>> Reach => Keys.Count > 0 ? Keys : DefaultKeys;
}

/// <summary>A real item an actor uses in the scene (<see cref="ProcAnimActor.Items"/>).</summary>
[DataDefinition]
public sealed partial class ProcAnimItem
{
    /// <summary>Name the props refer to (<c>item: baton</c>, or <c>item: attacker.baton</c> from another actor).</summary>
    [DataField(required: true)] public string Id = "";

    [DataField(required: true)] public ProcAnimItemMatch Match = new();

    /// <summary>
    /// Where to look, in order: <c>hands</c> or an inventory slot (the item worn there, or inside the storage worn there,
    /// nested containers included). Empty: hands, then every slot.
    /// </summary>
    [DataField] public List<string> From = new();

    /// <summary>Animation time it is taken into a free hand; null: in the pre-beat, so it is in hand from the start.</summary>
    [DataField] public float? Draw;

    /// <summary>
    /// Animation time it goes back to where it was found (its slot / container; else stowed like
    /// <see cref="ProcAnimActor.StowHands"/>, else kept in hand); null: when the scene ends.
    /// </summary>
    [DataField("return")] public float? Return;

    /// <summary>The scene refuses to start (<c>procanim-error-missing-item</c>) without it.</summary>
    [DataField] public bool Required = true;

    /// <summary>Wielded (two-handed) once drawn, when it can be.</summary>
    [DataField] public bool Wield;

    /// <summary>Offline preview only: sprite of a typical matching item (the game always uses the real one).</summary>
    [DataField] public ResPath? PreviewSprite;

    [DataField] public string PreviewState = "icon";

    /// <summary>Index in <see cref="ProcAnimScenePrototype.Items"/> (resolved on load).</summary>
    [ViewVariables] public int Index { get; internal set; }

    /// <summary>Owning actor index (resolved on load).</summary>
    [ViewVariables] public int Actor { get; internal set; }

    /// <summary>A prop draws it: its own in-hand sprite is hidden while it is bound (resolved on load).</summary>
    [ViewVariables] public bool HasProp { get; internal set; }
}

[DataDefinition]
public sealed partial class ProcAnimView
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
public sealed partial class ProcAnimDrag
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
public sealed partial class ProcAnimDaze
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
    [DataField] public List<ProcAnimKey> Weight = new();
    [DataField] public float Amount = 1f;
    [DataField] public ProcAnimStars? Stars;
}

[DataDefinition]
public sealed partial class ProcAnimStars
{
    [DataField] public ProcAnimBone From = ProcAnimBone.Head;
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
public sealed partial class ProcAnimFlight
{
    [DataField] public float Start;
    /// <summary>Time the planned landing spot is reached (the displacement at this time is what gets checked).</summary>
    [DataField] public float End;
}

[DataDefinition]
public sealed partial class ProcAnimWalk
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
    [DataField] public List<ProcAnimKey> Weight = new();
}

[DataDefinition]
public sealed partial class ProcAnimIdle
{
    [DataField] public float Period = 2.6f;
    [DataField] public float Breath = 0.5f;
    [DataField] public float WeightShift = 1f;
    [DataField] public float HeadNod = 1.5f;
    [DataField] public float ArmSway = 2f;
}

[DataDefinition]
public sealed partial class ProcAnimLook
{
    /// <summary>Own eye point on the head (px, rest pose, forward frame).</summary>
    [DataField] public Vector2 At = new(0, 9);
    /// <summary>Role looked at; null: the default partner (the second actor, or the first for the second one).</summary>
    [DataField] public string? Actor;
    [ViewVariables] public int ActorIndex { get; internal set; }
    [DataField] public ProcAnimBone Target = ProcAnimBone.Head;
    /// <summary>Point looked at on the partner's <see cref="Target"/> part.</summary>
    [DataField] public Vector2 TargetAt = new(0, 9);
    /// <summary>Max absolute head pitch, degrees.</summary>
    [DataField] public float Max = 30f;
    /// <summary>Max head rotation relative to the body, degrees.</summary>
    [DataField] public float Neck = 40f;
    /// <summary>Look weight over time (X); constant <see cref="Weight"/> when empty.</summary>
    [DataField] public List<ProcAnimKey> Keys = new();
    [DataField] public float Weight = 1f;
}

[DataDefinition]
public sealed partial class ProcAnimBoneTrack
{
    /// <summary>Rotation pivot in pixels relative to the sprite centre (forward frame): neck, shoulder, hip...</summary>
    [DataField]
    public Vector2 Pivot;

    [DataField]
    public List<ProcAnimKey> Keys = new();
}

[DataDefinition]
public sealed partial class ProcAnimKey
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
    public ProcAnimEase Ease = ProcAnimEase.InOut;
}

[DataDefinition]
public sealed partial class ProcAnimEvent
{
    [DataField("t")]
    public float Time;

    /// <summary>Role the event happens on (sound, flash, damage, ...); null: the second actor (or the only one).</summary>
    [DataField]
    public string? Target;

    /// <summary><see cref="Target"/> resolved to an actor index (on load).</summary>
    [ViewVariables]
    public int TargetIndex { get; internal set; }

    /// <summary>Popup shown over the target (locale id).</summary>
    [DataField]
    public ProcAnimPopup? Popup;

    /// <summary>
    /// The target's held items go from the hands to the back here (same as its <see cref="ProcAnimActor.HolsterAt"/>,
    /// which wins when both are set; implies <see cref="ProcAnimActor.HolsterHeldItems"/>).
    /// </summary>
    [DataField]
    public bool Holster;

    /// <summary>The target's holstered items are back in the hands from here (<see cref="ProcAnimActor.UnholsterAt"/>).</summary>
    [DataField]
    public bool Unholster;

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
    public ProcAnimBlood? Blood;

    /// <summary>v12 hit effects / aftermath on the target (spatter, recoil, bruise, drips, pool, dust, shake, stagger).</summary>
    [DataField]
    public List<ProcAnimEffect> Effects = new();

    /// <summary>Server-side floor decal left at the event (e.g. a blood splatter).</summary>
    [DataField]
    public ProcAnimDecal? Decal;

    /// <summary>
    /// Server: damage dealt to the target at this beat, each entry to one body part (dealt by the first actor). With
    /// <see cref="ProcAnimHit.IgnoreArmor"/> armour and the damage modifier sets do nothing, see ProcAnimSystem.ApplyHit.
    /// </summary>
    [DataField]
    public List<ProcAnimHit> Damage = new();

    /// <summary>Server: target body parts torn off at this beat (Shitmed amputation: the limb drops as an item).</summary>
    [DataField]
    public List<TargetBodyPart> Amputate = new();

    /// <summary>
    /// Server: after the damage / amputation of this beat, releases the target's hold
    /// (<see cref="ProcAnimActor.HoldAlive"/>) and kills it (tops the damage up to the death threshold, then forces the dead state if still alive).
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
public sealed partial class ProcAnimHit
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
public sealed partial class ProcAnimProp
{
    /// <summary>RSI path (under Textures/). With <see cref="Item"/>: only the fallback when the item has no sprite.</summary>
    [DataField]
    public ResPath? Sprite;

    [DataField]
    public string State = "icon";

    /// <summary>
    /// A real item of the scene (<see cref="ProcAnimActor.Items"/> id, <c>role.id</c> for another actor's): the prop
    /// is drawn with that entity's own sprite (its <see cref="ItemState"/>), and the item's in-hand sprite is hidden.
    /// </summary>
    [DataField]
    public string? Item;

    /// <summary>State on the bound item's RSI (default <c>icon</c>; the item's first layer state when missing).</summary>
    [DataField]
    public string ItemState = "icon";

    /// <summary>Extra rotation (degrees) aligning the item's sprite with the axis the keys were written for.</summary>
    [DataField]
    public float ItemRotation;

    /// <summary>Scale of the item sprite (instead of <see cref="Scale"/>).</summary>
    [DataField]
    public float? ItemScale;

    /// <summary><see cref="Item"/> resolved to <see cref="ProcAnimScenePrototype.Items"/> (-1: none).</summary>
    [ViewVariables]
    public int ItemIndex { get; internal set; } = -1;

    [DataField]
    public float Start;

    [DataField]
    public float End = float.MaxValue;

    [DataField]
    public ProcAnimBone Part = ProcAnimBone.Body;

    [DataField]
    public List<ProcAnimKey> Keys = new();

    [DataField]
    public float Scale = 1f;

    /// <summary>
    /// v12: draw below the part's layers instead of above them, so whatever the part covers is hidden (a baton
    /// rammed into the mouth shows only the handle sticking out of the face).
    /// </summary>
    [DataField]
    public bool Under;

    /// <summary>
    /// Drawn above every puppet layer of the actor (a weapon held in the far hand still shows in front of the body,
    /// the way the held item is drawn in the real side view).
    /// </summary>
    [DataField]
    public bool Front;
}

/// <summary>
/// From <see cref="Time"/> on, these parts are no longer drawn (e.g. torn-off arms); with <see cref="Until"/> only
/// until then (v13: e.g. an actor vanishing and reappearing elsewhere).
/// </summary>
[DataDefinition]
public sealed partial class ProcAnimHide
{
    [DataField("t")]
    public float Time;

    /// <summary>Drawn again from this time on; null = hidden to the end.</summary>
    [DataField]
    public float? Until;

    [DataField]
    public List<ProcAnimBone> Parts = new();
}

/// <summary>Client-side blood burst: deterministic ballistic droplets that land and fade.</summary>
[DataDefinition]
public sealed partial class ProcAnimBlood
{
    [DataField] public ProcAnimBone From = ProcAnimBone.Head;
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
public sealed partial class ProcAnimDecal
{
    [DataField(required: true)] public string Id = default!;
    [DataField] public ProcAnimBone From = ProcAnimBone.Head;
    [DataField] public Vector2 At = new(0, 9);
    /// <summary>Tint; RGB is replaced by the target's blood colour, alpha is kept.</summary>
    [DataField] public Color Color = Color.FromHex("#80000099");
}

[DataDefinition]
public sealed partial class ProcAnimPopup
{
    [DataField(required: true)] public LocId Text;
    [DataField] public PopupType Type = PopupType.Medium;
}

/// <summary>
/// Joint limits of the humanoid rig, degrees relative to the parent segment in the actor's forward frame
/// (+ = swung towards its facing); (min, max). Defaults: <see cref="ProcAnimTimeline"/> constants.
/// </summary>
[DataDefinition]
public sealed partial class ProcAnimJointLimits
{
    public static readonly ProcAnimJointLimits Default = new();

    /// <summary>The shin only folds back behind the thigh.</summary>
    [DataField] public Vector2 Knee = new(ProcAnimTimeline.KneeMin, ProcAnimTimeline.KneeMax);
    /// <summary>The forearm only folds forward / up.</summary>
    [DataField] public Vector2 Elbow = new(ProcAnimTimeline.ElbowMin, ProcAnimTimeline.ElbowMax);
    [DataField] public Vector2 Hip = new(ProcAnimTimeline.HipMin, ProcAnimTimeline.HipMax);
    [DataField] public Vector2 Shoulder = new(ProcAnimTimeline.ShoulderMin, ProcAnimTimeline.ShoulderMax);
    /// <summary>Back / front views have no facing in the screen plane: knees / elbows only get a +- range.</summary>
    [DataField] public float BackViewBend = ProcAnimTimeline.BackViewBend;
}

public enum ProcAnimIkBend : byte
{
    /// <summary>The bend direction the joint limits allow (elbows forward, knees back).</summary>
    Auto,
    /// <summary>Lower segment rotated counter-clockwise relative to the upper one (forward frame).</summary>
    Positive,
    Negative,
}

/// <summary>
/// Two-bone IK effector: rotates the upper (<see cref="Limb"/>) and lower segment so the limb's tip (hand grip / foot)
/// reaches the keyed target, blended with the keyed pose by <see cref="Weight"/> / <see cref="Weights"/>; the joint
/// limits are applied again afterwards. Bone lengths come from the pivots (shoulder → elbow → hand), so the same
/// track works for any species / puppet.
/// </summary>
[DataDefinition]
public sealed partial class ProcAnimIkTrack
{
    /// <summary>Upper bone of the chain: ArmNear, ArmFar, LegNear or LegFar.</summary>
    [DataField(required: true)] public ProcAnimBone Limb = ProcAnimBone.ArmNear;

    /// <summary>
    /// Target frame: null = the actor's own sprite frame (px from its origin, forward frame); <c>role.Bone</c> (e.g.
    /// <c>victim.Head</c>) or <c>role</c> (= its Body) = a point on another actor's (or own) bone, <see cref="At"/> in
    /// that bone's rest frame, followed as it moves.
    /// </summary>
    [DataField] public string? RelativeTo;

    /// <summary>Point on the <see cref="RelativeTo"/> bone (px, rest pose, forward frame).</summary>
    [DataField] public Vector2 At;

    /// <summary>Target offset keys (x, y px, forward frame; rot is unused).</summary>
    [DataField] public List<ProcAnimKey> Keys = new();

    /// <summary>Constant blend weight when <see cref="Weights"/> is empty.</summary>
    [DataField] public float Weight = 1f;

    /// <summary>Weight over time (X, 0..1).</summary>
    [DataField] public List<ProcAnimKey> Weights = new();

    /// <summary>Effector point on the lower segment (rest px); null = the hand / foot pivot.</summary>
    [DataField] public Vector2? Tip;

    [DataField] public ProcAnimIkBend Bend = ProcAnimIkBend.Auto;

    /// <summary>Resolved <see cref="RelativeTo"/>: actor index (-1 = own sprite frame) and bone.</summary>
    [ViewVariables] public int TargetActor { get; private set; } = -1;
    [ViewVariables] public ProcAnimBone TargetBone { get; private set; } = ProcAnimBone.Body;

    /// <summary>False when <see cref="RelativeTo"/> names an unknown role / bone (the track is ignored).</summary>
    [ViewVariables] public bool Valid { get; private set; } = true;

    internal void Resolve(ProcAnimScenePrototype scene, int self)
    {
        TargetActor = -1;
        TargetBone = ProcAnimBone.Body;
        Valid = Limb is ProcAnimBone.ArmNear or ProcAnimBone.ArmFar or ProcAnimBone.LegNear or ProcAnimBone.LegFar;
        if (RelativeTo is not { } rel)
            return;

        var dot = rel.IndexOf('.');
        var role = dot < 0 ? rel : rel[..dot];
        TargetActor = scene.ActorIndex(role);
        if (TargetActor < 0)
            Valid = false;
        if (dot >= 0)
        {
            if (Enum.TryParse<ProcAnimBone>(rel[(dot + 1)..], true, out var bone))
                TargetBone = bone;
            else
                Valid = false;
        }
    }
}

/// <summary>
/// Stub keying API for future facial expression / eye state layers: step keys (hold until the next one).
/// Nothing renders them yet; the client raises <c>ProcAnimExpressionChangedEvent</c> when the value changes.
/// </summary>
[DataDefinition]
public sealed partial class ProcAnimExpressionKey
{
    [DataField("t")] public float Time;
    /// <summary>Expression id, e.g. <c>neutral</c>, <c>angry</c>, <c>pain</c>; null = unchanged.</summary>
    [DataField] public string? Face;
    /// <summary>Eye state, e.g. <c>open</c>, <c>closed</c>, <c>wide</c>; null = unchanged.</summary>
    [DataField] public string? Eyes;
}

/// <summary>Evaluated expression state (null = no key yet).</summary>
public readonly record struct ProcAnimExpression(string? Face, string? Eyes);

/// <summary>Near = the limb on the side facing the camera in a side view (right side when facing east).</summary>
[Serializable, NetSerializable]
public enum ProcAnimBone : byte
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
    /// <summary>Item held in the near / far hand (until <see cref="ProcAnimActor.HolsterAt"/>), carried by the hand.</summary>
    HeldNear,
    HeldFar,
}

[Serializable, NetSerializable]
public enum ProcAnimEase : byte
{
    Linear,
    /// <summary>Cubic ease-in (u^3).</summary>
    In,
    /// <summary>Cubic ease-out.</summary>
    Out,
    /// <summary>Cubic ease-in-out; joined by the monotone spline on <see cref="ProcAnimActor.Smooth"/> actors.</summary>
    InOut,
    /// <summary>Jumps to this key's value at its time.</summary>
    Step,
    /// <summary>Overshoots backwards first (anticipation).</summary>
    BackIn,
    /// <summary>Overshoots past the key, then settles (follow-through).</summary>
    BackOut,
    BackInOut,
    /// <summary>Springy wind-up.</summary>
    ElasticIn,
    /// <summary>Springy settle (wobble) after reaching the key.</summary>
    ElasticOut,
    /// <summary>Impact freeze: holds the previous value for the first 35% of the segment, then snaps in (cubic out).</summary>
    HitStop,
    /// <summary>Aliases of In / Out / InOut.</summary>
    CubicIn = In,
    CubicOut = Out,
    CubicInOut = InOut,
}

/// <summary>Evaluated pose of one part: translation in pixels, rotation in degrees (forward frame).</summary>
public readonly record struct ProcAnimPose(float X, float Y, float Rot);
