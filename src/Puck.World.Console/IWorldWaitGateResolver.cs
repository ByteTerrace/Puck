namespace Puck.World;

/// <summary>
/// Resolves the <see cref="WorldConsoleWaitGate"/> a <c>world.wait</c> invocation arms. The desktop's implementation
/// always answers its one process-wide gate (one row); a host running several rows answers each row's own — a
/// singleton gate would arm whichever row it was constructed against regardless of which row
/// <see cref="Server.IWorldConsoleAuthority"/> actually resolved.
/// </summary>
public interface IWorldWaitGateResolver {
    /// <summary>Gets the gate bound to <paramref name="instance"/>'s own <c>world.wait</c>.</summary>
    /// <param name="instance">The invocation's resolved row.</param>
    WorldConsoleWaitGate GateFor(WorldInstance instance);
}
