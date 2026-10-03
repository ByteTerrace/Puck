using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: offscreen, the fixed-step pump never advances past an armed capture's tick until that capture
/// is served or refused. The harness is the offscreen host without a GPU: the real <see cref="FixedStepPump"/> holding
/// its clock and taking at most one step an iteration (<see cref="FixedStepPump.TryStep"/>), stepping a real <see cref="WorldServer"/> whose <see cref="WorldCaptureScheduler"/> is published after
/// every step, and a real <see cref="SdfWorldResidency"/> whose view a render graph renders over <c>FakeGpuDevice</c>, producing
/// one graph frame after every pump call. The residency's pipeline factory blocks every creation on a gate the law holds, which is how a cold driver shader
/// cache behaves: the view presents nothing and serves no capture until the gate opens and its build installs. The
/// scheduler reads the view's readiness, so the host time held while the build is held is spent from the
/// pipeline-build budget, never from the capture hold budget. A capture a script arms outside the schedule
/// (<c>world.screenshot</c>, <see cref="WorldCaptureScheduler.ArmUnscheduled"/>) is held for the same way; its laws hold
/// the display encode's graphics pipeline instead, the build a capture of the view's float output waits on once the
/// view is ready.
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
            pacing: HostPacing.OneTickPerFrame,
            context: in context,
            publishTick: _ => scheduler.PublishTick(tick: (server.NextInputTick - 1UL)),
            server: server,
            tape: null
        );
    }
    // Forwards every request to the view's instance and keeps it, so the law can read each request's own outcome.
    private sealed class RecordingTarget(ICaptureRequestTarget target) : ICaptureRequestTarget {
        public string? PendingCapturePath => target.PendingCapturePath;
        public List<FrameCaptureRequest> Requests { get; } = [];

        public void RequestCapture(FrameCaptureRequest request) {
            target.RequestCapture(request: request);
            Requests.Add(item: request);
        }
    }
    // The view's readiness as the World's render probe presents it.
    private sealed class ViewReadiness(SdfTestView view) : IWorldEngineReadiness {
        public bool CapturesSettled => true;
        public bool IsReady => view.IsReady;
        public string? NotReadyReason => view.NotReadyReason;
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

        private readonly ManualResetEventSlim m_encodeEntered = new(initialState: false);

        private readonly ManualResetEventSlim m_encodeGate;

        private readonly ManualResetEventSlim m_entered = new(initialState: false);
        private readonly ManualResetEventSlim m_gate = new(initialState: false);

        private readonly bool m_holdsClock;
        private readonly FixedStepPump m_pump;
        private readonly InputRouter m_router;
        private readonly HostRow m_row;
        private readonly ulong m_stepTicks;

        // A run whose scheduled rows are left out arms nothing on its own; one that holds the encode blocks the display
        // encode's pipeline build in the driver until the law releases it.
        public Run(string directory, bool holdsClock, bool scheduled = true, bool holdsEncode = false) {
            m_holdsClock = holdsClock;
            m_encodeGate = new ManualResetEventSlim(initialState: !holdsEncode);

            var gpu = new FakeGpuDevice() {
                BeforeComputePipeline = _ => {
                    m_entered.Set();
                    m_gate.Wait();
                },
                BeforeGraphicsPipeline = description => {
                    if (string.Equals(
                        a: description.Name,
                        b: SurfaceEncoder.Description.Name,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        m_encodeEntered.Set();
                        m_encodeGate.Wait();
                    }
                },
            };

            m_row = HostRow.Build(
                definition: (Fixtures.BuildDocument() with {
                    Captures = new WorldCapturesSection(
                        Directory: directory,
                        Rows: (scheduled
                            ? [
                                Row(
                                    station: "first",
                                    tick: FirstTick
                                ),
                                Row(
                                    station: "second",
                                    tick: SecondTick
                                ),
                            ]
                            : [])
                    ),
                }),
                name: "boot"
            );
            var pipelines = SdfTestPipelines.Cache();

            View = new SdfTestView(
                device: gpu,
                extent: Extent,
                pipelines: pipelines,
                residency: new SdfWorldResidency(
                    brickPoolVoxelCapacity: 0,
                    frameSource: new FixedFrameSource(frame: Frame()),
                    height: Extent,
                    kernels: SdfTestPipelines.Kernels(),
                    name: SdfTestView.Instance,
                    pipelines: pipelines,
                    width: Extent
                )
            );
            Target = new RecordingTarget(target: View.CaptureTarget);
            Scheduler = new WorldCaptureScheduler(
                backend: "vulkan",
                captureTarget: _ => Target,
                directory: directory,
                readiness: new ViewReadiness(view: View),
                server: m_row.Server,
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

        public WorldCaptureScheduler Scheduler { get; }
        public WorldServer Server => m_row.Server;
        public CaptureStepSimulation Simulation { get; }
        public RecordingTarget Target { get; }
        public SdfTestView View { get; }

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
            m_encodeGate.Set();
            View.Dispose();
            m_router.Dispose();
            m_row.Dispose();
            m_gate.Dispose();
            m_entered.Dispose();
            m_encodeGate.Dispose();
            m_encodeEntered.Dispose();
        }
        // One host iteration: host time (one step's worth unless given) through the pump, as the offscreen host takes it
        // (one step at most) when the pump holds its clock and as a wall-clock host does (every step the time covers)
        // when it does not, then one produced graph frame.
        public void Iterate(ulong? hostTicks = null) {
            if (m_holdsClock) {
                _ = m_pump.TryStep(
                    intervalTicks: (hostTicks ?? m_stepTicks),
                    stepTicks: m_stepTicks
                );
            } else {
                _ = m_pump.Advance(
                    deltaTicks: (hostTicks ?? m_stepTicks),
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: m_stepTicks
                );
            }

            _ = View.Produce(context: in m_context);

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
        public void IterateUntilDecided(int captures, ulong? hostTicks = null) => TestLiveness.Until(
            reason: () => $"Only {Scheduler.Entries.Count} of {captures} captures were decided.",
            step: () => {
                Iterate(hostTicks: hostTicks);

                return (Scheduler.Entries.Count >= captures);
            }
        );
        public void Release() => m_gate.Set();
        public void ReleaseEncode() => m_encodeGate.Set();
        // Iterates until the view is ready, its build released; the bound is liveness, and decides nothing.
        public void IterateUntilReady() => TestLiveness.Until(
            step: () => {
                Iterate();

                return View.IsReady;
            }
        );
        // Iterates until the display encode's build is held in the driver, which the first frame serving a capture of the
        // view's float output starts; the bound is liveness, and decides nothing.
        public void IterateUntilEncoding() => TestLiveness.Until(
            step: () => {
                Iterate();

                return m_encodeEntered.IsSet;
            }
        );
        // Arms a capture outside the schedule on the view, as world.screenshot does, returning its request.
        public FrameCaptureRequest Screenshot(string path) {
            var request = new FrameCaptureRequest(path: path);

            Scheduler.ArmUnscheduled(
                request: request,
                target: Target
            );

            return request;
        }
        // Iterates until the residency's build is held in the driver and the view names it, so a refusal's reason reads the
        // same on every run; the bound is liveness, and decides nothing.
        public void IterateUntilBuilding() {
            Iterate();
            Assert.True(condition: m_entered.Wait(
                cancellationToken: TestContext.Current.CancellationToken,
                timeout: TestLiveness.Bound
            ));
            TestLiveness.Until(
                step: () => {
                    Iterate();

                    return (View.NotReadyReason?.StartsWith(
                        comparisonType: StringComparison.Ordinal,
                        value: "the engine's pipeline set is building"
                    ) ?? false);
                }
            );
        }
        // The offscreen host's teardown order: settle what is owed a frame, then dispose the render root. The gates
        // open first only because the residency's and the encoder's disposals wait out their builds.
        public void EndRun() {
            Simulation.SettleOwedFrames();
            m_gate.Set();
            m_encodeGate.Set();
            View.Dispose();
        }
        // Asserts every request the scheduler armed ended by a frame or by the scheduler's own refusal, never by the
        // view's disposal.
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

        Assert.False(condition: run.View.IsReady);
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
    /// <summary>Law 1b: the capture hold counts from readiness. A build held for longer than the capture hold budget but
    /// inside the pipeline-build budget spends none of the capture hold, so its install still serves the capture.</summary>
    [Fact]
    public void ABuildHeldPastTheCaptureHoldBudgetStillServesTheCaptureOnceItInstalls() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true
        );

        run.IterateUntilBuilding();

        // One second of host time per iteration: twice the capture hold budget, well inside the build budget.
        for (var iteration = 0; (iteration < (2 * WorldCaptureScheduler.HoldBudgetSeconds)); iteration++) {
            run.Iterate(hostTicks: EngineTicks.PerSecond);
        }

        Assert.False(condition: run.View.IsReady);
        Assert.Equal(
            expected: FirstTick,
            actual: run.Tick
        );
        Assert.Empty(collection: run.Scheduler.Entries);

        run.Release();
        run.IterateUntilDecided(captures: 1);

        var entry = Assert.Single(collection: run.Scheduler.Entries);

        Assert.Null(@object: entry.Refusal);
        Assert.Equal(
            expected: "first~10.png",
            actual: entry.Frame
        );
    }
    /// <summary>Law 2: with the build held past the pipeline-build budget, the capture is refused by name, the reason
    /// naming the build and its progress, and withdrawn; the run steps on and refuses the next unservable capture at
    /// once, a frame produced after the install writes neither, and the view's disposal finds nothing owed.</summary>
    [Fact]
    public void ABuildHeldPastTheBudgetRefusesByNameAndTheRunEndsWithNothingReachingDisposal() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true
        );

        run.IterateUntilBuilding();

        // One second of host time per iteration, so the build budget is spent in about as many iterations as it has
        // seconds.
        run.IterateUntilDecided(
            captures: 2,
            hostTicks: EngineTicks.PerSecond
        );

        Assert.False(condition: run.View.IsReady);
        Assert.Equal(
            expected: [
                "first:10:Unserved:the engine's pipeline set is building (0 of 12 pipelines created; waiting on sdf-beam, sdf-instance-cull, sdf-cull-args, sdf-world-primary, sdf-world-surface, sdf-world-ambient, sdf-world-shadow, sdf-world-views, sdf-world-views-core, sdf-world-views-folds, sdf-sky-runs, sdf-composite) (the host held its clock at tick 10 while the engine's pipeline set built, until its 180-second pipeline-build hold budget was spent)",
                "second:30:Unserved:the engine's pipeline set is building (0 of 12 pipelines created; waiting on sdf-beam, sdf-instance-cull, sdf-cull-args, sdf-world-primary, sdf-world-surface, sdf-world-ambient, sdf-world-shadow, sdf-world-views, sdf-world-views-core, sdf-world-views-folds, sdf-sky-runs, sdf-composite) (the host held its clock at tick 30 while the engine's pipeline set built, until its 180-second pipeline-build hold budget was spent)",
            ],
            actual: run.Scheduler.Entries.Select(selector: static entry => $"{entry.Station}:{entry.Tick}:{entry.Refusal}:{entry.Detail}")
        );
        Assert.Equal(
            expected: 0L,
            actual: run.TicksWhileArmed
        );

        run.Release();
        TestLiveness.Until(
            step: () => {
                run.Iterate();

                return run.View.IsReady;
            }
        );
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
    /// it settles what is owed, before the view's disposal could refuse it.</summary>
    [Fact]
    public void ARunEndingWhileACaptureIsHeldRefusesItBeforeTheNodeIsDisposed() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true
        );

        run.IterateUntilBuilding();

        for (var iteration = 0; (iteration < HeldIterations); iteration++) {
            run.Iterate();
        }

        run.EndRun();

        var entry = Assert.Single(collection: run.Scheduler.Entries);

        Assert.Equal(
            expected: (WorldCaptureRefusal.Unserved, "the run ended before any frame served it (last completed tick 10); the engine's pipeline set is building (0 of 12 pipelines created; waiting on sdf-beam, sdf-instance-cull, sdf-cull-args, sdf-world-primary, sdf-world-surface, sdf-world-ambient, sdf-world-shadow, sdf-world-views, sdf-world-views-core, sdf-world-views-folds, sdf-sky-runs, sdf-composite)"),
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
    /// <summary>Law 4: a pump explicitly configured not to hold its clock never asks, and steps one tick per
    /// iteration past the armed tick while the build is held, exactly as before; the scheduler counts those ticks.</summary>
    [Fact]
    public void ControlANonholdingPumpNeverAsksAndStepsPastTheArmedTick() {
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
    /// <summary>Law 5: a screenshot armed outside the schedule while the display encode it is read through is still
    /// building holds the clock at the tick it was armed after, as a scheduled capture does; the view renders every
    /// iteration and no tick steps until a frame serves it.</summary>
    [Fact]
    public async Task AScreenshotWaitingOnTheDisplayEncodeHoldsTheClockAtItsTick() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true,
            holdsEncode: true,
            scheduled: false
        );

        run.Release();
        run.IterateUntilReady();

        var armed = run.Tick;
        var request = run.Screenshot(path: Path.Combine(
            path1: m_directory.RootPath,
            path2: "shot.png"
        ));

        run.IterateUntilEncoding();

        for (var iteration = 0; (iteration < HeldIterations); iteration++) {
            run.Iterate();
        }

        Assert.True(condition: run.View.IsReady);
        Assert.False(condition: request.Completion.IsCompleted);
        Assert.Equal(
            expected: armed,
            actual: run.Tick
        );
        Assert.True(condition: run.Scheduler.AwaitsFrame);

        run.ReleaseEncode();
        TestLiveness.Until(
            step: () => {
                run.Iterate();

                return request.Completion.IsCompleted;
            }
        );

        Assert.True(condition: (await request.Completion).Succeeded);
        Assert.Equal(
            expected: armed,
            actual: run.Served[request.Path].Tick
        );

        run.Iterate();

        Assert.True(condition: (run.Tick > armed));
        Assert.Empty(collection: run.Scheduler.Entries);
    }
    /// <summary>Law 6: a run that ends while a screenshot armed outside the schedule still waits on the display encode
    /// refuses it by name when it settles what is owed, before the view's disposal could refuse it, and writes no
    /// manifest entry for it.</summary>
    [Fact]
    public async Task ARunEndingWhileAScreenshotWaitsOnTheDisplayEncodeRefusesItByNameBeforeTheNodeIsDisposed() {
        using var run = new Run(
            directory: m_directory.RootPath,
            holdsClock: true,
            holdsEncode: true,
            scheduled: false
        );

        run.Release();
        run.IterateUntilReady();

        var armed = run.Tick;
        var request = run.Screenshot(path: Path.Combine(
            path1: m_directory.RootPath,
            path2: "shot.png"
        ));

        run.IterateUntilEncoding();

        for (var iteration = 0; (iteration < 10); iteration++) {
            run.Iterate();
        }

        run.EndRun();

        Assert.True(condition: request.Completion.IsCompletedSuccessfully);

        var error = Assert.IsType<OperationCanceledException>(@object: (await request.Completion).Error);

        Assert.Equal(
            expected: $"the run ended before any frame served it (armed after tick {armed}, last completed tick {armed})",
            actual: error.Message
        );
        Assert.False(condition: File.Exists(path: request.Path));
        Assert.Empty(collection: run.Scheduler.Entries);
        run.AssertNothingReachedDisposal();
    }
}
