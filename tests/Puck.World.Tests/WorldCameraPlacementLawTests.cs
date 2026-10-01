using System.Buffers.Binary;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The host prepares its graph before the residency captures the world. That capture composes both the
/// cameras and placements, and an eased rect does not change a view's scheduled allocation extent.</summary>
public sealed class WorldCameraPlacementLawTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new();

    public void Dispose() => m_directory.Dispose();
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Theory]
    public void EveryTransitionFramePlacesTheRectItsCameraProjects(bool interrupted, bool subpixel) {
        Run(checkPlacement: true, interrupted: interrupted, subpixel: subpixel);
    }
    [Fact]
    public void AnEasedRectCrossesQuantizationStepsWithoutChangingTheNodesExtent() {
        Run(checkPlacement: false, interrupted: false, subpixel: false);
    }
    [Fact]
    public void AnArrivingCameraKeepsAFiniteProjectionAtItsCollapsedFirstFrame() {
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_directory,
            world: "tests/Puck.Counters/counters.world.json",
            edit: definition => {
                var camera = definition.Views.Layouts[0].Slots[0].Camera;

                return definition with {
                    ViewsRaw = definition.Views with {
                        Layouts = [
                            new WorldViewLayout(Name: "one", Slots: [new WorldViewSlot(Camera: camera)]),
                            Layout(camera: camera, name: "two", width: 0.75f),
                        ],
                    },
                };
            }).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var composition = host.Services.GetRequiredService<WorldCompositionState>();

        composition.ActiveLayout = "one";
        _ = presenter.CaptureFrame(deltaSeconds: 0.1f, height: 128, interpolationAlpha: 1f, width: 256);
        composition.ActiveLayout = "two";
        var arriving = presenter.CaptureFrame(deltaSeconds: 0.1f, height: 128, interpolationAlpha: 1f, width: 256);

        Assert.Equal(expected: 2, actual: arriving.Views.Count);
        Assert.Equal(expected: 0f, actual: arriving.Views[1].Region.Width);
        Assert.Equal(expected: 0f, actual: arriving.Views[1].Region.Height);
        Assert.True(condition: float.IsFinite(f: arriving.Views[1].Camera.AspectRatio));
        Assert.True(condition: (arriving.Views[1].Camera.AspectRatio > 0f));
        var growing = presenter.CaptureFrame(deltaSeconds: 0.01f, height: 128, interpolationAlpha: 1f, width: 256);

        Assert.True(condition: (growing.Views[1].Region.Width > 0f));
        Assert.Equal(expected: ((2f * growing.Views[1].Region.Width) / growing.Views[1].Region.Height),
            actual: growing.Views[1].Camera.AspectRatio);
    }
    [Fact]
    public void APairedPaneProjectsAtItsPlacedAspectWhileItsAllocationKeepsTheEnvelope() {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the pane's pipeline."
        );

        const string Pane = "pane";
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_directory,
            world: "tests/Puck.Counters/counters.world.json",
            edit: definition => definition with {
                ViewsRaw = definition.Views with {
                    Graphs = [new WorldViewGraph(
                        Camera: definition.Views.Layouts[0].Slots[0].Camera,
                        Name: Pane,
                        Source: "../../src/Puck.World/Assets/pipelines/ink.graph.json")],
                    Layouts = [
                        new WorldViewLayout(Name: "full", Slots: [new WorldViewSlot(Instance: Pane)], TransitionSeconds: 0.6f),
                        new WorldViewLayout(Name: "quarter", Slots: [new WorldViewSlot(Instance: Pane, Width: 0.25f)], TransitionSeconds: 0.6f),
                    ],
                },
            }).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var graphs = host.Services.GetRequiredService<WorldViewGraphHost>();
        var composition = host.Services.GetRequiredService<WorldCompositionState>();
        using var instances = FakeGraphInstances.Attach(
            host: graphs,
            create: static name => new ShaderPipelineRenderNode(
                deviceContext: new RefusingGpuDevice(), height: 4, hostsOnDirectX: false,
                name: name, pipelines: new GpuPassPipelineCache(), width: 4));
        // The frame block a node writes, its own extent the allocation envelope the root reads the pane at.
        var layout = ShaderPipelineParameterLayout.ForPackage(config: null, members: [], package: "placed");
        var placedOffset = ((int)layout.Layout.Bindings
            .Single(predicate: static binding => ((binding.Set == 0) && (binding.Members.Count != 0)))
            .Members.Single(predicate: static member => (member.Name == ShaderFrameInterface.PlacedExtent)).Offset);
        var block = new byte[layout.FrameBlockSizeBytes];
        var frames = new List<(float Width, float Height, float CameraAspect, float PlacedWidth, float PlacedHeight)>();

        presenter.ResizeDisplay(height: 500, width: 1000);
        presenter.ViewRendered = static _ => true;
        composition.ActiveLayout = "full";
        for (var index = 0; (index < 20); index++) {
            if (index == 2) { composition.ActiveLayout = "quarter"; }
            var context = new FrameContext(
                AccumulatorTicks: 0, DeltaTicks: 5040, ElapsedTicks: (((ulong)index) * 5040),
                FrameDeltaTicks: 5040, Host: null!, StepTicks: 5040, TargetHeight: 500, TargetWidth: 1000);

            presenter.PrepareGraph(context: in context);
            _ = presenter.CaptureFrame(deltaSeconds: 0.1f, height: 500, interpolationAlpha: 1f, width: 1000);
            Assert.True(condition: graphs.TryGet(instance: WorldViewGraphs.MainInstance, pass: Pane, placement: out var placement));
            var footprint = Assert.Single(collection: graphs.Footprints, predicate: static footprint => (footprint.Producer == Pane));
            var envelope = (Width: ((uint)(footprint.Width * 1000d)), Height: ((uint)(footprint.Height * 500d)));

            Assert.True(condition: ((IRenderGraphHitScene)graphs).TryCamera(
                camera: out var camera, instance: instances.Instances.IndexOf(name: Pane)));
            layout.WriteFrame(block: block, extent: envelope, frame: 0UL, values: instances.NodeOf(instance: Pane)!.Frame);
            var placedWidth = BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: placedOffset));
            var placedHeight = BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: (placedOffset + 4)));

            // The allocation holds the full display through the ease.
            Assert.Equal(actual: envelope, expected: (1000u, 500u));
            frames.Add(item: (placement.Width, placement.Height, camera.AspectRatio, placedWidth, placedHeight));
        }
        // Settled at a quarter of a 1000x500 display, the pane's 2:1 allocation is shown at 1:2, and its pass projects at
        // the camera's 1:2, not the allocation's 2:1.
        var settled = frames[^1];

        Assert.Equal(actual: settled.Width, expected: 0.25f);
        Assert.Equal(actual: settled.CameraAspect, expected: 0.5f);
        Assert.Equal(actual: (settled.PlacedWidth / settled.PlacedHeight), expected: settled.CameraAspect);
        // Every eased frame between: the pass maps its output onto the rect the camera projects for.
        Assert.True(condition: (frames.Select(selector: static frame => frame.Width).Distinct().Count() > 3));
        Assert.All(collection: frames, action: static frame => {
            Assert.Equal(actual: frame.PlacedWidth, expected: (frame.Width * 1000f));
            Assert.Equal(actual: frame.PlacedHeight, expected: (frame.Height * 500f));
            Assert.Equal(actual: (frame.PlacedWidth / frame.PlacedHeight), expected: frame.CameraAspect);
        });
    }
    [Fact]
    public void AResidencyReusingAFrozenCaptureStillPlacesEveryPreparedFrame() {
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_directory,
            world: "tests/Puck.Counters/counters.world.json",
            edit: definition => definition with {
                ViewsRaw = definition.Views with {
                    Layouts = [Layout(camera: definition.Views.Layouts[0].Slots[0].Camera, name: "split", width: 0.75f)],
                },
            }).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var graphs = host.Services.GetRequiredService<WorldViewGraphHost>();
        using var instances = FakeGraphInstances.Attach(
            host: graphs,
            create: static name => new ShaderPipelineRenderNode(
                deviceContext: new RefusingGpuDevice(), height: 4, hostsOnDirectX: false,
                name: name, pipelines: new GpuPassPipelineCache(), width: 4));
        var context = new FrameContext(
            AccumulatorTicks: 0, DeltaTicks: 5040, ElapsedTicks: 5040,
            FrameDeltaTicks: 5040, Host: null!, StepTicks: 5040, TargetHeight: 128, TargetWidth: 256);
        var request = new FrameCaptureRequest(path: Path.Combine(path1: m_directory.RootPath, path2: "frozen.png"), converge: 8);
        var composed = 0;

        presenter.FrameComposed = () => composed++;
        presenter.ViewRendered = static _ => true;
        presenter.BeginConvergence(request: request);
        presenter.PrepareGraph(context: in context);
        var first = presenter.CaptureFrame(deltaSeconds: 0f, height: 128, interpolationAlpha: 1f, width: 256);

        for (var index = 0; (index < 8); index++) {
            // SdfWorldResidency.Capture returns its frozen frame here without calling the presenter again.
            presenter.PrepareGraph(context: in context);
            Assert.True(condition: graphs.TryGet(instance: "main", pass: graphs.Synthesized!.ViewPasses[1], placement: out var placement));
            Assert.True(condition: placement.Shown);
            Assert.Equal(expected: first.Views[1].Region, actual: new NormalizedRect(
                Height: placement.Height, Width: placement.Width, X: placement.Left, Y: placement.Top));
            Assert.Contains(collection: graphs.Footprints, filter: footprint => (footprint.Producer == WorldRootGraph.ProducerOf(view: 1)));
            Assert.Equal(actual: composed, expected: (index + 2));
        }
        Assert.True(condition: request.TryFail(error: new OperationCanceledException()));
    }

    private void Run(bool interrupted, bool checkPlacement, bool subpixel) {
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_directory,
            world: "tests/Puck.Counters/counters.world.json",
            edit: definition => {
                var camera = definition.Views.Layouts[0].Slots[0].Camera;

                return definition with {
                    ViewsRaw = definition.Views with {
                        Layouts = [
                            Layout(camera: camera, name: "wide", width: 0.75f),
                            Layout(camera: camera, name: "narrow", width: (subpixel ? 0.001f : 0.25f)),
                        ],
                    },
                };
            }).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var graphs = host.Services.GetRequiredService<WorldViewGraphHost>();
        var composition = host.Services.GetRequiredService<WorldCompositionState>();
        var gpu = new FakeGpuDevice(trackObjects: true);
        using var instances = FakeGraphInstances.Attach(
            host: graphs,
            create: static name => new ShaderPipelineRenderNode(
                deviceContext: new RefusingGpuDevice(), height: 4, hostsOnDirectX: false,
                name: name, pipelines: new GpuPassPipelineCache(), width: 4));
        using var node = new ShaderPipelineRenderNode(
            deviceContext: gpu, height: 4, hostsOnDirectX: false,
            name: WorldRootGraph.ProducerOf(view: 1), pipelines: new GpuPassPipelineCache(), width: 4);

        presenter.ResizeDisplay(height: 128, width: 256);
        presenter.ViewRendered = static _ => true;
        composition.ActiveLayout = "wide";
        RenderGraphSchedule[]? schedules = null;
        RenderGraphHistory? history = null;
        (int Width, int Height)? extent = null;
        ShaderPipelinePlan? installed = null;
        var creations = 0;
        var regions = new HashSet<NormalizedRect>();

        for (var index = 0; (index < 20); index++) {
            if (index == 2) { composition.ActiveLayout = "narrow"; }
            if (interrupted && (index == 5)) { composition.ActiveLayout = "wide"; }
            var context = new FrameContext(
                AccumulatorTicks: 0, DeltaTicks: 5040, ElapsedTicks: (((ulong)index) * 5040),
                FrameDeltaTicks: 5040, Host: null!, StepTicks: 5040, TargetHeight: 128, TargetWidth: 256);

            presenter.PrepareGraph(context: in context);
            var frame = presenter.CaptureFrame(deltaSeconds: 0.1f, height: 128, interpolationAlpha: 1f, width: 256);
            var view = frame.Views[1];

            _ = regions.Add(item: view.Region);
            if (checkPlacement) {
                Assert.True(condition: graphs.TryGet(instance: "main", pass: graphs.Synthesized!.ViewPasses[1], placement: out var placement));
                Assert.Equal(expected: view.Region, actual: new NormalizedRect(
                    Height: placement.Height, Width: placement.Width, X: placement.Left, Y: placement.Top));
                Assert.Equal(expected: ((2f * view.Region.Width) / view.Region.Height), actual: view.Camera.AspectRatio);
            } else {
                schedules ??= [new RenderGraphSchedule(set: instances.Instances), new RenderGraphSchedule(set: instances.Instances)];
                var schedule = schedules[(index % 2)];

                history ??= RenderGraphHistory.Empty(set: instances.Instances);
                RenderGraphScheduler.Schedule(
                    frame: new RenderGraphFrame(DisplayHeight: 128, DisplayHertz: 60, DisplayWidth: 256,
                        Footprints: graphs.Footprints, Index: index, Roots: [new RenderGraphRoot(Height: 1, Instance: "main", Width: 1)], Tick: index),
                    history: history, schedule: schedule, set: instances.Instances);
                history = schedule.Next;
                var row = schedule.Instances[instances.Instances.IndexOf(name: WorldRootGraph.ProducerOf(view: 1))];

                if (index == 0) { continue; }
                extent ??= (row.Width, row.Height);
                Assert.Equal(expected: extent.Value, actual: (row.Width, row.Height));
                node.Resize(width: ((uint)row.Width), height: ((uint)row.Height));
                if (index == 1) {
                    // Run the real node on a device-free fill graph. Its allocation extent comes from the same
                    // schedule the runtime uses; camera dressing and SDF grid dipping have their own package laws.
                    node.Swap(pipeline: Fill());
                    TestLiveness.Until(step: () => {
                        _ = node.ProduceFrame(context: default);
                        return node.IsReady;
                    });
                    installed = node.Plan;
                    creations = gpu.Created.Count;
                } else {
                    _ = node.ProduceFrame(context: in context);
                    Assert.Same(expected: installed, actual: node.Plan);
                    Assert.Equal(expected: creations, actual: gpu.Created.Count);
                }
            }
        }
        Assert.True(condition: (regions.Count > 3));
    }
    private static CompiledShaderPipeline Fill() {
        var plan = new ShaderPipelineCompiler().Compile(definition: new RenderGraphDefinition(
            name: "fill",
            outputs: ["image"],
            passes: [new ShaderPipelinePass(
                EntryPoint: "main", Inputs: [], Kind: ShaderPipelineDocumentPassKind.Compute,
                Name: "fill", Outputs: ["image"], Source: "fill.hlsl")],
            resources: [new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(), Format: "R8G8B8A8Unorm", Name: "image")]
        ));
        var bytecode = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = new byte[] { 0x03, 0x02, 0x23, 0x07 } };

        return new CompiledShaderPipeline(plan: plan, shaders: new Dictionary<string, CompiledShader> {
            ["fill"] = new(diagnostics: [], dxil: bytecode, name: "fill", sourceHash: "fill", sourcePath: "fill.hlsl", spirv: bytecode),
        });
    }
    private static WorldViewLayout Layout(string? camera, string name, float width) => new(
        Name: name,
        Slots: [new WorldViewSlot(Camera: camera, Width: 0.25f), new WorldViewSlot(Camera: camera, Width: width, X: 0.25f)],
        TransitionRenderScale: 0.5f,
        TransitionSeconds: 0.6f);
}
