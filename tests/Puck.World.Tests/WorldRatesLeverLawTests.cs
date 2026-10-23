using System.Reflection;
using Puck.Commands;
using Puck.SdfVm;
using Puck.Testing;
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
    private static (CommandRegistry Registry, WorldRenderSettings Settings, List<WorldSessionLever> Levers) Rig(WorldRenderProbe? probe = null) {
        var settings = new WorldRenderSettings(defaults: Fixtures.BuildDocument().Render);
        var link = DispatchProxy.Create<IServerLink, WorldCullLeverLawTests.LeverRecorder>();
        var levers = ((WorldCullLeverLawTests.LeverRecorder)((object)link)).Levers;
        var module = new WorldRenderLeverCommandModule(
            link: link,
            renderProbe: probe!,
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
            expected: "[world.rates: bodies=on views=on screens=on shadow-far=on bodies.posed=0 bodies.held=0 views.rated=0 screens.rated=0 shadow-far.rendered=0 shadow-far.held=1 display=0hz grid=none period-ms: 30hz=33.3 15hz=66.7 longest-ms: bodies.30hz=- bodies.15hz=- views.30hz=- views.15hz=- screens.30hz=- screens.15hz=- shadow-far.30hz=- bodies: - views: - screens: -]",
            actual: registry.Submit(line: "world.rates").Output
        );

        // On a 144 Hz signal a tier's period is whole refreshes, and each lever prints the longest interval it achieved.
        settings.RateIntervals.Advance(frameSeconds: (1d / 144d), rate: new Puck.Hosting.PresentationRate(DisplayHertz: 144, GridHertz: 144d));
        settings.RateIntervals.Record(hertz: 15, lever: SdfUpdateRates.Views, seconds: (9d / 144d));
        Assert.EndsWith(
            expectedEndString: " display=144hz grid=144hz period-ms: 30hz=27.8 15hz=62.5 longest-ms: bodies.30hz=- bodies.15hz=- views.30hz=- views.15hz=62.5 screens.30hz=- screens.15hz=- shadow-far.30hz=- bodies: - views: - screens: -]",
            actualString: registry.Submit(line: "world.rates").Output
        );

        // Each lever lists the items it rated on the latest frame: a body by its pixels per metre (or near, inside a
        // view's near plane), a view by its display pixels and a screen by its share of its image's height.
        settings.RateReadings.Publish(lever: SdfUpdateRates.Bodies, readings: [new(Hertz: 30, Name: "body:3", Size: 24.14d), new(Hertz: 60, Name: "body:1", Size: double.PositiveInfinity)]);
        settings.RateReadings.Publish(lever: SdfUpdateRates.Views, readings: [new(Hertz: 15, Name: "session", Size: 54.6d)]);
        settings.RateReadings.Publish(lever: SdfUpdateRates.Screens, readings: [new(Hertz: 30, Name: "deck", Size: 0.625d)]);
        Assert.EndsWith(
            expectedEndString: " bodies: body:3=24.1px/m@30hz body:1=near@60hz views: session=55px@15hz screens: deck=0.63x@30hz]",
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
    // world.debug-view selects a tier view and the frames carry their tiers while it shows; toggle puts back the final
    // image, and next steps past the last view to off.
    [Fact]
    public void ATierViewCarriesTheFramesTiersWhileItShows() {
        var probe = new WorldRenderProbe();
        using var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new EmptyFrameSource(),
            height: 8U,
            kernels: SdfTestPipelines.Kernels(),
            name: "world",
            pipelines: SdfTestPipelines.Cache(),
            width: 8U
        );

        probe.Residency = residency;

        var (registry, settings, _) = Rig(probe: probe);

        Assert.Equal(expected: "[world.debug-view: rates]", actual: registry.Submit(line: "world.debug-view toggle rates").Output);
        Assert.True(condition: settings.RateTints);
        Assert.Equal(expected: DebugViewModes.Rates, actual: residency.DebugMode);
        Assert.Equal(expected: "[world.debug-view: rate-flash]", actual: registry.Submit(line: "world.debug-view next").Output);
        Assert.True(condition: settings.RateTints);
        Assert.Equal(expected: "[world.debug-view: off]", actual: registry.Submit(line: "world.debug-view next").Output);
        Assert.False(condition: settings.RateTints);
        Assert.Equal(expected: "[world.debug-view: rates]", actual: registry.Submit(line: "world.debug-view rates").Output);
        Assert.Equal(expected: "[world.debug-view: off]", actual: registry.Submit(line: "world.debug-view toggle rates").Output);
        Assert.False(condition: settings.RateTints);
        Assert.True(condition: registry.Submit(line: "world.debug-view toggle sideways").IsError);
    }

    private sealed class EmptyFrameSource : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => throw new InvalidOperationException(message: "No frame is captured.");
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
