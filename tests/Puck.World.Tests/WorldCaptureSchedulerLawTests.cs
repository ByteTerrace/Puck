using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Testing;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The <c>captures</c> schedule driven the way the offscreen host drives it, without a GPU: the real
/// <see cref="FixedStepPump"/> stepping a real <see cref="WorldServer"/> through <see cref="WorldServerStepShell"/>,
/// the scheduler published after every step as <c>WorldHostStep</c> publishes it, and one composed frame after every
/// pump call. The frame is a fake render chain whose PNG records the tick and capture-scope state hash of the server
/// at the moment it was composed, so a manifest entry can be checked against what its frame really showed. Every
/// armed capture must end as exactly one manifest entry: the frame showing its tick, or a named refusal.</summary>
public sealed class WorldCaptureSchedulerLawTests : IDisposable {
    private const ulong BurstSteps = 60UL;
    private const ulong FirstTick = 10UL;
    private const ulong SecondTick = 30UL;

    private readonly TemporaryDirectory m_directory = new();

    // A render chain that serves an armed request with the next frame it composes, writing the shown tick and the
    // server's capture-scope hash into the PNG's first pixels.
    private sealed class StampingFrameTarget(WorldServer server, bool serves) : ICaptureRequestTarget {
        private FrameCaptureRequest? m_request;

        public int Frames { get; private set; }
        public string? PendingCapturePath => m_request?.Path;

        public void Compose() {
            Frames++;

            if (
                !serves ||
                (m_request is not { } request)
            ) {
                return;
            }

            m_request = null;
            _ = request.Write(writer: path => {
                var tick = (server.NextInputTick - 1UL);
                var rgba = new byte[((8 * 2) * 4)];
                Span<byte> stamp = stackalloc byte[16];

                BinaryPrimitives.WriteUInt64LittleEndian(
                    destination: stamp,
                    value: tick
                );
                BinaryPrimitives.WriteUInt64LittleEndian(
                    destination: stamp[8..],
                    value: WorldStateHashComposition.Hash(
                        scope: WorldStateHashScope.Capture,
                        server: server,
                        tick: tick
                    )
                );

                for (var index = 0; (index < stamp.Length); index++) {
                    rgba[(index * 4)] = stamp[index];
                    rgba[((index * 4) + 3)] = 255;
                }

                PngEncoder.Write(
                    height: 2,
                    path: path,
                    rgba: rgba,
                    width: 8
                );
            });
        }
        public void RequestCapture(FrameCaptureRequest request) {
            if (
                (m_request is not null) ||
                request.Completion.IsCompleted
            ) {
                throw new InvalidOperationException(message: "A capture is already pending or the request is terminal.");
            }

            m_request = request;
        }
    }
    // Steps the server exactly as WorldHostStep does and records every completed tick's authoritative state hash.
    private sealed class CaptureStepSimulation(WorldServer server, WorldCaptureScheduler scheduler, bool honoursFrames) : IFixedStepSimulation {
        public bool AwaitsFrame => (honoursFrames && scheduler.AwaitsFrame);

        public bool HoldsClock(ulong withheldTicks) => (honoursFrames && scheduler.HoldsClock(withheldTicks: withheldTicks));
        public void SettleOwedFrames() => scheduler.Drain();

        public List<(ulong Tick, ulong Hash)> Hashes { get; } = [];
        public uint RatePerSecond => ((uint)server.Definition.SimulationRateHz);

        public void Step(in FixedStepContext context, in CommandSnapshot commands) {
            _ = WorldServerStepShell.Step(
                context: in context,
                publishTick: _ => scheduler.PublishTick(tick: (server.NextInputTick - 1UL)),
                server: server,
                tape: null
            );

            var tick = (server.NextInputTick - 1UL);

            Hashes.Add(item: (tick, WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.Authoritative,
                server: server,
                tick: tick
            )));
        }
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
    private sealed class Run : IDisposable {
        private readonly FixedStepPump m_pump;
        private readonly InputRouter m_router;
        private readonly HostRow m_row;
        private readonly ulong m_stepTicks;

        public Run(string directory, bool honoursFrames, bool serves) {
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
            Target = new StampingFrameTarget(
                server: m_row.Server,
                serves: serves
            );
            Scheduler = new WorldCaptureScheduler(
                backend: "vulkan",
                captureTarget: () => Target,
                directory: directory,
                server: m_row.Server,
                worldFile: "fixture.world.json"
            );
            Simulation = new CaptureStepSimulation(
                honoursFrames: honoursFrames,
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
                inputRouter: m_router,
                registry: registry,
                simulation: Simulation
            );
            m_stepTicks = EngineTicks.PerRate(ratePerSecond: Simulation.RatePerSecond);
        }

        public WorldCaptureScheduler Scheduler { get; }
        public CaptureStepSimulation Simulation { get; }
        public StampingFrameTarget Target { get; }

