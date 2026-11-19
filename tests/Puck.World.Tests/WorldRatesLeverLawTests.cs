using System.Reflection;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>world.rates</c> switches every lever with
/// <c>on</c>/<c>off</c> or one lever by name, refuses any other word, and submits the chosen set as the <c>rates</c>
/// session lever carrying its flags, without touching the settings itself: the server's grant check applies the lever.
/// </summary>
public sealed class WorldRatesLeverLawTests {
    private static (CommandRegistry Registry, WorldRenderSettings Settings, List<WorldSessionLever> Levers) Rig() {
        var settings = new WorldRenderSettings(defaults: Fixtures.BuildDocument().Render);
        var link = DispatchProxy.Create<IServerLink, WorldCullLeverLawTests.LeverRecorder>();
        var levers = ((WorldCullLeverLawTests.LeverRecorder)((object)link)).Levers;
        var module = new WorldRenderLeverCommandModule(
            link: link,
            renderProbe: null!,
            server: null!,
            settings: settings
        );

        return (new CommandRegistry(modules: [module]), settings, levers);
    }

    [InlineData("on", SdfUpdateRates.All)]
    [InlineData("off", SdfUpdateRates.None)]
    [InlineData("toggle", SdfUpdateRates.None)]
    [InlineData("bodies off", (SdfUpdateRates.All & ~SdfUpdateRates.Bodies))]
    [InlineData("views off", (SdfUpdateRates.All & ~SdfUpdateRates.Views))]
    [InlineData("screens off", (SdfUpdateRates.All & ~SdfUpdateRates.Screens))]
    [InlineData("shadow-far off", (SdfUpdateRates.All & ~SdfUpdateRates.ShadowFar))]
    [InlineData("shadow-distant off", (SdfUpdateRates.All & ~SdfUpdateRates.ShadowDistant))]
    [InlineData("shadow-far on", SdfUpdateRates.All)]
    [Theory]
    public void AChoiceIsSubmittedAsTheRatesLever(string words, SdfUpdateRates rates) {
        var (registry, settings, levers) = Rig();
        var result = registry.Submit(line: $"world.rates {words}");

        Assert.False(condition: result.IsError);

        var lever = Assert.Single(collection: levers);

        Assert.Equal(
            expected: (WorldSessionLevers.Rates, ((double)rates)),
            actual: (lever.Name, lever.A)
        );
        Assert.Equal(expected: SdfUpdateRates.All, actual: settings.UpdateRates);
    }
    [InlineData("world.rates sideways")]
    [InlineData("world.rates bodies")]
    [InlineData("world.rates wheels on")]
    [InlineData("world.rates bodies maybe")]
    [Theory]
    public void AnythingElseIsRefusedNamingTheLevers(string line) {
        var (registry, _, levers) = Rig();
        var result = registry.Submit(line: line);

        Assert.True(condition: result.IsError);
        // The refusal names the form nearest the words typed (CommandForms.Refuse).
        Assert.True(condition: (result.Output.Contains(value: "expected [status|on|off|toggle]", comparisonType: StringComparison.Ordinal) ||
            result.Output.Contains(value: "expected <bodies|views|screens|shadow-far|shadow-distant> on|off", comparisonType: StringComparison.Ordinal)), userMessage: result.Output);
        Assert.Empty(collection: levers);
    }
}
