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

    internal ImmutableArray<WorldAdjacencyProjection> PrepareAdjacency(ulong tick) {
        if (m_adjacencies is null) { return []; }
        if (m_population.HasAdjacencyImage(tick)) { return m_population.AdjacencyProjections; }
        return m_adjacencyFields is { } fields ? fields.Prepare() : [.. m_adjacencies.Visuals()];
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

    /// <summary>Prepares the upcoming tick's adjacency input for a continuum applied at a host boundary.</summary>
    public void PrepareAdjacencyContacts() {
        lock (AuthorityGate) {
            if (m_adjacencies is null || m_tick.PendingReason is not null) { return; }
            var tick = NextInputTick;
            m_population.BeginAdjacencyContacts(tick, PrepareAdjacency(tick));
        }
    }
}
