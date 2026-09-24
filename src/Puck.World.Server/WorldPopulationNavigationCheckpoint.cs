using Puck.Physics.Navigation;

namespace Puck.World.Server;

/// <summary>One body's cached deterministic route and producer binding.</summary>
public readonly record struct WorldPopulationNavigationCheckpoint(
    int ActiveProducerDomainIndex,
    int DomainIndex,
    int GoalCell,
    int Waypoint,
    int ExpandedLast,
    NavigationStatus Status,
    int[] Path
);
