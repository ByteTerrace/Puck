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
