using System.Diagnostics.CodeAnalysis;

namespace Puck.State;

/// <summary>
/// A seeded patch of a Penrose rhomb tiling and the facts a document reads off every tile of it: the tile's rhomb
/// kind, the two ribbons running through it, and the thin chain it belongs to.
/// </summary>
/// <remarks>
/// <para>A <b>ribbon</b> is a maximal straight run of tiles entered through one side and left through the opposite,
/// parallel one — the de Bruijn ribbon, and the only long chain the P3 tiling carries. Each rhomb has two pairs of
/// parallel sides, so exactly two ribbons run through it: <see cref="RibbonOf"/> takes an axis, <c>0</c> for the
/// sides <c>0</c> and <c>2</c> of <see cref="PenroseTiles.Sides"/> and <c>1</c> for the sides <c>1</c> and <c>3</c>.
/// Both axes are numbered out of one sequence, so two tiles lie on a common ribbon exactly when their id pairs
/// intersect, and a run is cut where the patch ends.</para>
/// <para>A <b>thin chain</b> is a maximal run of thin rhombs sharing a side. In the P3 tiling a thin rhomb has at
/// most one thin neighbour, so a chain is one thin rhomb or a pair of them; <see cref="ThinChainOf"/> answers
/// <see cref="NoChain"/> for a fat rhomb.</para>
/// <para>Both numberings run in ascending order of the least tile each one holds, so they are a function of the
/// patch alone.</para>
/// <para>A patch is grown breadth-first from a seeded start tile over the tiling's own side adjacency, so one seed
/// yields one board: the tiles are a connected set, listed in the tiling's own cell order, and the patch's cells
/// keep the tiling's cell ids.</para>
/// </remarks>
public sealed class PenrosePatch {
    private readonly PenroseRhomb[] m_kinds;
    private readonly int[] m_chains;
    private readonly int[] m_ribbons;
    private readonly int[] m_tiles;

    private PenrosePatch(LatticeTopology.Graph graph, int[] tiles, PenroseRhomb[] kinds, int[] chains, int chainCount, int[] ribbons, int ribbonCount) {
        Graph = graph;
        RibbonCount = ribbonCount;
        ThinChainCount = chainCount;
        m_chains = chains;
        m_kinds = kinds;
        m_ribbons = ribbons;
        m_tiles = tiles;
    }

    /// <summary>How many ribbons run through one tile, one per pair of parallel sides.</summary>
    public const int RibbonAxes = 2;
    /// <summary>The thin-chain id a tile that belongs to no chain carries.</summary>
    public const int NoChain = -1;

    /// <summary>Gets the patch's own graph topology: the selected tiles, keeping the tiling's cell ids and centres,
    /// and the sides they share with one another.</summary>
    public LatticeTopology.Graph Graph { get; }
    /// <summary>Gets how many ribbons the patch holds, across both axes.</summary>
    public int RibbonCount { get; }
    /// <summary>Gets how many thin chains the patch holds.</summary>
    public int ThinChainCount { get; }
    /// <summary>Gets how many tiles the patch holds.</summary>
    public int TileCount => m_kinds.Length;

