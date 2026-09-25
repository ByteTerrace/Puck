using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the frame thread never blocks on GPU pipeline creation. A host pump drains the console and then
/// produces a frame from the SDF engine node, the way the windowed and offscreen hosts do; the node's pipeline factory
/// blocks every creation until the law releases it, which is how a cold driver cache behaves under load. While that
/// build is held, every produced frame returns at once with nothing new to present, console lines keep being answered,
/// and a <c>pipeline.wait</c> armed through a text session reaches its deadline and reports it. Released, the build
/// completes and the node renders.
/// </summary>
public sealed class SdfPipelineBuildLivenessLawTests {
    private const uint Extent = 32;

    [Fact]
    public void WhileAPipelineBuildIsHeldThePumpDrainsTheConsoleAndAWaitReachesItsDeadline() {
        using var directory = new TemporaryDirectory();
        using var gate = new ManualResetEventSlim(initialState: false);
        using var entered = new ManualResetEventSlim(initialState: false);
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
            BeforeComputePipeline = () => {
                entered.Set();
                gate.Wait();
            },
        };
        var reports = new List<string>();
        var answered = new List<string>();
        using var runtime = new WorldPipelineRuntime(
            documentDirectory: directory.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(
                cacheDirectory: directory.PathOf(name: "cache"),
                toolchainDirectory: directory.PathOf(name: "no-tools")
            ))
        ) {
            Report = (name, message) => reports.Add(item: $"[pipeline: {name} {message}]"),
        };

        // An instance nothing resizes, so a wait for another extent can only end at its deadline.
        runtime.Register(
            name: "ink",
            node: new ShaderPipelineRenderNode(
                deviceContext: gpu,
                height: 4,
                hostsOnDirectX: false,
                name: "ink",
                width: 4
            )
        );

        var source = new TextCommandSource(registry: new CommandRegistry(modules: [new ProbeModule(runtime: runtime)]));
        var session = source.CreateSession(
            onResult: (line, _) => answered.Add(item: line),
            principal: Principal.Console
        );
        using var node = new SdfEngineNode(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            pipelines: new SdfWorldPipelineCache(),
            width: Extent
        );
        // Disposed before the node, so a failing assertion releases the held build instead of leaving the node's
        // disposal waiting on it.
        using var opener = new GateOpener(gate: gate);
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> {
                [typeof(IGpuDeviceContext)] = gpu,
            }),
            StepTicks: 0UL,
            TargetHeight: Extent,
            TargetWidth: Extent
        );
        var emptyFrames = 0;

        // One host frame: drain the console, then produce.
        void Pump() {
            source.Collect();

            if (node.ProduceFrame(context: in context).IsEmpty) {
                emptyFrames++;
            }
        }

        Pump();
        Assert.True(
            condition: entered.Wait(
                cancellationToken: TestContext.Current.CancellationToken,
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The first produced frame did not start the pipeline build."
        );

        session.Enqueue(line: "probe before");
        session.Enqueue(line: "arm");
        session.Enqueue(line: "probe after");

        // The bound is liveness for the law itself; the one-second wait deadline is what it observes.
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                Pump();

                return answered.Contains(item: "probe after");
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        Assert.False(condition: gate.IsSet);
        Assert.False(condition: node.IsReady);
        Assert.Equal(
            actual: answered,
            expected: ["probe before", "arm", "probe after"]
        );
        Assert.Single(
            collection: reports,
            predicate: static line => line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "[pipeline: ink wait resized 8 8 timed out after 1s"
            )
        );
        Assert.True(
            condition: (emptyFrames > 1),
            userMessage: $"Only {emptyFrames} frames were produced while the build was held."
        );

        gate.Set();
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                Pump();

                return node.IsReady;
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        Assert.False(condition: node.ProduceFrame(context: in context).IsEmpty);
    }
    [Fact]
    public void WhileTheEngineBuildIsHeldAHostedPaneStillProducesEveryFrame() {
        using var gate = new ManualResetEventSlim(initialState: false);
        using var entered = new ManualResetEventSlim(initialState: false);
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
            BeforeComputePipeline = () => {
                entered.Set();
                gate.Wait();
            },
        };
        var pane = new CountingNode();
        using var node = new SdfEngineNode(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame(child: "pane")),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            pipelines: new SdfWorldPipelineCache(),
            width: Extent
        );
        // Disposed before the node, so a failing assertion releases the held build instead of leaving the node's
        // disposal waiting on it.
        using var opener = new GateOpener(gate: gate);
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> {
                [typeof(IGpuDeviceContext)] = gpu,
            }),
            StepTicks: 0UL,
            TargetHeight: Extent,
            TargetWidth: Extent
        );

        node.RegisterChild(
            name: "pane",
            node: pane
        );
        Assert.True(condition: node.ProduceFrame(context: in context).IsEmpty);
        Assert.True(condition: entered.Wait(
                cancellationToken: TestContext.Current.CancellationToken,
                timeout: TimeSpan.FromSeconds(value: 30)
            ));

        for (var frame = 0; (frame < 4); frame++) {
            Assert.True(condition: node.ProduceFrame(context: in context).IsEmpty);
        }

        Assert.False(condition: gate.IsSet);
        Assert.False(condition: node.IsReady);
        Assert.Equal(
            actual: pane.Produced,
            expected: 5
        );
    }
    [Fact]
    public void ADeviceLossWaitsOutAHeldBuildAndTheNextFrameStartsAnother() {
        using var gate = new ManualResetEventSlim(initialState: false);
        using var entered = new ManualResetEventSlim(initialState: false);
        var creations = 0;
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
            BeforeComputePipeline = () => {
                _ = Interlocked.Increment(location: ref creations);
                entered.Set();
                gate.Wait();
            },
        };
        using var node = new SdfEngineNode(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            pipelines: new SdfWorldPipelineCache(),
            width: Extent
        );
        // Disposed before the node, so a failing assertion releases the held build instead of leaving the node's
        // disposal waiting on it.
        using var opener = new GateOpener(gate: gate);
        var context = new FrameContext(
            AccumulatorTicks: 0UL,
            DeltaTicks: 0UL,
            ElapsedTicks: 0UL,
            FrameDeltaTicks: 0UL,
            Host: new HostContext(capabilities: new Dictionary<Type, object> {
                [typeof(IGpuDeviceContext)] = gpu,
            }),
            StepTicks: 0UL,
            TargetHeight: Extent,
            TargetWidth: Extent
        );

        Assert.True(condition: node.ProduceFrame(context: in context).IsEmpty);
        Assert.True(condition: entered.Wait(
                cancellationToken: TestContext.Current.CancellationToken,
                timeout: TimeSpan.FromSeconds(value: 30)
            ));

        // The loss cancels the held build and blocks until its current creation returns: released from another thread,
        // the build stops at its next pipeline instead of creating the rest on a device about to be recreated.
        var release = new Thread(start: () => {
            Thread.Sleep(millisecondsTimeout: 50);
            gate.Set();
        });

        release.Start();
        node.OnDeviceLost();
        release.Join();
        Assert.Equal(expected: 1, actual: Volatile.Read(location: ref creations));
        Assert.False(condition: node.IsReady);

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                _ = node.ProduceFrame(context: in context);

                return node.IsReady;
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
    }

    private static SdfFrame Frame(string? child = null) {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: false,
            Time: 0f,
            Views: [new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(
                    fieldOfViewRadians: 1f,
                    position: new Vector3(x: 0f, y: 0f, z: -5f),
                    target: Vector3.Zero,
                    viewportHeight: Extent,
                    viewportWidth: Extent
                ),
                Region: new NormalizedRect(
                    Height: 1f,
                    Width: 1f,
                    X: 0f,
                    Y: 0f
                )
            ) {
                Child = child,
            }]
        );
    }

    private sealed class GateOpener(ManualResetEventSlim gate) : IDisposable {
        public void Dispose() => gate.Set();
    }
    // A hosted pane that has not published an image yet, counting how often its host produces it.
    private sealed class CountingNode : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "pane",
            SurfaceId: SurfaceId.New()
        );
        public int Produced { get; private set; }

        public void Dispose() { }
        public Surface ProduceFrame(in FrameContext context) {
            Produced++;

            return default;
        }
    }
    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
    // "probe" answers at once; "arm" holds its session behind a one-second pipeline.wait for an extent no one requests.
    private sealed class ProbeModule(WorldPipelineRuntime runtime) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Answers at once.",
                handler: static (_, _) => CommandResult.None,
                name: "probe"
            );
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Unbindable,
                description: "Holds the session behind a pipeline wait.",
                handler: (context, _) => {
                    context.TextSession!.HoldWhile(hold: runtime.ArmWait(
                        extent: (8u, 8u),
                        name: "ink",
                        phase: WorldPipelinePhase.Resized,
                        seconds: 1,
                        submissions: 0
                    ));

                    return CommandResult.None;
                },
                name: "arm"
            );
        }
    }
}