        private static WorldCaptureRow Row(string station, ulong tick) => new(
            Palette: [new WorldCapturePaletteEntry(
                Color: "#000000",
                Material: 0
            )],
            Station: CellName.Parse(candidate: station),
            Ticks: [tick]
        );

        // The offscreen host loop: one pump call owing every step of a stalled iteration, then one composed frame,
        // repeated until the owed time is spent; then the run-end drain.
        public void BurstThenDrain() {
            var owed = (BurstSteps * m_stepTicks);

            do {
                _ = m_pump.Advance(
                    deltaTicks: owed,
                    maxFrameTicks: ulong.MaxValue,
                    stepTicks: m_stepTicks
                );
                owed = 0UL;
                Target.Compose();
            } while (m_pump.AccumulatorTicks >= m_stepTicks);

            Scheduler.Drain();
        }
        public void Dispose() {
            m_router.Dispose();
            m_row.Dispose();
        }
    }

    private static List<JsonElement> ReadManifest(string directory) {
        using var document = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: directory,
            path2: "manifest.json"
        )));

        return [.. document.RootElement.GetProperty(propertyName: "captures").EnumerateArray().Select(selector: static entry => entry.Clone())];
    }
    private static (ulong Tick, string Hash) ReadStamp(string path) {
        var pixels = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: path)).RgbaPixels;
        Span<byte> stamp = stackalloc byte[16];

        for (var index = 0; (index < stamp.Length); index++) {
            stamp[index] = pixels[(index * 4)];
        }

        return (
            BinaryPrimitives.ReadUInt64LittleEndian(source: stamp),
            BinaryPrimitives.ReadUInt64LittleEndian(source: stamp[8..]).ToString(
                format: "x16",
                provider: CultureInfo.InvariantCulture
            )
        );
    }

    public void Dispose() => m_directory.Dispose();
    [Fact]
    public void TwoCapturesArmedInsideOneBurstEachLandTheFrameShowingTheirTick() {
        using var run = new Run(
            directory: m_directory.RootPath,
            honoursFrames: true,
            serves: true
        );

        run.BurstThenDrain();

        var entries = ReadManifest(directory: m_directory.RootPath);

        Assert.Equal(
            expected: 2,
            actual: entries.Count
        );

        Assert.Equal(
            expected: ["first~10.png", "second~30.png"],
            actual: entries.Select(selector: static entry => entry.GetProperty(propertyName: "frame").GetString())
        );

        foreach (var (entry, tick) in entries.Zip(second: ((ulong[])[FirstTick, SecondTick]))) {
            Assert.Equal(
                expected: tick,
                actual: entry.GetProperty(propertyName: "tick").GetUInt64()
            );
            Assert.False(condition: entry.TryGetProperty(
                propertyName: "refusal",
                value: out _
            ));

            var shown = ReadStamp(path: Path.Combine(
                path1: m_directory.RootPath,
                path2: entry.GetProperty(propertyName: "frame").GetString()!
            ));

            Assert.Equal(
                actual: shown.Tick,
                expected: tick
            );
            Assert.Equal(
                expected: entry.GetProperty(propertyName: "stateHash").GetString(),
                actual: shown.Hash
            );
        }
    }
    [Fact]
    public void ControlWithoutTheFrameStopTheBurstRefusesBothCapturesByName() {
        using var run = new Run(
            directory: m_directory.RootPath,
            honoursFrames: false,
            serves: true
        );

        run.BurstThenDrain();

        var entries = ReadManifest(directory: m_directory.RootPath);

        Assert.Equal(
            expected: 2,
            actual: entries.Count
        );
        Assert.Equal(
            expected: ["second:30:busy:first tick 10 still holds the render chain", "first:10:stale:armed at tick 10, but the frame that served it showed tick 60"],
            actual: entries.Select(selector: static entry => $"{entry.GetProperty(propertyName: "station").GetString()}:{entry.GetProperty(propertyName: "tick").GetUInt64()}:{entry.GetProperty(propertyName: "refusal").GetString()}:{entry.GetProperty(propertyName: "detail").GetString()}")
        );
        Assert.False(condition: File.Exists(path: Path.Combine(
            path1: m_directory.RootPath,
            path2: "first~10.png"
        )));
    }
    [Fact]
    public void ACaptureNoFrameServesIsRefusedAsUnservedAndTheNextAsBusy() {
        using var run = new Run(
            directory: m_directory.RootPath,
            honoursFrames: true,
            serves: false
        );

        run.BurstThenDrain();

        Assert.Equal(
            expected: ["second:30:Busy", "first:10:Unserved"],
            actual: run.Scheduler.Entries.Select(selector: static entry => $"{entry.Station}:{entry.Tick}:{entry.Refusal}")
        );
        Assert.Contains(
            actualString: run.Scheduler.Entries[1].Detail,
            expectedSubstring: "the run ended before any frame served it"
        );
    }
    [Fact]
    public void AwaitingAFrameChangesOnlyTheFrameInterleavingNeverTheTicksOrTheirState() {
        using var honoured = new Run(
            directory: m_directory.PathOf(name: "honoured"),
            honoursFrames: true,
            serves: true
        );
        using var ignored = new Run(
            directory: m_directory.PathOf(name: "ignored"),
            honoursFrames: false,
            serves: true
        );

        honoured.BurstThenDrain();
        ignored.BurstThenDrain();

        // These are the server's own per-tick authoritative state hashes, not replay-tape hashes.
        Assert.Equal(
            expected: [.. Enumerable.Range(count: ((int)BurstSteps), start: 1).Select(selector: static tick => ((ulong)tick))],
            actual: honoured.Simulation.Hashes.Select(selector: static step => step.Tick)
        );
        Assert.Equal(
            expected: ignored.Simulation.Hashes,
            actual: honoured.Simulation.Hashes
        );
        Assert.Equal(
            expected: 3,
            actual: honoured.Target.Frames
        );
        Assert.Equal(
            expected: 1,
            actual: ignored.Target.Frames
        );
    }
    [Fact]
    public void PublishingATickWithNothingArmedAllocatesNothing() {
        using var run = new Run(
            directory: m_directory.RootPath,
            honoursFrames: true,
            serves: true
        );

        run.Scheduler.PublishTick(tick: 1UL);
        _ = run.Scheduler.AwaitsFrame;

        // Nothing is armed before the first capture tick, so a repeated window publishes the same quiet ticks again.
        Assert.Equal(
            expected: 0L,
            actual: AllocationWindow.Least(window: () => {
                for (var tick = 2UL; (tick < FirstTick); tick++) {
                    run.Scheduler.PublishTick(tick: tick);
                    _ = run.Scheduler.AwaitsFrame;
                }
            })
        );
    }
    /// <summary>A station camera's <c>select</c> key bound to a row that advances with the engine tick is read at the
    /// tick a capture arms at, through a state mirror: after one and a half simulated seconds the key reads one, so the
    /// inside-check probes the far camera, where a read at engine tick zero would still pick the near one.</summary>
    [Fact]
    public void ASelectKeyIsReadAtTheArmedTick() {
        static WorldCamera Camera(string name, params WorldCameraProgramOp[] operations) => new(
            Name: name,
            Rig: new WorldCameraProgram(
                Name: name,
                Operations: operations,
                Version: WorldCameraProgram.CurrentVersion
            ),
            RenderWidth: 64U,
            RenderHeight: 64U
        );
        var lens = new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f));

        static WorldCameraProgramOp.Anchor At(float x) => new(Subject: new WorldCameraSubject.WorldPoint(Point: new DocumentVector3(
            x: x,
            y: 0f,
            z: 0f
        )));

        var definition = Fixtures.BuildDocument().WithWorldState(rows: [new WorldStateRow(
            Name: CellName.Parse(candidate: "station"),
            Kind: CellKind.Int,
            Min: 0,
            Max: 1000,
            Advance: new StateAdvance(
                PerSecondDenominator: 1,
                PerSecondNumerator: 1
            ),
            Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
        )]) with {
            CamerasRaw = [
                Camera(
                    name: "station",
                    operations: new WorldCameraProgramOp.SelectProgram(
                        Cases: [
                            new WorldCameraSelectCase(
                                Program: "near",
                                Value: 0L
                            ),
                            new WorldCameraSelectCase(
                                Program: "far",
                                Value: 1L
                            ),
                        ],
                        Default: "near",
                        Key: new BindableScalar(binding: "state.station")
                    )
                ),
                Camera(
                    "near",
                    At(x: 0f),
                    lens
                ),
                Camera(
                    "far",
                    At(x: 10f),
                    lens
                ),
            ],
            ViewsRaw = new WorldViewDefaults(
                Layouts: [new WorldViewLayout(
                    Name: "main",
                    Slots: [new WorldViewSlot(Camera: "station")]
                )],
                SeatControlRaw: Fixtures.BuildDocument().Views.SeatControlRaw,
                SeatRigRaw: Fixtures.BuildDocument().Views.SeatRigRaw
            ),
        };

        using var fixture = Fixtures.FreshServer(definition: definition);
        var scheduler = new WorldCaptureScheduler(
            backend: "vulkan",
            captureTarget: null,
            directory: string.Empty,
            server: fixture.Server,
            worldFile: "fixture.world.json"
        );

        Assert.True(
            condition: scheduler.TryResolveCameraPosition(
                position: out var atBoot,
                reason: out var bootReason
            ),
            userMessage: bootReason
        );
        Assert.Equal(
            actual: atBoot.X,
            expected: 0f
        );

        var steps = ((fixture.Server.Definition.SimulationRateHz * 3) / 2);

        for (var step = 0; (step < steps); step++) {
            fixture.Step();
        }

        Assert.True(
            condition: scheduler.TryResolveCameraPosition(
                position: out var armed,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: armed.X,
            expected: 10f
        );
    }
}
