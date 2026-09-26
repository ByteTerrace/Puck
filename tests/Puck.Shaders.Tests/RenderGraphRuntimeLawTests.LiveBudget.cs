using System.Text;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// The live budget (RenderGraphLiveBudget): the runtime's latest schedule read back per instance, with its decision,
// extent, divisor, passes, pass-pixels and its newest completed submission's counts.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string CameraSource = "camera";

    // A root showing a pane refreshed every other frame and a static camera source, and the frames that drive it.
    private static (RenderGraphRuntime Runtime, Func<RenderGraphFrame> Next) LiveBudgetScene(FakePipelineGpu gpu) {
        var recorders = new Recorders();

        recorders.Registry.RegisterProducer(
            factory: _ => new FakeProducer(gpu: gpu),
            package: RenderGraphInstance.SourcePackage(producer: CameraSource)
        );

        var set = Set(
            RenderGraphInstance.Source(
                name: CameraSource,
                producer: CameraSource
            ),
            Instance(name: "pane") with { Refresh = RenderGraphRefresh.Every(divisor: 2) },
            Instance(
                name: "main",
                reads: [new RenderGraphRead(Producer: "pane"), new RenderGraphRead(Producer: CameraSource)]
            )
        );
        var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            null!,
            Graph(ScreensGraph(false)),
            Graph(ScreensGraph(false, "pane", CameraSource), ("pane", "pane"), (CameraSource, CameraSource))
        );
        RenderGraphFootprint[] footprints = [
            new(Consumer: "main", Height: 0.5, Producer: "pane", Width: 0.5),
            new(Consumer: "main", Height: 1.0, Producer: CameraSource, Width: 1.0),
        ];
        RenderGraphRoot[] roots = [new(Height: 1.0, Instance: "main", Width: 1.0)];
        RenderGraphSourceState[] sources = [new(Cadence: ImageSourceCadence.Static, Height: 48, Instance: CameraSource, Width: 64)];
        var index = 0L;

        return (runtime, () => new RenderGraphFrame(
            DisplayHeight: Display,
            DisplayHertz: 60,
            DisplayWidth: Display,
            Footprints: footprints,
            Index: index++,
            Roots: roots,
            Sources: sources
        ));
    }
    private static string LiveBudget(RenderGraphLiveBudget budget, RenderGraphRuntime runtime) {
        var text = new StringBuilder();

        budget.Describe(
            into: text,
            runtime: runtime
        );

        return text.ToString();
    }
    // One instance's row of the live budget, from its name to the next row.
    private static string RowOf(string budget, string instance) => budget.Split(separator: "; ").Single(predicate: row => row.StartsWith(comparisonType: StringComparison.Ordinal, value: (instance + " ")));

    /// <summary>Across frames the budget reads what the scheduler decided: the root renders every frame at the
    /// display's extent, the pane at half the display every other frame, and the static source once, at its negotiated
    /// extent, then waits; a rendered instance's ledger counts the passes its completed submission executed.</summary>
    [Fact]
    public void TheLiveBudgetReadsEachInstancesScheduleAcrossFrames() {
        var gpu = new FakePipelineGpu();

        var (runtime, next) = LiveBudgetScene(gpu: gpu);
        var budget = new RenderGraphLiveBudget();

        using (runtime) {
            Assert.Equal(expected: "live not scheduled yet", actual: LiveBudget(budget: budget, runtime: runtime));

            var renderedPane = new List<bool>();
            var renderedCamera = new List<bool>();

            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        var frame = next();

                        _ = runtime.ProduceFrame(context: default, frame: in frame);

                        return runtime.IsSettled;
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: "The runtime's scheduled instances never all produced."
            );

            for (var frame = 0; (frame < 4); frame++) {
                var described = next();

                _ = runtime.ProduceFrame(context: default, frame: in described);

                var text = LiveBudget(budget: budget, runtime: runtime);
                var pane = RowOf(budget: text, instance: "pane");
                var camera = RowOf(budget: text, instance: CameraSource);
                var main = RowOf(budget: text, instance: "main");

                Assert.StartsWith(actualString: text, expectedStartString: $"live frame {described.Index}: 3 instance(s), ");
                Assert.StartsWith(actualString: main, expectedStartString: $"main rendered root {Display}x{Display} every frame passes 1 ");
                Assert.Contains(actualString: pane, expectedSubstring: $" {(Display / 2)}x{(Display / 2)} 1/2 frames passes 1 ");
                Assert.StartsWith(actualString: camera, expectedStartString: $"{CameraSource} waiting source 64x48 every frame passes 1 0 pass-pixels");
                Assert.Contains(actualString: main, expectedSubstring: " ledger ");
                renderedPane.Add(item: pane.StartsWith(comparisonType: StringComparison.Ordinal, value: "pane rendered "));
                renderedCamera.Add(item: camera.StartsWith(comparisonType: StringComparison.Ordinal, value: $"{CameraSource} rendered "));
            }

            // The pane alternates, whichever frame it starts on, and the static source rendered once during the
            // settling frames and never again.
            Assert.Equal(expected: 2, actual: renderedPane.Count(predicate: static rendered => rendered));
            Assert.NotEqual(expected: renderedPane[0], actual: renderedPane[1]);
            Assert.DoesNotContain(collection: renderedCamera, expected: true);
        }
    }
    /// <summary>The budget is stable between frames and reads without allocating once its builder has room.</summary>
    [Fact]
    public void TheLiveBudgetIsStableAndReadsWithoutAllocating() {
        var gpu = new FakePipelineGpu();

        var (runtime, next) = LiveBudgetScene(gpu: gpu);
        var budget = new RenderGraphLiveBudget();

        using (runtime) {
            for (var frame = 0; (frame < 8); frame++) {
                var described = next();

                _ = runtime.ProduceFrame(context: default, frame: in described);
            }

            var first = LiveBudget(budget: budget, runtime: runtime);

            Assert.Equal(expected: first, actual: LiveBudget(budget: budget, runtime: runtime));

            var text = new StringBuilder(capacity: (first.Length * 2));

            Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
                _ = text.Clear();
                budget.Describe(
                    into: text,
                    runtime: runtime
                );
            }));
        }
    }
}
