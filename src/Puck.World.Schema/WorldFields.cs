using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>
/// The <c>fields</c> document section — a lattice of named fixed-point scalar fields over a region of the world
/// (heat, moisture, fuel, …), the per-cell reactions that evolve them, and the paint that seeds them. A field with a
/// <see cref="WorldFieldRow.HeightScale"/> is geometry: its value raises a solid surface above the lattice origin that
/// bodies stand on and the renderer shows, so a glacier or a forest is field content rather than authored solids.
/// Boot-authored; the live cell values are simulation state beside the population (checkpointed, snapshot-delivered),
/// never document rows.
/// </summary>
/// <param name="Lattice">The lattice's extent, resolution, and step cadence.</param>
/// <param name="Fields">The declared fields, in order; a reaction, paint, or render row names them.</param>
/// <param name="Reactions">The per-step reactions, applied in document order each lattice step.</param>
/// <param name="Paint">The initial fills, applied in order over the fields' <see cref="WorldFieldRow.Initial"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldsSection(
    WorldFieldLatticeDefinition Lattice,
    IReadOnlyList<WorldFieldRow> Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldReaction>? Reactions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldFieldPaint>? Paint = null
);
/// <summary>A lattice's footprint: cubic cells from <paramref name="Origin"/> along +X (<paramref name="Width"/>),
/// +Y (<paramref name="Layers"/>), and +Z (<paramref name="Depth"/>). <paramref name="Layers"/> = 1 is a ground
/// lattice; more layers is a voxel volume and costs proportionally.</summary>
/// <param name="Origin">The minimum corner, world units.</param>
/// <param name="CellSize">The cubic cell edge, world units.</param>
/// <param name="Width">Cells along +X.</param>
/// <param name="Depth">Cells along +Z.</param>
/// <param name="Layers">Cells along +Y.</param>
/// <param name="StepEveryTicks">Simulation ticks between reaction steps.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldLatticeDefinition(
    DocumentVector3 Origin,
    float CellSize,
    int Width,
    int Depth,
    int Layers = 1,
    int StepEveryTicks = 8
);
/// <summary>One field: a scalar per cell clamped to [<paramref name="Min"/>, <paramref name="Max"/>].</summary>
/// <param name="Name">The field's name — unique within the section, dot-free.</param>
/// <param name="Initial">The value every cell starts at before paint.</param>
/// <param name="Min">The least value a cell holds.</param>
/// <param name="Max">The greatest value a cell holds.</param>
/// <param name="HeightScale">World units of solid surface per unit of value above the lattice origin — 0 for a
/// field that is not geometry.</param>
/// <param name="Color">The <c>#RRGGBB</c> the field's surface shades with; required when <paramref name="HeightScale"/>
/// is nonzero.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldRow(
    string Name,
    float Initial = 0f,
    float Min = 0f,
    float Max = 1f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float HeightScale = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null
);
/// <summary>One initial fill: every cell whose XZ centre lies in the rectangle takes <paramref name="Value"/>.</summary>
/// <param name="Field">The field painted.</param>
/// <param name="Value">The value written.</param>
/// <param name="MinX">The rectangle's least X, world units.</param>
/// <param name="MinZ">The rectangle's least Z, world units.</param>
/// <param name="MaxX">The rectangle's greatest X, world units.</param>
/// <param name="MaxZ">The rectangle's greatest Z, world units.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldPaint(string Field, float Value, float MinX, float MinZ, float MaxX, float MaxZ);
/// <summary>One per-cell condition of a <see cref="WorldReaction.Transform"/>.</summary>
/// <param name="Field">The field read at the cell.</param>
/// <param name="Comparison">The comparison.</param>
/// <param name="Value">The constant compared against.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldCondition(string Field, WorldFieldComparison Comparison, float Value);
/// <summary>One per-cell write of a <see cref="WorldReaction.Transform"/>.</summary>
/// <param name="Field">The field written at the cell.</param>
/// <param name="Op">Set or add.</param>
/// <param name="Value">The constant written or added; the result clamps to the field's range.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldWrite(string Field, WorldFieldWriteOp Op, float Value);
/// <summary>One reaction — a local rule every cell (or every tagged body) applies each lattice step. Reactions run
/// in document order; <see cref="Diffuse"/> reads the previous step's values for the whole field, every other kind
/// reads and writes cells in place, row-major.</summary>
[JsonDerivedType(typeof(WorldReaction.Diffuse), typeDiscriminator: "diffuse")]
[JsonDerivedType(typeof(WorldReaction.Decay), typeDiscriminator: "decay")]
[JsonDerivedType(typeof(WorldReaction.Transform), typeDiscriminator: "transform")]
[JsonDerivedType(typeof(WorldReaction.Emit), typeDiscriminator: "emit")]
[JsonDerivedType(typeof(WorldReaction.Expose), typeDiscriminator: "expose")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldReaction {
    /// <summary>Moves each cell a fraction of the way toward the mean of its face neighbours (four on a ground
    /// lattice, six in a volume).</summary>
    /// <param name="Field">The field diffused.</param>
    /// <param name="Rate">The fraction per step, in [0, 1].</param>
    public sealed record Diffuse(string Field, float Rate) : WorldReaction;
    /// <summary>Scales each cell toward zero: <c>v -= v·rate</c>.</summary>
    /// <param name="Field">The field decayed.</param>
    /// <param name="Rate">The fraction per step, in [0, 1].</param>
    public sealed record Decay(string Field, float Rate) : WorldReaction;
    /// <summary>Where every condition holds at a cell, applies every write at that cell — ignition, melting,
    /// evaporation, freezing are rows of this shape.</summary>
    /// <param name="When">The conditions, all of which must hold.</param>
    /// <param name="Then">The writes, applied in order.</param>
    public sealed record Transform(IReadOnlyList<WorldFieldCondition> When, IReadOnlyList<WorldFieldWrite> Then) : WorldReaction;
    /// <summary>Every active body whose cell in the keyed state row <paramref name="Tag"/> reads nonzero adds
    /// <paramref name="Amount"/> to <paramref name="Field"/> at the cell it occupies — a burning body is a heat
    /// source.</summary>
    /// <param name="Tag">The keyed int state row whose nonzero cells name the emitting bodies.</param>
    /// <param name="Field">The field deposited into.</param>
    /// <param name="Amount">The amount per step; the cell clamps to the field's range.</param>
    public sealed record Emit(string Tag, string Field, float Amount) : WorldReaction;
    /// <summary>Writes 1 or 0 into <paramref name="Row"/>'s cell for every active body, by whether
    /// <paramref name="Field"/> at the body's cell satisfies the comparison — a body standing in a burning cell
    /// becomes tagged, and body-level chemistry takes it from there.</summary>
    /// <param name="Field">The field sampled at the body.</param>
    /// <param name="Comparison">The comparison.</param>
    /// <param name="Value">The constant compared against.</param>
    /// <param name="Row">The keyed int state row written, keyed by body index.</param>
    public sealed record Expose(string Field, WorldFieldComparison Comparison, float Value, string Row) : WorldReaction;
}
/// <summary>How a field value compares against a constant.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldFieldComparison>))]
public enum WorldFieldComparison : byte {
    /// <summary>Equal.</summary>
    Equal,
    /// <summary>Not equal.</summary>
    NotEqual,
    /// <summary>Less than.</summary>
    Less,
    /// <summary>Less than or equal.</summary>
    LessOrEqual,
    /// <summary>Greater than.</summary>
    Greater,
    /// <summary>Greater than or equal.</summary>
    GreaterOrEqual,
}
/// <summary>How a transform writes a field cell.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldFieldWriteOp>))]
public enum WorldFieldWriteOp : byte {
    /// <summary>Replace the cell.</summary>
    Set,
    /// <summary>Add to the cell.</summary>
    Add,
}
/// <summary>Capacity constants for a lattice.</summary>
public static class WorldFieldCapacity {
    /// <summary>The most cells a lattice may declare (width × depth × layers). Eight full fields plus the rest of a
    /// primer snapshot must fit the federation wire's 32 MiB frame ceiling.</summary>
    public const int MaxCells = (1 << 18);
    /// <summary>The most fields a section may declare.</summary>
    public const int MaxFields = 8;
    /// <summary>The most cells along any one axis.</summary>
    public const int MaxExtent = 1024;
    /// <summary>The most layers along +Y — one render brick's edge.</summary>
    public const int MaxLayers = 128;
    /// <summary>The most cells of surface a height field may raise above the origin — the render brick's edge less
    /// the voxel below the origin and the one above the top.</summary>
    public const int MaxSurfaceCells = 126;
    /// <summary>The most reactions a section may declare.</summary>
    public const int MaxReactions = 64;
    /// <summary>The most conditions or writes one transform reaction may declare.</summary>
    public const int MaxTransformTerms = 64;
    /// <summary>The most paint rows a section may declare.</summary>
    public const int MaxPaint = 256;
}
