using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

// A look value (a colour, a material's roughness, a shade program's parameter) changes a program's appearance and leaves
// its field as it was, and the field derivation (validation, the Lipschitz and bounds analyses, the rigid plan, the part
// programs, the packing) is nearly all of a build. Of hands a rebuild the program it replaces, and when the field
// constructor would read the same inputs the new program shares that program's field derivation, its field's words
// included, and packs only its own appearance section (WithAppearance), or is that program itself when the appearance is
// the same too.
//
// A derivation is named by the instruction array its constructor snapshotted and patched, which only a program sharing
// it (a MemberwiseClone in WithAppearance) holds.
//
// Invariant: a program shares another's field derivation only when every input of the field constructor is equal to
// the one that derivation read. Enforced by Of, the one door to the private constructor: it compares through
// FieldInputsMatch, whose parameters are the constructor's own, before it takes the reuse branch. The comparison is
// exact for values (the instructions through a 128-bit hash of every lane, as the mesh store keys a geometry) and by
// identity for objects (a sampled field, a height field), so it may miss a reuse but never grants one the constructor
// would derive differently. A new constructor input is a new parameter of both.
public sealed partial class SdfProgram {
    private static readonly SdfInstancePart[] NoParts = [];

    // Of's record of the inputs the constructor keeps no copy of: the instructions as handed in, before the constructor
    // patched any lane in place (scope scales, table offsets, sampled handles), and the reserved path words. Of writes
    // them, outside the constructor, so they stay out of the mesh bake's identity; an excerpt never records them and is
    // never reused.
    private UInt128 m_instructionsKey;
    private int m_reservedPathWords;

    /// <summary>Returns whether another program shares this program's field derivation: one derived it and the other took
    /// it with a new appearance (<see cref="Of"/>), so their instructions, instances, tables and packed field words are the
    /// same and only the appearance section and the materials differ. A consumer whose product reads the field alone (a
    /// baked mesh, a bake plan, a slot layout) keeps that product across such a program.</summary>
    /// <param name="other">The other program, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="other"/> is this program or shares its field.</returns>
    public bool SharesField(SdfProgram? other) => ((other is not null) && ReferenceEquals(objA: m_instructions, objB: other.m_instructions));

    // Whether the field constructor, handed these inputs, would read exactly what this program's derivation read; the
    // instructions are compared by their key (InstructionsKey), which the caller has read.
    private bool FieldInputsMatch(IReadOnlyList<SdfInstruction> instructions, UInt128 instructionsKey, SdfAppearanceOutline appearance, IReadOnlyList<SdfInstanceRange>? instances, IReadOnlyList<(int InstructionIndex, Vector2[] Vertices)>? convexPolygonProfiles, IReadOnlyList<SdfSweepCurve>? sweepCurves, IReadOnlyList<SdfCompiledPath>? paths, int reservedPathWords, IReadOnlyList<SdfSampledBinding>? sampledBindings, ulong glyphAtlas, float glyphStroke) {
        if (
            (m_appearance is null) ||
            (instructions.Count != m_instructions.Length) ||
            (reservedPathWords != m_reservedPathWords) ||
            (glyphAtlas != GlyphAtlas) ||
            (BitConverter.SingleToUInt32Bits(value: glyphStroke) != BitConverter.SingleToUInt32Bits(value: GlyphStroke)) ||
            !appearance.Matches(other: m_outline)
        ) {
            return false;
        }

        var instanceList = (instances ?? []);
        var curveList = (sweepCurves ?? []);
        var pathList = (paths ?? []);
        var profileList = (convexPolygonProfiles ?? []);
        var sampledList = (sampledBindings ?? []);

        if ((instanceList.Count != m_instances.Length) || (curveList.Count != m_sweepCurves.Length) || (pathList.Count != m_paths.Length) ||
            (profileList.Count != m_convexPolygonProfiles.Length) || (sampledList.Count != m_sampledBindings.Length)) {
            return false;
        }
        for (var index = 0; (index < instanceList.Count); index++) {
            if (!SameInstance(left: instanceList[index], right: m_instances[index])) { return false; }
        }
        for (var index = 0; (index < curveList.Count); index++) {
            if (curveList[index] != m_sweepCurves[index]) { return false; }
        }
        for (var index = 0; (index < pathList.Count); index++) {
            if ((pathList[index].InstructionIndex != m_paths[index].InstructionIndex) || !pathList[index].Edges.SequenceEqual(second: m_paths[index].Edges)) { return false; }
        }
        for (var index = 0; (index < profileList.Count); index++) {
            if ((profileList[index].InstructionIndex != m_convexPolygonProfiles[index].InstructionIndex) || !profileList[index].Vertices.AsSpan().SequenceEqual(other: m_convexPolygonProfiles[index].Vertices)) { return false; }
        }
        for (var index = 0; (index < sampledList.Count); index++) {
            if ((sampledList[index].InstructionIndex != m_sampledBindings[index].InstructionIndex) || !ReferenceEquals(objA: sampledList[index].Field, objB: m_sampledBindings[index].Field)) { return false; }
        }

        return (instructionsKey == m_instructionsKey);
    }
    // Two instance ranges compared by every field, their part and operand lists by content and a height field by identity.
    private static bool SameInstance(in SdfInstanceRange left, in SdfInstanceRange right) =>
        ((left with { Parts = NoParts, Operands = NoParts }) == (right with { Parts = NoParts, Operands = NoParts })) &&
        left.Parts.SequenceEqual(second: right.Parts) &&
        left.Operands.SequenceEqual(second: right.Operands);

    // A 128-bit hash of the instructions' every lane, read bit for bit: two streams hash alike only when the field
    // constructor would read them alike.
    private static UInt128 InstructionsKey(IReadOnlyList<SdfInstruction> instructions) {
        const int Chunk = 256;
        var hasher = new XxHash128();
        Span<SdfInstructionWords> chunk = stackalloc SdfInstructionWords[Chunk];
        var filled = 0;

        for (var index = 0; (index < instructions.Count); index++) {
            chunk[filled++] = new SdfInstructionWords(instruction: instructions[index]);
            if (filled == Chunk) {
                hasher.Append(source: MemoryMarshal.AsBytes(span: chunk));
                filled = 0;
            }
        }
        hasher.Append(source: MemoryMarshal.AsBytes(span: chunk[..filled]));
        return hasher.GetCurrentHashAsUInt128();
    }

    // Returns a program sharing this one's field derivation and field words with another appearance of the same outline,
    // packed into a section of its own as Of packs a fresh program's.
    private SdfProgram WithAppearance(SdfAppearance appearance) {
        var program = (SdfProgram)MemberwiseClone();
        var section = new uint[m_appearanceWordCount];

        appearance.Pack(materialVector: ((int)m_words[ProgramMaterialOffsetLane]), section: section);
        program.m_appearanceWords = section;
        program.m_appearance = appearance;
        program.m_hasGlassShadows = null;

        return program;
    }

    // Whether an appearance is this program's own in every material and screen surface, so the program packs it already.
    private bool HasAppearance(SdfAppearance appearance) =>
        (m_appearance is { } own) && own.Materials.SequenceEqual(second: appearance.Materials) && own.ScreenSurfaces.SequenceEqual(second: appearance.ScreenSurfaces);
}
