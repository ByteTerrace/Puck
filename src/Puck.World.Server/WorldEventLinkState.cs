namespace Puck.World.Server;

/// <summary>One authored adjacency row's checkpointed link-liveness state.</summary>
/// <param name="Adjacency">The authored <c>adjacencies</c> row name.</param>
/// <param name="DeliveredTick">The highest neighbour snapshot tick observed on this edge; <c>0</c> when nothing
/// has ever been delivered.</param>
/// <param name="StaleTicks">Simulation ticks since the last delivered refresh.</param>
/// <param name="PendingRefresh">Whether a refresh has been observed since the last <see cref="WorldEventFeed.Collect"/>.</param>
/// <param name="Dropped">Whether the last edge emitted for this row was <see cref="WorldEventFamily.LinkDropped"/>.</param>
public readonly record struct WorldEventLinkState(string Adjacency, ulong DeliveredTick, long StaleTicks, bool PendingRefresh, bool Dropped);
