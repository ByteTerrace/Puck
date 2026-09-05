namespace Puck.World;

public sealed partial class CompiledWorldTopology {
    private readonly int[][] m_images = [];
    private readonly string[] m_elementNames = [];

    /// <summary>Gets the number of elements in the topology's point group, the identity included: 8 for a square
    /// grid, 4 for a rectangle, 12 for a hex board, 1 for a ring.</summary>
    public int ElementCount => m_elementNames.Length;

    /// <summary>Gets an element's name by ordinal; ordinal 0 is the identity.</summary>
    /// <param name="element">The element ordinal.</param>
    public string ElementName(int element) => m_elementNames[element];

    /// <summary>Finds an element by name.</summary>
    /// <param name="name">A name <see cref="ElementName"/> answers.</param>
    /// <returns>The element ordinal, or -1.</returns>
    public int Element(string name) => Array.IndexOf(m_elementNames, name);

    /// <summary>Gets the cell an element carries a cell to.</summary>
    /// <param name="element">The element ordinal.</param>
    /// <param name="cell">The cell ordinal.</param>
    public int Image(int element, int cell) => m_images[element][cell];

    // The point group of the cell arrangement about its center, as permutations of cell ordinals: the square
    // dihedral group when a grid is square, its rectangle subgroup otherwise, the hexagonal dihedral group of a hex
    // board, and the identity alone for a ring. Element 0 is always the identity, so a caller may fold over all
    // elements and rely on the untransformed board being among the images.
    internal static (int[][] Images, string[] Names) BuildSymmetry(WorldTopologyKind kind, int width, int depth, int layers,
        IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        var names = new List<string> { "identity" };
        var maps = new List<Func<(int X, int Y, int Z), (int X, int Y, int Z)>> { p => p };
        if (kind == WorldTopologyKind.Grid) {
            names.AddRange(["rot180", "mirrorX", "mirrorZ"]);
            maps.AddRange([
                p => (width - 1 - p.X, depth - 1 - p.Y, 0),
                p => (width - 1 - p.X, p.Y, 0),
                p => (p.X, depth - 1 - p.Y, 0),
            ]);
            if (width == depth) {
                names.AddRange(["rot90", "rot270", "mirrorMain", "mirrorAnti"]);
                maps.AddRange([
                    p => (width - 1 - p.Y, p.X, 0),
                    p => (p.Y, width - 1 - p.X, 0),
                    p => (p.Y, p.X, 0),
                    p => (width - 1 - p.Y, width - 1 - p.X, 0),
                ]);
            }
        } else if (kind == WorldTopologyKind.Hex) {
            // Axial (q, r): one sixth turn is (q, r) -> (-r, q + r); the mirror swaps the axes.
            static (int X, int Y, int Z) Turn((int X, int Y, int Z) p, int sixths) {
                for (var step = 0; step < sixths; step++) { p = (-p.Y, p.X + p.Y, 0); }
                return p;
            }
            for (var sixths = 1; sixths < 6; sixths++) {
                var turns = sixths;
                names.Add($"rot{sixths * 60}");
                maps.Add(p => Turn(p, turns));
            }
            for (var sixths = 0; sixths < 6; sixths++) {
                var turns = sixths;
                names.Add($"mirror{sixths}");
                maps.Add(p => Turn((p.Y, p.X, 0), turns));
            }
        } else if (kind == WorldTopologyKind.Box) {
            // The box's group is closed by breadth-first composition of its generators: a mirror per axis, and a
            // swap of each pair of axes whose extents agree. Names spell where +X, +Y, +Z land.
            return BuildBoxSymmetry(width, depth, layers, coordinates, indices);
        }
        var images = new int[maps.Count][];
        for (var element = 0; element < maps.Count; element++) {
            var image = new int[coordinates.Count];
            for (var cell = 0; cell < coordinates.Count; cell++) {
                image[cell] = indices[maps[element](coordinates[cell])];
            }
            images[element] = image;
        }
        return (images, [.. names]);
    }

    // A box element is a signed axis permutation: (axis[0], sign[0]) says which source axis lands on +X with which
    // sign, and so on. Extents are (width, depth, layers) along (X, Y, Z).
    private readonly record struct AxisMap(int A0, int S0, int A1, int S1, int A2, int S2) {
        public int this[int axis] => axis switch { 0 => A0, 1 => A1, _ => A2 };
        public int Sign(int axis) => axis switch { 0 => S0, 1 => S1, _ => S2 };
        public string Name => this == new AxisMap(0, 1, 1, 1, 2, 1) ? "identity" : $"{Spell(0)}{Spell(1)}{Spell(2)}";
        private string Spell(int axis) => (Sign(axis) > 0 ? "+" : "-") + "xyz"[this[axis]];
        public AxisMap Then(AxisMap next) => new(this[next.A0], Sign(next.A0) * next.S0, this[next.A1], Sign(next.A1) * next.S1, this[next.A2], Sign(next.A2) * next.S2);
    }

    private static (int[][] Images, string[] Names) BuildBoxSymmetry(int width, int depth, int layers,
        IReadOnlyList<(int X, int Y, int Z)> coordinates, Dictionary<(int, int, int), int> indices) {
        int[] extents = [width, depth, layers];
        var generators = new List<AxisMap> {
            new(0, -1, 1, 1, 2, 1), new(0, 1, 1, -1, 2, 1), new(0, 1, 1, 1, 2, -1),
        };
        if (width == depth) { generators.Add(new(1, 1, 0, 1, 2, 1)); }
        if (width == layers) { generators.Add(new(2, 1, 1, 1, 0, 1)); }
        if (depth == layers) { generators.Add(new(0, 1, 2, 1, 1, 1)); }
        var elements = new List<AxisMap> { new(0, 1, 1, 1, 2, 1) };
        var seen = new HashSet<AxisMap>(elements);
        for (var index = 0; index < elements.Count; index++) {
            foreach (var generator in generators) {
                var composed = elements[index].Then(generator);
                if (seen.Add(composed)) { elements.Add(composed); }
            }
        }
        var images = new int[elements.Count][];
        var names = new string[elements.Count];
        for (var element = 0; element < elements.Count; element++) {
            var map = elements[element];
            names[element] = map.Name;
            var image = new int[coordinates.Count];
            for (var cell = 0; cell < coordinates.Count; cell++) {
                var p = coordinates[cell];
                int[] source = [p.X, p.Y, p.Z];
                var target = new int[3];
                for (var axis = 0; axis < 3; axis++) {
                    var value = source[map[axis]];
                    target[axis] = map.Sign(axis) > 0 ? value : extents[map[axis]] - 1 - value;
                }
                image[cell] = indices[(target[0], target[1], target[2])];
            }
            images[element] = image;
        }
        return (images, names);
    }
}
