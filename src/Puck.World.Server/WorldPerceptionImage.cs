using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>
/// One frozen image of a population's bodies for a tick's perception: each active body's position, orientation,
/// travel and generation as of <see cref="Freeze"/>, and one spatial neighbourhood per cell width, built over the
/// image the first time a query asks for that width after the freeze. Every observer that perceives through one image
/// reads the same instant, whatever order the observers run in and whatever moves between their reads.
/// </summary>
/// <remarks>Derived scratch, never simulation state: nothing hashes or checkpoints it. Its owner freezes it where its
/// own perception instant falls in the tick (a flock before the bodies step, a decision before the rules fire), so two
/// owners hold two images of two instants through this one mechanism.</remarks>
internal sealed class WorldPerceptionImage {
    private sealed class Grid(int capacity, long widthRaw) {
        public readonly FixedSpatialNeighborhood Neighborhood = new(
            capacity: capacity,
            cellWidth: FixedQ4816.FromRawBits(value: widthRaw)
        );

        public bool Built;
    }

    private readonly Dictionary<long, Grid> m_grids = [];
    private readonly FixedSpatialPoint[] m_points;
    private readonly FixedVector3[] m_positions;
    private readonly FixedVector3[] m_travel;
    private readonly FixedQuaternion[] m_orientations;
    private readonly int[] m_generations;
    private readonly bool[] m_present;

    /// <summary>Initializes an image sized to a population.</summary>
    /// <param name="capacity">The population's slot count.</param>
    public WorldPerceptionImage(int capacity) {
        m_points = new FixedSpatialPoint[capacity];
        m_positions = new FixedVector3[capacity];
        m_travel = new FixedVector3[capacity];
        m_orientations = new FixedQuaternion[capacity];
        m_generations = new int[capacity];
        m_present = new bool[capacity];
    }

    /// <summary>Gets the slot count the image was sized to.</summary>
    public int Capacity => m_present.Length;
    /// <summary>Gets the bodies the last freeze copied.</summary>
    public int Count { get; private set; }
    /// <summary>Gets the cell widths the image holds a neighbourhood for.</summary>
    public int Scales => m_grids.Count;
    /// <summary>Gets how many neighbourhoods queries have built over the image since it was created.</summary>
    public long Builds { get; private set; }

    /// <summary>Ensures the image holds a neighbourhood of one cell width, so a query for it allocates nothing.</summary>
    /// <param name="cellWidthRaw">The cell width, as Q48.16 raw bits.</param>
    public void Declare(long cellWidthRaw) {
        if (!m_grids.ContainsKey(key: cellWidthRaw)) {
            m_grids.Add(key: cellWidthRaw, value: new Grid(capacity: Capacity, widthRaw: cellWidthRaw));
        }
    }
    /// <summary>Copies every active body's pose, travel since its last step and generation, and marks every
    /// neighbourhood for rebuilding on its next query.</summary>
    /// <param name="population">The population to copy; its capacity is the image's.</param>
    public void Freeze(WorldPopulation population) {
        var count = 0;

        Array.Clear(array: m_present);
        for (var index = 0; (index < m_present.Length); index++) {
            if (!population.IsActive(index: index) || (population.EntryBody(index: index) is not { } body)) { continue; }

            var position = body.FixedPosition;

            m_present[index] = true;
            m_generations[index] = population.Generation(index: index);
            m_positions[index] = position;
            // Displacement carries actual movement (free flight and contact corrections included). Its common
            // previous-step time scale cancels wherever a reader takes a weighted mean direction.
            m_travel[index] = (position - body.FixedPreviousPosition);
            m_orientations[index] = body.FixedOrientation;
            m_points[count++] = new FixedSpatialPoint(Index: index, Position: position);
        }
        Count = count;
        foreach (var grid in m_grids.Values) { grid.Built = false; }
        WorldTickWork.Process.Add(amount: 1L, kind: WorldTickWork.PerceptionFreezes);
        WorldTickWork.Process.Add(amount: count, kind: WorldTickWork.PerceptionPoints);
    }
    /// <summary>Returns whether the freeze found an active body in a slot.</summary>
    /// <param name="index">The slot.</param>
    /// <returns><see langword="true"/> for a slot inside the image that held a body.</returns>
    public bool Present(int index) => ((((uint)index) < ((uint)m_present.Length)) && m_present[index]);
    /// <summary>Returns the generation of the body a slot held at the freeze.</summary>
    /// <param name="index">A slot <see cref="Present"/> answers for.</param>
    /// <returns>The generation.</returns>
    public int Generation(int index) => m_generations[index];
    /// <summary>Returns the frozen position of a slot's body.</summary>
    /// <param name="index">A slot <see cref="Present"/> answers for.</param>
    /// <returns>The position.</returns>
    public ref readonly FixedVector3 Position(int index) => ref m_positions[index];
    /// <summary>Returns the frozen orientation of a slot's body.</summary>
    /// <param name="index">A slot <see cref="Present"/> answers for.</param>
    /// <returns>The orientation.</returns>
    public ref readonly FixedQuaternion Orientation(int index) => ref m_orientations[index];
    /// <summary>Returns how far a slot's body moved over the step before the freeze.</summary>
    /// <param name="index">A slot <see cref="Present"/> answers for.</param>
    /// <returns>The displacement.</returns>
    public ref readonly FixedVector3 Travel(int index) => ref m_travel[index];
    /// <summary>Samples the frozen bodies near a point through the neighbourhood of one cell width, building it over
    /// the image on the first query since the freeze.</summary>
    /// <param name="cellWidthRaw">A cell width <see cref="Declare"/> was given.</param>
    /// <param name="origin">The point to search about.</param>
    /// <param name="radius">The search radius.</param>
    /// <param name="excludedIndex">The slot to leave out, the observer's own.</param>
    /// <param name="candidateBudget">The most occupants to examine.</param>
    /// <param name="sampleOrdinal">The phase that rotates which occupants a limited budget examines.</param>
    /// <param name="destination">Receives the neighbours found.</param>
    /// <param name="requireBuilt">Whether the neighbourhood must already be built: a query from a kit partition only
    /// reads, so <see cref="BuildDeclared"/> builds every neighbourhood before the partitions run.</param>
    /// <returns>The work the query took and the neighbours it wrote.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="requireBuilt"/> is <see langword="true"/> and the
    /// neighbourhood has not been built since the freeze.</exception>
    public FixedNeighborhoodWork Query(long cellWidthRaw, in FixedVector3 origin, FixedQ4816 radius, int excludedIndex, int candidateBudget, ulong sampleOrdinal, Span<FixedSpatialNeighbor> destination, bool requireBuilt = false) {
        var grid = m_grids[cellWidthRaw];

        if (!grid.Built) {
            if (requireBuilt) { throw new InvalidOperationException(message: "A perception query from a kit partition met a neighbourhood not built before the fork."); }
            Build(grid: grid);
        }

        return grid.Neighborhood.Query(
            origin,
            radius,
            excludedIndex,
            candidateBudget,
            sampleOrdinal,
            destination
        );
    }
    /// <summary>Builds every declared neighbourhood not yet built since the freeze, so the queries that follow only read.</summary>
    public void BuildDeclared() {
        foreach (var grid in m_grids.Values) {
            if (!grid.Built) { Build(grid: grid); }
        }
    }
    private void Build(Grid grid) {
        grid.Neighborhood.Rebuild(points: m_points.AsSpan(length: Count, start: 0));
        grid.Built = true;
        Builds++;
        WorldTickWork.Process.Add(amount: 1L, kind: WorldTickWork.PerceptionGrids);
    }
}
