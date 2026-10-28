using Puck.Physics.Navigation;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The population's own checkpointed state — see <see cref="WorldPopulation.Capture"/>.</summary>
public sealed record WorldPopulationCheckpoint(int SimulatedCount, int Revision, byte SeatKit, IReadOnlyList<WorldPopulationEntryCheckpoint> Entries,
    int[] Generations, int[] EventBodies,
    IReadOnlyList<(int Support, int Body)> SleepingSupports,
    IReadOnlyList<(int Body, FixedVector3? Acceleration)> SleepingGravity,
    NavigationSharedCheckpoint[]? SharedNavigation = null);
