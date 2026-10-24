namespace Puck.SdfVm;

/// <summary>
/// A residency's lasting placement of its mesh region (<see cref="SdfMeshRegion"/>): a reserved run of draw records, then
/// an arena where each distinct mesh lies whole, its vertices, triangle materials and indices together, at the place it
/// was given when a draw list first named it. A mesh keeps that place for as long as the arena keeps it, so a list that
/// adds a mesh owes only the draw records and the new mesh's words, and a list that drops one leaves the mesh where it
/// lies: a list that names it again, as when a body that left the world returns, owes nothing for it.
/// <para>A plan relocates every mesh a list names, packing the arena from the start in the list's order and forgetting
/// every mesh the list does not name, only when the list holds more draws than the reserve, which then doubles, or when
/// the arena's words outside the named meshes exceed both the named meshes' words and <see cref="RetainedSlackWords"/>,
/// or keeping those words would append past <see cref="SdfWorldTables.MostMeshRegionBytes"/>.
/// The plan states what it owes: <see cref="Relocated"/>, or the meshes it placed (<see cref="Placed"/>).</para>
/// </summary>
public sealed class SdfMeshRegionArena {
    /// <summary>The fewest draw records the region reserves.</summary>
    public const int MinimumDrawCapacity = 256;
    /// <summary>The words outside the named meshes (meshes no list names any more) the arena keeps, however few words the
    /// named meshes hold, before a plan packs it again: 32 MB, so a body crossing between worlds finds its meshes where
    /// it left them rather than writing them again.</summary>
    public const int RetainedSlackWords = (1 << 23);

    private readonly Dictionary<SdfMesh, SdfMeshRegionMesh> m_placements = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly HashSet<SdfMesh> m_named = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_placed = [];
    private readonly int m_retainedSlackWords;
    private int m_drawCapacity;
    private int m_end;

    /// <summary>Initializes a new instance of the <see cref="SdfMeshRegionArena"/> class.</summary>
    /// <param name="retainedSlackWords">The words outside the named meshes the arena keeps before a plan packs it again,
    /// however few words the named meshes hold; <see cref="RetainedSlackWords"/> for a residency's tables.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retainedSlackWords"/> is negative.</exception>
    public SdfMeshRegionArena(int retainedSlackWords = RetainedSlackWords) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: retainedSlackWords);

        m_retainedSlackWords = retainedSlackWords;
    }

    /// <summary>Gets each placed mesh's words, keyed by reference: every mesh the latest list names, and the meshes
    /// earlier lists named that the arena still keeps.</summary>
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
    /// <exception cref="ArgumentException">The list holds more than <see cref="SdfMeshRegion.MaxDraws"/> draws.</exception>
    /// <exception cref="OverflowException">The region holds more words than an <see cref="int"/> counts.</exception>
    public void Plan(IReadOnlyList<SdfMeshDraw> draws) {
        ArgumentNullException.ThrowIfNull(draws);

        if (draws.Count > SdfMeshRegion.MaxDraws) {
            throw new ArgumentException(message: $"A mesh region holds at most {SdfMeshRegion.MaxDraws} draws; the list holds {draws.Count}.", paramName: nameof(draws));
        }

        m_named.Clear();
        m_placed.Clear();

        var namedWords = 0L;
        var placedNamedWords = 0L;

        foreach (var draw in draws) {
            if (!m_named.Add(item: draw.Mesh)) {
                continue;
            }

            var words = SdfMeshRegion.WordsOf(mesh: draw.Mesh);

            namedWords += words;

            if (m_placements.ContainsKey(key: draw.Mesh)) {
                placedNamedWords += words;
            }
        }

        // The arena's words that no named mesh holds: meshes no list names any more.
        var unnamed = ((m_end - (((long)m_drawCapacity) * SdfMeshRecord.DrawWords)) - placedNamedWords);
        var appendedWords = (m_end + (namedWords - placedNamedWords));
        var relocate = (
            (draws.Count > m_drawCapacity) ||
            ((unnamed > namedWords) && (unnamed > m_retainedSlackWords)) ||
            ((unnamed > 0) && (((ulong)appendedWords) > (SdfWorldTables.MostMeshRegionBytes / sizeof(uint))))
        );

        if (relocate) {
            m_drawCapacity = Math.Min(val1: SdfMeshRegion.MaxDraws, val2: Math.Max(val1: draws.Count, val2: Math.Max(val1: MinimumDrawCapacity, val2: (2 * m_drawCapacity))));
            m_end = checked(m_drawCapacity * SdfMeshRecord.DrawWords);
            m_placements.Clear();
        }

        foreach (var draw in draws) {
            if (m_placements.ContainsKey(key: draw.Mesh)) { continue; }

            var mesh = draw.Mesh;
            var words = SdfMeshRegion.WordsOf(mesh: mesh);
            var vertexWord = m_end;
            var materialWord = checked(vertexWord + (mesh.Positions.Length * SdfMeshRecord.VertexWords));
            var indexWord = checked(materialWord + mesh.TriangleMaterials.Length);
            var skinWord = (mesh.VertexMembers.IsEmpty ? 0 : checked(indexWord + mesh.Indices.Length));

            m_placements.Add(key: mesh, value: new SdfMeshRegionMesh(IndexCount: mesh.Indices.Length, IndexWord: indexWord, MaterialWord: materialWord, VertexWord: vertexWord, SkinWord: skinWord));
            m_end = checked(m_end + words);
            m_placed.Add(item: mesh);
        }

        Relocated = relocate;
    }
}
