using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: offscreen, the fixed-step pump never advances past an armed capture's tick until that capture
/// is served or refused. The harness is the offscreen host without a GPU: the real <see cref="FixedStepPump"/> holding
/// its clock, stepping a real <see cref="WorldServer"/> whose <see cref="WorldCaptureScheduler"/> is published after
/// every step, and a real <see cref="SdfEngineNode"/> over <c>FakeGpuDevice</c> producing one frame after every pump
/// call. The node's pipeline factory blocks every creation on a gate the law holds, which is how a cold driver shader
/// cache behaves: the node presents nothing and serves no capture until the gate opens and its build installs.
/// </summary>
public sealed class WorldCaptureHoldLawTests : IDisposable {
    private const uint Extent = 32;
    private const ulong FirstTick = 10UL;
    private const int HeldIterations = 200;
    private const ulong SecondTick = 30UL;

    private readonly TemporaryDirectory m_directory = new();

    // Steps the server as WorldHostStep does, and counts every hold question a pump asks.
    private sealed class CaptureStepSimulation(WorldServer server, WorldCaptureScheduler scheduler) : IFixedStepSimulation {
        public bool AwaitsFrame => scheduler.AwaitsFrame;
        public int HoldQuestions { get; private set; }
        public uint RatePerSecond => ((uint)server.Definition.SimulationRateHz);

        public bool HoldsClock(ulong withheldTicks) {
            HoldQuestions++;

            return scheduler.HoldsClock(withheldTicks: withheldTicks);
        }
        public void SettleOwedFrames() => scheduler.Drain();
        public void Step(in FixedStepContext context, in CommandSnapshot commands) => _ = WorldServerStepShell.Step(
            context: in context,
            publishTick: _ => scheduler.PublishTick(tick: (server.NextInputTick - 1UL)),
            server: server,
            tape: null
        );
    }
    // Forwards every request to the node and keeps it, so the law can read each request's own outcome.
    private sealed class RecordingTarget(SdfEngineNode node) : ICaptureRequestTarget {
        public string? PendingCapturePath => node.PendingCapturePath;
        public List<FrameCaptureRequest> Requests { get; } = [];

        public void RequestCapture(FrameCaptureRequest request) {
            node.RequestCapture(request: request);
            Requests.Add(item: request);
        }
    }
    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
    private sealed class Run : IDisposable {
        private readonly FrameContext m_context;

        private readonly ManualResetEventSlim m_gate = new(initialState: false);

        private readonly FixedStepPump m_pump;
        private readonly InputRouter m_router;
        private readonly HostRow m_row;
        private readonly ulong m_stepTicks;

        public Run(string directory, bool holdsClock) {
            var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version) {
                BeforeComputePipeline = () => m_gate.Wait(),
            };

            m_row = HostRow.Build(
                definition: (Fixtures.BuildDocument() with {
                    Captures = new WorldCapturesSection(
                        Directory: directory,
                        Rows: [
                            Row(
                                station: "first",
                                tick: FirstTick
                            ),
                            Row(
                                station: "second",
                                tick: SecondTick
                            ),
                        ]
                    ),
                }),
                name: "boot"
            );
            Node = new SdfEngineNode(
                brickPoolVoxelCapacity: 0,
                frameSource: new FixedFrameSource(frame: Frame()),
                height: Extent,
                kernels: SdfTestPipelines.Kernels(),
                pipelines: new SdfWorldPipelineCache(),
                width: Extent
            );
            Target = new RecordingTarget(node: Node);
            Scheduler = new WorldCaptureScheduler(
                backend: "vulkan",
                captureTarget: () => Target,
                directory: directory,
                server: m_row.Server,
                unservedReason: () => Node.UnservedCaptureReason,
                worldFile: "fixture.world.json"
            );
            Simulation = new CaptureStepSimulation(
                scheduler: Scheduler,
                server: m_row.Server
            );

            var registry = new CommandRegistry(modules: []);

            m_router = new InputRouter(
                bindings: new NoBindings(),
                principalResolver: new ConsolePrincipal(),
                registry: registry
            );
            m_pump = new FixedStepPump(
                captureOriginTicks: 0UL,
                holdsClock: holdsClock,
                inputRouter: m_router,
                registry: registry,
                simulation: Simulation
            );
            m_stepTicks = EngineTicks.PerRate(ratePerSecond: Simulation.RatePerSecond);
            m_context = new FrameContext(
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
        }

