using System.Numerics;
using Content.Shared._CorvaxGoob.ProcAnim;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client._CorvaxGoob.ProcAnim;

/// <summary>
/// Draws the finisher dizzy stars and blood bursts: deterministic ballistic droplets (<see cref="ProcAnimTimeline.Droplets"/>)
/// as whole-pixel squares in world space; landed drops stay as small stains until the burst fades.
/// </summary>
public sealed class ProcAnimBloodOverlay : Overlay
{
    private readonly ProcAnimVisualsSystem _system;
    private readonly IGameTiming _timing;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public ProcAnimBloodOverlay(ProcAnimVisualsSystem system, IGameTiming timing)
    {
        _system = system;
        _timing = timing;
        ZIndex = 5;
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _system.Bursts.Count > 0 || _system.Stars.Count > 0;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var now = _timing.CurTime;
        var handle = args.WorldHandle;
        var bursts = _system.Bursts;
        for (var b = bursts.Count - 1; b >= 0; b--)
        {
            var burst = bursts[b];
            var age = (float) (now - burst.Start).TotalSeconds;
            if (age > burst.Blood.Life)
            {
                bursts.RemoveAt(b);
                continue;
            }

            if (burst.Map != args.MapId)
                continue;

            foreach (var drop in ProcAnimTimeline.Droplets(burst.Blood, burst.EventIndex, burst.Origin, age))
            {
                var x = MathF.Round(drop.Position.X);
                var y = MathF.Round(drop.Position.Y);
                var center = burst.Anchor + burst.AxisX * x + burst.AxisY * y;
                var half = drop.Size / 64f; // drop.Size px wide, 32 px per tile
                var color = new Color(
                    MathF.Min(1f, burst.Color.R * drop.Shade),
                    MathF.Min(1f, burst.Color.G * drop.Shade),
                    MathF.Min(1f, burst.Color.B * drop.Shade),
                    drop.Alpha);
                handle.DrawRect(new Box2(center - new Vector2(half), center + new Vector2(half)), color);
            }
        }

        // Dizzy stars: a 1 px dot, or a 1 px-armed plus when big.
        const float px = 1f / 32f;
        foreach (var star in _system.Stars)
        {
            if (star.Map != args.MapId)
                continue;
            var h = new Vector2(px / 2f);
            handle.DrawRect(new Box2(star.Position - h, star.Position + h), star.Color);
            if (star.Size < 2)
                continue;
            foreach (var d in new[] { new Vector2(px, 0), new Vector2(-px, 0), new Vector2(0, px), new Vector2(0, -px) })
                handle.DrawRect(new Box2(star.Position + d - h, star.Position + d + h), star.Color);
        }
    }
}
