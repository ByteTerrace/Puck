using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>
/// The runtime lattice composite compiled from the state section (see <c>Compile</c>) — a lattice of named fixed-point scalar fields over a region of the world
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
public sealed record WorldFieldsSection(
    WorldFieldLatticeDefinition Lattice,
    IReadOnlyList<WorldFieldRow> Fields,
    IReadOnlyList<WorldReaction>? Reactions = null,
    IReadOnlyList<WorldLatticeFill>? Paint = null
) {
    /// <summary>Determines without allocation whether an authored state section still compiles to a cached
    /// composite.</summary>
    internal static bool MatchesState(WorldFieldsSection? composite, WorldStateSection? state) {
        if (WorldTopologyCompilation.FindPhysical(state) is not { } topology) {
            return (composite is null);
        }

        if (
            (composite is null) ||
            (composite.Lattice.Origin != topology.Origin) ||
            (composite.Lattice.CellSize != topology.CellSize) ||
            (composite.Lattice.Width != topology.Width) ||
            (composite.Lattice.Depth != topology.Depth) ||
            (composite.Lattice.Layers != topology.Layers) ||
            (composite.Lattice.StepEveryTicks != topology.StepEveryTicks) ||
            !ReactionsEqual(left: composite.Reactions, right: topology.Reactions)
        ) {
            return false;
        }

        var fieldIndex = 0;
        var paintIndex = 0;
        var paint = (composite.Paint ?? []);
        var worldRows = (state?.World ?? []);

        for (var rowIndex = 0; (rowIndex < worldRows.Count); rowIndex++) {
            if (worldRows[rowIndex] is not { } row) {
                return false;
            }

            if (row.Field is not { } trait) {
                continue;
            }

            if (((uint)fieldIndex) >= ((uint)composite.Fields.Count)) {
                return false;
            }

            var field = composite.Fields[fieldIndex++];

            if (
                !string.Equals(a: field.Name, b: row.Name, comparisonType: StringComparison.Ordinal) ||
                (field.Initial != trait.Initial) ||
                (field.Min != trait.Min) ||
                (field.Max != trait.Max) ||
                (field.HeightScale != trait.HeightScale) ||
                !string.Equals(a: field.Color, b: trait.Color, comparisonType: StringComparison.Ordinal) ||
                (field.Medium != (trait.Medium is not null))
            ) {
                return false;
            }

            var fills = (trait.Paint ?? []);

            for (var fillIndex = 0; (fillIndex < fills.Count); fillIndex++) {
                var fill = fills[fillIndex];

                if (
                    (((uint)paintIndex) >= ((uint)paint.Count)) ||
                    (fill is null) ||
                    !string.Equals(a: paint[paintIndex].Field, b: row.Name, comparisonType: StringComparison.Ordinal) ||
                    !FillEqual(authored: fill, compiled: paint[paintIndex])
                ) {
                    return false;
                }

                paintIndex++;
            }
        }

        return (
            (fieldIndex == composite.Fields.Count) &&
            (paintIndex == paint.Count)
        );
    }
    /// <summary>Determines whether two compiled composites carry the same runtime field inputs, including paint.</summary>
    internal bool HasSameCompilation(WorldFieldsSection other) => (
        (Lattice == other.Lattice) &&
        Fields.SequenceEqual(second: other.Fields) &&
        ReactionsEqual(left: Reactions, right: other.Reactions) &&
        (Paint ?? []).SequenceEqual(second: (other.Paint ?? []))
    );
    /// <summary>Determines whether two compiled composites lower to the same reaction program. Paint and display
    /// metadata remain inputs to the companion composite, not to reaction scheduling.</summary>
    internal bool HasSameProgram(WorldFieldsSection other) => (
        (Lattice.Width == other.Lattice.Width) &&
        (Lattice.Depth == other.Lattice.Depth) &&
        (Lattice.Layers == other.Lattice.Layers) &&
        ProgramFieldsEqual(left: Fields, right: other.Fields) &&
        ReactionsEqual(left: Reactions, right: other.Reactions)
    );

    /// <summary>Compiles the state section's lattice topology and lattice-shaped rows into the runtime composite, or
    /// <see langword="null"/> when the section declares no topology. Row order is state-section declaration order --
    /// the order every wire index, checkpoint entry, and render channel keys off. Reaction collections are
    /// snapshotted so mutating a caller-owned list cannot rewrite an already compiled composite.</summary>
    /// <param name="state">The state section.</param>
    /// <returns>The composite, or <see langword="null"/>.</returns>
    public static WorldFieldsSection? Compile(WorldStateSection? state) {
        if (WorldTopologyCompilation.FindPhysical(state) is not { } topology) {
            return null;
        }

        var rows = new List<WorldFieldRow>();
        var paint = new List<WorldLatticeFill>();

        foreach (var row in (state?.World ?? [])) {
            if (row.Field is not { } trait) {
                continue;
            }

            rows.Add(item: new WorldFieldRow(
                Color: trait.Color,
                HeightScale: trait.HeightScale,
                Initial: trait.Initial,
                Max: trait.Max,
                Medium: (trait.Medium is not null),
                Min: trait.Min,
                Name: row.Name.Value
            ));

            foreach (var fill in (trait.Paint ?? [])) {
                paint.Add(item: fill with { Field = row.Name.Value });
            }
        }

        return new WorldFieldsSection(
            Fields: rows,
            Lattice: new WorldFieldLatticeDefinition(
                CellSize: topology.CellSize,
                Depth: topology.Depth,
                Layers: topology.Layers,
                Origin: topology.Origin,
                StepEveryTicks: topology.StepEveryTicks,
                Width: topology.Width
            ),
            Paint: paint,
            Reactions: (topology.Reactions ?? []).Select(selector: static reaction => reaction switch {
                WorldReaction.Transform transform => transform with {
                    When = [.. (transform.When ?? [])],
                    Then = [.. (transform.Then ?? [])],
                },
                WorldReaction.Flow flow => flow with {
                    Over = [.. (flow.Over ?? [])],
                },
                _ => reaction,
            }).ToArray()
        );
    }
    /// <summary>Decompiles a composite back into the state-section spelling — the inverse of <see cref="Compile"/>,
    /// for the projection reconstruction that must hand a client-side definition the SAME lattice through the state
    /// section the compile reads. <see langword="null"/> in, empty section out.</summary>
    /// <param name="composite">The composite, or <see langword="null"/>.</param>
    /// <returns>A state section carrying the topology and one lattice-shaped row per composite row.</returns>
    public static WorldStateSection ToStateSection(WorldFieldsSection? composite) {
        if (composite is null) {
            return new WorldStateSection(World: []);
        }

        var rows = new List<WorldStateRow>();

        foreach (var row in composite.Fields) {
            rows.Add(item: new WorldStateRow(
                Kind: CellKind.Fixed,
                Domain: new WorldStateDomain.CellsOf(Topology: DefaultTopologyName),
                Field: new WorldStateFieldTrait(
                    Color: row.Color,
                    HeightScale: row.HeightScale,
                    Initial: row.Initial,
                    Max: row.Max,
                    Medium: (row.Medium ? new WorldLatticeMedium() : null),
                    Min: row.Min,
                    Paint: null
                ),
                Name: WorldCellName.Parse(candidate: row.Name)
            ));
        }

        return new WorldStateSection(
            Lattices: [new WorldStateLatticeTopology(
                CellSize: composite.Lattice.CellSize,
                Depth: composite.Lattice.Depth,
                Layers: composite.Lattice.Layers,
                Name: DefaultTopologyName,
                Origin: composite.Lattice.Origin,
                Reactions: composite.Reactions,
                StepEveryTicks: composite.Lattice.StepEveryTicks,
                Width: composite.Lattice.Width
            )],
            World: rows
        );
    }

    /// <summary>The topology name a decompiled projection lattice carries — one lattice exists today, so the
    /// round-trip needs no authored name.</summary>
    public const string DefaultTopologyName = "world";

    private static bool ReactionsEqual(IReadOnlyList<WorldReaction>? left, IReadOnlyList<WorldReaction>? right) {
        var leftRows = (left ?? []);
        var rightRows = (right ?? []);

        if (leftRows.Count != rightRows.Count) {
            return false;
        }

        for (var index = 0; (index < leftRows.Count); index++) {
            if (!ReactionEqual(left: leftRows[index], right: rightRows[index])) {
                return false;
            }
        }

        return true;
    }
    private static bool ReactionEqual(WorldReaction left, WorldReaction right) => (left, right) switch {
        (WorldReaction.Diffuse a, WorldReaction.Diffuse b) => (a == b),
        (WorldReaction.Decay a, WorldReaction.Decay b) => (a == b),
        (WorldReaction.Emit a, WorldReaction.Emit b) => (a == b),
        (WorldReaction.Expose a, WorldReaction.Expose b) => (a == b),
        (WorldReaction.Transform a, WorldReaction.Transform b) => (
            (a.When ?? []).SequenceEqual(second: (b.When ?? [])) &&
            (a.Then ?? []).SequenceEqual(second: (b.Then ?? []))
        ),
        (WorldReaction.Flow a, WorldReaction.Flow b) => (
            string.Equals(a: a.Field, b: b.Field, comparisonType: StringComparison.Ordinal) &&
            (a.Rate == b.Rate) &&
            (a.Over ?? []).SequenceEqual(second: (b.Over ?? [])) &&
            string.Equals(a: a.SpillRow, b: b.SpillRow, comparisonType: StringComparison.Ordinal)
        ),
        _ => false,
    };
    private static bool FillEqual(WorldLatticeFill authored, WorldLatticeFill compiled) => (authored, compiled) switch {
        (WorldLatticeFill.Rect a, WorldLatticeFill.Rect b) => (
            (a.Value == b.Value) &&
            (a.MinX == b.MinX) &&
            (a.MinZ == b.MinZ) &&
            (a.MaxX == b.MaxX) &&
            (a.MaxZ == b.MaxZ)
        ),
        (WorldLatticeFill.Noise a, WorldLatticeFill.Noise b) => (
            (a.Value == b.Value) &&
            (a.Frequency == b.Frequency) &&
            (a.Threshold == b.Threshold) &&
            (a.Octaves == b.Octaves) &&
            (a.Seed == b.Seed)
        ),
        (WorldLatticeFill.Scatter a, WorldLatticeFill.Scatter b) => (
            (a.Value == b.Value) &&
            (a.Spacing == b.Spacing) &&
            (a.Radius == b.Radius) &&
            (a.Seed == b.Seed)
        ),
        _ => false,
    };
    private static bool ProgramFieldsEqual(IReadOnlyList<WorldFieldRow> left, IReadOnlyList<WorldFieldRow> right) {
        if (left.Count != right.Count) {
            return false;
        }

        for (var index = 0; (index < left.Count); index++) {
            var a = left[index];
            var b = right[index];

            if (
                !string.Equals(a: a.Name, b: b.Name, comparisonType: StringComparison.Ordinal) ||
                (a.Initial != b.Initial) ||
                (a.Min != b.Min) ||
                (a.Max != b.Max) ||
                (a.HeightScale != b.HeightScale) ||
                (a.Medium != b.Medium)
            ) {
                return false;
            }
        }

        return true;
    }
}
/// <summary>One <c>state.lattices</c> topology -- the footprint, cadence, and reactions every lattice-shaped state
/// row referencing it shares. At most one physical field topology and bounded discrete topologies may coexist.</summary>
/// <param name="Name">The topology's name -- what a row's <c>lattice.topology</c> references.</param>
/// <param name="Origin">The minimum corner, world units.</param>
/// <param name="CellSize">The cubic cell edge, world units.</param>
/// <param name="Width">Cells along +X.</param>
/// <param name="Depth">Cells along +Z.</param>
/// <param name="Layers">Cells along +Y -- 1 is a ground lattice.</param>
/// <param name="StepEveryTicks">Simulation ticks between reaction steps.</param>
/// <param name="Reactions">The per-step reactions over a physical field topology, applied in document order.</param>
/// <param name="Kind">Physical fields or a discrete grid, ring, or axial hexagon.</param>
/// <param name="Wrap">Wrapped axes for a discrete grid; rings always wrap.</param>
/// <param name="Radius">The axial hexagon radius; zero for other kinds.</param>
/// <param name="Band">For a <see cref="WorldTopologyKind.Grid"/>, the vertical half-extent about the origin's Y a
/// position must lie within to resolve to a cell (<c>cellOf</c>); 0 resolves any height, so a piece on the floor
/// beneath a table still reads as on its square.</param>
/// <param name="LayerHeight">For a <see cref="WorldTopologyKind.Box"/>, the world-space height of one layer, so a
/// position's Y above the origin resolves to a layer the way X and Z resolve to a column; positive, and only on a
/// box.</param>
/// <param name="Directions">The topology's own direction vocabulary — its neighbour steps and the compass/element
/// names a rule, <c>$board:</c>/<c>$match:</c> token, or leaper reach spells them with. <see langword="null"/> (the
/// unauthored default) compiles to exactly the fixed set every <see cref="WorldTopologyKind"/> carried before this
/// field existed — Grid's eight compass points, Hex's six, Box's 26, Ring's forward/backward — so no shipped world's
/// behavior moves. An authored list replaces that default WHOLESALE: it becomes the topology's only directions, its
/// only compass names, and the only vocabulary <see cref="CompiledWorldTopology.Direction"/> resolves — the seam a
/// 4-connected grid (orthogonal steps only) or a custom leaper reach declares without inventing a parallel
/// mechanism.</param>
/// <param name="ElementAliases">Friendlier names for this topology's point-group elements (<c>"rot90"</c> for
/// whatever signed-axis permutation a square grid's quarter turn spells) — <see cref="CompiledWorldTopology.Element"/>
/// resolves an alias alongside the canonical name; <see cref="CompiledWorldTopology.ElementName"/> always answers
/// the canonical spelling.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateLatticeTopology(
    string Name,
    DocumentVector3 Origin,
    float CellSize,
    int Width,
    int Depth,
    int Layers = 1,
    int StepEveryTicks = 8,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldReaction>? Reactions = null,
    WorldTopologyKind Kind = WorldTopologyKind.Field,
    WorldTopologyWrap Wrap = WorldTopologyWrap.None,
    int Radius = 0,
    float Band = 0f,
    float LayerHeight = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldTopologyDirection>? Directions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldTopologyElementAlias>? ElementAliases = null
);
/// <summary>One authored direction of a discrete <see cref="WorldStateLatticeTopology"/>: the (X, Y, Z) cell step a
/// neighbour walk, ray, or leaper offset takes, and the case-sensitive token a rule or <c>$board:</c>/<c>$match:</c>
/// channel names it by. <paramref name="X"/>/<paramref name="Y"/> are the topology's own planar axes (a Grid's
/// column/row, a Hex's q/r); <paramref name="Z"/> is a Box's layer step and must be zero on every other kind.</summary>
/// <param name="Name">The direction's token — dot-free, distinct within the topology, matched case-sensitively.</param>
/// <param name="X">The signed planar column step.</param>
/// <param name="Y">The signed planar row step.</param>
/// <param name="Z">The signed layer step; zero except on a <see cref="WorldTopologyKind.Box"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldTopologyDirection(string Name, int X, int Y, int Z = 0);
/// <summary>A friendlier name for one point-group element, resolved by <see cref="CompiledWorldTopology.Element"/>
/// alongside its canonical signed-axis spelling (<see cref="CompiledWorldTopology.ElementName"/> always answers the
/// canonical form).</summary>
/// <param name="Name">The alias token a rule or console verb may use instead of <paramref name="Element"/>.</param>
/// <param name="Element">The canonical element name (a <see cref="CompiledWorldTopology.ElementName"/> value) this
/// alias resolves to.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldTopologyElementAlias(string Name, string Element);
/// <summary>A state row's <c>field</c> trait — carried only by a row whose <see cref="WorldStateRow.Domain"/> is
/// <see cref="WorldStateDomain.CellsOf"/> over a <c>Field</c>-kind topology: the row holds one
/// <see cref="CellKind.Fixed"/> scalar per cell of that topology instead of sparse board cells. Values are authored
/// DECIMAL (like every lattice quantity), not raw Q48.16 bits; a field row refuses slot/keyed members (<c>cells</c>,
/// <c>capacity</c>, <c>advance</c>, <c>dynamics</c>, <c>draw</c> -- per-cell draws are the spatial-draw seam, refused
/// until it lands).</summary>
/// <param name="Initial">The value every cell starts at before paint.</param>
/// <param name="Min">The least value a cell holds.</param>
/// <param name="Max">The greatest value a cell holds.</param>
/// <param name="HeightScale">World units of solid surface per unit of value above the lattice origin -- 0 for a row
/// that is not geometry.</param>
/// <param name="Color">The color the row's surface shades with — a <c>#RRGGBB</c> literal or a
/// <c>state.&lt;row&gt;[.&lt;key&gt;]</c> Text-cell binding (<see cref="WorldColor"/>'s shared grammar, resolved live
/// at emit); required when <paramref name="HeightScale"/> is nonzero.</param>
/// <param name="Paint">The initial fills, applied in order over <paramref name="Initial"/>.</param>
/// <param name="Medium">Marks this field a fluid MEDIUM, or <see langword="null"/> for an ordinary field. A medium
/// field's value times <paramref name="HeightScale"/> over the lattice origin is a free surface every active body
/// samples each tick at its coupled cell — the same body-coupling ceiling <see cref="WorldReaction.Emit"/>/
/// <see cref="WorldReaction.Expose"/> resolve against — refused unless <paramref name="HeightScale"/> is greater
/// than zero (a surface-less medium is meaningless).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateFieldTrait(
    float Initial = 0f,
    float Min = 0f,
    float Max = 1f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float HeightScale = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldLatticeFill>? Paint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldLatticeMedium? Medium = null
);
/// <summary>Marks a lattice-shaped field as a fluid medium (see <see cref="WorldStateFieldTrait.Medium"/>). No
/// required members today — the growth seam a future medium trait (density, drag) widens without moving what
/// already exists, the same trailing-member shape as every other optional-facet record in this document.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldLatticeMedium;
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
/// <param name="Color">The color the field's surface shades with — a <c>#RRGGBB</c> literal or a
/// <c>state.&lt;row&gt;[.&lt;key&gt;]</c> Text-cell binding; required when <paramref name="HeightScale"/>
/// is nonzero.</param>
/// <param name="Medium">Whether this field is a fluid medium (see <see cref="WorldStateFieldTrait.Medium"/>).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldRow(
    string Name,
    float Initial = 0f,
    float Min = 0f,
    float Max = 1f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] float HeightScale = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Medium = false
);
/// <summary>One initial fill of a lattice row, applied in order over the row's <c>initial</c>. Every kind decides
/// per CELL with integer hashes and Q48.16 arithmetic only, so a fill is bit-identical on every machine and backend
/// -- lattice values are simulation state. The <c>Field</c> member is compile-stamped from the carrying row (see
/// <see cref="WorldFieldsSection.Compile"/>) and omitted in the document.</summary>
[JsonDerivedType(typeof(WorldLatticeFill.Rect), typeDiscriminator: "rect")]
[JsonDerivedType(typeof(WorldLatticeFill.Noise), typeDiscriminator: "noise")]
[JsonDerivedType(typeof(WorldLatticeFill.Scatter), typeDiscriminator: "scatter")]
[JsonDerivedType(typeof(WorldLatticeFill.Draw), typeDiscriminator: "draw")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldLatticeFill {
    /// <summary>Gets the filled row's name -- compile-stamped, never authored inside a trait.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Field { get; init; } = string.Empty;

    /// <summary>Every cell whose XZ centre lies in the rectangle takes <paramref name="Value"/>.</summary>
    /// <param name="Value">The value written.</param>
    /// <param name="MinX">The rectangle's least X, world units.</param>
    /// <param name="MinZ">The rectangle's least Z, world units.</param>
    /// <param name="MaxX">The rectangle's greatest X, world units.</param>
    /// <param name="MaxZ">The rectangle's greatest Z, world units.</param>
    public sealed record Rect(float Value, float MinX, float MinZ, float MaxX, float MaxZ) : WorldLatticeFill;
    /// <summary>Patchy fill: fixed-point hash-lattice fBm over the CELL INDEX decides each cell. Where the octave
    /// sum <c>n</c> (in [0, 1]) reaches <paramref name="Threshold"/>, the cell takes
    /// <c>Value · (n − Threshold) / (1 − Threshold)</c> -- a smooth patch interior rising to the full value. The
    /// hash folds the fill's <paramref name="Seed"/> with the world seed (the generation section's reroll lever),
    /// and every blend runs in Q48.16, so two boots of one document at one seed are bit-identical.</summary>
    /// <param name="Value">The peak value written inside a patch.</param>
    /// <param name="Frequency">Lattice cells per noise cell reciprocal -- noise-cell edge in CELLS (e.g. 8 = one
    /// noise cell spans 8 lattice cells). At least 1.</param>
    /// <param name="Threshold">The patch admission level in [0, 1); higher = sparser patches.</param>
    /// <param name="Octaves">Octave count, 1..4.</param>
    /// <param name="Seed">The hash seed, folded with the world seed.</param>
    public sealed record Noise(float Value, int Frequency, float Threshold = 0.5f, int Octaves = 3, uint Seed = 0u) : WorldLatticeFill;
    /// <summary>Scattered discs: one jittered point per <paramref name="Spacing"/>-cell block (integer-hash offset),
    /// and every cell within <paramref name="Radius"/> cells of a point takes <paramref name="Value"/> -- the CPU
    /// boot-fill sibling of the renderer's CellJitter scatter.</summary>
    /// <param name="Value">The value written inside each disc.</param>
    /// <param name="Spacing">The scatter block edge in CELLS (at least 2).</param>
    /// <param name="Radius">The disc radius in CELLS (at least 1; must leave the disc inside its block's jitter
    /// envelope, refused otherwise).</param>
    /// <param name="Seed">The hash seed, folded with the world seed.</param>
    public sealed record Scatter(float Value, int Spacing, int Radius = 1, uint Seed = 0u) : WorldLatticeFill;
    /// <summary>Every cell drawn from a numeric authored-randomness source — the per-cell lattice draw. The fill is
    /// one pass of the row's own draw stream (seeded through the same ladder as a state-row site, under the row's
    /// <c>state.&lt;row&gt;</c> descriptor): cell <c>k</c>, in cell-index order, takes the sample a site at
    /// <c>drawCursor + k</c> would draw, so a weighted source under an exhausting mode draws its units across the
    /// field and restarts as it goes. The row's <see cref="WorldStateRow.DrawCursor"/>/<see cref="WorldStateRow.DrawnMasks"/>
    /// name the pass currently painted; <c>generate &lt;row&gt;</c> advances them one whole pass and repaints. Draw
    /// occupies its authored paint position (overwriting earlier fills and preceding later fills); boot, rebuild,
    /// load/reset, and a draw-rewinding undo repaint the pass the document names.</summary>
    /// <param name="Source">A declared <c>generators</c> row, or <see langword="null"/> when <paramref name="Generator"/> is inlined.</param>
    /// <param name="Generator">An inline numeric source, or <see langword="null"/> when <paramref name="Source"/> is named.</param>
    public sealed record Draw([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCellName? Source = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGenerator? Generator = null) : WorldLatticeFill;

    /// <summary>Returns the one <see cref="Draw"/> fill a field trait's paint carries, or <see langword="null"/>.</summary>
    /// <param name="trait">The trait, or <see langword="null"/> for a row that is not a field.</param>
    public static Draw? FindDraw(WorldStateFieldTrait? trait) {
        foreach (var fill in (trait?.Paint ?? [])) {
            if (fill is Draw draw) {
                return draw;
            }
        }

        return null;
    }
}
/// <summary>A reaction scalar: a literal number, or a reference to a scalar <c>fixed</c>-kind state row's slot cell
/// read at each lattice step -- how a rule-driven row (a season, a weather intensity) modulates reaction chemistry
/// live without a new reaction kind. In JSON a plain number is the literal and <c>{"row": "name"}</c> the
/// reference; an unwritten referenced slot reads 0, so a season-gated reaction is inert until something writes the
/// row.</summary>
/// <param name="Literal">The literal value, or <see langword="null"/> when <paramref name="Row"/> is set.</param>
/// <param name="Row">The referenced scalar state row, or <see langword="null"/> for a literal.</param>
[JsonConverter(typeof(WorldLatticeScalarJsonConverter))]
public readonly record struct WorldLatticeScalar(float? Literal = null, string? Row = null) {
    /// <summary>Creates a literal scalar.</summary>
    /// <param name="value">The literal.</param>
    public static implicit operator WorldLatticeScalar(float value) => new(Literal: value);
}
/// <summary>Reads a JSON number as a literal <see cref="WorldLatticeScalar"/> and <c>{"row": "name"}</c> as a row
/// reference; writes the same spellings back.</summary>
public sealed class WorldLatticeScalarJsonConverter : System.Text.Json.Serialization.JsonConverter<WorldLatticeScalar> {
    /// <inheritdoc/>
    public override WorldLatticeScalar Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number) {
            return new WorldLatticeScalar(Literal: reader.GetSingle());
        }

        if (reader.TokenType != System.Text.Json.JsonTokenType.StartObject) {
            throw new System.Text.Json.JsonException(message: "a reaction scalar is a number or {\"row\": \"name\"}.");
        }

        string? row = null;

        while (reader.Read() && (reader.TokenType != System.Text.Json.JsonTokenType.EndObject)) {
            if ((reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) || (reader.GetString() != "row")) {
                throw new System.Text.Json.JsonException(message: "a reaction scalar object carries exactly one member, 'row'.");
            }

            _ = reader.Read();
            row = reader.GetString();
        }

        if (string.IsNullOrEmpty(value: row)) {
            throw new System.Text.Json.JsonException(message: "a reaction scalar's 'row' must name a state row.");
        }

        return new WorldLatticeScalar(Row: row);
    }
    /// <inheritdoc/>
    public override void Write(System.Text.Json.Utf8JsonWriter writer, WorldLatticeScalar value, System.Text.Json.JsonSerializerOptions options) {
        if (value.Row is { } row) {
            writer.WriteStartObject();
            writer.WriteString(propertyName: "row", value: row);
            writer.WriteEndObject();

            return;
        }

        writer.WriteNumberValue(value: (value.Literal ?? 0f));
    }
}
/// <summary>One per-cell condition of a <see cref="WorldReaction.Transform"/>.</summary>
/// <param name="Field">The field read at the cell.</param>
/// <param name="Comparison">The comparison.</param>
/// <param name="Value">The scalar compared against (literal or state-row reference).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldCondition(string Field, WorldFieldComparison Comparison, WorldLatticeScalar Value);
/// <summary>One per-cell write of a <see cref="WorldReaction.Transform"/>.</summary>
/// <param name="Field">The field written at the cell.</param>
/// <param name="Op">Set or add.</param>
/// <param name="Value">The constant written or added; the result clamps to the field's range.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFieldWrite(string Field, WorldFieldWriteOp Op, WorldLatticeScalar Value);
/// <summary>One reaction — a local rule every cell (or every tagged body) applies each lattice step. Reactions run
/// in document order; <see cref="Diffuse"/> reads the previous step's values for the whole field, every other kind
/// reads and writes cells in place, row-major.</summary>
[JsonDerivedType(typeof(WorldReaction.Diffuse), typeDiscriminator: "diffuse")]
[JsonDerivedType(typeof(WorldReaction.Decay), typeDiscriminator: "decay")]
[JsonDerivedType(typeof(WorldReaction.Transform), typeDiscriminator: "transform")]
[JsonDerivedType(typeof(WorldReaction.Emit), typeDiscriminator: "emit")]
[JsonDerivedType(typeof(WorldReaction.Expose), typeDiscriminator: "expose")]
[JsonDerivedType(typeof(WorldReaction.Flow), typeDiscriminator: "flow")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldReaction {
    /// <summary>Moves each cell a fraction of the way toward the mean of its face neighbours (four on a ground
    /// lattice, six in a volume).</summary>
    /// <param name="Field">The field diffused.</param>
    /// <param name="Rate">The fraction per step, in [0, 1].</param>
    public sealed record Diffuse(string Field, WorldLatticeScalar Rate) : WorldReaction;
    /// <summary>Scales each cell toward zero: <c>v -= v·rate</c>.</summary>
    /// <param name="Field">The field decayed.</param>
    /// <param name="Rate">The fraction per step, in [0, 1].</param>
    public sealed record Decay(string Field, WorldLatticeScalar Rate) : WorldReaction;
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
    public sealed record Emit(string Tag, string Field, WorldLatticeScalar Amount) : WorldReaction;
    /// <summary>Writes 1 or 0 into <paramref name="Row"/>'s cell for every active body, by whether
    /// <paramref name="Field"/> at the body's cell satisfies the comparison — a body standing in a burning cell
    /// becomes tagged, and body-level chemistry takes it from there.</summary>
    /// <param name="Field">The field sampled at the body.</param>
    /// <param name="Comparison">The comparison.</param>
    /// <param name="Value">The constant compared against.</param>
    /// <param name="Row">The keyed int state row written, keyed by body index.</param>
    public sealed record Expose(string Field, WorldFieldComparison Comparison, WorldLatticeScalar Value, string Row) : WorldReaction;
    /// <summary>Moves <paramref name="Field"/> downhill, cell to cell, over the combined surface height of
    /// <paramref name="Over"/> plus <paramref name="Field"/>'s own value -- mass-conserving except where a cell's
    /// clamp to its declared [min, max] binds. Every cell donates an equal share of its previous-step value to each
    /// of the lattice's active-axis directions (four on a ground lattice, six in a volume -- the SAME count for
    /// every cell, whether a direction reaches a neighbour or the lattice edge), and sends that share only along a
    /// direction that is strictly downhill; a level or uphill direction moves nothing.</summary>
    /// <param name="Field">The field transported.</param>
    /// <param name="Rate">The fraction of a cell's per-direction share that actually moves each step, in
    /// [0, 1].</param>
    /// <param name="Over">The other lattice rows forming the terrain basis a downhill direction is measured
    /// against; empty or omitted means the field flows over its own height alone.</param>
    /// <param name="SpillRow">The scalar <c>fixed</c>-kind state row an edge cell's outward share accumulates into
    /// each step (a clamped add), or <see langword="null"/> to treat every lattice edge as a wall.</param>
    public sealed record Flow(string Field, WorldLatticeScalar Rate, IReadOnlyList<string>? Over = null, string? SpillRow = null) : WorldReaction;
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
