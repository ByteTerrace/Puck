namespace Puck.SdfVm;

/// <summary>
/// A residency's lasting placement of its mesh region (<see cref="SdfMeshRegion"/>): a reserved run of draw records, then
/// an arena where each distinct mesh lies whole, its vertices, triangle materials and indices together, at the place it
/// was given when a draw list first named it. A mesh keeps that place for as long as each new list names it, so a list
/// that adds a mesh owes only the draw records and the new mesh's words, and a list that drops one leaves a hole.
/// <para>A plan relocates every mesh, packing the arena from the start in the list's order, only when the list holds
/// more draws than the reserve, which then doubles, or when the holes hold more words than the meshes do and more than
/// <see cref="CompactionSlackWords"/>. The plan states what it owes: <see cref="Relocated"/>, or the meshes it placed
/// (<see cref="Placed"/>).</para>
/// </summary>
public sealed class SdfMeshRegionArena {
    /// <summary>The fewest draw records the region reserves.</summary>
    public const int MinimumDrawCapacity = 256;
    /// <summary>The fewest words the holes must hold before a plan packs the arena again.</summary>
    public const int CompactionSlackWords = (1 << 20);

    private readonly Dictionary<SdfMesh, SdfMeshRegionMesh> m_placements = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_named = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_dropped = [];
    private readonly List<SdfMesh> m_placed = [];
    private int m_drawCapacity;
    private int m_end;
    private int m_liveWords;

    /// <summary>Gets each placed mesh's words, keyed by reference.</summary>
    public Dictionary<SdfMesh, SdfMeshRegionMesh> Placements => m_placements;
    /// <summary>Gets the draw records the region reserves ahead of the arena.</summary>
    public int DrawCapacity => m_drawCapacity;
    /// <summary>Gets the region's words: the reserved draw records and the arena up to its last placed mesh.</summary>
    public int Words => m_end;
    /// <summary>Gets the meshes the latest plan placed, whose words it owes; every placed mesh when it
    /// <see cref="Relocated"/>.</summary>
    public IReadOnlyList<SdfMesh> Placed => m_placed;
    /// <summary>Gets whether the latest plan moved every mesh, so the region owes every word.</summary>
    public bool Relocated { get; private set; }

    /// <summary>Places the meshes a list of draws names: a mesh already placed keeps its words, and a new one is added at
    /// the arena's end, unless the plan relocates every mesh.</summary>
    /// <param name="draws">The draws.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The list holds more than <see cref="SdfMeshRegion.MaxDraws"/> draws or a draw
    /// whose indirect policy names no member.</exception>
    /// <exception cref="OverflowException">The region holds more words than an <see cref="int"/> counts.</exception>
    public void Plan(IReadOnlyList<SdfMeshDraw> draws) {
        ArgumentNullException.ThrowIfNull(draws);

        if (draws.Count > SdfMeshRegion.MaxDraws) {
            throw new ArgumentException(message: $"A mesh region holds at most {SdfMeshRegion.MaxDraws} draws; the list holds {draws.Count}.", paramName: nameof(draws));
        }

        m_named.Clear();
        m_placed.Clear();
        m_dropped.Clear();

        foreach (var draw in draws) {
            SdfMeshRegion.RequireIndirect(draw: draw);
            _ = m_named.Add(item: draw.Mesh);
        }
        foreach (var mesh in m_placements.Keys) {
            if (!m_named.Contains(item: mesh)) { m_dropped.Add(item: mesh); }
        }
        foreach (var mesh in m_dropped) {
            m_liveWords -= SdfMeshRegion.WordsOf(mesh: mesh);
            _ = m_placements.Remove(key: mesh);
        }

        var holes = ((m_end - (m_drawCapacity * SdfMeshRegion.DrawWords)) - m_liveWords);
        var relocate = ((draws.Count > m_drawCapacity) || ((holes > m_liveWords) && (holes > CompactionSlackWords)));

        if (relocate) {
            m_drawCapacity = Math.Min(val1: SdfMeshRegion.MaxDraws, val2: Math.Max(val1: draws.Count, val2: Math.Max(val1: MinimumDrawCapacity, val2: (2 * m_drawCapacity))));
            m_end = checked(m_drawCapacity * SdfMeshRegion.DrawWords);
            m_placements.Clear();
            m_liveWords = 0;
        }

        foreach (var draw in draws) {
            if (m_placements.ContainsKey(key: draw.Mesh)) { continue; }

            var mesh = draw.Mesh;
            var words = SdfMeshRegion.WordsOf(mesh: mesh);
            var vertexWord = m_end;
            var materialWord = checked(vertexWord + (mesh.Positions.Length * SdfMeshRegion.VertexWords));
            var indexWord = checked(materialWord + mesh.TriangleMaterials.Length);

            m_placements.Add(key: mesh, value: new SdfMeshRegionMesh(IndexCount: mesh.Indices.Length, IndexWord: indexWord, MaterialWord: materialWord, VertexWord: vertexWord));
            m_end = checked(m_end + words);
            m_liveWords += words;
            m_placed.Add(item: mesh);
        }

        Relocated = relocate;
    }
}
