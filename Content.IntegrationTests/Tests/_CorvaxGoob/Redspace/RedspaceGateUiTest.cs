using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._CorvaxGoob.Redspace.UI;
using Content.IntegrationTests.Tests.Interaction;
using Content.Shared._CorvaxGoob.Redspace;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Localization;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Redspace;

/// <summary>
/// Opens the Euclidean gate window for real on the client. The XAML namescope is only checked when the window is
/// constructed, so a name clash with FancyWindow (e.g. "CloseButton") compiles fine and only blows up here.
/// </summary>
[TestOf(typeof(RedspaceGateWindow))]
public sealed class RedspaceGateUiTest : InteractionTest
{
    [Test]
    public async Task WindowConstructsAndTakesState()
    {
        var loc = Client.ResolveDependency<ILocalizationManager>();
        await Client.WaitAssertion(() =>
        {
            RedspaceGateWindow window = null;
            Assert.DoesNotThrow(() => window = new RedspaceGateWindow());
            Assert.DoesNotThrow(() => window!.UpdateState(new RedspaceGateBoundUserInterfaceState
            {
                Open = true,
                GatePosition = new Vector2(1, 2),
                TargetMapName = "test",
                DiskInserted = true,
                DiskValid = true,
                Target = new Vector2(3, 4),
                Redspace = 2,
                BluespaceCapacity = 50,
                RedspaceCapacity = 24,
                Bluespace = 43,
            }));

            // Both fuel pools are shown separately with their own capacity.
            Assert.That(window!.FindControl<Label>("RedspaceCountLabel").Text, Is.EqualTo("2/24"));
            Assert.That(window.FindControl<Label>("BluespaceCountLabel").Text, Is.EqualTo("43/50"));

            // Plain counts, no burn-time explanations; total run time, next-burn countdown and the usage hint are gone.
            Assert.That(window.FindControl<Label>("BluespaceLabel").Text,
                Is.EqualTo(loc.GetString("redspace-gate-window-bluespace", ("count", 43))));
            Assert.That(window.FindControl<Label>("RedspaceLabel").Text,
                Is.EqualTo(loc.GetString("redspace-gate-window-redspace", ("count", 2))));
            Assert.That(window.FindControl<Label>("BluespaceLabel").Text, Does.Not.Contain("("));
            Assert.That(window.FindControl<Label>("RedspaceLabel").Text, Does.Not.Contain("("));
            var all = Descendants(window).ToList();
            Assert.That(all.Any(c => c.Name is "RemainingLabel" or "FuelHintLabel" or "NextBurnLabel"), Is.False);
            Assert.That(all.OfType<RichTextLabel>(), Is.Empty);

            // Our buttons must not shadow the window chrome's named controls (FancyWindow/DefaultWindow).
            var names = Descendants(window!).OfType<Button>().Select(b => b.Name).Where(n => n != null).ToList();
            Assert.That(names, Does.Contain("ClosePortalButton"));
            Assert.That(names.Count(n => n == "CloseButton"), Is.LessThanOrEqualTo(1));
            window.Close();
        });
    }

    [Test]
    public async Task ActivateOpensWindow()
    {
        await SpawnTarget("RedspaceGate");
        await RunTicks(5);
        await Activate();
        await RunTicks(10);

        Assert.That(IsUiOpen(RedspaceGateUiKey.Key), Is.True, "activating the gate should open its UI");
        var window = GetWindow<RedspaceGateWindow>();
        Assert.That(window.IsOpen, Is.True);

        await CloseBui(RedspaceGateUiKey.Key);
        Assert.That(IsUiOpen(RedspaceGateUiKey.Key), Is.False);
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (var child in control.Children)
        {
            yield return child;
            foreach (var sub in Descendants(child))
                yield return sub;
        }
    }
}
