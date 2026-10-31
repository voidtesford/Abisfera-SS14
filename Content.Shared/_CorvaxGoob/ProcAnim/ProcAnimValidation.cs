using System.Linq;

namespace Content.Shared._CorvaxGoob.ProcAnim;

/// <summary>
/// Authoring checks for <see cref="ProcAnimScenePrototype"/> that the serializer cannot express: unique roles, sorted
/// key / event times (the evaluator and the event cursor rely on them), resolvable actor / bone references.
/// Run over every scene by the integration tests; also used by <c>procanim play</c> before starting a scene.
/// </summary>
public static class ProcAnimValidation
{
    public static List<string> Validate(ProcAnimScenePrototype scene)
    {
        var errors = new List<string>();
        void Err(string msg) => errors.Add($"{scene.ID}: {msg}");

        if (scene.Length <= 0f)
            Err("length must be > 0");
        if (scene.Intro < 0f)
            Err("intro must be >= 0");
        if (scene.Actors.Count == 0)
            Err("no actors");

        var roles = new HashSet<string>();
        foreach (var actor in scene.Actors)
        {
            var who = $"actor '{actor.Role}'";
            if (string.IsNullOrWhiteSpace(actor.Role))
                Err("actor with an empty role");
            else if (!roles.Add(actor.Role.ToLowerInvariant()))
                Err($"duplicate role '{actor.Role}'");
            if (MathF.Abs(MathF.Abs(actor.Facing) - 1f) > 1e-4f)
                Err($"{who}: facing must be 1 or -1");

            Sorted(actor.Move.Select(k => k.Time), $"{who}.move", Err);
            foreach (var (bone, track) in actor.Parts)
                Sorted(track.Keys.Select(k => k.Time), $"{who}.parts.{bone}", Err);
            for (var i = 0; i < actor.Props.Count; i++)
            {
                var prop = actor.Props[i];
                Sorted(prop.Keys.Select(k => k.Time), $"{who}.props[{i}]", Err);
                if (prop.End < prop.Start)
                    Err($"{who}.props[{i}]: end < start");
                if (prop.Sprite == null && prop.Item == null)
                    Err($"{who}.props[{i}]: needs a sprite or an item");
                if (prop.Item != null && prop.ItemIndex < 0)
                    Err($"{who}.props[{i}]: unknown item '{prop.Item}'");
            }
            var ids = new HashSet<string>();
            foreach (var item in actor.Items)
            {
                if (!ids.Add(item.Id))
                    Err($"{who}.items: duplicate id '{item.Id}'");
                if (item.Match.Prototypes.Count + item.Match.Tags.Count + item.Match.Components.Count == 0)
                    Err($"{who}.items.{item.Id}: empty match");
                if (item.Draw is { } draw && (draw < -scene.Intro || draw > scene.Length))
                    Err($"{who}.items.{item.Id}: draw outside -intro..length");
                if (item.Return is { } ret && (ret < (item.Draw ?? -scene.Intro) || ret > scene.Length))
                    Err($"{who}.items.{item.Id}: return before draw or after length");
            }
            if (actor.StowHands is { } stow && (stow.Duration <= 0f || stow.Grab < 0f || stow.Grab > stow.Duration))
                Err($"{who}.stowHands: needs duration > 0 and 0 <= grab <= duration");
            foreach (var view in actor.Views)
            {
                if (view.End < view.Start)
                    Err($"{who}.views: end < start ({view.Start}..{view.End})");
            }
            if (actor.Walk != null)
                Sorted(actor.Walk.Weight.Select(k => k.Time), $"{who}.walk.weight", Err);
            if (actor.Daze != null)
                Sorted(actor.Daze.Weight.Select(k => k.Time), $"{who}.daze.weight", Err);
            if (actor.Look is { } look)
            {
                Sorted(look.Keys.Select(k => k.Time), $"{who}.look", Err);
                if (look.Actor != null && scene.ActorIndex(look.Actor) < 0)
                    Err($"{who}.look: unknown actor '{look.Actor}'");
                else if (scene.Actors.Count > 1 && look.ActorIndex == actor.Index)
                    Err($"{who}.look: an actor cannot look at itself");
            }
            for (var i = 0; i < actor.Ik.Count; i++)
            {
                var ik = actor.Ik[i];
                Sorted(ik.Keys.Select(k => k.Time), $"{who}.ik[{i}]", Err);
                Sorted(ik.Weights.Select(k => k.Time), $"{who}.ik[{i}].weights", Err);
                if (!ik.Valid)
                    Err($"{who}.ik[{i}]: limb must be ArmNear/ArmFar/LegNear/LegFar and relativeTo 'role' or 'role.Bone' (got {ik.Limb}, '{ik.RelativeTo}')");
            }
            Sorted(actor.Expressions.Select(k => k.Time), $"{who}.expressions", Err);
            foreach (var ex in actor.Expressions)
            {
                if (ex.Face != null && !ProcAnimFx.Faces.Contains(ex.Face))
                    Err($"{who}.expressions (t {ex.Time}): unknown face '{ex.Face}' (expected {string.Join("/", ProcAnimFx.Faces)})");
                if (ex.Eyes != null && !ProcAnimFx.Eyes.Contains(ex.Eyes))
                    Err($"{who}.expressions (t {ex.Time}): unknown eyes '{ex.Eyes}' (expected {string.Join("/", ProcAnimFx.Eyes)})");
            }
        }

        Sorted(scene.Events.Select(e => e.Time), "events", Err);
        for (var i = 0; i < scene.Events.Count; i++)
        {
            var ev = scene.Events[i];
            if (ev.Target != null && scene.ActorIndex(ev.Target) < 0)
                Err($"events[{i}] (t {ev.Time}): unknown target '{ev.Target}'");
            if (ev.Time < -scene.Intro - 1e-4f || ev.Time > scene.Length + 1e-4f)
                Err($"events[{i}] (t {ev.Time}): outside -intro..length");
            if (ev.Holster && ev.Unholster)
                Err($"events[{i}]: holster and unholster at once");
            for (var j = 0; j < ev.Effects.Count; j++)
            {
                var fx = ev.Effects[j];
                var where = $"events[{i}].effects[{j}] ({fx.Type}, t {ev.Time})";
                if (fx.Count < -1)
                    Err($"{where}: count must be >= 0 (or -1 = default)");
                if (fx.Duration < -1f || fx.Duration is > -1f and < 0f)
                    Err($"{where}: duration must be >= 0 (or -1 = default)");
                if (fx.Strength < 0f)
                    Err($"{where}: strength must be >= 0");
                if (fx.Spread < 0f || fx.Spread > 360f)
                    Err($"{where}: spread must be within 0..360");
                if (fx.Size <= 0f)
                    Err($"{where}: size must be > 0");
                if (fx.Type is ProcAnimEffectType.Recoil or ProcAnimEffectType.Stagger && ProcAnimFx.DurationOf(fx) <= 0f)
                    Err($"{where}: a pose effect needs a duration > 0");
            }
        }

        return errors;
    }

    private static void Sorted(IEnumerable<float> times, string what, Action<string> err)
    {
        var last = float.NegativeInfinity;
        foreach (var t in times)
        {
            if (t < last)
            {
                err($"{what}: times must be ascending ({t} after {last})");
                return;
            }
            last = t;
        }
    }
}
