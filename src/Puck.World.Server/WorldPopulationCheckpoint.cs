using Puck.Physics.Navigation;

namespace Puck.World.Server;

/// <summary>The population's own checkpointed state — see <see cref="WorldPopulation.Capture"/>.</summary>
public sealed record WorldPopulationCheckpoint(int SimulatedCount, int Revision, byte SeatKit, IReadOnlyList<WorldPopulationEntryCheckpoint> Entries,
    int[] Generations, NavigationSharedCheckpoint[]? SharedNavigation = null);
