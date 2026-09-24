namespace Puck.World.Server;

/// <summary>One <see cref="WorldEventFeed"/>'s checkpointed state — the edge-detection tables
/// (<see cref="WorldEventFeed.m_overlapping"/>/<see cref="WorldEventFeed.m_regionOccupancy"/>/<see cref="WorldEventFeed.m_seatOccupied"/>/
/// <see cref="WorldEventFeed.m_links"/>) a later tick's enter/exit comparison reads, plus the two buffers that must reproduce
/// exactly for a checkpoint taken mid-episode to resume the same edges.</summary>
public sealed record WorldEventFeedCheckpoint(
    IReadOnlyList<WorldEventEdge> Edges,
    IReadOnlyList<WorldEventEdge> PendingRoutes,
    bool[] SeatOccupied,
    IReadOnlyList<(int A, int B)> Overlapping,
    IReadOnlyList<(string Region, bool[] Occupancy)> RegionOccupancy,
    IReadOnlyList<WorldEventLinkState> Links
);
