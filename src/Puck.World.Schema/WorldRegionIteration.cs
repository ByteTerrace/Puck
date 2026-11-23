using Puck.State.Rules;

namespace Puck.World;

/// <summary>The authority's view of who stands in a placement region: what a rule sweeping the region's occupants
/// binds and what its schedule names.</summary>
public interface IWorldRegionOccupants : IFacet {
    /// <summary>Appends the index key of each body inside a region as of the latest event collection, in ascending body
    /// index order.</summary>
    /// <param name="placementId">The region-carrying placement's id.</param>
    /// <param name="priced">The occupants the work sheet priced the sweep at; the host counts the bodies past it.</param>
    /// <param name="into">The list the keys are appended to.</param>
    void CollectRegionOccupants(string placementId, int priced, List<CellKey> into);
    /// <summary>Names the versioned fact that moves when a region's occupants do, starting its versioning.</summary>
    /// <param name="placementId">The region-carrying placement's id.</param>
    /// <returns>The fact a schedule names for the region's occupancy.</returns>
    FactRead ScheduleRegionOccupancy(string placementId);
}
/// <summary>A compiled <c>forEach: region(placement)</c> sweep: one binding of <c>$each</c> per body inside the
/// placement's region, keyed by the body's index, so a rule about a place costs its occupants and never every body
/// that could enter it. The occupants are the event feed's own latches (the count <c>region(placement)</c> reads), in
/// ascending body index order.</summary>
/// <param name="PlacementId">The region-carrying placement's id.</param>
/// <param name="Occupants">The occupants the work sheet prices one sweep at: the region's authored
/// <see cref="WorldPlacementRegion.Capacity"/>, or the most bodies that fit it
/// (<see cref="WorldRuleWorkBudget.RegionSweepCapacity"/>). A sweep binds every occupant whatever this says, since a
/// rule about a place holds for everyone in it; the host counts the bodies past it, so a region priced too small
/// shows.</param>
/// <param name="BodyCapacity">The document's body capacity: every body index a sweep can bind.</param>
public sealed record WorldRegionIteration(string PlacementId, int Occupants, int BodyCapacity) : RuleIteration(Occupants) {
    /// <inheritdoc/>
    public override int KeySpan => BodyCapacity;
    /// <inheritdoc/>
    public override bool Versioned => true;

    /// <inheritdoc/>
    public override void Collect(IEffectHost host, List<CellKey> into) => ((IWorldRegionOccupants)host).CollectRegionOccupants(into: into, placementId: PlacementId, priced: Occupants);
    /// <inheritdoc/>
    /// <remarks>A binding is a body's index, which names one body for as long as it stays inside: a body that leaves is
    /// not swept, so its binding closes with the sweep, and re-entering starts a fresh one.</remarks>
    public override long Generation(IEffectHost host, CellKey key) => 0L;
    /// <inheritdoc/>
    public override bool TrySchedule(IStateReader reader, List<int> rows, List<FactRead> facts) {
        ArgumentNullException.ThrowIfNull(argument: facts);
        if (reader is not IWorldRegionOccupants occupants) {
            return false;
        }
        facts.Add(item: occupants.ScheduleRegionOccupancy(placementId: PlacementId));

        return true;
    }
}