        public SdfEngineNode Node { get; }
        public WorldCaptureScheduler Scheduler { get; }
        public WorldServer Server => m_row.Server;
        public CaptureStepSimulation Simulation { get; }
        public RecordingTarget Target { get; }

        // The tick and capture-scope state hash the server held when each served request's frame completed it.
        public Dictionary<string, (ulong Tick, string Hash)> Served { get; } = new(comparer: StringComparer.Ordinal);

        public ulong Tick => (Server.NextInputTick - 1UL);
        public long TicksWhileArmed => Read(kind: WorldCaptureScheduler.TicksWhileArmed);

        private static WorldCaptureRow Row(string station, ulong tick) => new(
            Palette: [new WorldCapturePaletteEntry(
                Color: "#000000",
                Material: 0
            )],
            Station: CellName.Parse(candidate: station),
            Ticks: [tick]
        );
        private long Read(Puck.Abstractions.Counting.WorkKind kind) {
            Assert.True(condition: Scheduler.Work.TryRead(
                kind: kind,
                value: out var value
            ));

            return value;
        }

        public void Dispose() {
            m_gate.Set();
            Node.Dispose();
            m_router.Dispose();
            m_row.Dispose();
            m_gate.Dispose();
        }
        // One offscreen host iteration: host time through the pump (one step's worth unless given), then one produced
        // frame.
        public void Iterate(ulong? hostTicks = null) {
            _ = m_pump.Advance(
                deltaTicks: (hostTicks ?? m_stepTicks),
                maxFrameTicks: ulong.MaxValue,
                stepTicks: m_stepTicks
            );
            _ = Node.ProduceFrame(context: in m_context);

            foreach (var request in Target.Requests) {
                if (
                    request.Completion.IsCompletedSuccessfully &&
                    request.Completion.Result.Succeeded &&
                    !Served.ContainsKey(key: request.Path)
                ) {
                    Served[request.Path] = (Tick, WorldStateHashComposition.Hash(
                        scope: WorldStateHashScope.Capture,
                        server: Server,
                        tick: Tick
                    ).ToString(
                        format: "x16",
                        provider: System.Globalization.CultureInfo.InvariantCulture
                    ));
                }
            }
        }
        // Iterates until the scheduler has decided the given number of captures; the bound is liveness for a build
        // over a fake device, and decides nothing.
        public void IterateUntilDecided(int captures, ulong? hostTicks = null) => Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    Iterate(hostTicks: hostTicks);

