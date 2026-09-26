using System.Numerics;
using Xunit;

namespace Puck.Commands.Tests;

/// <summary>Laws for the simulation destination's command channel: a pointer ray captured as two Axis3D signals arrives
/// in a tick's snapshot through the ordinary router, reads back quantized, and maps to the same source pixel however
/// many times the same input is replayed through a fresh router.</summary>
public sealed class SourcePointerCommandLawTests {
    private const string DirectionSource = "pointer.direction";
    private const string OriginSource = "pointer.origin";

    // A 160 by 144 screen two units wide facing +z at the origin, so the pixel under world point (x, y, 0) is
    // (80 + 80x, 72 − 96y).
    private static readonly SourceMapping Screen = new(
        Crop: SourcePixelRect.Whole(
            height: 144,
            width: 160
        ),
        Destination: SourceDestination.Simulation,
        Placement: new SourcePlacement.Surface(
            HalfHeight: 0.75f,
            HalfWidth: 1f,
            Origin: Vector3.Zero,
            Right: Vector3.UnitX,
            Up: Vector3.UnitY
        ),
        Source: SourceHandle.Producer(id: "cabinet"),
        SourceHeight: 144,
        SourceWidth: 160
    );

    private static SourceHit Replay(Vector3 origin, Vector3 direction) {
        var registry = new CommandRegistry(modules: [new PointerModule()]);
        var router = new InputRouter(
            bindings: new PointerBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: registry
        );

        router.Capture(signal: new InputSignal(
            DeviceId: default,
            Phase: CommandPhase.Active,
            Slot: 0,
            Source: OriginSource,
            Value: CommandValue.Axis(value: origin)
        ));
        router.Capture(signal: new InputSignal(
            DeviceId: default,
            Phase: CommandPhase.Active,
            Slot: 0,
            Source: DirectionSource,
            Value: CommandValue.Axis(value: direction)
        ));

        var lane = Assert.Single(collection: router.SnapshotForTick(
            tick: 1UL,
            windowEndTick: ulong.MaxValue
        ).Lanes);

        Assert.True(condition: registry.TryGetId(
            id: out var originId,
            name: SourcePointerCommands.Origin
        ));
        Assert.True(condition: registry.TryGetId(
            id: out var directionId,
            name: SourcePointerCommands.Direction
        ));
        Assert.True(condition: SourcePointerCommands.TryReadRay(
            directionId: directionId,
            lane: lane,
            originId: originId,
            ray: out var ray
        ));
        Assert.Equal(
            expected: CommandValueQuantization.QuantizeAxis3D(value: origin),
            actual: ray.Origin
        );

        return Screen.MapRay(ray: ray);
    }

