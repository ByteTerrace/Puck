using System.Globalization;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of a chain of package passes that draw nothing, shaped as a world's default root places its views: each pass
/// places one view over the base the pass before it wrote. A chain whose every pass draws nothing publishes the first
/// input it stands for, the world's image, with no pass drawing a copy; a pass that draws in the middle is handed that
/// image as its base, and the passes after it stand for its output. A reader that is not a package pass still refuses
/// the stand-in (<see cref="APassWhoseOutputAnotherPassReadsIsRefusedByNameWhenItDrawsNothing"/>).
/// </summary>
public sealed partial class RenderGraphRuntimeLawTests {
    private const int ChainViews = 4;

    // The root's graph over four views: view n places its own version of the world over the stage the view before it
    // wrote, the first over the world itself, the last into the published frame.
    private static CompiledShaderPipeline ChainGraph() {
        var passes = new List<RenderGraphPackagePass>();
        var resources = new List<ShaderPipelineResource> {
            Image(
                external: true,
                name: "world"
            ),
        };

        for (var view = 1; (view <= ChainViews); view++) {
            var source = ViewVersion(view: view);
            var output = ((view == ChainViews)
                ? "frame"
                : StageVersion(view: view));

            resources.Add(item: Image(
                external: true,
                name: source
            ));
            resources.Add(item: Image(name: output));
            passes.Add(item: new RenderGraphPackagePass(
                Inputs: [
                    ((view == 1)
                        ? "world"
                        : StageVersion(view: (view - 1))),
                    source,
                ],
                Name: source,
                Outputs: [output],
                Package: Place
            ));
        }

        return Compile(definition: new RenderGraphDefinition(
            Name: "chain",
            Outputs: ["frame"],
            Packages: passes,
            Resources: resources,
            Schema: RenderGraphSchemas.Graph
        ));
    }
    private static (RenderGraphRuntime Runtime, Frames Frames, Counter Main) ChainScene(FakePipelineGpu gpu, Recorders recorders) {
        var runtime = Runtime(
            gpu,
            recorders,
            Set(
                Instance(name: "camera"),
                Instance(
                    name: "main",
                    reads: new RenderGraphRead(Producer: "camera")
                )
            ),
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(
                ChainGraph(),
                [
                    ("world", "camera"),
                    .. Enumerable.Range(
                        count: ChainViews,
                        start: 1
                    ).Select(selector: static view => (ViewVersion(view: view), "camera")),
                ]
            )
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        frames.Settle();

        return (runtime, frames, recorders.Of(instance: "main"));
    }
    private static string StageVersion(int view) => ("stage" + view.ToString(provider: CultureInfo.InvariantCulture));
    private static string ViewVersion(int view) => ("view" + view.ToString(provider: CultureInfo.InvariantCulture));

    [Fact]
    public void AChainOfPassesThatDrawNothingPublishesTheWorldWithNoCopy() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, main) = ChainScene(
            gpu: gpu,
            recorders: new Recorders(Camera, Place)
        );

        using (runtime) {
            gpu.Recording = true;
            main.Outcome = RenderGraphPackageOutcome.DrewNothing;

            var shown = frames.Next();
            var world = main.PassRecords[ViewVersion(view: 1)].Input;

            Assert.NotEqual(
                actual: world,
                expected: 0
            );
            Assert.Equal(
                actual: shown.ImageHandle,
                expected: world
            );
            Assert.Equal(
                actual: runtime.Node(instance: 1).PublishedLayout,
                expected: GpuImageLayout.ShaderReadOnly
            );

            // Every pass was told it may stand in, and every pass after the first was handed the world as its base.
            for (var view = 1; (view <= ChainViews); view++) {
                var record = main.PassRecords[ViewVersion(view: view)];

                Assert.True(condition: record.MayStandIn);
                Assert.Equal(
                    actual: record.Input,
                    expected: world
                );
            }

            // A frame that draws again publishes the last pass's own output.
            main.Outcome = RenderGraphPackageOutcome.Drew;

            var drawn = frames.Next();

            Assert.Equal(
                actual: drawn.ImageHandle,
                expected: main.PassRecords[ViewVersion(view: ChainViews)].Output
            );
            Assert.NotEqual(
                actual: drawn.ImageHandle,
                expected: world
            );
            AssertTransitionsContinue(gpu: gpu);
        }
    }
    [Fact]
    public void APassThatDrawsInTheMiddleOfAChainReadsTheWorldAndTheRestStandForItsOutput() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, main) = ChainScene(
            gpu: gpu,
            recorders: new Recorders(Camera, Place)
        );

        using (runtime) {
            var middle = ViewVersion(view: 2);

            gpu.Recording = true;
            main.Outcome = RenderGraphPackageOutcome.DrewNothing;
            main.PassOutcomes[middle] = RenderGraphPackageOutcome.Drew;

            for (var frame = 0; (frame < 3); frame++) {
                var shown = frames.Next();
                var world = main.PassRecords[ViewVersion(view: 1)].Input;
                var drawn = main.PassRecords[middle];

                Assert.Equal(
                    actual: drawn.Input,
                    expected: world
                );
                Assert.NotEqual(
                    actual: drawn.Output,
                    expected: world
                );
                Assert.Equal(
                    actual: shown.ImageHandle,
                    expected: drawn.Output
                );

                for (var view = 3; (view <= ChainViews); view++) {
                    Assert.Equal(
                        actual: main.PassRecords[ViewVersion(view: view)].Input,
                        expected: drawn.Output
                    );
                }
            }

            AssertTransitionsContinue(gpu: gpu);
        }
    }
}
