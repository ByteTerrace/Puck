using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // Attached by Of once the field constructor has returned, so it is null for the whole field derivation.
    private SdfAppearance? m_appearance;
    private bool? m_hasGlass;

    /// <summary>Returns the program of a field joined to an appearance: the field is derived from its own inputs and
    /// the appearance's <see cref="SdfAppearance.Outline"/> alone, then the appearance packs its materials and paints into
    /// the stream's tail, after every table the field interpreter reads. The field constructor is the root of the mesh
    /// bake's identity (<c>SdfMeshBakeClosure</c>) and takes no appearance value, so a change to a material, a paint, a
    /// screen surface or their packing leaves every stored mesh standing.</summary>
    /// <param name="instructions">The instructions to execute for each field evaluation.</param>
    /// <param name="appearance">The materials, paints and screen surfaces the instructions name.</param>
    /// <param name="instances">Optional contiguous instruction ranges with world-space bounds for instance culling.</param>
    /// <param name="convexPolygonProfiles">Every <see cref="SdfShapeType.ConvexPolygon"/> instruction's own vertex
    /// list, keyed by that instruction's index in <paramref name="instructions"/> — exactly one entry per such
    /// instruction, no two claiming the same index. <see langword="null"/> (the default) is empty, valid only when
    /// <paramref name="instructions"/> carries no <see cref="SdfShapeType.ConvexPolygon"/> shape.</param>
    /// <param name="sweepCurves">Every <see cref="SdfShapeType.Sweep"/> instruction's own control points and radius
    /// endpoints, keyed by that instruction's <see cref="SdfSweepCurve.InstructionIndex"/> — exactly one entry per
    /// such instruction, no two claiming the same index. <see langword="null"/> (the default) is empty, valid only
    /// when <paramref name="instructions"/> carries no <see cref="SdfShapeType.Sweep"/> shape.</param>
    /// <param name="paths">One bounded edge table per Path instruction; snapshotted before validation.</param>
    /// <param name="reservedPathWords">Extra uint words reserved by a capacity probe for future path tables; never executed.</param>
    /// <param name="sampledBindings">Each sampled instruction's field (<see cref="SampledFields"/>).</param>
    /// <returns>The program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instructions"/> or <paramref name="appearance"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An instruction's opcode, shape, blend, or material lane is outside the domain
    /// the packed format carries, or names a material or screen the appearance does not declare; an operand lane that
    /// is not a reinterpreted integer field is not finite; field scopes are unbalanced, empty, nested beyond the
    /// supported depth, or cross an instance boundary; an instance range does not lie within the instruction stream; or
    /// two instance ranges claim one instruction.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An instance bound is not finite and non-negative, or a trapezoid's
    /// profile slant vanishes in the deterministic field's representation.</exception>
    public static SdfProgram Of(IReadOnlyList<SdfInstruction> instructions, SdfAppearance appearance, IReadOnlyList<SdfInstanceRange>? instances = null, IReadOnlyList<(int InstructionIndex, Vector2[] Vertices)>? convexPolygonProfiles = null, IReadOnlyList<SdfSweepCurve>? sweepCurves = null, IReadOnlyList<SdfCompiledPath>? paths = null, int reservedPathWords = 0, IReadOnlyList<SdfSampledBinding>? sampledBindings = null) {
        ArgumentNullException.ThrowIfNull(argument: appearance);

        var program = new SdfProgram(
            appearance: appearance.Outline,
            convexPolygonProfiles: convexPolygonProfiles,
            instances: instances,
            instructions: instructions,
            paths: paths,
            reservedPathWords: reservedPathWords,
            sampledBindings: sampledBindings,
            sweepCurves: sweepCurves
        );

        appearance.Pack(materialVector: ((int)program.m_words[ProgramMaterialOffsetLane]), words: program.m_words);
        program.m_appearance = appearance;

        return program;
    }

    /// <summary>Gets the program's appearance: its materials, paints and screen surfaces.</summary>
    /// <exception cref="InvalidOperationException">The field derivation reads it, which it never may.</exception>
    public SdfAppearance Appearance => (m_appearance ?? throw new InvalidOperationException(message: "A program's field is derived without its appearance; only SdfProgram.Of attaches it, after the field constructor returns."));

    /// <summary>Gets whether any drawn shape of the program carries a glass material (<see cref="SdfMaterial.IsGlass"/>):
    /// a view of such a program runs the glass passes (<c>SdfWorldPackage.WithGlass</c>), and a view of any other pays
    /// nothing for them. Measured once, on first read.</summary>
    public bool HasGlass => (m_hasGlass ??= MeasureGlass());

    /// <summary>Returns whether a material id names a glass material of the program's palette; a screen sentinel or an
    /// id past the palette is opaque.</summary>
    /// <param name="material">The material id.</param>
    /// <returns><see langword="true"/> for a glass material.</returns>
    public bool IsGlassMaterial(int material) => m_outline.IsGlass(material: material);
    /// <summary>Returns whether every drawn shape of an instruction range carries a glass material, and at least one
    /// shape is drawn: the shapes bake into a glass group. A detail shape counts as drawn; a range with no shape is
    /// opaque.</summary>
    /// <param name="first">The range's first instruction.</param>
    /// <param name="end">The instruction past the range's last.</param>
    /// <returns><see langword="true"/> for a range of glass shapes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range lies outside the instruction stream.</exception>
    public bool IsGlassRange(int first, int end) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: first);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: end, other: m_instructions.Length);

        var shapes = 0;

        for (var index = first; (index < end); index++) {
            var instruction = m_instructions[index];

            if (instruction.Op != SdfOp.ShapeBlend) { continue; }
            if (!IsGlassMaterial(material: unchecked((int)instruction.Material))) { return false; }
            shapes++;
        }

        return (shapes > 0);
    }

    private bool MeasureGlass() {
        foreach (var instruction in m_instructions) {
            if ((instruction.Op == SdfOp.ShapeBlend) && IsGlassMaterial(material: unchecked((int)instruction.Material))) {
                return true;
            }
        }

        return false;
    }
}