    [Fact]
    public void APointerRayInATicksSnapshotMapsToTheSamePixelOnEveryReplay() {
        var origin = new Vector3(
            x: 0.31f,
            y: -0.4f,
            z: 5f
        );
        var direction = new Vector3(
            x: 0.1f,
            y: 0.05f,
            z: -1f
        );
        var first = Replay(
            direction: direction,
            origin: origin
        );

        // The ray meets z = 0 at (0.81, -0.15): the point (144.8, 86.4) in source pixels.
        Assert.True(condition: first.IsOnSource);
        Assert.Equal(
            expected: (144L, 86L),
            actual: (first.PixelX, first.PixelY)
        );

        for (var replay = 0; (replay < 4); replay++) {
            Assert.Equal(
                expected: first,
                actual: Replay(
                    direction: direction,
                    origin: origin
                )
            );
        }
    }
    [Fact]
    public void ASustainedRayRidesEverySnapshotOfABurstUntilItEnds() {
        var dispatched = new List<(int Slot, Vector3 Value)>();
        var registry = new CommandRegistry(modules: [new PointerModule(dispatched: dispatched)]);
        var router = new InputRouter(
            bindings: new UnboundBindings(),
            principalResolver: new ConsolePrincipal(),
            registry: registry
        );
        var origin = new Vector3(
            x: 0.31f,
            y: -0.4f,
            z: 5f
        );
        var direction = new Vector3(
            x: 0.1f,
            y: 0.05f,
            z: -1f
        );

        Assert.True(condition: registry.TryGetId(
            id: out var originId,
            name: SourcePointerCommands.Origin
        ));
        Assert.True(condition: registry.TryGetId(
            id: out var directionId,
            name: SourcePointerCommands.Direction
        ));
        Assert.False(condition: router.Sustain(
            command: "pointer.unregistered",
            slot: 1,
            value: CommandValue.Axis(value: origin)
        ));
        // No binding names either command and no map is active: a sustained value reaches the lane regardless.
        Assert.True(condition: router.Sustain(
            command: SourcePointerCommands.Origin,
            slot: 1,
            value: CommandValue.Axis(value: origin)
        ));
        Assert.True(condition: router.Sustain(
            command: SourcePointerCommands.Direction,
            slot: 1,
            value: CommandValue.Axis(value: direction)
        ));

        // One host frame's catch-up burst of three ticks: every snapshot carries the whole ray, and every tick
        // dispatches both halves.
        for (var tick = 1UL; (tick <= 3UL); tick++) {
            var snapshot = router.SnapshotForTick(
                tick: tick,
                windowEndTick: ulong.MaxValue
            );
            var lane = Assert.Single(collection: snapshot.Lanes);

            Assert.Equal(
                actual: lane.Slot,
                expected: 1
            );
            Assert.True(condition: SourcePointerCommands.TryReadRay(
                directionId: directionId,
                lane: lane,
                originId: originId,
                ray: out var ray
            ));
            Assert.Equal(
                actual: ray,
                expected: new SourceRay(
                    Direction: CommandValueQuantization.QuantizeAxis3D(value: direction),
                    Origin: CommandValueQuantization.QuantizeAxis3D(value: origin)
                )
            );
            registry.ApplySnapshot(snapshot: in snapshot);
        }

        Assert.Equal(
            actual: dispatched.Count,
            expected: 6
        );
        Assert.All(
            action: static call => Assert.Equal(
                actual: call.Slot,
                expected: 1
            ),
            collection: dispatched
        );

        // Ending both halves ends the ray from the next snapshot on; ending again changes nothing.
        Assert.True(condition: router.EndSustain(
            command: SourcePointerCommands.Origin,
            slot: 1
        ));
        Assert.True(condition: router.EndSustain(
            command: SourcePointerCommands.Direction,
            slot: 1
        ));
        Assert.False(condition: router.EndSustain(
            command: SourcePointerCommands.Direction,
            slot: 1
        ));
        Assert.Empty(collection: router.SnapshotForTick(
            tick: 4UL,
            windowEndTick: ulong.MaxValue
        ).Lanes);
    }

    private sealed class PointerModule(List<(int Slot, Vector3 Value)>? dispatched = null) : ICommandModule {
        private CommandResult Record(CommandContext context) {
            dispatched?.Add(item: (context.Slot, context.Value.AsAxis3D));

            return CommandResult.None;
        }

        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Bindable,
                description: "The pointer ray's origin.",
                handler: Record,
                name: SourcePointerCommands.Origin,
                valueKind: CommandValueKind.Axis3D
            );
            yield return CommandDefinition.Verb(
                bindability: CommandBindability.Bindable,
                description: "The pointer ray's direction.",
                handler: Record,
                name: SourcePointerCommands.Direction,
                valueKind: CommandValueKind.Axis3D
            );
        }
    }
    private sealed class UnboundBindings : IInputBindings {
        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
    }
    private sealed class PointerBindings : IInputBindings {
        private readonly CommandBinding[] m_direction = [new CommandBinding(Command: SourcePointerCommands.Direction)];
        private readonly CommandBinding[] m_origin = [new CommandBinding(Command: SourcePointerCommands.Origin)];

        public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => source switch {
            OriginSource => m_origin,
            DirectionSource => m_direction,
            _ => null,
        };
    }
    private sealed class ConsolePrincipal : IPrincipalResolver {
        public Principal PrincipalOf(int slot) => Principal.Console;
    }
}
