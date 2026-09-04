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
    internal static (int[][] Images, string[] Names) BuildSymmetry(WorldTopologyKind kind, int width, int depth,
        IReadOnlyList<(int X, int Y)> coordinates, Dictionary<(int, int), int> indices) {
        var names = new List<string> { "identity" };
        var maps = new List<Func<(int X, int Y), (int X, int Y)>> { p => p };
        if (kind == WorldTopologyKind.Grid) {
            names.AddRange(["rot180", "mirrorX", "mirrorZ"]);
            maps.AddRange([
                p => (width - 1 - p.X, depth - 1 - p.Y),
                p => (width - 1 - p.X, p.Y),
                p => (p.X, depth - 1 - p.Y),
            ]);
            if (width == depth) {
                names.AddRange(["rot90", "rot270", "mirrorMain", "mirrorAnti"]);
                maps.AddRange([
                    p => (width - 1 - p.Y, p.X),
                    p => (p.Y, width - 1 - p.X),
                    p => (p.Y, p.X),
                    p => (width - 1 - p.Y, width - 1 - p.X),
                ]);
            }
        } else if (kind == WorldTopologyKind.Hex) {
            // Axial (q, r): one sixth turn is (q, r) -> (-r, q + r); the mirror swaps the axes.
            static (int X, int Y) Turn((int X, int Y) p, int sixths) {
                for (var step = 0; step < sixths; step++) { p = (-p.Y, p.X + p.Y); }
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
                maps.Add(p => Turn((p.Y, p.X), turns));
            }
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
}
