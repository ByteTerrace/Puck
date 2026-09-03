using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    internal delegate bool FlockAffinityReader(CompiledWorldExpressionToken[] program, int observer, int neighbor, out long value);
    private readonly Dictionary<(string Kit, string Producer), CompiledWorldFlockAffinities> m_flockAffinities = [];
    private FlockAffinityReader? m_flockAffinityReader;
    private long m_flockAffinityCeiling;

    // The authority owns state and social memory; the population owns perception. Bind once per installation,
    // not per neighbor. Authored names survive checkpoint deserialization, unlike object-reference keys.
    internal void BindFlockAffinities(WorldDefinition definition, FlockAffinityReader reader) {
        m_flockAffinities.Clear();
        m_flockAffinityReader = reader;
        var maximum = 0L;
        foreach (var kit in definition.Kits) {
            foreach (var (name, parameters) in kit.Producers) {
                if (parameters.Flock is not { } profile ||
                    (profile.CohesionAffinity is null && profile.AlignmentAffinity is null)) { continue; }
                var compiled = new CompiledWorldFlockAffinities(profile, definition);
                m_flockAffinities.Add((kit.Name, name), compiled);
                maximum = Math.Max(maximum, profile.MaxNeighbors * compiled.WorkUnitsPerNeighbor);
            }
        }
        m_flockAffinityCeiling = definition.Population.Capacity * maximum;
    }

    private FixedQ4816 ReadFlockAffinity(CompiledWorldExpressionToken[]? expression, int observer, int neighbor) {
        if (expression is null) { return FixedQ4816.One; }
        var success = m_flockAffinityReader!(expression, observer, neighbor, out var value);
        FlockStatistics = FlockStatistics with {
            AffinityEvaluations = FlockStatistics.AffinityEvaluations + 1,
            AffinityFailures = FlockStatistics.AffinityFailures + (success ? 0 : 1),
        };
        return success ? FixedQ4816.FromRawBits(Math.Clamp(value, 0, FixedQ4816.One.Value)) : FixedQ4816.Zero;
    }
}
