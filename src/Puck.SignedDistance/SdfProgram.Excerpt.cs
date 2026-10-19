namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>Returns a program of some of this program's instructions, in the order given, each rewritten into the
    /// instructions <paramref name="rewrite"/> answers for it: the field one part of the program draws, such as the
    /// instances a mesh bake group samples, evaluated alone. The excerpt keeps every material and screen surface, and the
    /// side-table entry (a convex polygon's vertices, a sweep's curve, a path's edges) of each shape it keeps, which
    /// follows the last instruction its source's rewrite answers; it declares no instance, so nothing culls its
    /// field.</summary>
    /// <param name="indices">The indices in <see cref="Instructions"/> of the instructions the excerpt holds.</param>
    /// <param name="rewrite">The instructions that stand for an instruction, given its index and the instruction; its own
    /// instruction alone when <see langword="null"/>. A shape carrying a side-table entry ends its own answer.</param>
    /// <returns>The excerpt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="indices"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index names no instruction.</exception>
    /// <exception cref="ArgumentException">The excerpt is no valid program, such as a field scope it opens and never
    /// closes.</exception>
    public SdfProgram Excerpt(IReadOnlyList<int> indices, Func<int, SdfInstruction, IEnumerable<SdfInstruction>>? rewrite = null) {
        ArgumentNullException.ThrowIfNull(argument: indices);

        var instructions = new List<SdfInstruction>(capacity: indices.Count);
        var polygons = new List<(int InstructionIndex, System.Numerics.Vector2[] Vertices)>();
        var sweeps = new List<SdfSweepCurve>();
        var paths = new List<SdfCompiledPath>();

        foreach (var index in indices) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: index, paramName: nameof(indices));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: m_instructions.Length, paramName: nameof(indices));

            var source = m_instructions[index];

            if (rewrite is null) {
                instructions.Add(item: source);
            } else {
                instructions.AddRange(collection: rewrite(arg1: index, arg2: source));
            }

            var at = (instructions.Count - 1);

            if ((source.Op != SdfOp.ShapeBlend) || (at < 0)) { continue; }
            if (TryFindConvexPolygonVertices(convexPolygonProfiles: m_convexPolygonProfiles, instructionIndex: index, vertices: out var vertices)) {
                polygons.Add(item: (at, vertices));
            }
            foreach (var sweep in m_sweepCurves) {
                if (sweep.InstructionIndex == index) { sweeps.Add(item: (sweep with { InstructionIndex = at })); }
            }
            foreach (var path in m_paths) {
                if (path.InstructionIndex == index) { paths.Add(item: (path with { InstructionIndex = at })); }
            }
        }

        return new SdfProgram(
            convexPolygonProfiles: polygons,
            instructions: instructions,
            materials: m_materialsView,
            paths: paths,
            screenSurfaces: m_screenSurfaces,
            sweepCurves: sweeps
        );
    }
}
