using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldFramePresenter.PrepareGraph"/>, the presenter's half of every frame of a world
/// whose views place graph instances, allocates nothing once its frame is steady: it reads the composed slots, places the
/// views and each instance's pane, pairs an instance with its camera, and hands its node the frame values, all over
/// storage the host and presenter already hold. The presenter is the one an offscreen boot composes, resolved with its
/// device sealed, over the counters world with one graph instance, the shipped ink pipeline, paired with the world's
/// camera beside the camera's own view.
/// </summary>
public sealed class WorldFramePresenterGraphLawTests : IDisposable {
    private const float Delta = (StepTicks / 50400f);
    private const uint Display = 64;
    private const string Pane = "pane";
    private const ulong StepTicks = 1680;
    private const string World = "tests/Puck.Counters/counters.world.json";
    // The shipped ink pipeline, relative to the counters world's directory.
    private const string InkPipeline = "../../src/Puck.World/Assets/pipelines/ink.graph.json";

    private readonly TemporaryDirectory m_stateDirectory = new(prefix: "puck-presenter-graph-");

    // The counters world's one camera slot on the left half, and a graph instance paired with that camera on the right.
    private static WorldDefinition WithPane(WorldDefinition definition) {
        var layout = definition.Views.Layouts[0];
        var camera = layout.Slots[0].Camera;

        return (definition with {
            ViewsRaw = (definition.Views with {
                Graphs = [new WorldViewGraph(
                    Camera: camera,
                    Name: Pane,
                    Source: InkPipeline
                )],
                Layouts = [(layout with {
                    Slots = [
                        new WorldViewSlot(Camera: camera, Height: 1f, Width: 0.5f, X: 0f, Y: 0f),
                        new WorldViewSlot(Height: 1f, Instance: Pane, Width: 0.5f, X: 0.5f, Y: 0f),
                    ],
                })],
            }),
        });
    }
    // One frame of the counters world's 30 Hz step, in engine ticks.
    private static FrameContext Frame(ulong index) => new(
        AccumulatorTicks: 0,
        DeltaTicks: StepTicks,
        ElapsedTicks: (index * StepTicks),
        FrameDeltaTicks: StepTicks,
        Host: null!,
        StepTicks: StepTicks,
        TargetHeight: Display,
        TargetWidth: Display
    );
    // A frame as the host presents one: the presenter captures the frame, which composes the slots, then prepares the
    // graph over them.
    private static void Present(WorldFramePresenter presenter, ulong index) {
        var frame = Frame(index: index);

        _ = presenter.CaptureFrame(
            deltaSeconds: Delta,
            height: Display,
            interpolationAlpha: 1f,
            width: Display
        );
        presenter.PrepareGraph(context: in frame);
    }

    public void Dispose() => m_stateDirectory.Dispose();
    [Fact]
    public void ASteadyGraphFrameIsPreparedWithoutAllocating() {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the pane's pipeline."
        );

        using var host = WorldBootHarness.Compose(
            edit: WithPane,
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        using var instances = FakeGraphInstances.Attach(
            create: static name => new ShaderPipelineRenderNode(
                deviceContext: new RefusingGpuDevice(),
                height: 4,
                hostsOnDirectX: false,
                name: name,
                width: 4
            ),
            host: host.Services.GetRequiredService<WorldViewGraphHost>()
        );
        var index = 0UL;

        // Frames until the pane's pipeline has compiled and installed, then a few more so every frame after is steady.
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    Present(index: index++, presenter: presenter);

                    return instances.Installed.Contains(item: Pane);
                },
                timeout: TimeSpan.FromSeconds(value: 60)
            ),
            userMessage: "The pane's pipeline never installed."
        );

        for (var settle = 0; (settle < 4); settle++) {
            Present(index: index++, presenter: presenter);
        }

        var steady = Frame(index: index);

        _ = presenter.CaptureFrame(
            deltaSeconds: Delta,
            height: Display,
            interpolationAlpha: 1f,
            width: Display
        );

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => presenter.PrepareGraph(context: in steady)),
            expected: 0L
        );
        // The pane's node read its paired camera, so the steady frame covered the camera path.
        Assert.NotEqual(
            actual: instances.NodeOf(instance: Pane)!.Frame.CameraFov,
            expected: 0f
        );
    }
}
