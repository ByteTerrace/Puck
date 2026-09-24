namespace Puck.Commands;

/// <summary>The command channel a <see cref="SourceDestination.Simulation"/> source reads: a pointer ray arriving as two
/// <see cref="CommandValueKind.Axis3D"/> commands in a tick's <see cref="CommandSnapshot"/>, through the same router,
/// bindings and lanes as every other command. The simulation quantizes the ray once
/// (<see cref="CommandValueQuantization.QuantizeAxis3D"/>) and maps it with <see cref="SourceMapping.MapRay"/> from
/// document data, so the same snapshots map to the same source pixels on every run.</summary>
public static class SourcePointerCommands {
    /// <summary>The Axis3D command carrying the pointer ray's world-space origin, in world units.</summary>
    public const string Origin = "source.pointer.origin";
    /// <summary>The Axis3D command carrying the pointer ray's world-space direction; it need not be unit length.</summary>
    public const string Direction = "source.pointer.direction";

    /// <summary>Reads the pointer ray a lane carries this tick.</summary>
    /// <param name="lane">The slot's lane of the tick's snapshot.</param>
    /// <param name="originId">The interned id of <see cref="Origin"/> in the snapshot's registry.</param>
    /// <param name="directionId">The interned id of <see cref="Direction"/> in the snapshot's registry.</param>
    /// <param name="ray">The quantized ray when this returns <see langword="true"/>; default otherwise.</param>
    /// <returns><see langword="true"/> when the lane carries both commands and each value is
    /// <see cref="CommandValueKind.Axis3D"/>.</returns>
    public static bool TryReadRay(in CommandLane lane, ushort originId, ushort directionId, out SourceRay ray) {
        if (
            lane.TryGetEntry(
                commandId: originId,
                entry: out var origin
            ) &&
            lane.TryGetEntry(
                commandId: directionId,
                entry: out var direction
            ) &&
            (origin.Value.Kind == CommandValueKind.Axis3D) &&
            (direction.Value.Kind == CommandValueKind.Axis3D)
        ) {
            ray = new SourceRay(
                Direction: CommandValueQuantization.QuantizeAxis3D(value: direction.Value.AsAxis3D),
                Origin: CommandValueQuantization.QuantizeAxis3D(value: origin.Value.AsAxis3D)
            );

            return true;
        }

        ray = default;

        return false;
    }
}
