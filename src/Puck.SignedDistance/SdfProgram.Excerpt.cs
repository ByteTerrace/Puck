namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>Returns a program of some of this program's instructions, in the order given, each rewritten into the
    /// instructions <paramref name="rewrite"/> answers for it: the field one part of the program draws, such as the
    /// instances a mesh bake group samples, evaluated alone on the CPU. The excerpt is a field and nothing more: it is
    /// derived by the field constructor from this program's <see cref="SdfAppearanceOutline"/> and
    /// <see cref="GlyphAtlas"/>, carries no appearance (reading its <see cref="Appearance"/> throws) and leaves its packed
    /// appearance section unwritten, so evaluating it reaches no appearance code and it is never uploaded. It keeps the
    /// side-table entry (a convex polygon's vertices, a sweep's curve, a path's edges) of each shape it keeps, which
    /// follows the last instruction its source's rewrite answers; it declares no instance, so nothing culls its
    /// field. A masked relief carries its mask rows (<see cref="SdfOp.ReliefRow"/>): unless the excerpt's last
    /// instructions are already those rows, in order, they are emitted before it, each through
    /// <paramref name="rewrite"/>. A row named without the relief that reads it, or a rewrite that drops or repeats a row,
    /// leaves a run the excerpt's program refuses.</summary>
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
        var samples = new List<SdfSampledBinding>();
        var rawShapes = new HashSet<int>();
        // The source of each instruction the excerpt holds, so a relief can tell whether its rows already precede it.
        var sources = new List<int>(capacity: indices.Count);

        void Emit(int index) {
            var before = instructions.Count;

            if (rewrite is null) {
                instructions.Add(item: InstructionRows[index]);
            } else {
                instructions.AddRange(collection: rewrite(arg1: index, arg2: InstructionRows[index]));
            }

            for (var added = before; (added < instructions.Count); added++) { sources.Add(item: index); }
        }

        foreach (var index in indices) {
            ArgumentOutOfRangeException.ThrowIfNegative(value: index, paramName: nameof(indices));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: InstructionRows.Length, paramName: nameof(indices));

            var source = InstructionRows[index];

            if ((SdfFieldOps.StreamOf(value: source.Op) == SdfReliefStreamRole.ReliefOwner) && (source.Material > 0u)) {
                var rows = ((int)source.Material);
                var carried = (sources.Count >= rows);

                for (var row = 0; (carried && (row < rows)); row++) {
                    carried = (sources[((sources.Count - rows) + row)] == ((index - rows) + row));
                }

                for (var row = (index - rows); (!carried && (row < index)); row++) {
                    Emit(index: row);
                }
            }

            Emit(index: index);

            var at = (instructions.Count - 1);

            if ((source.Op != SdfOp.ShapeBlend) || (at < 0)) { continue; }
            if (ShapeStepScaleAt(instructionIndex: index) == 1f) { _ = rawShapes.Add(item: at); }
            foreach (var entry in m_sampledBindings) {
                if (entry.InstructionIndex == index) { samples.Add(entry with { InstructionIndex = at }); }
            }
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
            appearance: m_outline,
            convexPolygonProfiles: polygons,
            glyphAtlas: GlyphAtlas,
            glyphStroke: GlyphStroke,
            instances: null,
            instructions: instructions,
            paths: paths,
            rawExcerptShapes: rawShapes,
            reservedPathWords: 0,
            sampledBindings: samples,
            sweepCurves: sweeps
        );
    }
}
