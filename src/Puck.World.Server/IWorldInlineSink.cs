using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A client sink that may receive on whatever thread makes a delivery: the thread a tick executes on, or a
/// socket task applying an operation between ticks. It copies what it keeps before each call returns and touches no
/// state the host loop owns, as a sink that only fills a wire queue does. A sink that does not declare this receives
/// through <see cref="WorldOutputHub"/>'s outbox: a delivery made while the hub records reaches it when the hub's
/// collector replays the record (<see cref="WorldSinkRoute"/>).</summary>
public interface IWorldInlineSink : IClientSink;
