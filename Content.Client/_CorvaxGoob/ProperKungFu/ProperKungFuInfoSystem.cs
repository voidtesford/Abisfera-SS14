using System.Numerics;
using Content.Client.CharacterInfo;
using Content.Client.Stylesheets;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._CorvaxGoob.ProperKungFu;

/// <summary>
/// Adds the "Proper Kung Fu" section (the trimmed one for pupils) to the character info window,
/// under the objectives: skills and combos with icons, combo inputs as intent icons, passives, synergies, finishers.
/// </summary>
public sealed class ProperKungFuInfoSystem : EntitySystem
{
    [Dependency] private readonly SpriteSystem _sprite = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ProperKungFuComponent, CharacterInfoSystem.GetCharacterInfoControlsEvent>(OnGetControls);
    }

    private void OnGetControls(Entity<ProperKungFuComponent> ent, ref CharacterInfoSystem.GetCharacterInfoControlsEvent args)
    {
        // Wolf Pack doubles carry the component too, but never have a character window of their own.
        args.Controls.Add(BuildSection(ent.Comp.Pupil));
    }

    public Control BuildSection(bool pupil)
    {
        var box = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(5),
        };

        box.AddChild(new Label
        {
            Text = Loc.GetString(pupil ? "proper-kung-fu-info-title-pupil" : "proper-kung-fu-info-title"),
            StyleClasses = { StyleBase.StyleClassLabelHeading },
            Modulate = Color.FromHex("#d33b3b"),
        });

        ProperKungFuInfo.Kind? section = null;
        foreach (var entry in ProperKungFuInfo.GetEntries(pupil))
        {
            if (section != entry.Kind)
            {
                section = entry.Kind;
                box.AddChild(new Label
                {
                    Text = Loc.GetString(ProperKungFuInfo.SectionName(entry.Kind)),
                    FontColorOverride = Color.FromHex("#e08080"),
                    Margin = new Thickness(0, 6, 0, 2),
                });
            }

            box.AddChild(BuildRow(entry));
        }

        return box;
    }

    private Control BuildRow(ProperKungFuInfo.Entry entry)
    {
        var row = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            Margin = new Thickness(0, 2),
        };

        if (entry.Icon is { } icon)
        {
            row.AddChild(new TextureRect
            {
                Texture = _sprite.Frame0(icon),
                TextureScale = new Vector2(1, 1),
                MinSize = new Vector2(32, 32),
                VerticalAlignment = Control.VAlignment.Top,
                Stretch = TextureRect.StretchMode.KeepCentered,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }
        else
        {
            row.AddChild(new Control { MinSize = new Vector2(38, 0) });
        }

        var text = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
        };

        var header = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        var name = Loc.GetString(entry.Name);
        if (entry.Threshold is { } threshold)
            name = Loc.GetString("proper-kung-fu-info-threshold", ("name", name), ("threshold", threshold));

        header.AddChild(new Label { Text = name, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var input in entry.Inputs)
        {
            header.AddChild(new TextureRect
            {
                Texture = _sprite.Frame0(ProperKungFuInfo.IntentIcon(input)),
                ToolTip = Loc.GetString($"proper-kung-fu-info-intent-{input.ToString().ToLowerInvariant()}"),
                TooltipDelay = 0.2f,
                VerticalAlignment = Control.VAlignment.Center,
                Margin = new Thickness(1, 0),
            });
        }

        text.AddChild(header);

        var desc = new RichTextLabel { HorizontalExpand = true };
        desc.SetMessage(Loc.GetString(entry.Description));
        text.AddChild(desc);

        row.AddChild(text);
        return row;
    }
}
