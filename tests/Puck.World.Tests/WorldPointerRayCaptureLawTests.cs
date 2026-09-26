using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Input;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="WorldPointerRayCapture"/> sustains the pointer ray only on the seat that still holds the mouse
/// the position is attributed to. When that mouse leaves the seat (a leave, then another device seated there) or moves
/// to another seat (an assign), the seat's next snapshot carries no pointer commands, whether or not the pointer sink
/// forgot the position first; a position the platform attributes to no mouse, and a pointer that left the window,
/// point nowhere. Each law opens with its control: the same seat pointing before the change. The positioned seat and
/// its device are read as one pair, which never tears while another thread reports positions.
/// </summary>
public sealed class WorldPointerRayCaptureLawTests {
    private const int Seat = 1;

    private static readonly Vector2 Centre = new(
        x: 800f,
        y: 450f
    );
    private static readonly InputDeviceId Gamepad = InputDeviceId.FromKey(key: "gamepad:pointer-law");
    private static readonly InputDeviceId Mouse = InputDeviceId.FromKey(key: "mouse:pointer-law");

    // Two eager seats and the fixture's screen taking the simulation destination.
    private static WorldDefinition Document() {
        var document = Fixtures.BuildDocument();

        return document with {
            PopulationRaw = document.Population with {
                SeatActivationRaw = [.. Enumerable.Range(
                    count: WorldBodiesLimits.LocalSeatCount,
                    start: 0
                ).Select(selector: seat => ((seat < 2)
                    ? SeatActivationPolicy.Eager
                    : SeatActivationPolicy.OnDemand))],
            },
            ScreensRaw = [.. document.Screens.Select(selector: row => row with {
                Route = (row.Route with { Input = SourceDestination.Simulation }),
            })],
        };
    }

    private sealed class Rig : IDisposable {
        private readonly WorldFixture m_fixture;

        private ulong m_tick;

        public Rig(bool withSink) {
            m_fixture = Fixtures.FreshServer(definition: Document());

            var definition = m_fixture.Server.Definition;

            Roster = new PlayerRoster(
                definition: definition,
                link: new LoopbackTransport(server: m_fixture.Server),
                seatBindings: new WorldSeatBindings(definition: definition)
            );
            Registry = new CommandRegistry(modules: [new PointerModule()]);
            Router = new InputRouter(
                bindings: new NoBindings(),
                principalResolver: Roster,
                registry: Registry
            );
            Sink = (withSink
                ? new WorldPointerSink(
                    consumers: [],
                    pointer: Pointer,
                    roster: Roster
                )
                : null);
            Capture = new WorldPointerRayCapture(
                definition: () => definition,
                pointer: Pointer,
                roster: Roster,
                router: Router,
                viewports: Viewports
            );
            Roster.ObserveDeviceKind(
                device: Mouse,
                kind: InputDeviceKind.Mouse
            );
            Assert.Equal(
                actual: Roster.AssignDevice(
                    actingPrincipal: Principal.Console,
                    device: Mouse,
                    targetSlot: Seat
                ),
                expected: AssignOutcome.JoinedTeam
            );
        }

        public WorldPointerRayCapture Capture { get; }

        public WorldPointer Pointer { get; } = new();

        public CommandRegistry Registry { get; }
        public PlayerRoster Roster { get; }
        public InputRouter Router { get; }
        public WorldPointerSink? Sink { get; }

        public WorldSeatViewports Viewports { get; } = new();

