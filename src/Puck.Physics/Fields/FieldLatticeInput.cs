using Puck.Maths;
using Puck.State;

namespace Puck.Physics.Fields;

/// <summary>How a compiled transform node writes a field cell.</summary>
public enum FieldWriteOp : byte {
    /// <summary>Replace the cell.</summary>
    Set,
    /// <summary>Add to the cell.</summary>
    Add,
}
/// <summary>A field lattice's footprint and step cadence — the host's own document-derived shape, carrying no
/// document type.</summary>
/// <param name="Origin">The minimum corner, world units.</param>
/// <param name="CellSize">The cubic cell edge, world units.</param>
/// <param name="Width">Cells along +X.</param>
/// <param name="Depth">Cells along +Z.</param>
/// <param name="Layers">Cells along +Y.</param>
/// <param name="StepEveryTicks">Simulation ticks between reaction steps.</param>
public readonly record struct FieldLatticeTopology(FixedVector3 Origin, FixedQ4816 CellSize, int Width, int Depth, int Layers, int StepEveryTicks);
/// <summary>One fixed-point scalar input to a compiled reaction node: a literal, or a live state row the host
/// resolves each step.</summary>
/// <param name="Literal">The literal value. Ignored when <paramref name="State"/> is valid.</param>
/// <param name="State">The state-row handle the host reads each step, or the invalid handle for a literal.</param>
public readonly record struct FieldScalarInput(FixedQ4816 Literal, StateHandle State) {
    /// <summary>Gets whether this input reads a live state row.</summary>
    public bool IsState => State.IsValid;
}
/// <summary>One declared field's compiled envelope.</summary>
/// <param name="Name">The field's stable declared name.</param>
/// <param name="Initial">The value every cell starts at before paint.</param>
/// <param name="Minimum">The inclusive cell-value floor.</param>
/// <param name="Maximum">The inclusive cell-value ceiling.</param>
/// <param name="HeightScale">World units of geometry height per field unit.</param>
/// <param name="IsMedium">Whether this field is a fluid medium — its value times <paramref name="HeightScale"/>
/// over the lattice origin is a free surface bodies sample.</param>
/// <param name="Color">The authored display color, or the state-row binding token, carried through unread by the
/// kernel and reported back at <see cref="FieldLattice.Describe"/> only.</param>
public sealed record FieldDescriptorInput(string Name, FixedQ4816 Initial, FixedQ4816 Minimum, FixedQ4816 Maximum, FixedQ4816 HeightScale, bool IsMedium, string? Color);
/// <summary>One per-cell condition of a <see cref="FieldReactionInput.Transform"/>.</summary>
/// <param name="Field">The field ordinal read at the cell.</param>
/// <param name="Comparison">The comparison.</param>
/// <param name="Value">The scalar compared against.</param>
public sealed record FieldConditionInput(int Field, ActionStateComparison Comparison, FieldScalarInput Value);
/// <summary>One per-cell write of a <see cref="FieldReactionInput.Transform"/>.</summary>
/// <param name="Field">The field ordinal written at the cell.</param>
/// <param name="Op">Set or add.</param>
/// <param name="Value">The constant written or added; the result clamps to the field's range.</param>
public sealed record FieldWriteInput(int Field, FieldWriteOp Op, FieldScalarInput Value);
/// <summary>One reaction node in a compiled field program, in execution order.</summary>
public abstract record FieldReactionInput {
    /// <summary>Moves each cell a fraction of the way toward the mean of its face neighbours.</summary>
    public sealed record Diffuse(int Field, FieldScalarInput Rate) : FieldReactionInput;
    /// <summary>Scales each cell toward zero: <c>v -= v·rate</c>.</summary>
    public sealed record Decay(int Field, FieldScalarInput Rate) : FieldReactionInput;
    /// <summary>Where every condition holds at a cell, applies every write at that cell.</summary>
    public sealed record Transform(IReadOnlyList<FieldConditionInput> When, IReadOnlyList<FieldWriteInput> Then) : FieldReactionInput;
    /// <summary>Every active body whose cell in the keyed state row <paramref name="Tag"/> reads nonzero adds
    /// <paramref name="Amount"/> to <paramref name="Field"/> at the cell it occupies.</summary>
    public sealed record Emit(StateHandle Tag, int Field, FieldScalarInput Amount) : FieldReactionInput;
    /// <summary>Writes 1 or 0 into <paramref name="Row"/>'s cell for every active body, by whether
    /// <paramref name="Field"/> at the body's cell satisfies the comparison.</summary>
    public sealed record Expose(int Field, ActionStateComparison Comparison, FieldScalarInput Value, StateHandle Row) : FieldReactionInput;
    /// <summary>Moves <paramref name="Field"/> downhill, cell to cell, over the combined surface height of
    /// <paramref name="Over"/> plus <paramref name="Field"/>'s own value.</summary>
    /// <param name="Field">The field transported.</param>
    /// <param name="Rate">The fraction of a cell's per-direction share that actually moves each step.</param>
    /// <param name="Over">The other fields forming the terrain basis a downhill direction is measured against.</param>
    /// <param name="SpillRow">The scalar state row an edge cell's outward share accumulates into, or the invalid
    /// handle to treat every lattice edge as a wall.</param>
    public sealed record Flow(int Field, FieldScalarInput Rate, IReadOnlyList<int> Over, StateHandle SpillRow) : FieldReactionInput;
}
/// <summary>One entry of a field's authored paint, in document order.</summary>
/// <param name="Field">The field ordinal painted.</param>
public abstract record FieldFillInput(int Field) {
    /// <summary>Every cell whose XZ centre lies in the rectangle takes <paramref name="Value"/>.</summary>
    public sealed record Rect(int Field, FixedQ4816 Value, FixedQ4816 MinX, FixedQ4816 MinZ, FixedQ4816 MaxX, FixedQ4816 MaxZ) : FieldFillInput(Field);
    /// <summary>Patchy fill: hash-lattice fBm over the cell index decides each cell.</summary>
    public sealed record Noise(int Field, FixedQ4816 Value, int Frequency, FixedQ4816 Threshold, int Octaves, uint Seed) : FieldFillInput(Field);
    /// <summary>Scattered discs: one jittered point per block, admitting every cell within a radius of it.</summary>
    public sealed record Scatter(int Field, FixedQ4816 Value, int Spacing, int Radius, uint Seed) : FieldFillInput(Field);
    /// <summary>The document position of a per-cell draw fill — the host applies the drawn values itself
    /// (<see cref="FieldLattice.FillFromDraw"/>); this marker only preserves the paint order the fills before and
    /// after it apply in.</summary>
    public sealed record DrawMarker(int Field) : FieldFillInput(Field);
}
/// <summary>The complete input a <see cref="FieldLattice"/> is built or reinstalled from — every document value the
/// kernel reads, already resolved to fixed point and state handles. The host builds this once from its own document
/// and compiled program; the kernel parses neither.</summary>
/// <param name="Lattice">The lattice's footprint and cadence.</param>
/// <param name="Fields">The declared fields, in order; a reaction or paint entry names them by ordinal.</param>
/// <param name="Reactions">The per-step reactions, applied in document order each lattice step.</param>
/// <param name="Paint">The initial fills, applied in order over each field's <see cref="FieldDescriptorInput.Initial"/>.</param>
public sealed record FieldLatticeInput(
    FieldLatticeTopology Lattice,
    IReadOnlyList<FieldDescriptorInput> Fields,
    IReadOnlyList<FieldReactionInput> Reactions,
    IReadOnlyList<FieldFillInput> Paint
);
