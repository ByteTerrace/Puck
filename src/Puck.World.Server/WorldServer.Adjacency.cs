using System.Collections.Immutable;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private WorldAdjacencyFields? m_adjacencyFields;
    private IWorldAdjacencySource? m_adjacencies;
    [ThreadStatic]
    private static WorldServer? PreparingArrival;

    /// <summary>Gets or sets the read-only source of the authority's neighbour projections.</summary>
    public IWorldAdjacencySource? Adjacencies {
        get => m_adjacencies;
        set {
            m_adjacencyFields = value as WorldAdjacencyFields;
            m_adjacencies = value;
            m_population.ConfigureAdjacencies(value is null ? null : AuthorityIdentity);
        }
    }

    /// <summary>Resolves which authority each of this world's adjacency neighbours is and attaches to it. The host
    /// calls it at a boundary where no authority it steps holds a prepared or executing tick
    /// (<see cref="WorldAdjacencyFields.ResolveNeighbours"/>).</summary>
    public void ResolveAdjacencyNeighbours() => m_adjacencyFields?.ResolveNeighbours();
    internal ImmutableArray<WorldAdjacencyProjection> PrepareAdjacency(ulong tick) {
        if (m_adjacencies is null) { return []; }
        if (m_population.HasAdjacencyImage(tick)) { return m_population.AdjacencyProjections; }
        return CaptureAdjacency();
    }

    // A row that has yet to step can still be presented while paused. Its first actual tick captures its own input.
    private void PrepareUnsteppedAdjacencyPresentation() {
        if (m_adjacencyFields is { HasTickImage: false } fields) { fields.PreparePresentation(); }
    }

    // Local transfer commits and mailbox work run on the host. A mapped arrival can open the upcoming tick's
    // image after installing its mobility identity, so its contact and the next tick spend the same budget.
    private ArrivalPreparation PrepareArrivals() => new(this);

    private readonly ref struct ArrivalPreparation {
        private readonly WorldServer? m_previous;

        public ArrivalPreparation(WorldServer server) {
            m_previous = PreparingArrival;
            PreparingArrival = server;
        }

        public void Dispose() => PreparingArrival = m_previous;
    }

    internal void PrepareAdjacencyForArrival() {
        if (ReferenceEquals(PreparingArrival, this)) { PrepareAdjacencyContacts(); }
    }

    /// <summary>Pins the upcoming tick's adjacency input at a host boundary, for a continuum applied there. Each call
    /// reads the neighbours' records again, so the input leaves out every body that has landed here since the last:
    /// a neighbour's record still shows a traveler at the slot it left until that neighbour's next delivery. The
    /// tick's correction allowance opens with the first call and carries through the tick.</summary>
    public void PrepareAdjacencyContacts() {
        lock (AuthorityGate) {
            if (m_adjacencies is null || m_tick.PendingReason is not null) { return; }
            m_population.BeginAdjacencyContacts(NextInputTick, CaptureAdjacency());
        }
    }

    private ImmutableArray<WorldAdjacencyProjection> CaptureAdjacency() =>
        m_population.HoldAdjacency(m_adjacencyFields?.Prepare() ?? m_adjacencies?.Visuals());

    /// <summary>Lands a recorded arrival again under the preparation a live arrival lands under
    /// (<see cref="WorldTransferEscrow.TryReland"/>), so a re-drive pins the same input the recording did.</summary>
    /// <param name="arrival">The recorded arrival.</param>
    /// <param name="recorded">The outcome the recording kept.</param>
    /// <param name="reason">Why the arrival did not land again, or empty.</param>
    /// <returns><see langword="true"/> when the arrival landed as recorded.</returns>
    internal bool RelandArrival(WorldCrossingArrival arrival, WorldArrivalOutcome? recorded, out string reason) {
        using var gate = EnterAuthorityGate();
        using var prepare = PrepareArrivals();

        return m_transferEscrow.TryReland(arrival: arrival, reason: out reason, recorded: recorded);
    }
}