                    return (Scheduler.Entries.Count >= captures);
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: $"Only {Scheduler.Entries.Count} of {captures} captures were decided."
        );
        public void Release() => m_gate.Set();
        // The offscreen host's teardown order: settle what is owed a frame, then dispose the render root. The gate
        // opens first only because the node's disposal waits out its build.
        public void EndRun() {
            Simulation.SettleOwedFrames();
            m_gate.Set();
            Node.Dispose();
        }
        // Asserts every request the scheduler armed ended by a frame or by the scheduler's own refusal, never by the
        // node's disposal.
        public void AssertNothingReachedDisposal() {
            Assert.NotEmpty(collection: Target.Requests);

            foreach (var request in Target.Requests) {
                Assert.True(condition: request.Completion.IsCompletedSuccessfully);
                Assert.IsNotType<ObjectDisposedException>(@object: request.Completion.Result.Error);
            }

            Assert.DoesNotContain(
                collection: Scheduler.Entries,
                filter: static entry => (entry.Detail?.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: "disposed"
                ) ?? false)
            );
        }
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

    public void Dispose() => m_directory.Dispose();
    /// <summary>Law 1: with the engine's pipeline build held, a capture armed at its tick holds the clock there; the
    /// install serves it with the state of that tick, and only then does the simulation step on.</summary>
    [Fact]
    public void AHeldBuildHoldsTheClockAtTheArmedTickAndTheInstallServesThatTicksState() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true
        );

        for (var iteration = 0; (iteration < HeldIterations); iteration++) {
            run.Iterate();
        }

        Assert.False(condition: run.Node.IsReady);
        Assert.Equal(
            expected: FirstTick,
            actual: run.Tick
        );
        Assert.True(condition: run.Scheduler.AwaitsFrame);
        Assert.Empty(collection: run.Scheduler.Entries);

        run.Release();
        run.IterateUntilDecided(captures: 1);

        var entry = Assert.Single(collection: run.Scheduler.Entries);

        Assert.Null(@object: entry.Refusal);
        Assert.Equal(
            expected: "first~10.png",
            actual: entry.Frame
        );
        Assert.Equal(
            expected: (FirstTick, entry.StateHash),
            actual: run.Served[Path.Combine(
                path1: m_directory.RootPath,
                path2: entry.Frame!
            )]
        );
        Assert.True(condition: (run.Tick > FirstTick));
    }
    /// <summary>Law 2: with the build held past the hold budget, the capture is refused by name and withdrawn, the run
    /// steps on and refuses the next unservable capture at once, a frame produced after the install writes neither,
    /// and the node's disposal finds nothing owed.</summary>
    [Fact]
    public void ABuildHeldPastTheBudgetRefusesByNameAndTheRunEndsWithNothingReachingDisposal() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true
        );

        // One second of host time per iteration, so the sixty-second budget is spent in about sixty iterations.
        run.IterateUntilDecided(
            captures: 2,
            hostTicks: EngineTicks.PerSecond
        );

        Assert.False(condition: run.Node.IsReady);
        Assert.Equal(
            expected: [
                "first:10:Unserved:the engine's pipelines never installed (the host held its clock at tick 10 until its 60-second capture hold budget was spent)",
                "second:30:Unserved:the engine's pipelines never installed (the host held its clock at tick 30 until its 60-second capture hold budget was spent)",
            ],
            actual: run.Scheduler.Entries.Select(selector: static entry => $"{entry.Station}:{entry.Tick}:{entry.Refusal}:{entry.Detail}")
        );
        Assert.Equal(
            expected: 0L,
            actual: run.TicksWhileArmed
        );

        run.Release();
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                run.Iterate();

                return run.Node.IsReady;
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
        run.Iterate();
        run.EndRun();

        Assert.True(condition: (run.Tick > SecondTick));
        Assert.Empty(collection: run.Served);
        Assert.Empty(collection: Directory.EnumerateFiles(
            path: m_directory.RootPath,
            searchPattern: "*.png"
        ));
        Assert.Equal(
            expected: 2,
            actual: run.Scheduler.Entries.Count
        );
        run.AssertNothingReachedDisposal();
    }
    /// <summary>Law 2, at the run's end: a run that ends while a capture holds its clock refuses it as unserved when
    /// it settles what is owed, before the node's disposal could refuse it.</summary>
    [Fact]
    public void ARunEndingWhileACaptureIsHeldRefusesItBeforeTheNodeIsDisposed() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true
        );

        for (var iteration = 0; (iteration < HeldIterations); iteration++) {
            run.Iterate();
        }

        run.EndRun();

        var entry = Assert.Single(collection: run.Scheduler.Entries);

        Assert.Equal(
            expected: (WorldCaptureRefusal.Unserved, "the run ended before any frame served it (last completed tick 10)"),
            actual: (entry.Refusal!.Value, entry.Detail!)
        );
        run.AssertNothingReachedDisposal();
    }
    /// <summary>Law 3: the scheduler's own count of ticks stepped while a capture was armed and unserved stays zero
    /// across a whole offscreen run whose first capture waits out a held build.</summary>
    [Fact]
    public void NoTickIsSteppedWhileACaptureIsArmedAndUnservedOffscreen() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true
        );

        for (var iteration = 0; (iteration < HeldIterations); iteration++) {
            run.Iterate();
        }

        run.Release();
        run.IterateUntilDecided(captures: 2);

        Assert.Equal(
            expected: ["first~10.png", "second~30.png"],
            actual: run.Scheduler.Entries.Select(selector: static entry => entry.Frame)
        );
        Assert.Equal(
            expected: 0L,
            actual: run.TicksWhileArmed
        );
    }
    /// <summary>Law 4: a pump that does not hold its clock (the windowed host's) never asks, and steps one tick per
    /// iteration past the armed tick while the build is held, exactly as before; the scheduler counts those ticks.</summary>
    [Fact]
    public void ControlTheWindowedPumpNeverAsksAndStepsPastTheArmedTick() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: false
        );

        for (var iteration = 0; (iteration < HeldIterations); iteration++) {
            run.Iterate();
        }

        Assert.Equal(
            expected: ((ulong)HeldIterations),
            actual: run.Tick
        );
        Assert.Equal(
            expected: 0,
            actual: run.Simulation.HoldQuestions
        );
        Assert.Equal(
            expected: ((long)(((ulong)HeldIterations) - FirstTick)),
            actual: run.TicksWhileArmed
        );
    }
}
