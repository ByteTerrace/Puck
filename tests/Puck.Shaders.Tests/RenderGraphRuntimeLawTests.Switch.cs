namespace Puck.Shaders.Tests;

// A package instance whose counter moves: an sdf.world instance's counter moves when it resolves a residency its passes
// cannot record as built (SdfWorldPasses.CanFollow), as a seat's view does when the world it crosses into needs more
// instances than the one it left. A residency its passes can record is followed in place, and its counter never moves.
public sealed partial class RenderGraphRuntimeLawTests {
    // The frames a view shows its last image while its passes rebuild against a residency they cannot follow, when each
    // rebuild has finished before the next frame. The frame the counter moves starts the rebuild and presents the last
    // image (ShaderPipelineRenderNode's CountsChanged hold), and the next installs and renders it.
    private const int ResidencySwitchHeldFrames = 1;

    [Fact]
    public void AViewWhoseCounterMovesHoldsItsLastImageUntilItsRebuiltPassesInstall() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(
            factory: view,
            package: RenderGraphPackageCatalog.SdfWorld
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(PackageInstance()),
            PackageView,
            new RenderGraphRuntimeGraph[1]
        );
        var parts = SdfWorldPackage.Fragment.Passes.Count;
        var index = 0L;

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                ProducePackageFrame(
                    frameIndex: index++,
                    runtime: runtime
                );

                return (view.Parts.Count >= parts);
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        var builds = view.Builds;
        var recorded = view.Parts.Count;
        var held = 0;

        view.Revision++;

        while (view.Parts.Count == recorded) {
            ProducePackageFrame(
                frameIndex: index++,
                runtime: runtime
            );

            if (view.Parts.Count != recorded) {
                break;
            }

            held++;

            Assert.True(
                condition: (held <= 30),
                userMessage: "the view never rendered again"
            );
            // Every pass has rebuilt before the next frame, so the frames counted are the hold's own, not a slow
            // build's.
            Assert.True(condition: SpinWait.SpinUntil(
                condition: () => (view.Builds >= (builds + parts)),
                timeout: TimeSpan.FromSeconds(value: 30)
            ));
            Thread.Sleep(millisecondsTimeout: 50);
        }

        Assert.Equal(
            actual: held,
            expected: ResidencySwitchHeldFrames
        );
    }
}