        public void Dispose() {
            Router.Dispose();
            m_fixture.Dispose();
        }
        // Positions the pointer at the centre of the seat's view as the platform reports it: through the sink when one
        // is wired, straight into the store otherwise.
        public void Point(InputDeviceId device) {
            if (Sink is { } sink) {
                sink.Observe(inputEvent: WindowInputEvent.PointerAbsolute(
                    deviceId: device,
                    position: Centre
                ));
            } else {
                Pointer.SetPosition(
                    device: device,
                    position: Centre,
                    slot: Seat
                );
            }
        }
        // One host frame: every seat's view re-published, the capture run, and the frame's one tick snapshotted.
        // Returns the seats whose lane carries both pointer commands.
        public int[] Frame() {
            Viewports.BeginFrame();

            for (var slot = 0; (slot < 2); slot++) {
                Viewports.Publish(
                    camera: default,
                    height: 900u,
                    region: new NormalizedRect(
                        Height: 1f,
                        Width: 1f,
                        X: 0f,
                        Y: 0f
                    ),
                    slot: slot,
                    width: 1600u
                );
            }

            Capture.CaptureFrame(frameKey: ++m_tick);

            var snapshot = Router.SnapshotForTick(
                tick: m_tick,
                windowEndTick: ulong.MaxValue
            );

            Assert.True(condition: Registry.TryGetId(
                id: out var originId,
                name: SourcePointerCommands.Origin
            ));
            Assert.True(condition: Registry.TryGetId(
                id: out var directionId,
                name: SourcePointerCommands.Direction
            ));

            return [.. snapshot.Lanes.ToArray()
                .Where(predicate: lane => SourcePointerCommands.TryReadRay(
                    directionId: directionId,
                    lane: lane,
                    originId: originId,
                    ray: out _
                ))
                .Select(selector: static lane => lane.Slot)];
        }
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void ASeatWhoseMouseLeftAndWasReplacedCarriesNoPointer(bool withSink) {
        using var rig = new Rig(withSink: withSink);

        rig.Point(device: Mouse);
        Assert.Equal(
            actual: rig.Frame(),
            expected: [Seat]
        );

        Assert.True(condition: rig.Roster.Leave(
            actingPrincipal: Principal.Console,
            slot: Seat
        ));
        Assert.Equal(
            actual: rig.Roster.AssignDevice(
                actingPrincipal: Principal.Console,
                device: Gamepad,
                targetSlot: Seat
            ),
            expected: AssignOutcome.CreatedPending
        );

        Assert.Empty(collection: rig.Frame());
        Assert.Empty(collection: rig.Frame());

        if (withSink) {
            Assert.Null(@object: rig.Pointer.Positioned);
            Assert.False(condition: rig.Pointer.HasPosition(slot: Seat));
        }
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AMouseAssignedToAnotherSeatTakesThePointerWithIt(bool withSink) {
        using var rig = new Rig(withSink: withSink);

        rig.Point(device: Mouse);
        Assert.Equal(
            actual: rig.Frame(),
            expected: [Seat]
        );

        Assert.Equal(
            actual: rig.Roster.AssignDevice(
                actingPrincipal: Principal.Console,
                device: Mouse,
                targetSlot: 0
            ),
            expected: AssignOutcome.JoinedTeam
        );

        // The position still names the seat the mouse left, so neither seat points until the mouse reports again.
        Assert.Empty(collection: rig.Frame());

        if (withSink) {
            Assert.Null(@object: rig.Pointer.Positioned);
            rig.Point(device: Mouse);
            Assert.Equal(
                actual: rig.Frame(),
                expected: [0]
            );
        }
    }
    [Fact]
    public void APositionAttributedToNoMousePointsNowhere() {
        using var rig = new Rig(withSink: false);

        rig.Point(device: Mouse);
        Assert.Equal(
            actual: rig.Frame(),
            expected: [Seat]
        );

        rig.Point(device: default);
        Assert.Empty(collection: rig.Frame());

        rig.Point(device: Gamepad);
        Assert.Empty(collection: rig.Frame());
    }
    [Fact]
    public void APointerThatLeftTheWindowPointsNowhereUntilItReturns() {
        using var rig = new Rig(withSink: true);

        rig.Point(device: Mouse);
        Assert.Equal(
            actual: rig.Frame(),
            expected: [Seat]
        );

        rig.Sink!.Observe(inputEvent: WindowInputEvent.PointerLeft());

        Assert.Null(@object: rig.Pointer.Positioned);
        Assert.Empty(collection: rig.Frame());

        rig.Point(device: Mouse);
        Assert.Equal(
            actual: rig.Frame(),
            expected: [Seat]
        );
    }
    [Fact]
    public void ThePositionedSeatAndItsDeviceAreReadTogether() {
        const int LeastReads = 100_000;
        const int MostReads = 10_000_000;

        var pointer = new WorldPointer();
        var first = new WorldPointerPositioned(Device: Mouse, Slot: 0);
        var second = new WorldPointerPositioned(Device: Gamepad, Slot: Seat);
        var stop = 0;

        // Another thread alternates two reports, each writing a seat and its device, while this one reads the pair: a
        // read of the seat and the device apart would sometimes pair one report's seat with the other's device.
        var writer = new Thread(start: () => {
            while (Volatile.Read(location: ref stop) == 0) {
                foreach (var report in ((WorldPointerPositioned[])[first, second])) {
                    pointer.SetPosition(
                        device: report.Device,
                        position: Centre,
                        slot: report.Slot
                    );
                }
            }
        });

        var (sawFirst, sawSecond) = (false, false);

        writer.Start();

        try {
            for (var read = 0; ((read < MostReads) && ((read < LeastReads) || !sawFirst || !sawSecond)); read++) {
                if (pointer.Positioned is not { } positioned) {
                    continue;
                }

                Assert.True(condition: ((positioned == first) || (positioned == second)), userMessage: $"torn pair {positioned}");
                sawFirst |= (positioned == first);
                sawSecond |= (positioned == second);
            }
        } finally {
            Volatile.Write(location: ref stop, value: 1);
            writer.Join();
        }

        Assert.True(condition: (sawFirst && sawSecond), userMessage: "the reader never saw both reports, so the race was not exercised");
    }

    private sealed class PointerModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Bindable,
                description: "The pointer ray's origin.",
                handler: static _ => CommandResult.None,
                name: SourcePointerCommands.Origin,
                valueKind: CommandValueKind.Axis3D
            );
            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Bindable,
                description: "The pointer ray's direction.",
                handler: static _ => CommandResult.None,
                name: SourcePointerCommands.Direction,
                valueKind: CommandValueKind.Axis3D
            );
        }
    }
    private sealed class NoBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
}
