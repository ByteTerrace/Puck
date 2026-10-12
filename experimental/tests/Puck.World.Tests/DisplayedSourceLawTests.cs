using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Hosting;
using Puck.Platform;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST (acceptance law 4): a displayed source survives pause, retargeting and a destination change. The
/// uploaded-sources canary fixture boots headless; screen 0 shows its machine's video. Its line in <c>world.screens</c>
/// is read after every step: the mapping's source instance (name and handle) and the route's <c>input:</c>.
/// <list type="bullet">
/// <item>A world-rate pause and resume leaves the source.</item>
/// <item>A retarget to a QR source takes effect, and <c>screen.source 0 row</c> returns the screen to the source its row
/// authors, the baseline.</item>
/// <item>A route change from Presentation to Simulation and back changes <c>input:</c> alone, both from the baseline
/// and under the retarget, whose live bind survives it.</item>
/// <item>The camera-view census ends where it began.</item>
/// </list>
/// Headless, a pipeline node has no rendered instance to pause and the views are not configured, so the pipeline pause
/// and the camera-view retarget are not reached here.
/// </summary>
[Collection(SceneProbeCollection.Name)]
public sealed class DisplayedSourceLawTests {
    private const string World = "tests/Puck.World.Canaries/uploaded-sources/fixture.world.json";

    private static readonly ulong Step = EngineTicks.PerRate(ratePerSecond: 30u);

    private sealed class Boot : IDisposable {
        private readonly TemporaryDirectory m_state = new(prefix: "puck-displayed-source-");
        private readonly CommandRegistry m_registry;
        private readonly IWorldScreenPresenter m_binder;
        private readonly WorldInstanceHost m_instances;

        public Boot() {
            var builder = WorldBootHarness.Compose(presentation: WorldHostPresentation.None, stateDirectory: m_state, world: World);

            builder.Services.AddSingleton<ICameraCaptureService, NullCameraCaptureService>();

            var host = m_state.Own(owner: builder.Build());

            m_registry = host.Services.GetRequiredService<CommandRegistry>();
            m_binder = host.Services.GetRequiredService<IWorldScreenPresenter>();
            m_instances = host.Services.GetRequiredService<WorldInstanceHost>();
            Settle();
        }

        public void Dispose() => m_state.Dispose();
        // One step, then the presentation delivery a frame would make: the rows to the binder, and a publish.
        public void Settle() {
            var server = m_instances.Boot!.Server;

            server.Advance(stepTicks: Step);

            var definition = server.Definition;
            var context = new FrameContext(
                AccumulatorTicks: 0UL,
                DeltaTicks: 0UL,
                ElapsedTicks: 0UL,
                FrameDeltaTicks: 0UL,
                Host: new HostContext(capabilities: new Dictionary<Type, object> {
                    [typeof(IGpuDeviceContext)] = new FakeGpuDevice(),
                }),
                StepTicks: Step,
                TargetHeight: 64U,
                TargetWidth: 64U
            );

            m_binder.ReconcileScreens(screens: [.. definition.Screens, .. WorldPrototypeFacets.Seated(definition: definition)]);
            m_binder.Publish(context: in context);
        }
        public string Run(string line) {
            var result = m_registry.Submit(line: line);

            Assert.False(condition: result.IsError, userMessage: $"{line}: {result.Output}");
            Settle();
            return result.Output;
        }
        // Screen 0's segment of world.screens.
        public string Line() {
            var screens = Run(line: "world.screens");
            var first = screens["[world.screens: ".Length..].Split(separator: " | ")[0];

            Assert.StartsWith(actualString: first, comparisonType: StringComparison.Ordinal, expectedStartString: "0 ");
            return first;
        }
        public int Census() {
            var refresh = Run(line: "world.view-refresh");
            var count = refresh[(refresh.IndexOf(comparisonType: StringComparison.Ordinal, value: "; ") + 2)..].Split(separator: ' ')[0];

            return int.Parse(s: count, provider: System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // The mapping's source instance: its name and handle, the first token of the mapping segment.
    private static string Source(string line) => line[(line.IndexOf(comparisonType: StringComparison.Ordinal, value: " mapping ") + " mapping ".Length)..].Split(separator: ' ')[0];
    private static string Input(string line) => line.Split(separator: ' ').Single(predicate: static token => token.StartsWith(comparisonType: StringComparison.Ordinal, value: "input:"));
    private static void Route(Boot boot, string input) => _ = boot.Run(line: $"world.row.set screens 0 route.input \"{input}\"");

    [Fact]
    public void ADisplayedSourceSurvivesPauseRetargetingAndADestinationChange() {
        using var boot = new Boot();
        var baseline = boot.Line();
        var census = boot.Census();

        Assert.StartsWith(expectedStartString: "producer:source$machine$", actualString: Source(line: baseline), comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: "input:Presentation", actual: Input(line: baseline));

        // Pause: the world rate pauses for a step and resumes.
        _ = boot.Run(line: "world.rate pause");
        _ = boot.Run(line: "world.rate resume");
        Assert.Equal(expected: Source(line: baseline), actual: Source(line: boot.Line()));

        // Retarget there and back.
        _ = boot.Run(line: "screen.source 0 qr hello");
        Assert.NotEqual(expected: Source(line: baseline), actual: Source(line: boot.Line()));
        _ = boot.Run(line: "screen.source 0 row");
        Assert.Equal(expected: Source(line: baseline), actual: Source(line: boot.Line()));

        // The route from the baseline.
        Route(boot: boot, input: "Simulation");
        Assert.Equal(expected: "input:Simulation", actual: Input(line: boot.Line()));
        Assert.Equal(expected: Source(line: baseline), actual: Source(line: boot.Line()));
        Route(boot: boot, input: "Presentation");
        Assert.Equal(expected: "input:Presentation", actual: Input(line: boot.Line()));
        Assert.Equal(expected: Source(line: baseline), actual: Source(line: boot.Line()));

        // The route under the retarget: its live bind survives the row's change.
        _ = boot.Run(line: "screen.source 0 qr hello");
        var retargeted = Source(line: boot.Line());

        Route(boot: boot, input: "Simulation");
        Assert.Equal(expected: retargeted, actual: Source(line: boot.Line()));
        Assert.Contains(expectedSubstring: "'hello'", actualString: boot.Run(line: "screen.source 0 qr"));
        Route(boot: boot, input: "Presentation");
        Assert.Equal(expected: retargeted, actual: Source(line: boot.Line()));
        _ = boot.Run(line: "screen.source 0 row");
        Assert.Equal(expected: baseline, actual: boot.Line());

        Assert.Equal(expected: census, actual: boot.Census());
    }
}
