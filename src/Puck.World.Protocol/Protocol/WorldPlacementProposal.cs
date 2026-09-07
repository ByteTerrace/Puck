using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A detached layout preview. Its ordinary guarded batch is the only commit artifact.</summary>
/// <param name="Mutation">Placement changes and payment, with a fingerprint of their base document.</param>
/// <param name="Candidates">Candidate and overlap checks performed.</param>
/// <param name="Moved">Number of changed placement transforms.</param>
/// <param name="Cost">Authored Int payment.</param>
public sealed record WorldPlacementProposal(WorldMutation.Batch Mutation, int Candidates, int Moved, long Cost) {
    /// <summary>Placement ids whose rows are part of this atomic proposal.</summary>
    public IReadOnlyList<string> AffectedIds { get; init; } = [];
    /// <summary>Named planner limits and refusals surfaced for authoring clients.</summary>
    public IReadOnlyList<string> Constraints { get; init; } = [];
    /// <summary>The spatial reads carried by <see cref="Mutation"/>. This is a projection of the guarded batch so
    /// proposal metadata cannot drift from the commit artifact.</summary>
    public IReadOnlyList<WorldSpatialReadDependency> SpatialReads => Mutation.ExpectedSpatialReads ?? [];
}
