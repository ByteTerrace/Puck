using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Presentation;
using Puck.Assets.Documents;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
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
/// camera beside the camera's own view. A definition delivered between frames reaches the screen binder before the frame
/// publishes the screens and declares the reads they make, so a screen retargeted to another camera's view reads that view
/// on the frame of the change.
/// </summary>
public sealed class WorldFramePresenterGraphLawTests : IDisposable {
    private const float Delta = (StepTicks / 50400f);
    private const uint Display = 64;
    private const string FirstCamera = "first";
    private const string Pane = "pane";
    private const string SecondCamera = "second";
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
    // The counters world with two filming cameras and one screen showing the first.
    private static WorldDefinition WithScreen(WorldDefinition definition) => (definition with {
        CamerasRaw = [.. definition.Cameras, Filming(name: FirstCamera), Filming(name: SecondCamera)],
        ScreensRaw = [Showing(camera: FirstCamera)],
    });
    private static WorldCamera Filming(string name) => new(
        Anchor: null,
        Name: name,
        RenderHeight: 72U,
        RenderWidth: 128U,
        Rig: new WorldCameraProgram(
            Name: $"{name}-rig",
            Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f))],
            Version: WorldCameraProgram.CurrentVersion
        )
    );
    private static WorldScreen Showing(string camera) => new(
        HalfDepth: 0.1f,
        HalfHeight: 0.9f,
        HalfWidth: 1.2f,
        Index: 0,
        Origin: new DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: 0f)),
        Right: new DocumentVector3(value: Vector3.UnitX),
        Round: 0f,
        Route: WorldScreenRoute.Passive,
        Source: new WorldScreenSource.View(CameraName: camera),
        Up: new DocumentVector3(value: Vector3.UnitY)
    );
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
    public void AScreenRetargetedBetweenFramesReachesTheBinderBeforeTheFramePublishes() {
        var builder = WorldBootHarness.Compose(
            edit: WithScreen,
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        );
        var calls = new List<string>();
        var descriptor = builder.Services.Single(predicate: static descriptor => (descriptor.ServiceType == typeof(IWorldScreenPresenter)));
        var binder = descriptor.ImplementationFactory!;

        _ = builder.Services.Remove(item: descriptor);
        _ = builder.Services.AddSingleton<IWorldScreenPresenter>(implementationFactory: sp => new RecordingScreens(
            calls: calls,
            inner: ((IWorldScreenPresenter)binder(arg: sp))
        ));

        using var host = builder.Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var index = 0UL;

        for (var settle = 0; (settle < 3); settle++) {
            Present(index: index++, presenter: presenter);
        }

        calls.Clear();
        client.DeliverDefinition(definition: (client.Definition with { ScreensRaw = [Showing(camera: SecondCamera)] }), version: default);

        // The host prepares a frame before its runtime captures the world's, so the change frame publishes the screens,
        // and declares the views they read, before any capture has seen the delivery.
        var change = Frame(index: index);

        presenter.PrepareGraph(context: in change);

        Assert.Equal(
            actual: calls,
            expected: ["cameras", $"screens {SecondCamera}", "publish"]
        );
    }
    // A camera view is a view of the world's own frame. The dress hands the binder its own views once they are latched,
    // and the binder films each camera view into the frame after them, so the world's residency renders it. The
    // presentation places only its own views, so a filmed view never becomes a pane.
    [Fact]
    public void TheDressFilmsCameraViewsIntoTheFrameAfterItsOwnViews() {
        var builder = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        );
        var calls = new List<string>();
        var descriptor = builder.Services.Single(predicate: static descriptor => (descriptor.ServiceType == typeof(IWorldScreenPresenter)));
        var binder = descriptor.ImplementationFactory!;
        var filmed = new SdfViewSnapshot(
            Camera: CameraSnapshot.LookAt(
                fieldOfViewRadians: 0.9f,
                position: new Vector3(x: 1f, y: 2f, z: 3f),
                target: Vector3.Zero,
                viewportHeight: 72U,
                viewportWidth: 128U
            ),
            Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
        ) {
            Quality = new SdfViewQuality { DisableAmbientOcclusion = true, DisableSoftShadows = true },
        };

        _ = builder.Services.Remove(item: descriptor);
        _ = builder.Services.AddSingleton<IWorldScreenPresenter>(implementationFactory: sp => new RecordingScreens(
            calls: calls,
            filmed: filmed,
            inner: ((IWorldScreenPresenter)binder(arg: sp))
        ));

        using var host = builder.Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();

        for (var index = 0UL; (index < 3UL); index++) {
            Present(index: index, presenter: presenter);
        }

        calls.Clear();

        var frame = presenter.CaptureFrame(
            deltaSeconds: Delta,
            height: Display,
            interpolationAlpha: 1f,
            width: Display
        );
        var own = (frame.Views.Count - 1);

        Assert.True(condition: (own >= 1));
        Assert.Contains(collection: calls, expected: $"film {own}");
        Assert.Equal(actual: frame.Views[own], expected: filmed);
        Assert.DoesNotContain(collection: frame.Views.Take(count: own), filter: view => (view == filmed));

        presenter.ViewRendered = view => {
            Assert.InRange(actual: view, high: (own - 1), low: 0);

            return true;
        };

        var context = Frame(index: 3UL);

        presenter.PrepareGraph(context: in context);
    }
    [Fact]
    public void ACameraExportExtentDoesNotResizeThePresentersDisplay() {
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();

        foreach (var (width, height) in new[] { (128U, 72U), (96U, 64U) }) {
            presenter.ResizeDisplay(height: height, width: width);

            var frame = presenter.CaptureFrame(
                deltaSeconds: Delta,
                height: 256U,
                interpolationAlpha: 1f,
                width: 256U
            );
            var view = Assert.Single(collection: frame.Views);

            Assert.Equal(
                actual: view.Camera.AspectRatio,
                expected: (width / ((float)height))
            );
        }
    }
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
                pipelines: new GpuPassPipelineCache(),
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

    // The binder, recording the calls the presenter makes, and filming one more view into each frame when given one.
    private sealed class RecordingScreens(List<string> calls, IWorldScreenPresenter inner, SdfViewSnapshot? filmed = null) : IWorldScreenPresenter {
        public IAudioMachine? AudioMachine(int index) => inner.AudioMachine(index: index);
        public IAudioMachine? AudioOutput(string instance, string output) => inner.AudioOutput(
            instance: instance,
            output: output
        );
        public void NotifyDeviceLost() => inner.NotifyDeviceLost();
        public void PresentFrame(DynamicTransform[] transforms, ulong authoritativeTick) => inner.PresentFrame(
            authoritativeTick: authoritativeTick,
            transforms: transforms
        );
        public void FilmViews(DynamicTransform[] transforms, ulong authoritativeTick, float presentationSeconds, List<SdfViewSnapshot> views) {
            calls.Add(item: $"film {views.Count}");
            inner.FilmViews(
                authoritativeTick: authoritativeTick,
                presentationSeconds: presentationSeconds,
                transforms: transforms,
                views: views
            );

            if (filmed is { } view) {
                views.Add(item: view);
            }
        }
        public void Publish(in FrameContext context) {
            calls.Add(item: "publish");
            inner.Publish(context: in context);
        }
        public void ReconcileCameras(IReadOnlyList<WorldCamera> cameras) {
            calls.Add(item: "cameras");
            inner.ReconcileCameras(cameras: cameras);
        }
        public void ReconcileScreens(IReadOnlyList<WorldScreen> screens) {
            calls.Add(item: $"screens {string.Join(separator: ' ', values: screens.Select(selector: static screen => (screen.Source as WorldScreenSource.View)?.CameraName))}");
            inner.ReconcileScreens(screens: screens);
        }
        public WorldScreenSource.Text? TextSourceAt(int index) => inner.TextSourceAt(index: index);
    }
}
