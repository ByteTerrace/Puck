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
/// completes and the node renders. A release during the build — a device loss, or the last lease given up — waits only
/// for the pipelines already in the driver, at most <see cref="SdfWorldPipelines.BuildConcurrency"/> of them, counted
/// through the factory and never timed.
/// </summary>
public sealed class SdfPipelineBuildLivenessLawTests {
    private const uint Extent = 32;

    [Fact]
    public void WhileAPipelineBuildIsHeldThePumpDrainsTheConsoleAndAWaitReachesItsDeadline() {
        using var directory = new TemporaryDirectory();
        using var gate = new ManualResetEventSlim(initialState: false);
        using var entered = new ManualResetEventSlim(initialState: false);
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
            BeforeComputePipeline = _ => {
                entered.Set();
                gate.Wait();
            },
        };
        var reports = new List<string>();
        var answered = new List<string>();
        using var runtime = new WorldViewGraphHost(
            documentDirectory: directory.RootPath,
            packager: new ShaderPackager(compiler: new ShaderCompiler(
                cacheDirectory: directory.PathOf(name: "cache"),
                toolchainDirectory: directory.PathOf(name: "no-tools")
            ))
        ) {
            Report = (name, message) => reports.Add(item: $"[pipeline: {name} {message}]"),
        };

        // An instance nothing resizes, so a wait for another extent can only end at its deadline.
        using var instances = FakeGraphInstances.Attach(
            create: name => new ShaderPipelineRenderNode(
                pipelines: new GpuPassPipelineCache(),
                deviceContext: gpu,
                height: 4,
                hostsOnDirectX: false,
                name: name,
                width: 4
            ),
            host: runtime
        );

        runtime.Reconcile(views: new WorldViewDefaults(Graphs: [new WorldViewGraph(
            Name: "ink",
            Source: "ink.hlsl"
        )]));

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
            pipelines: SdfTestPipelines.Cache(),
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
    public void ADeviceLossWaitsOnlyForThePipelinesInTheDriverAndTheNextFrameStartsAnother() {
        using var driver = new HeldDriver();
        var cache = SdfTestPipelines.Cache();
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
            BeforeComputePipeline = driver.Enter,
        };
        using var node = new SdfEngineNode(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: Frame()),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            pipelines: cache,
            width: Extent
        );
        // Disposed before the node, so a failing assertion releases the held build instead of leaving the node's
        // disposal waiting on it.
        using var opener = new DriverOpener(driver: driver);
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
        driver.WaitUntilFull();

        // The loss cancels the build inside the cache's gate before the set leaves the cache, so once the cache no
        // longer lists it the held creations can return: each creator then finds the cancel before claiming another.
        var loss = new Thread(start: node.OnDeviceLost);

        loss.Start();
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => (cache.SharedSets == 0),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        driver.Open();
        loss.Join();

        Assert.Equal(
            actual: (driver.Entered, PipelinesCreated(cache: cache)),
            expected: (SdfWorldPipelines.BuildConcurrency, ((long)SdfWorldPipelines.BuildConcurrency))
        );
        Assert.False(condition: node.IsReady);

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                _ = node.ProduceFrame(context: in context);

                return node.IsReady;
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
    }
    [Fact]
    public void OnlyTheLastReleaseCancelsAndItWaitsOnlyForThePipelinesInTheDriver() {
        using var driver = new HeldDriver();
        var cache = SdfTestPipelines.Cache();
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
            BeforeComputePipeline = driver.Enter,
        };
        using var opener = new DriverOpener(driver: driver);
        var first = cache.Acquire(
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels()
        );
        var last = cache.Acquire(
            device: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels()
        );

        driver.WaitUntilFull();

        // A release that leaves another holder cancels nothing and returns while the build is still held.
        first.Release();
        Assert.Equal(
            actual: (cache.SharedSets, last.Progress.Describe()),
            expected: (1, "building (0 of 10 pipelines created)")
        );

        var release = new Thread(start: last.Release);

        release.Start();
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => (cache.SharedSets == 0),
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        driver.Open();
        release.Join();

        Assert.Equal(
            actual: (driver.Entered, PipelinesCreated(cache: cache), last.Progress.Created),
            expected: (SdfWorldPipelines.BuildConcurrency, ((long)SdfWorldPipelines.BuildConcurrency), SdfWorldPipelines.BuildConcurrency)
        );
        Assert.Null(@object: last.Current);
    }

    private static long PipelinesCreated(SdfWorldPipelineCache cache) {
        Assert.True(condition: cache.Work.TryRead(
            kind: GpuWork.PipelinesCreated,
            value: out var created
        ));

        return created;
    }
    private static SdfFrame Frame() {
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
            )]
        );
    }

    private sealed class GateOpener(ManualResetEventSlim gate) : IDisposable {
        public void Dispose() => gate.Set();
    }
    private sealed class DriverOpener(HeldDriver driver) : IDisposable {
        public void Dispose() => driver.Open();
    }
    // A driver that holds every pipeline creation until the law opens it, counting the creations that entered. Once
    // it is open a creation passes straight through, so a creator that claimed a pipeline after a cancel is counted
    // rather than deadlocked.
    private sealed class HeldDriver : IDisposable {
        private readonly ManualResetEventSlim m_full = new(initialState: false);
        private readonly ManualResetEventSlim m_open = new(initialState: false);

        private int m_entered;

        public int Entered => Volatile.Read(location: ref m_entered);

        public void Dispose() {
            m_open.Set();
            m_full.Dispose();
            m_open.Dispose();
        }
        public void Enter(GpuComputePipelineDescription description) {
            // The device's region-copy pipeline builds beside the set, from another cache, so the driver lets it pass.
            if (
                m_open.IsSet ||
                ReferenceEquals(
                    objA: description,
                    objB: GpuRegion.CopyPipeline
                )
            ) {
                return;
            }

            if (Interlocked.Increment(location: ref m_entered) == SdfWorldPipelines.BuildConcurrency) {
                m_full.Set();
            }

            m_open.Wait();
        }
        public void Open() => m_open.Set();
        // Waits until as many creations as a build runs at once are held; the bound is liveness, and decides nothing.
        public void WaitUntilFull() => Assert.True(condition: m_full.Wait(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
    }
    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
    // "probe" answers at once; "arm" holds its session behind a one-second pipeline.wait for an extent no one requests.
    private sealed class ProbeModule(WorldViewGraphHost runtime) : ICommandModule {
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