    private static LatticeTopology.Graph Restrict(LatticeTopology.Tiling tiling, LatticeTopology.Graph whole, int[] tiles) {
        var cells = new List<GraphCell>(capacity: tiles.Length);
        var kept = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < tiles.Length); index++) {
            var cell = whole.Cells[tiles[index]];

            cells.Add(item: cell);
            _ = kept.Add(item: cell.Id);
        }

        var edges = new List<GraphEdge>();

        foreach (var edge in whole.Edges) {
            if (
                kept.Contains(item: edge.From) &&
                kept.Contains(item: edge.To)
            ) {
                edges.Add(item: edge);
            }
        }

        return new LatticeTopology.Graph(
            Name: tiling.Name,
            Origin: tiling.Origin,
            CellSize: tiling.CellSize,
            Cells: cells,
            Directions: whole.Directions,
            Edges: edges
        );
    }
    // Thin rhombs joined across a shared side are one chain; a chain's id is the order its least member appears in.
    private static int[] Chains(int[] tiles, PenroseRhomb[] kinds, Dictionary<int, int> position, IReadOnlyList<IReadOnlyList<int>> sides, out int chainCount) {
        var chains = new int[tiles.Length];
        var frontier = new Stack<int>();

        Array.Fill(
            array: chains,
            value: NoChain
        );

        chainCount = 0;

        for (var index = 0; (index < tiles.Length); index++) {
            if (
                (kinds[index] != PenroseRhomb.Thin) ||
                (chains[index] != NoChain)
            ) {
                continue;
            }

            var chain = chainCount++;

            chains[index] = chain;
            frontier.Push(item: index);

            while (frontier.Count != 0) {
                var current = frontier.Pop();

                foreach (var neighbour in sides[tiles[current]]) {
                    if (
                        (neighbour < 0) ||
                        !position.TryGetValue(
                        key: neighbour,
                        value: out var next
                    ) ||
                        (kinds[next] != PenroseRhomb.Thin) ||
                        (chains[next] != NoChain)
                    ) {
                        continue;
                    }

                    chains[next] = chain;
                    frontier.Push(item: next);
                }
            }
        }

        return chains;
    }
    // A ribbon runs straight: entered through a side, it leaves through the opposite one. Walking both ways from
    // every (tile, axis) start that no walk has reached yet numbers the runs in ascending order of their least
    // member, and a run stops where the patch ends.
    private static int[] Ribbons(int[] tiles, Dictionary<int, int> position, IReadOnlyList<IReadOnlyList<int>> sides, out int ribbonCount) {
        var ribbons = new int[(tiles.Length * RibbonAxes)];

        Array.Fill(
            array: ribbons,
            value: -1
        );

        ribbonCount = 0;

        for (var node = 0; (node < ribbons.Length); node++) {
            if (ribbons[node] >= 0) {
                continue;
            }

            var ribbon = ribbonCount++;

            ribbons[node] = ribbon;

            for (var direction = 0; (direction < RibbonAxes); direction++) {
                var current = (node / RibbonAxes);
                var side = ((node % RibbonAxes) + (direction * RibbonAxes));

                while (true) {
                    var neighbour = sides[tiles[current]][side];

                    if (
                        (neighbour < 0) ||
                        !position.TryGetValue(
                        key: neighbour,
                        value: out var next
                    )
                    ) {
                        break;
                    }

                    var back = -1;

                    for (var candidate = 0; (candidate < 4); candidate++) {
                        if (sides[tiles[next]][candidate] == tiles[current]) {
                            back = candidate;
                        }
                    }
                    if (back < 0) {
                        break;
                    }

                    var reached = ((next * RibbonAxes) + (back % RibbonAxes));

                    if (ribbons[reached] >= 0) {
                        break;
                    }

                    ribbons[reached] = ribbon;
                    current = next;
                    side = ((back + 2) % 4);
                }
            }
        }

        return ribbons;
    }
    private static bool TrySelect(PenroseTiles tiles, int start, int tileCount, out int[] selected, out string reason) {
        var order = new List<int>(capacity: tileCount);
        var queue = new Queue<int>();
        var seen = new HashSet<int> { start };

        queue.Enqueue(item: start);

        while (
            (queue.Count != 0) &&
            (order.Count < tileCount)
        ) {
            var current = queue.Dequeue();

            order.Add(item: current);

            foreach (var neighbour in tiles.Sides[current]) {
                if (
                    (neighbour >= 0) &&
                    seen.Add(item: neighbour)
                ) {
                    queue.Enqueue(item: neighbour);
                }
            }
        }

        if (order.Count != tileCount) {
            reason = $"the tiling reaches {order.Count} tiles from the drawn start tile, fewer than the {tileCount} the patch asks for";
            selected = [];

            return false;
        }

        order.Sort();

        reason = string.Empty;
        selected = [.. order];

        return true;
    }

    /// <summary>Returns a tile's rhomb kind.</summary>
    /// <param name="cell">The patch cell ordinal.</param>
    /// <returns>The rhomb kind.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> names no tile of the patch.</exception>
    public PenroseRhomb KindOf(int cell) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: m_kinds.Length,
            value: cell
        );

        return m_kinds[cell];
    }
    /// <summary>Builds the derived row a document reads tile kinds from: one lattice cell per tile, carrying
    /// <c>0</c> for a thin rhomb and <c>1</c> for a fat one.</summary>
    /// <param name="name">The row's name.</param>
    /// <returns>The row.</returns>
    public StateRow KindRow(CellName name) => Row(
        name: name,
        value: cell => ((long)m_kinds[cell])
    );
    /// <summary>Returns the ribbon running through a tile along one axis.</summary>
    /// <param name="cell">The patch cell ordinal.</param>
    /// <param name="axis">Which pair of parallel sides the ribbon crosses: <c>0</c> for sides <c>0</c> and <c>2</c>,
    /// <c>1</c> for sides <c>1</c> and <c>3</c>.</param>
    /// <returns>The ribbon id.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> names no tile of the patch, or
    /// <paramref name="axis"/> names no axis.</exception>
    public int RibbonOf(int cell, int axis) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: TileCount,
            value: cell
        );
        ArgumentOutOfRangeException.ThrowIfNegative(value: axis);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: RibbonAxes,
            value: axis
        );

        return m_ribbons[((cell * RibbonAxes) + axis)];
    }
    /// <summary>Builds the derived row a document reads one axis's ribbon ids from: one lattice cell per tile,
    /// carrying the ribbon that axis runs along.</summary>
    /// <param name="name">The row's name.</param>
    /// <param name="axis">The ribbon axis, <c>0</c> or <c>1</c>.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="axis"/> names no axis.</exception>
    public StateRow RibbonRow(CellName name, int axis) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: axis);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: RibbonAxes,
            value: axis
        );

        return Row(
            name: name,
            value: cell => m_ribbons[((cell * RibbonAxes) + axis)]
        );
    }
    /// <summary>Returns the thin chain a tile belongs to.</summary>
    /// <param name="cell">The patch cell ordinal.</param>
    /// <returns>The chain id, or <see cref="NoChain"/> for a fat rhomb.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> names no tile of the patch.</exception>
    public int ThinChainOf(int cell) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: m_chains.Length,
            value: cell
        );

        return m_chains[cell];
    }
    /// <summary>Builds the derived row a document reads thin-chain ids from: one lattice cell per tile, carrying the
    /// tile's chain id and the row's empty value for a fat rhomb.</summary>
    /// <param name="name">The row's name.</param>
    /// <returns>The row.</returns>
    public StateRow ThinChainRow(CellName name) => Row(
        name: name,
        value: cell => m_chains[cell]
    );
    /// <summary>Returns the tiling cell ordinal a patch cell came from.</summary>
    /// <param name="cell">The patch cell ordinal.</param>
    /// <returns>The tiling's own cell ordinal.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> names no tile of the patch.</exception>
    public int TileOf(int cell) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: m_tiles.Length,
            value: cell
        );

        return m_tiles[cell];
    }
    /// <summary>Describes a Penrose tiling's whole patch: every tile it lays down, with its kind, ribbons, and thin chain.</summary>
    /// <param name="tiling">The tiling.</param>
    /// <param name="patch">The patch, on success.</param>
    /// <param name="reason">Why the tiling carries no patch, or empty on success.</param>
    /// <returns><see langword="true"/> when the tiling is a Penrose one.</returns>
    public static bool TryDescribe(LatticeTopology.Tiling tiling, [NotNullWhen(true)] out PenrosePatch? patch, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: tiling);

        if (!TilingGenerator.TryDescribePenrose(
            reason: out reason,
            tiles: out var tiles,
            tiling: tiling
        )) {
            patch = null;

            return false;
        }

        var whole = TilingGenerator.Generate(tiling: tiling);
        var selected = new int[tiles.Kinds.Count];

        for (var index = 0; (index < selected.Length); index++) {
            selected[index] = index;
        }

        patch = Create(
            selected: selected,
            tiles: tiles,
            tiling: tiling,
            whole: whole
        );

        return true;
    }
    /// <summary>Draws a patch of a Penrose tiling: the seed picks one start tile, and the patch is the
    /// <paramref name="tileCount"/> tiles the tiling reaches from it first, so one seed yields one board.</summary>
    /// <param name="tiling">The tiling to cut the patch out of.</param>
    /// <param name="documentSeed">The document's own reroll lever.</param>
    /// <param name="instanceIdentity">The running instance's identity.</param>
    /// <param name="site">The drawing site's descriptor.</param>
    /// <param name="tileCount">How many tiles the patch holds.</param>
    /// <param name="patch">The patch, on success.</param>
    /// <param name="reason">Why the patch was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the patch was cut.</returns>
    public static bool TryDraw(LatticeTopology.Tiling tiling, ulong documentSeed, string instanceIdentity, string site, int tileCount, [NotNullWhen(true)] out PenrosePatch? patch, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: tiling);

        patch = null;

        if (!TilingGenerator.TryDescribePenrose(
            reason: out reason,
            tiles: out var tiles,
            tiling: tiling
        )) {
            return false;
        }

        var available = tiles.Kinds.Count;

        if (((uint)(tileCount - 1)) >= ((uint)available)) {
            reason = $"tiling '{tiling.Name}' lays down {available} tiles, so a patch of {tileCount} names none of it";

            return false;
        }

        var source = new StateGenerator(
            RangeMax: (available - 1),
            RangeMin: 0L,
            Source: GeneratorSource.UniformRange
        );

        if (!GeneratorEngine.TryFire(
            cursor: 0L,
            generator: source,
            masks: null,
            reason: out reason,
            result: out var drawn,
            seedState: GeneratorEngine.ComputeSeedState(
                documentSeed: documentSeed,
                instanceIdentity: instanceIdentity,
                site: site
            ),
            stream: GeneratorEngine.ComputeStreamId(site: site),
            targetKind: CellKind.Int
        )) {
            return false;
        }

        var start = ((int)(drawn.Numeric ?? 0L));

        if (!TrySelect(
            reason: out reason,
            selected: out var selected,
            start: start,
            tileCount: tileCount,
            tiles: tiles
        )) {
            return false;
        }

        patch = Create(
            selected: selected,
            tiles: tiles,
            tiling: tiling,
            whole: TilingGenerator.Generate(tiling: tiling)
        );

        return true;
    }

    private static PenrosePatch Create(LatticeTopology.Tiling tiling, LatticeTopology.Graph whole, PenroseTiles tiles, int[] selected) {
        var kinds = new PenroseRhomb[selected.Length];
        var position = new Dictionary<int, int>(capacity: selected.Length);

        for (var index = 0; (index < selected.Length); index++) {
            kinds[index] = tiles.Kinds[selected[index]];
            position[selected[index]] = index;
        }

        var chains = Chains(
            chainCount: out var chainCount,
            kinds: kinds,
            position: position,
            sides: tiles.Sides,
            tiles: selected
        );
        var ribbons = Ribbons(
            position: position,
            ribbonCount: out var ribbonCount,
            sides: tiles.Sides,
            tiles: selected
        );

        return new PenrosePatch(
            chainCount: chainCount,
            chains: chains,
            graph: Restrict(
                tiles: selected,
                tiling: tiling,
                whole: whole
            ),
            kinds: kinds,
            ribbonCount: ribbonCount,
            ribbons: ribbons,
            tiles: selected
        );
    }
    private StateRow Row(CellName name, Func<int, long> value) {
        var cells = new List<StateCell>(capacity: TileCount);

        for (var cell = 0; (cell < TileCount); cell++) {
            cells.Add(item: new StateCell(
                Key: CellName.Parse(candidate: Graph.Cells[cell].Id),
                Value: CellValue.Int(value: value(arg: cell))
            ));
        }

        return new StateRow(
            Name: name,
            Kind: CellKind.Int,
            Cells: cells,
            Domain: new StateDomain.CellsOf(
                Empty: NoChain,
                Topology: Graph.Name
            )
        ) {
            Generated = true,
        };
    }
}
