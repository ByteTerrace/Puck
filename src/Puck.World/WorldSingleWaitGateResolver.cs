namespace Puck.World;

/// <summary>The desktop's <see cref="IWorldWaitGateResolver"/> — one process, one row, one gate, regardless of
/// which row <c>world.wait</c> resolved.</summary>
internal sealed class WorldSingleWaitGateResolver(WorldConsoleWaitGate gate) : IWorldWaitGateResolver {
    /// <inheritdoc/>
    public WorldConsoleWaitGate GateFor(WorldInstance instance) => gate;
}
