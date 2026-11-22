namespace Puck.World.Server;

/// <summary>A client sink that can end its own <see cref="WorldOutputHub"/> subscription with a named reason, for an
/// ending that is part of its contract rather than a fault: a bounded queue that filled, or a route that is no longer
/// current. The hub reads <see cref="DetachReason"/> after each delivery it hands the sink, including the attach
/// primer, and detaches the sink once it is set; a sink that throws is still detached as a fault. A detachable sink is
/// an inline sink, since its reason is read straight after the delivery that set it.</summary>
public interface IWorldDetachableSink : IWorldInlineSink {
    /// <summary>Gets the named reason this sink ended its subscription, or <see langword="null"/> while it still takes
    /// deliveries. Once set, it never changes.</summary>
    string? DetachReason { get; }
}
