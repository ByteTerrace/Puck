namespace Puck.World;

public sealed partial class CompiledWorldTopology {
    private readonly int[][] m_images = [];
    private readonly string[] m_elementNames = [];
    private readonly Dictionary<string, int> m_elementAliases = new(StringComparer.Ordinal);

    /// <summary>Gets the number of elements in the topology's point group, the identity included: 8 for a square
    /// grid, 4 for a rectangle, 12 for a hex board, 1 for a ring.</summary>
    public int ElementCount => m_elementNames.Length;

    /// <summary>Gets an element's canonical signed-axis name by ordinal; ordinal 0 is the identity.</summary>
    /// <param name="element">The element ordinal.</param>
    public string ElementName(int element) => m_elementNames[element];

    /// <summary>Finds an element by its canonical name or an authored alias.</summary>
    /// <param name="name">A name <see cref="ElementName"/> answers, or an authored
    /// <see cref="IDiscreteLatticeTopology.ElementAliases"/> entry.</param>
    /// <returns>The element ordinal, or -1.</returns>
    public int Element(string name) {
        var canonical = Array.IndexOf(m_elementNames, name);
        return (canonical >= 0) ? canonical : (m_elementAliases.TryGetValue(name, out var aliased) ? aliased : -1);
    }

    /// <summary>Gets every authored alias name and the canonical element name it resolves to, for a read-back.</summary>
    public IEnumerable<(string Alias, string Canonical)> ElementAliases() {
        foreach (var (alias, element) in m_elementAliases) {
            yield return (alias, m_elementNames[element]);
        }
    }

    /// <summary>Gets the cell an element carries a cell to.</summary>
    /// <param name="element">The element ordinal.</param>
    /// <param name="cell">The cell ordinal.</param>
    public int Image(int element, int cell) => m_images[element][cell];

    // Every point-group element — Grid's, Hex's, and Box's alike — is a signed-axis permutation: it carries source
    // axis A to output position k with sign S, spelled "+x-y+z" (letter per axis, sign first). A Box already needed
    // this to name 48 cube elements by hand; Grid (2 planar axes, letters "xz") and Hex (3 cube coordinates q/r/s
    // summing to zero, letters "qrs") read the SAME AxisMap/Spell mechanism instead of the hand-picked
    // "mirrorMain"/"mirror3" names they used to carry. Element 0 is always the identity, so a caller may fold over
    // all elements and rely on the untransformed board being among the images.
    internal static (int[][] Images, string[] Names) BuildSymmetry(WorldTopologyKind kind, int width, int depth, int layers,
        IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var group = EnumerateGroup(kind, width, depth, layers);
        return (kind == WorldTopologyKind.Hex)
            ? MaterializeHex(group.Elements, coordinates, indices)
            : MaterializeAxis(group.Elements, group.AxisCount, group.Letters, group.Extents, coordinates, indices);
    }

    /// <summary>Names every element of a topology's point group without materializing per-cell images — the
    /// bare-group enumeration a validator uses to check an authored <see cref="IDiscreteLatticeTopology.ElementAliases"/>
    /// entry names a real element before any topology cell exists to carry.</summary>
    /// <param name="kind">The topology kind.</param>
    /// <param name="width">Cells along +X.</param>
    /// <param name="depth">Cells along +Z.</param>
    /// <param name="layers">Cells along +Y.</param>
    /// <returns>Every element's canonical signed-axis name, identity first.</returns>
    internal static string[] ElementNames(WorldTopologyKind kind, int width, int depth, int layers) {
        var group = EnumerateGroup(kind, width, depth, layers);
        var names = new string[group.Elements.Count];
        for (var element = 0; element < names.Length; element++) {
            names[element] = group.Elements[element].Name(group.AxisCount, group.Letters);
        }
        return names;
    }

    private readonly record struct Group(List<AxisMap> Elements, int AxisCount, string Letters, int[] Extents);

    private static Group EnumerateGroup(WorldTopologyKind kind, int width, int depth, int layers) => kind switch {
        WorldTopologyKind.Grid => EnumerateAxisGroup(axisCount: 2, extents: [width, depth, 1], letters: "xz"),
        WorldTopologyKind.Box => EnumerateAxisGroup(axisCount: 3, extents: [width, depth, layers], letters: "xyz"),
        WorldTopologyKind.Hex => EnumerateHexGroup(),
        _ => new([AxisMap.Identity], 3, "xyz", [width, depth, layers]),
    };

    // A signed permutation of up to three axes: axis k's SOURCE is this[k], carried with sign Sign(k). Slots beyond
    // an axis count in play (Grid uses 2) stay the identity (axis 2, sign +1) by construction — no generator ever
    // touches them — so the same three-slot representation and per-cell loop serve every kind.
    private readonly record struct AxisMap(int A0, int S0, int A1, int S1, int A2, int S2) {
        public static readonly AxisMap Identity = new(0, 1, 1, 1, 2, 1);
        public int this[int axis] => axis switch { 0 => A0, 1 => A1, _ => A2 };
        public int Sign(int axis) => axis switch { 0 => S0, 1 => S1, _ => S2 };
        public bool IsIdentity(int axisCount) {
            for (var axis = 0; axis < axisCount; axis++) {
                if (this[axis] != axis || Sign(axis) != 1) { return false; }
            }
            return true;
        }
        public string Name(int axisCount, string letters) {
            if (IsIdentity(axisCount)) { return "identity"; }
            var name = string.Empty;
            for (var axis = 0; axis < axisCount; axis++) {
                name += (Sign(axis) > 0 ? "+" : "-") + letters[this[axis]];
            }
            return name;
        }
        public AxisMap Then(AxisMap next) => new(
            this[next.A0], Sign(next.A0) * next.S0,
            this[next.A1], Sign(next.A1) * next.S1,
            this[next.A2], Sign(next.A2) * next.S2
        );
    }

