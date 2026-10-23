using System.Reflection;
using Puck.Commands;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>world.rates</c> echoes each live lever and its counts, switches every lever with
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

    [Fact]
    public void NoArgumentAndStatusEchoEachLeverAndItsCounts() {
        var (registry, settings, levers) = Rig();

        settings.RateCounts.Count(kind: SdfUpdateRateCounts.ShadowFarHeld);
        Assert.Equal(
            expected: "[world.rates: bodies=on views=on screens=on shadow-far=on bodies.posed=0 bodies.held=0 views.rated=0 screens.rated=0 shadow-far.rendered=0 shadow-far.held=1 display=0hz grid=none period-ms: 30hz=33.3 15hz=66.7 longest-ms: bodies.30hz=- bodies.15hz=- views.30hz=- views.15hz=- screens.30hz=- screens.15hz=- shadow-far.30hz=-]",
            actual: registry.Submit(line: "world.rates").Output
        );

        // On a 144 Hz signal a tier's period is whole refreshes, and each lever prints the longest interval it achieved.
        settings.RateIntervals.Advance(frameSeconds: (1d / 144d), rate: new Puck.Hosting.PresentationRate(DisplayHertz: 144, GridHertz: 144d));
        settings.RateIntervals.Record(hertz: 15, lever: SdfUpdateRates.Views, seconds: (9d / 144d));
        Assert.EndsWith(
            expectedEndString: " display=144hz grid=144hz period-ms: 30hz=27.8 15hz=62.5 longest-ms: bodies.30hz=- bodies.15hz=- views.30hz=- views.15hz=62.5 screens.30hz=- screens.15hz=- shadow-far.30hz=-]",
            actualString: registry.Submit(line: "world.rates").Output
        );

        settings.UpdateRates = SdfUpdateRates.Views;

        Assert.StartsWith(
            expectedStartString: "[world.rates: bodies=off views=on screens=off shadow-far=off ",
            actualString: registry.Submit(line: "world.rates status").Output
        );
        Assert.Empty(collection: levers);
    }
    [InlineData("on", SdfUpdateRates.All)]
    [InlineData("off", SdfUpdateRates.None)]
    [InlineData("toggle", SdfUpdateRates.None)]
    [InlineData("bodies off", (SdfUpdateRates.Views | SdfUpdateRates.Screens | SdfUpdateRates.ShadowFar))]
    [InlineData("views off", (SdfUpdateRates.Bodies | SdfUpdateRates.Screens | SdfUpdateRates.ShadowFar))]
    [InlineData("screens off", (SdfUpdateRates.Bodies | SdfUpdateRates.Views | SdfUpdateRates.ShadowFar))]
    [InlineData("shadow-far off", (SdfUpdateRates.Bodies | SdfUpdateRates.Views | SdfUpdateRates.Screens))]
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
        Assert.Contains(expectedSubstring: "status|on|off|toggle|bodies|views|screens|shadow-far on|off]", actualString: result.Output);
        Assert.Empty(collection: levers);
    }
}