    // Grid's and Box's point group is generated by mirroring each in-play axis and swapping every pair of equal
    // extent, closed by breadth-first composition — the same closure a Box's cube symmetry always used, now shared
    // with Grid's rectangle/square case instead of a second hand-written generator list.
    private static Group EnumerateAxisGroup(int axisCount, int[] extents, string letters) {
        var generators = new List<AxisMap>();
        for (var axis = 0; axis < axisCount; axis++) {
            generators.Add(FlipAxis(axis));
        }
        for (var a = 0; a < axisCount; a++) {
            for (var b = a + 1; b < axisCount; b++) {
                if (extents[a] == extents[b]) { generators.Add(SwapAxes(a, b)); }
            }
        }
        var elements = new List<AxisMap> { AxisMap.Identity };
        var seen = new HashSet<AxisMap>(elements);
        for (var index = 0; index < elements.Count; index++) {
            foreach (var generator in generators) {
                var composed = elements[index].Then(generator);
                if (seen.Add(composed)) { elements.Add(composed); }
            }
        }
        return new(elements, axisCount, letters, extents);
    }

    private static AxisMap FlipAxis(int axis) => axis switch {
        0 => new(0, -1, 1, 1, 2, 1),
        1 => new(0, 1, 1, -1, 2, 1),
        _ => new(0, 1, 1, 1, 2, -1),
    };

    private static AxisMap SwapAxes(int a, int b) {
        Span<int> axis = [0, 1, 2];
        (axis[a], axis[b]) = (axis[b], axis[a]);
        return new(axis[0], 1, axis[1], 1, axis[2], 1);
    }

    private static (int[][] Images, string[] Names) MaterializeAxis(List<AxisMap> elements, int axisCount, string letters, int[] extents,
        IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var images = new int[elements.Count][];
        var names = new string[elements.Count];
        for (var element = 0; element < elements.Count; element++) {
            var map = elements[element];
            names[element] = map.Name(axisCount, letters);
            var image = new int[coordinates.Count];
            for (var cell = 0; cell < coordinates.Count; cell++) {
                var p = coordinates[cell];
                int[] source = [p.X, p.Y, p.Z];
                var target = new int[3];
                for (var axis = 0; axis < 3; axis++) {
                    var value = source[map[axis]];
                    target[axis] = (map.Sign(axis) > 0) ? value : (extents[map[axis]] - 1 - value);
                }
                image[cell] = indices[(target[0], target[1], target[2])];
            }
            images[element] = image;
        }
        return (images, names);
    }

    // A hex's axial (q, r) is one plane of the cube coordinates (q, r, s) with q + r + s = 0. The elements of its
    // point group are exactly the signed permutations of (q, r, s) that keep every point on that plane: a bare
    // permutation of the three (six of them), or the same six permutations composed with negating all three — any
    // OTHER sign pattern moves a sum-zero point off the plane. That is 12 elements, matching the hexagon's own
    // dihedral group, enumerated directly rather than discovered by closure.
    private static Group EnumerateHexGroup() {
        Span<int> identityAxes = [0, 1, 2];
        var permutations = new List<int[]>();
        Permute(identityAxes, 0, permutations);

        var elements = new List<AxisMap>();
        foreach (var sign in new[] { 1, -1 }) {
            foreach (var permutation in permutations) {
                elements.Add(new(permutation[0], sign, permutation[1], sign, permutation[2], sign));
            }
        }
        elements.Sort((left, right) => left.IsIdentity(3) ? -1 : right.IsIdentity(3) ? 1 : 0);
        return new(elements, 3, "qrs", [0, 0, 0]);
    }

    private static (int[][] Images, string[] Names) MaterializeHex(List<AxisMap> elements, IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var images = new int[elements.Count][];
        var names = new string[elements.Count];
        for (var element = 0; element < elements.Count; element++) {
            var map = elements[element];
            names[element] = map.Name(3, "qrs");
            var image = new int[coordinates.Count];
            for (var cell = 0; cell < coordinates.Count; cell++) {
                var (q, r, _) = coordinates[cell];
                var s = -q - r;
                int[] cube = [q, r, s];
                var target = new int[3];
                for (var axis = 0; axis < 3; axis++) {
                    target[axis] = map.Sign(axis) * cube[map[axis]];
                }
                image[cell] = indices[(target[0], target[1], 0)];
            }
            images[element] = image;
        }
        return (images, names);
    }

    private static void Permute(Span<int> axes, int from, List<int[]> results) {
        if (from == axes.Length) {
            results.Add(axes.ToArray());
            return;
        }
        for (var index = from; index < axes.Length; index++) {
            (axes[from], axes[index]) = (axes[index], axes[from]);
            Permute(axes, from + 1, results);
            (axes[from], axes[index]) = (axes[index], axes[from]);
        }
    }

    // Authored friendlier names ("rot90" for whatever axis permutation a square grid's quarter turn is) resolve
    // through Element(string) alongside the canonical spelling; ElementName always answers the canonical form. The
    // validator already proved every alias names a real element, so a miss here can only mean the alias outlived
    // its topology's own recompile — install it defensively rather than throw.
    internal void InstallElementAliases(IReadOnlyList<WorldTopologyElementAlias>? aliases) {
        m_elementAliases.Clear();
        foreach (var alias in aliases ?? []) {
            var canonical = Array.IndexOf(m_elementNames, alias.Element);
            if (canonical >= 0) {
                m_elementAliases[alias.Name] = canonical;
            }
        }
    }
}
