using System.Globalization;
using System.Text;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The body-position and state-row seam a <see cref="WorldFieldLattice.Step"/> reaches through — implemented
/// once by the owning host (<see cref="WorldServer"/>) so a step reaches body state through a plain interface call
/// rather than allocating fresh delegates every tick. Every method receives the stepping tick explicitly since the
/// interface itself carries none.</summary>
public interface IWorldFieldLatticeHost {
    /// <summary>Resolves an active body's position, or <see langword="null"/> for an inactive slot.</summary>
    /// <param name="body">The body index.</param>
    FixedVector3? BodyPosition(int body);
    /// <summary>Reads a typed keyed int state row's cell for a body index (0 when absent).</summary>
    /// <param name="row">The compiled state row handle.</param>
    /// <param name="body">The body index used as the cell's key.</param>
    /// <param name="tick">The stepping tick.</param>
    long ReadTag(WorldStateHandle row, int body, ulong tick);
    /// <summary>Writes a typed keyed int state row's cell for a body index.</summary>
    /// <param name="row">The compiled state row handle.</param>
    /// <param name="body">The body index used as the cell's key.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="tick">The stepping tick.</param>
    void WriteTag(WorldStateHandle row, int body, long value, ulong tick);
    /// <summary>Reads a scalar fixed-kind state row's slot cell for a row-referenced reaction scalar (0 when absent
    /// or unwritten).</summary>
    /// <param name="row">The compiled state row handle.</param>
    /// <param name="tick">The stepping tick.</param>
    FixedQ4816 ReadScalar(WorldStateHandle row, ulong tick);
    /// <summary>Adds to a scalar fixed-kind state row's slot cell, clamped to the row's declared envelope (a
    /// <see cref="WorldReaction.Flow"/> spill accumulator) — the host resolves and applies the clamp itself, so this
    /// never refuses.</summary>
    /// <param name="row">The compiled state row handle.</param>
    /// <param name="amount">The raw amount to add.</param>
    /// <param name="tick">The stepping tick.</param>
    void AddScalar(WorldStateHandle row, FixedQ4816 amount, ulong tick);
}
/// <summary>
/// The live cell values of a world's <c>fields</c> section and the reactions that evolve them — simulation state
/// beside the population: stepped from <c>WorldServer.Step</c> on the lattice's cadence, checkpointed, and delivered
/// to clients as cell deltas on the snapshot. Values are <see cref="FixedQ4816"/>; every reaction is integer
/// arithmetic in a fixed cell order, so the same document and input reproduce the same fields bit for bit.
/// </summary>
public sealed class WorldFieldLattice {
    private readonly FixedQ4816 m_cellSize;
    private readonly int m_depth;
    private readonly List<int> m_deltas = [];
    private readonly bool[][] m_deltaDirty;

    private WorldFieldsSection m_document;

    private readonly FixedQ4816[] m_heightScale;
    private readonly bool[] m_isMedium;
    private readonly int m_layers;
    private readonly FixedQ4816 m_bodyCouplingCeiling;
    private readonly FixedQ4816[] m_max;
    private readonly FixedQ4816[] m_min;
    private readonly string[] m_names;
    private readonly FixedVector3 m_origin;

    private WorldFieldProgram m_program;

    private readonly FixedQ4816[] m_scratch;
    private readonly Int128[] m_flowDelta;
    private readonly FixedQ4816[] m_flowHeights;
    private readonly int m_flowDirections;
    private readonly int m_stepEveryTicks;
    private readonly FixedQ4816[][] m_values;
    private readonly int m_width;

    private bool m_fullResync = true;
    private int m_revision;

    /// <summary>One captured lattice: raw Q48.16 cell values per field, field-major.</summary>
    /// <param name="Raw">The raw values, one array per declared field.</param>
    public sealed record WorldFieldCheckpoint(IReadOnlyList<long[]> Raw);

    // A reaction scalar compiled once: the literal in Q48.16, or the scalar state row it reads at each step. An
    // unwritten referenced slot reads 0 (the row's slot cell is minted by its first write), so a row-gated reaction
    // is inert until something writes the row.
    private readonly record struct CompiledScalar(FixedQ4816 Literal, string? Row) {
        public static CompiledScalar Compile(WorldLatticeScalar scalar) => new(
            Literal: FixedQ4816.FromDouble(value: (scalar.Literal ?? 0f)),
            Row: scalar.Row
        );
        public FixedQ4816 Resolve(Func<string, FixedQ4816> readScalar) => ((Row is { } row)
            ? readScalar(row)
            : Literal
        );
    }

    /// <summary>Creates the live lattice from its complete topology/paint companion and the authoritative compiled
    /// reaction program. The constructor never recompiles authored reactions.</summary>
    /// <param name="document">The complete companion owning topology, cadence, paint, and presentation.</param>
    /// <param name="program">The typed reaction program compiled from <paramref name="document"/>.</param>
    /// <param name="worldSeed">The deterministic paint seed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="program"/> is
    /// null.</exception>
    /// <exception cref="ArgumentException"><paramref name="program"/> was compiled from incompatible field or
    /// reaction declarations.</exception>
    public WorldFieldLattice(WorldFieldsSection document, WorldFieldProgram program, ulong worldSeed = 0UL) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: program);

        if (!program.MatchesProgram(document: document)) {
            throw new ArgumentException(
                message: "The field program does not represent the companion document's field declarations and reactions.",
                paramName: nameof(program)
            );
        }

        m_document = document;
        m_program = program;
        m_width = document.Lattice.Width;
        m_depth = document.Lattice.Depth;
        m_layers = document.Lattice.Layers;
        m_cellSize = FixedQ4816.FromDouble(value: document.Lattice.CellSize);
        m_origin = new FixedVector3(
            X: FixedQ4816.FromDouble(value: document.Lattice.Origin.X),
            Y: FixedQ4816.FromDouble(value: document.Lattice.Origin.Y),
            Z: FixedQ4816.FromDouble(value: document.Lattice.Origin.Z)
        );
        m_stepEveryTicks = document.Lattice.StepEveryTicks;

        var fields = program.Fields;

        m_names = new string[fields.Count];
        m_min = new FixedQ4816[fields.Count];
        m_max = new FixedQ4816[fields.Count];
        m_heightScale = new FixedQ4816[fields.Count];
        m_isMedium = new bool[fields.Count];
        m_values = new FixedQ4816[fields.Count][];
        m_deltaDirty = new bool[fields.Count][];
        m_scratch = new FixedQ4816[CellCount];
        m_flowDelta = new Int128[CellCount];
        m_flowHeights = new FixedQ4816[CellCount];
        // Every cell donates an equal share to each of the lattice's active-axis directions -- an axis with a
        // single cell (Layers = 1 on a ground lattice) has no directions at all, never a "missing neighbour".
        m_flowDirections = (
            (((document.Lattice.Width > 1) ? 2 : 0) +
            ((document.Lattice.Depth > 1) ? 2 : 0)) +
            ((document.Lattice.Layers > 1) ? 2 : 0)
        );

        for (var field = 0; (field < fields.Count); field++) {
            var row = fields[field];

            m_names[field] = row.Name;
            m_min[field] = row.Minimum;
            m_max[field] = row.Maximum;
            m_heightScale[field] = row.HeightScale;
            m_isMedium[field] = row.IsMedium;
            m_values[field] = new FixedQ4816[CellCount];
            m_deltaDirty[field] = new bool[CellCount];

            var initial = row.Initial;

            Array.Fill(
                array: m_values[field],
                value: initial
            );

            // DERIVED, never authored: the tallest surface any height-bearing field can raise. A body standing ON
            // that surface still sits above the top voxel row, so the Emit/Expose body coupling must reach it —
            // see TryBodyCellOf.
            var surfaceReach = (m_heightScale[field] * m_max[field]);

            if (surfaceReach > m_bodyCouplingCeiling) {
                m_bodyCouplingCeiling = surfaceReach;
            }
        }

        m_bodyCouplingCeiling += (m_cellSize * FixedQ4816.FromInteger(value: m_layers));

        foreach (var fill in (document.Paint ?? [])) {
            var field = FieldIndex(name: fill.Field);

            if (fill is not WorldLatticeFill.Draw) {
                ApplyPaintFill(
                    field: field,
                    fill: fill,
                    trackDeltas: false,
                    worldSeed: worldSeed
                );
            }
        }

    }

    /// <summary>Gets the declared reaction count.</summary>
    public int ReactionCount => m_program.Nodes.Count;
    /// <summary>Gets the declared step cadence in simulation ticks.</summary>
    public int StepEveryTicks => m_stepEveryTicks;
    /// <summary>Gets the lattice's cell count (width × layers × depth).</summary>
    public int CellCount => ((m_width * m_layers) * m_depth);
    /// <summary>Gets the declared cubic cell edge.</summary>
    public FixedQ4816 CellSize => m_cellSize;
    /// <summary>Gets the authored section.</summary>
    public WorldFieldsSection Document => m_document;
    /// <summary>Gets the authoritative typed reaction program currently executed by <see cref="Step"/>.</summary>
    public WorldFieldProgram Program => m_program;
    /// <summary>Gets the number of declared fields.</summary>
    public int FieldCount => m_values.Length;
    /// <summary>Gets the lattice's minimum corner.</summary>
    public FixedVector3 Origin => m_origin;
    /// <summary>Gets a counter that moves on every cell write.</summary>
    public int Revision => m_revision;

    /// <summary>Describes the exact structural field-program work performed on one cadence step.</summary>
    /// <param name="activeBodyCount">The number of active body slots.</param>
    /// <param name="bodyCapacity">The body-table capacity every body node scans.</param>
    /// <returns>The node, cadence, cell-visit, and body-slot-visit cost line.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The counts are negative or <paramref name="activeBodyCount"/>
    /// exceeds <paramref name="bodyCapacity"/>.</exception>
    public string DescribeCost(int activeBodyCount, int bodyCapacity) {
        if (
            (activeBodyCount < 0) ||
            (bodyCapacity < 0) ||
            (activeBodyCount > bodyCapacity)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(activeBodyCount),
                message: "Active body count must be within the body-table capacity."
            );
        }

        var cellVisits = checked((((long)CellCount) * m_program.CellPassCount));
        var bodySlotVisits = checked((((long)bodyCapacity) * m_program.BodyPassCount));

        return $"lattice {m_program.Nodes.Count} node(s) every {m_stepEveryTicks} tick(s): {CellCount} cell(s) x {m_program.CellPassCount} pass(es) = {cellVisits} cell visit(s); bodies {activeBodyCount}/{bodyCapacity} active/capacity x {m_program.BodyPassCount} pass(es) = {bodySlotVisits} slot visit(s)";
    }
    /// <summary>Checks whether a replacement companion/program pair can be installed without reallocating or
    /// reseeding cell storage. Reaction-only, colour, and paint changes are compatible; topology, cadence, and field
    /// envelope changes require a host restart.</summary>
    /// <param name="document">The candidate complete companion.</param>
    /// <param name="program">The candidate typed reaction program.</param>
    /// <param name="reason">The named incompatibility on refusal; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the pair can replace the live program without migrating cells.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="program"/> is
    /// null.</exception>
    public bool CanInstallProgram(WorldFieldsSection document, WorldFieldProgram program, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: program);

        if (!program.MatchesProgram(document: document)) {
            reason = "the compiled field program does not match the candidate field declarations and reactions";

            return false;
        }

        if (
            (document.Lattice != m_document.Lattice) ||
            (program.Fields.Count != m_program.Fields.Count)
        ) {
            reason = "the field lattice topology or cadence differs from the live allocation; restart the host to load it";

            return false;
        }

        for (var index = 0; (index < program.Fields.Count); index++) {
            var current = m_program.Fields[index];
            var candidate = program.Fields[index];

            if (
                !string.Equals(a: current.Name, b: candidate.Name, comparisonType: StringComparison.Ordinal) ||
                (current.Initial != candidate.Initial) ||
                (current.Minimum != candidate.Minimum) ||
                (current.Maximum != candidate.Maximum) ||
                (current.HeightScale != candidate.HeightScale) ||
                (current.IsMedium != candidate.IsMedium)
            ) {
                reason = $"field declaration {index} differs from the live allocation; restart the host to load it";

                return false;
            }
        }

        reason = null;

        return true;
    }
    /// <summary>Installs a compatible replacement reaction program while retaining every live cell, pending delta,
    /// revision, and checkpoint shape.</summary>
    /// <param name="document">The replacement complete companion.</param>
    /// <param name="program">The replacement typed reaction program.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="program"/> is
    /// null.</exception>
    /// <exception cref="InvalidOperationException">The pair requires a live lattice allocation migration.</exception>
    public void InstallProgram(WorldFieldsSection document, WorldFieldProgram program) {
        if (!CanInstallProgram(
            document: document,
            program: program,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: reason);
        }

        m_document = document;
        m_program = program;
    }

    private static bool Holds(WorldFieldComparison comparison, FixedQ4816 value, FixedQ4816 expected) => comparison switch {
        WorldFieldComparison.Equal => (value == expected),
        WorldFieldComparison.NotEqual => (value != expected),
        WorldFieldComparison.Less => (value < expected),
        WorldFieldComparison.LessOrEqual => (value <= expected),
        WorldFieldComparison.Greater => (value > expected),
        _ => (value >= expected),
    };
    private int CellIndex(int x, int y, int z) => ((((z * m_layers) + y) * m_width) + x);
    private FixedQ4816 Clamp(int field, FixedQ4816 value) => FixedQ4816.Clamp(
        maximum: m_max[field],
        minimum: m_min[field],
        value: value
    );
    private FixedQ4816 AddClamped(int field, FixedQ4816 x, FixedQ4816 y) {
        var raw = (((Int128)x.Value) + y.Value);

        if (raw <= m_min[field].Value) {
            return m_min[field];
        }

        if (raw >= m_max[field].Value) {
            return m_max[field];
        }

        return FixedQ4816.FromRawBits(value: ((long)raw));
    }
    // The exact wide division every reaction that must round a sum without a second rounding pass shares:
    // round-half-to-even on the remainder, computed in UInt128 magnitude then resigned. divisor is always positive.
    private static Int128 DivideRoundHalfEven(Int128 numerator, Int128 divisor) {
        var negative = (numerator < Int128.Zero);
        var magnitude = ((UInt128)(negative ? -numerator : numerator));
        var divisorMagnitude = ((UInt128)divisor);
        var quotient = (magnitude / divisorMagnitude);
        var remainder = (magnitude % divisorMagnitude);

        quotient = FixedPointRounding.RoundToNearestTiesToEven(
            distanceToNext: (divisorMagnitude - remainder),
            distanceToTruncated: remainder,
            truncated: quotient
        );

        return (negative ? -((Int128)quotient) : ((Int128)quotient));
    }
    private static FixedQ4816 Mean(Int128 rawSum, int count) => FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: DivideRoundHalfEven(divisor: count, numerator: rawSum)));
    private void ClearDeltas() {
        foreach (var key in m_deltas) {
            var field = (key / CellCount);
            var cell = (key - (field * CellCount));

            m_deltaDirty[field][cell] = false;
        }

        m_deltas.Clear();
    }
    private int FieldIndex(string name) {
        var index = Array.IndexOf(
            array: m_names,
            value: name
        );

        return ((index < 0)
            ? throw new InvalidOperationException(message: $"fields: '{name}' is not a declared field.")
            : index
        );
    }
    private void Write(int field, int cell, FixedQ4816 value) {
        var clamped = Clamp(
            field: field,
            value: value
        );

        if (m_values[field][cell] == clamped) {
            return;
        }

        m_values[field][cell] = clamped;

        if (!m_deltaDirty[field][cell]) {
            m_deltaDirty[field][cell] = true;
            m_deltas.Add(item: ((field * CellCount) + cell));
        }

        m_revision++;
    }

    /// <summary>Resolves the cell a BODY couples to for the <see cref="WorldReaction.Emit"/>/
    /// <see cref="WorldReaction.Expose"/> reactions: the column under the body, with Y admitted up to the lattice's
    /// derived coupling ceiling (the volume's top plus the tallest surface any height-bearing field can raise) and
    /// clamped onto the top layer. A bare <see cref="TryCellOf"/> requires the position INSIDE the voxel volume, which
    /// no body standing ON a one-layer ground lattice ever is — its feet rest on the raised surface, above the half-
    /// unit slab — so body-coupled reactions would never fire on the documented ground-lattice shape.</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="cell">The cell index.</param>
    /// <returns><see langword="true"/> when the body stands over the lattice within the coupling ceiling.</returns>
    public bool TryBodyCellOf(in FixedVector3 position, out int cell) {
        cell = -1;

        var localX = (((Int128)position.X.Value) - m_origin.X.Value);
        var localY = (((Int128)position.Y.Value) - m_origin.Y.Value);
        var localZ = (((Int128)position.Z.Value) - m_origin.Z.Value);

        if (
            (localX < Int128.Zero) ||
            (localY < Int128.Zero) ||
            (localZ < Int128.Zero) ||
            (localY > m_bodyCouplingCeiling.Value)
        ) {
            return false;
        }

        var x = (localX / m_cellSize.Value);
        var z = (localZ / m_cellSize.Value);

        if (
            (x >= m_width) ||
            (z >= m_depth)
        ) {
            return false;
        }

        var y = (localY / m_cellSize.Value);

        if (y >= m_layers) {
            y = (m_layers - 1);
        }

        cell = CellIndex(
            x: ((int)x),
            y: ((int)y),
            z: ((int)z)
        );

        return true;
    }
    /// <summary>Resolves the cell a world position falls in, or <see langword="false"/> when it lies outside the
    /// lattice.</summary>
    /// <param name="position">The world position.</param>
    /// <param name="cell">The cell index.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool TryCellOf(in FixedVector3 position, out int cell) {
        cell = -1;

        var localX = (((Int128)position.X.Value) - m_origin.X.Value);
        var localY = (((Int128)position.Y.Value) - m_origin.Y.Value);
        var localZ = (((Int128)position.Z.Value) - m_origin.Z.Value);

        if (
            (localX < Int128.Zero) ||
            (localY < Int128.Zero) ||
            (localZ < Int128.Zero)
        ) {
            return false;
        }

        var x = (localX / m_cellSize.Value);
        var y = (localY / m_cellSize.Value);
        var z = (localZ / m_cellSize.Value);

        if (
            (x >= m_width) ||
            (y >= m_layers) ||
            (z >= m_depth)
        ) {
            return false;
        }

        cell = CellIndex(
            x: ((int)x),
            y: ((int)y),
            z: ((int)z)
        );

        return true;
    }
    /// <summary>Reads one cell.</summary>
    /// <param name="field">The field index.</param>
    /// <param name="cell">The cell index.</param>
    /// <returns>The value.</returns>
    public FixedQ4816 Value(int field, int cell) => m_values[field][cell];
    /// <summary>Resolves the free surface a body at <paramref name="position"/> would float against: the highest
    /// medium field's value times its height scale, over the lattice origin, at the body's coupled cell (the same
    /// coupling <see cref="TryBodyCellOf"/> resolves for <see cref="WorldReaction.Emit"/>/<see cref="WorldReaction.Expose"/>).
    /// <see langword="null"/> when the body lies outside the lattice's coupling ceiling, or every medium field reads
    /// zero or less there.</summary>
    /// <param name="position">The body's world position.</param>
    /// <returns>The surface's world-space Y, or <see langword="null"/> for no medium.</returns>
    public FixedQ4816? MediumSurface(in FixedVector3 position) {
        if (!TryBodyCellOf(cell: out var cell, position: in position)) {
            return null;
        }

        FixedQ4816? best = null;

        for (var field = 0; (field < m_values.Length); field++) {
            if (!m_isMedium[field]) {
                continue;
            }

            var value = m_values[field][cell];

            if (value <= FixedQ4816.Zero) {
                continue;
            }

            var surface = (m_origin.Y + (value * m_heightScale[field]));

            if (
                (best is not { } current) ||
                (surface > current)
            ) {
                best = surface;
            }
        }

        return best;
    }
    /// <summary>Resolves a declared field's index by name.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="field">The field index.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a declared field.</returns>
    public bool TryFieldIndex(string name, out int field) {
        field = Array.IndexOf(
            array: m_names,
            value: name
        );

        return (field >= 0);
    }
    /// <summary>Evaluates one condition (the same grammar a <see cref="WorldReaction.Transform"/>/
    /// <see cref="WorldReaction.Expose"/> condition uses) against a live cell — the seam a consumer outside the
    /// reaction program (a placement's response trait) tests a cell through, never a second comparison grammar.</summary>
    /// <param name="field">The field index (see <see cref="TryFieldIndex"/>).</param>
    /// <param name="cell">The cell index.</param>
    /// <param name="comparison">The comparison.</param>
    /// <param name="expected">The scalar compared against (literal or state-row reference).</param>
    /// <param name="readScalar">Reads a scalar state row's slot cell for <paramref name="expected"/>'s row form.</param>
    public bool Holds(int field, int cell, WorldFieldComparison comparison, WorldLatticeScalar expected, Func<string, FixedQ4816> readScalar) => Holds(
        comparison: comparison,
        value: Value(cell: cell, field: field),
        expected: CompiledScalar.Compile(scalar: expected).Resolve(readScalar: readScalar)
    );
    /// <summary>Gets the solid surface height of a column — the greatest height any height field raises there, or
    /// the lattice origin's Y when none does.</summary>
    /// <param name="x">The column's X cell index.</param>
    /// <param name="z">The column's Z cell index.</param>
    /// <returns>The surface height, world units; <see langword="null"/> when no height field is nonzero.</returns>
    public FixedQ4816? ColumnHeight(int x, int z) {
        FixedQ4816? best = null;

        for (var field = 0; (field < m_values.Length); field++) {
            if (m_heightScale[field] == FixedQ4816.Zero) {
                continue;
            }

            // A ground lattice's column is its layer-0 cell; a volume's column height stacks every layer.
            var raised = FixedQ4816.Zero;

            for (var y = 0; (y < m_layers); y++) {
                raised += (m_values[field][CellIndex(x: x, y: y, z: z)] * m_heightScale[field]);
            }

            if (raised <= FixedQ4816.Zero) {
                continue;
            }

            var top = (m_origin.Y + raised);

            if (
                (best is not { } current) ||
                (top > current)
            ) {
                best = top;
            }
        }

        return best;
    }

    /// <summary>Writes one whole-field pass of drawn cell values — <paramref name="raw"/> holds one raw
    /// <see cref="FixedQ4816"/> value per cell in cell-index order — then reapplies every later authored paint for
    /// the same field, preserving document order. Every changed cell is marked for the next snapshot delta.</summary>
    /// <param name="field">The field index (see <see cref="TryFieldIndex"/>).</param>
    /// <param name="raw">The drawn raw values, exactly <see cref="CellCount"/> long.</param>
    /// <param name="worldSeed">The deterministic seed later noise/scatter fills use.</param>
    /// <exception cref="ArgumentException"><paramref name="raw"/> is not one value per cell.</exception>
    public void FillFromDraw(int field, ReadOnlySpan<long> raw, ulong worldSeed) {
        if (raw.Length != CellCount) {
            throw new ArgumentException(message: $"a draw fill supplies one value per cell ({CellCount}); received {raw.Length}", paramName: nameof(raw));
        }

        for (var cell = 0; (cell < raw.Length); cell++) {
            Write(
                cell: cell,
                field: field,
                value: FixedQ4816.FromRawBits(value: raw[cell])
            );
        }

        var afterDraw = false;

        foreach (var fill in (m_document.Paint ?? [])) {
            if (!string.Equals(a: fill.Field, b: m_names[field], comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            if (fill is WorldLatticeFill.Draw) {
                afterDraw = true;

                continue;
            }

            if (afterDraw) {
                ApplyPaintFill(
                    field: field,
                    fill: fill,
                    trackDeltas: true,
                    worldSeed: worldSeed
                );
            }
        }
    }
    /// <summary>Gets the lattice's width in cells.</summary>
    public int Width => m_width;
    /// <summary>Gets the lattice's depth in cells.</summary>
    public int Depth => m_depth;
    /// <summary>Gets the lattice's layer count.</summary>
    public int Layers => m_layers;

    private void ApplyPaintFill(int field, WorldLatticeFill fill, bool trackDeltas, ulong worldSeed) {
        switch (fill) {
            case WorldLatticeFill.Noise noise:
                ApplyNoiseFill(
                    field: field,
                    fill: noise,
                    trackDeltas: trackDeltas,
                    worldSeed: worldSeed
                );
                break;
            case WorldLatticeFill.Scatter scatter:
                ApplyScatterFill(
                    field: field,
                    fill: scatter,
                    trackDeltas: trackDeltas,
                    worldSeed: worldSeed
                );
                break;
            case WorldLatticeFill.Rect rect:
                ApplyRectFill(
                    field: field,
                    fill: rect,
                    trackDeltas: trackDeltas
                );
                break;
        }
    }
    private void SetPaintValue(int field, int cell, FixedQ4816 value, bool trackDeltas) {
        if (trackDeltas) {
            Write(
                cell: cell,
                field: field,
                value: value
            );
        }
        else {
            m_values[field][cell] = value;
        }
    }
    private void ApplyRectFill(int field, WorldLatticeFill.Rect fill, bool trackDeltas) {
        var value = Clamp(
            field: field,
            value: FixedQ4816.FromDouble(value: fill.Value)
        );
        var minX = FixedQ4816.FromDouble(value: fill.MinX);
        var maxX = FixedQ4816.FromDouble(value: fill.MaxX);
        var minZ = FixedQ4816.FromDouble(value: fill.MinZ);
        var maxZ = FixedQ4816.FromDouble(value: fill.MaxZ);
        var half = (m_cellSize / FixedQ4816.FromInteger(value: 2));

        for (var z = 0; (z < m_depth); z++) {
            var centreZ = ((m_origin.Z + (m_cellSize * FixedQ4816.FromInteger(value: z))) + half);

            if ((centreZ < minZ) || (centreZ > maxZ)) {
                continue;
            }

            for (var x = 0; (x < m_width); x++) {
                var centreX = ((m_origin.X + (m_cellSize * FixedQ4816.FromInteger(value: x))) + half);

                if ((centreX < minX) || (centreX > maxX)) {
                    continue;
                }

                for (var y = 0; (y < m_layers); y++) {
                    SetPaintValue(
                        cell: CellIndex(x: x, y: y, z: z),
                        field: field,
                        trackDeltas: trackDeltas,
                        value: value
                    );
                }
            }
        }
    }
    private void ApplyNoiseFill(int field, WorldLatticeFill.Noise fill, bool trackDeltas, ulong worldSeed) {
        var value = Clamp(
            field: field,
            value: FixedQ4816.FromDouble(value: fill.Value)
        );
        var threshold = FixedQ4816.FromDouble(value: fill.Threshold);
        var one = FixedQ4816.One;
        var span = (one - threshold);
        var seed = unchecked((uint)(fill.Seed ^ ((uint)worldSeed) ^ ((uint)(worldSeed >> 32))));

        for (var z = 0; (z < m_depth); z++) {
            for (var x = 0; (x < m_width); x++) {
                // fBm: per-octave halved amplitude, halved noise-cell edge (floored at 1), decorrelated seed stream.
                var amplitude = FixedQ4816.One;
                var total = FixedQ4816.Zero;
                var weight = FixedQ4816.Zero;
                var cells = fill.Frequency;

                for (var octave = 0; (octave < fill.Octaves); octave++) {
                    total += (amplitude * Pcg3dLatticeNoise.ValueNoise01(
                        cellX: x,
                        cellZ: z,
                        noiseCells: System.Math.Max(val1: 1, val2: cells),
                        seed: unchecked((seed + (((uint)octave) * 0x9E3779B9u)))
                    ));
                    weight += amplitude;
                    amplitude = FixedQ4816.FromRawBits(value: (amplitude.Value >> 1));
                    cells = System.Math.Max(val1: 1, val2: (cells >> 1));
                }

                var n = (total / weight);

                if (n < threshold) {
                    continue;
                }

                var scaled = ((span.Value > 0) ? (value * ((n - threshold) / span)) : value);

                for (var y = 0; (y < m_layers); y++) {
                    SetPaintValue(
                        cell: CellIndex(x: x, y: y, z: z),
                        field: field,
                        trackDeltas: trackDeltas,
                        value: Clamp(field: field, value: scaled)
                    );
                }
            }
        }
    }
    private void ApplyScatterFill(int field, WorldLatticeFill.Scatter fill, bool trackDeltas, ulong worldSeed) {
        var value = Clamp(
            field: field,
            value: FixedQ4816.FromDouble(value: fill.Value)
        );
        var seed = unchecked((uint)(fill.Seed ^ ((uint)worldSeed) ^ ((uint)(worldSeed >> 32))));
        var spacing = System.Math.Max(val1: 2, val2: fill.Spacing);
        var radius = System.Math.Max(val1: 1, val2: fill.Radius);
        var radiusSquared = (radius * radius);

        for (var z = 0; (z < m_depth); z++) {
            for (var x = 0; (x < m_width); x++) {
                // The cell tests its own block and the 8 neighbours — a jittered point near a block edge reaches
                // across it, and 3×3 covers every reachable point while radius stays within one block.
                var blockX = (x / spacing);
                var blockZ = (z / spacing);
                var hit = false;

                for (var dz = -1; (!hit && (dz <= 1)); dz++) {
                    for (var dx = -1; (!hit && (dx <= 1)); dx++) {
                        var bx = (blockX + dx);
                        var bz = (blockZ + dz);
                        var h = Pcg3dLatticeNoise.Pcg3d(
                            x: unchecked((uint)bx),
                            y: unchecked((uint)bz),
                            z: seed
                        );
                        // The point sits inside its block, radius-inset so a disc never leaves the block.
                        var inset = System.Math.Max(val1: 0, val2: (spacing - (2 * radius)));
                        var px = (((bx * spacing) + radius) + ((inset > 0) ? (int)(h.X % ((uint)inset)) : 0));
                        var pz = (((bz * spacing) + radius) + ((inset > 0) ? (int)(h.Y % ((uint)inset)) : 0));
                        var ddx = (x - px);
                        var ddz = (z - pz);

                        hit = (((ddx * ddx) + (ddz * ddz)) <= radiusSquared);
                    }
                }

                if (!hit) {
                    continue;
                }

                for (var y = 0; (y < m_layers); y++) {
                    SetPaintValue(
                        cell: CellIndex(x: x, y: y, z: z),
                        field: field,
                        trackDeltas: trackDeltas,
                        value: value
                    );
                }
            }
        }
    }

    /// <summary>Steps the reactions once when <paramref name="tick"/> falls on the cadence; a no-op otherwise. The
    /// host is invoked directly (no per-call delegate is allocated) so a world with a <c>fields</c> section pays
    /// nothing beyond the cadence check on the ticks the lattice does not react on.</summary>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="bodyCount">The entity-table capacity; bodies are visited by index.</param>
    /// <param name="host">The body-position and state-row seam.</param>
    public void Step(ulong tick, int bodyCount, IWorldFieldLatticeHost host) {
        if ((tick % ((ulong)m_stepEveryTicks)) != 0UL) {
            return;
        }

        ArgumentNullException.ThrowIfNull(argument: host);

        foreach (var reaction in m_program.Nodes) {
            switch (reaction) {
                case WorldFieldNode.Diffuse diffuse:
                    StepDiffuse(
                        field: diffuse.Field.Ordinal,
                        rate: ClampRate(rate: Resolve(host: host, input: diffuse.Rate, tick: tick))
                    );
                    break;
                case WorldFieldNode.Decay decay:
                    StepDecay(
                        field: decay.Field.Ordinal,
                        rate: ClampRate(rate: Resolve(host: host, input: decay.Rate, tick: tick))
                    );
                    break;
                case WorldFieldNode.Transform transform:
                    StepTransform(
                        host: host,
                        reaction: transform,
                        tick: tick
                    );
                    break;
                case WorldFieldNode.Emit emit:
                    for (var body = 0; (body < bodyCount); body++) {
                        if (
                            (host.BodyPosition(body: body) is not { } position) ||
                            (host.ReadTag(row: emit.Tag, body: body, tick: tick) == 0L) ||
                            !TryBodyCellOf(
                            cell: out var cell,
                            position: in position
                        )
                        ) {
                            continue;
                        }

                        Write(
                            cell: cell,
                            field: emit.Field.Ordinal,
                            value: AddClamped(
                                field: emit.Field.Ordinal,
                                x: m_values[emit.Field.Ordinal][cell],
                                y: Resolve(host: host, input: emit.Amount, tick: tick)
                            )
                        );
                    }

                    break;
                case WorldFieldNode.Flow flow:
                    StepFlow(
                        reaction: flow,
                        rate: ClampRate(rate: Resolve(host: host, input: flow.Rate, tick: tick)),
                        host: host,
                        tick: tick
                    );
                    break;
                case WorldFieldNode.Expose expose:
                    for (var body = 0; (body < bodyCount); body++) {
                        if (host.BodyPosition(body: body) is not { } position) {
                            continue;
                        }

                        var exposed = (TryBodyCellOf(
                            cell: out var cell,
                            position: in position
                        ) && Holds(
                            comparison: expose.Comparison,
                            expected: Resolve(host: host, input: expose.Value, tick: tick),
                            value: m_values[expose.Field.Ordinal][cell]
                        ));

                        host.WriteTag(
                            body: body,
                            row: expose.Row,
                            tick: tick,
                            value: (exposed
                                ? 1L
                                : 0L)
                        );
                    }

                    break;
            }
        }
    }

    private static FixedQ4816 Resolve(IWorldFieldLatticeHost host, WorldFieldScalarInput input, ulong tick) => (input.IsState
        ? host.ReadScalar(row: input.State, tick: tick)
        : input.Literal
    );
    private static FixedQ4816 ClampRate(FixedQ4816 rate) => ((rate < FixedQ4816.Zero)
        ? FixedQ4816.Zero
        : ((rate > FixedQ4816.One) ? FixedQ4816.One : rate)
    );
    private void StepDiffuse(int field, FixedQ4816 rate) {
        var values = m_values[field];

        Array.Copy(
            sourceArray: values,
            destinationArray: m_scratch,
            length: values.Length
        );

        for (var z = 0; (z < m_depth); z++) {
            for (var y = 0; (y < m_layers); y++) {
                for (var x = 0; (x < m_width); x++) {
                    var cell = CellIndex(x: x, y: y, z: z);
                    Int128 rawSum = 0;
                    var count = 0;

                    if (x > 0) { rawSum += m_scratch[CellIndex(x: (x - 1), y: y, z: z)].Value; count++; }
                    if (x < (m_width - 1)) { rawSum += m_scratch[CellIndex(x: (x + 1), y: y, z: z)].Value; count++; }
                    if (z > 0) { rawSum += m_scratch[CellIndex(x: x, y: y, z: (z - 1))].Value; count++; }
                    if (z < (m_depth - 1)) { rawSum += m_scratch[CellIndex(x: x, y: y, z: (z + 1))].Value; count++; }
                    if (y > 0) { rawSum += m_scratch[CellIndex(x: x, y: (y - 1), z: z)].Value; count++; }
                    if (y < (m_layers - 1)) { rawSum += m_scratch[CellIndex(x: x, y: (y + 1), z: z)].Value; count++; }

                    if (count == 0) {
                        continue;
                    }

                    var mean = Mean(count: count, rawSum: rawSum);
                    var current = m_scratch[cell];

                    Write(
                        cell: cell,
                        field: field,
                        value: (current + ((mean - current) * rate))
                    );
                }
            }
        }
    }
    private void StepDecay(int field, FixedQ4816 rate) {
        var values = m_values[field];

        for (var cell = 0; (cell < values.Length); cell++) {
            var current = values[cell];

            if (current == FixedQ4816.Zero) {
                continue;
            }

            Write(
                cell: cell,
                field: field,
                value: (current - (current * rate))
            );
        }
    }
    private void StepTransform(WorldFieldNode.Transform reaction, IWorldFieldLatticeHost host, ulong tick) {
        // Row-referenced terms resolve ONCE per step, before the cell loop — a season row's value is a step-wide
        // constant, never a per-cell read.
        var whenValues = new FixedQ4816[reaction.When.Length];
        var thenValues = new FixedQ4816[reaction.Then.Length];

        for (var index = 0; (index < reaction.When.Length); index++) {
            whenValues[index] = Resolve(host: host, input: reaction.When[index].Value, tick: tick);
        }
        for (var index = 0; (index < reaction.Then.Length); index++) {
            thenValues[index] = Resolve(host: host, input: reaction.Then[index].Value, tick: tick);
        }

        for (var cell = 0; (cell < CellCount); cell++) {
            var holds = true;

            for (var index = 0; (index < reaction.When.Length); index++) {
                var condition = reaction.When[index];

                if (!Holds(
                    comparison: condition.Comparison,
                    expected: whenValues[index],
                    value: m_values[condition.Field.Ordinal][cell]
                )) {
                    holds = false;
                    break;
                }
            }

            if (!holds) {
                continue;
            }

            for (var index = 0; (index < reaction.Then.Length); index++) {
                var write = reaction.Then[index];

                Write(
                    cell: cell,
                    field: write.Field.Ordinal,
                    value: ((write.Op == WorldFieldWriteOp.Add)
                        ? AddClamped(
                            field: write.Field.Ordinal,
                            x: m_values[write.Field.Ordinal][cell],
                            y: thenValues[index]
                        )
                        : thenValues[index])
                );
            }
        }
    }
    // Mass-conserving directional transport. h_i (m_flowHeights) is snapshotted once per step: this field's own
    // PREVIOUS-step value (Jacobi, like StepDiffuse) plus every 'over' field's LIVE value -- Flow never writes an
    // over field, so live and snapshot agree there.
    //
    // A donor's fair share toward one direction is rate * (its previous-step value / m_flowDirections) -- because
    // rate <= 1, the sum of every direction's share never exceeds a donor's own previous-step value, so a donor can
    // never be driven negative by this reaction alone. A boundary direction (spills into SpillRow when declared,
    // else the edge is a wall and the share stays put) always moves exactly this fair share.
    //
    // A paired direction (a real downhill neighbour) additionally caps the fair share at HALF the pair's own height
    // gap -- at rate 1 an isolated pair moves exactly to a shared height, never past it -- whenever the field's own
    // value feeds back into height (HeightScale > 0): without that cap, a cell donating its full fair share to
    // several downhill neighbours at once can overshoot past their shared level and rebound next step, since the
    // very act of moving mass changes the height ordering that decided it. A field with HeightScale 0 never
    // contributes to its own height (Flow transports it, but only an 'over' field's static terrain decides
    // direction), so that feedback cannot occur and the half-gap cap is skipped.
    //
    // Deltas accumulate exactly in Int128 and clamp only once, at the final write, so mass is conserved exactly
    // whenever that clamp does not bind.
    private void StepFlow(WorldFieldNode.Flow reaction, FixedQ4816 rate, IWorldFieldLatticeHost host, ulong tick) {
        if (m_flowDirections == 0) {
            return;
        }

        var field = reaction.Field.Ordinal;
        var values = m_values[field];

        Array.Copy(
            sourceArray: values,
            destinationArray: m_scratch,
            length: values.Length
        );

        for (var cell = 0; (cell < CellCount); cell++) {
            var height = (m_scratch[cell] * m_heightScale[field]);

            foreach (var over in reaction.Over) {
                height += (m_values[over.Ordinal][cell] * m_heightScale[over.Ordinal]);
            }

            m_flowHeights[cell] = height;
        }

        Array.Clear(array: m_flowDelta);

        var directionDivisor = FixedQ4816.FromInteger(value: m_flowDirections);
        var ownHeightScale = m_heightScale[field];
        var hasSpill = reaction.SpillRow.IsValid;
        var spilled = Int128.Zero;

        FixedQ4816 FairShare(int donorCell) => (m_scratch[donorCell] / directionDivisor);

        void Pair(int a, int b) {
            if (m_flowHeights[a] == m_flowHeights[b]) {
                return;
            }

            var aIsDonor = (m_flowHeights[a] > m_flowHeights[b]);
            var donor = (aIsDonor ? a : b);
            var receiver = (aIsDonor ? b : a);
            var capped = FairShare(donorCell: donor);

            if (ownHeightScale > FixedQ4816.Zero) {
                var gap = FixedQ4816.Abs(value: (m_flowHeights[donor] - m_flowHeights[receiver]));
                var halfGapShare = (gap / (ownHeightScale + ownHeightScale));

                capped = FixedQ4816.Min(x: capped, y: halfGapShare);
            }

            var flux = (capped * rate).Value;

            m_flowDelta[donor] -= flux;
            m_flowDelta[receiver] += flux;
        }

        void Spill(int cell) {
            var flux = (FairShare(donorCell: cell) * rate).Value;

            m_flowDelta[cell] -= flux;
            spilled += flux;
        }

        for (var z = 0; (z < m_depth); z++) {
            for (var y = 0; (y < m_layers); y++) {
                for (var x = 0; (x < m_width); x++) {
                    var cell = CellIndex(x: x, y: y, z: z);

                    if (m_width > 1) {
                        if (x < (m_width - 1)) {
                            Pair(a: cell, b: CellIndex(x: (x + 1), y: y, z: z));
                        } else if (hasSpill) {
                            Spill(cell: cell);
                        }

                        if ((x == 0) && hasSpill) {
                            Spill(cell: cell);
                        }
                    }

                    if (m_depth > 1) {
                        if (z < (m_depth - 1)) {
                            Pair(a: cell, b: CellIndex(x: x, y: y, z: (z + 1)));
                        } else if (hasSpill) {
                            Spill(cell: cell);
                        }

                        if ((z == 0) && hasSpill) {
                            Spill(cell: cell);
                        }
                    }

                    if (m_layers > 1) {
                        if (y < (m_layers - 1)) {
                            Pair(a: cell, b: CellIndex(x: x, y: (y + 1), z: z));
                        } else if (hasSpill) {
                            Spill(cell: cell);
                        }

                        if ((y == 0) && hasSpill) {
                            Spill(cell: cell);
                        }
                    }
                }
            }
        }

        for (var cell = 0; (cell < CellCount); cell++) {
            if (m_flowDelta[cell] == Int128.Zero) {
                continue;
            }

            Write(
                cell: cell,
                field: field,
                value: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: (((Int128)m_scratch[cell].Value) + m_flowDelta[cell])))
            );
        }

        if (hasSpill && (spilled != Int128.Zero)) {
            host.AddScalar(
                row: reaction.SpillRow,
                amount: FixedQ4816.FromRawBits(value: FixedSaturate.ToInt64(value: spilled)),
                tick: tick
            );
        }
    }

    /// <summary>Takes the cell deltas written since the last take — or every cell, when a full resync is owed
    /// (construction, restore, or a primer snapshot).</summary>
    /// <param name="full">Whether to send every cell rather than the pending deltas.</param>
    /// <param name="isFull">Whether the returned set covers every cell.</param>
    /// <returns>The deltas.</returns>
    public FieldCellDelta[] TakeDeltas(bool full, out bool isFull) {
        if (full || m_fullResync) {
            var all = new FieldCellDelta[(FieldCount * CellCount)];
            var index = 0;

            for (var field = 0; (field < FieldCount); field++) {
                for (var cell = 0; (cell < CellCount); cell++) {
                    all[index++] = new FieldCellDelta(
                        Cell: cell,
                        Field: ((byte)field),
                        Raw: m_values[field][cell].Value
                    );
                }
            }

            // An explicit full take is a per-sink primer and must not steal the shared incremental stream. Only the
            // lattice-owned resync flag (construction/restore) consumes pending writes for everybody.
            if (!full) {
                ClearDeltas();
                m_fullResync = false;
            }
            isFull = true;

            return all;
        }

        isFull = false;

        if (m_deltas.Count == 0) {
            return [];
        }

        var taken = new FieldCellDelta[m_deltas.Count];

        for (var index = 0; (index < m_deltas.Count); index++) {
            var key = m_deltas[index];
            var field = (key / CellCount);
            var cell = (key - (field * CellCount));

            taken[index] = new FieldCellDelta(
                Cell: cell,
                Field: ((byte)field),
                Raw: m_values[field][cell].Value
            );
        }

        ClearDeltas();

        return taken;
    }
    /// <summary>Captures every cell.</summary>
    /// <returns>The checkpoint.</returns>
    public WorldFieldCheckpoint Capture() {
        var raw = new long[FieldCount][];

        for (var field = 0; (field < FieldCount); field++) {
            raw[field] = new long[CellCount];

            for (var cell = 0; (cell < CellCount); cell++) {
                raw[field][cell] = m_values[field][cell].Value;
            }
        }

        return new WorldFieldCheckpoint(Raw: raw);
    }
    /// <summary>Validates that a checkpoint has this lattice's shape and declared value ranges.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    public void ValidateCheckpoint(WorldFieldCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        if (checkpoint.Raw.Count != FieldCount) {
            throw new InvalidOperationException(message: $"fields checkpoint carries {checkpoint.Raw.Count} fields; the lattice declares {FieldCount}.");
        }

        for (var field = 0; (field < FieldCount); field++) {
            if (checkpoint.Raw[field].Length != CellCount) {
                throw new InvalidOperationException(message: $"fields checkpoint field {field} carries {checkpoint.Raw[field].Length} cells; the lattice declares {CellCount}.");
            }

            for (var cell = 0; (cell < CellCount); cell++) {
                var value = FixedQ4816.FromRawBits(value: checkpoint.Raw[field][cell]);

                if (
                    (value < m_min[field]) ||
                    (value > m_max[field])
                ) {
                    throw new InvalidOperationException(message: $"fields checkpoint field {field} cell {cell} is outside the declared range.");
                }
            }
        }
    }
    /// <summary>Restores every cell from a checkpoint whose shape and values match this lattice.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    public void Restore(WorldFieldCheckpoint checkpoint) {
        ValidateCheckpoint(checkpoint: checkpoint);

        for (var field = 0; (field < FieldCount); field++) {
            for (var cell = 0; (cell < CellCount); cell++) {
                m_values[field][cell] = FixedQ4816.FromRawBits(value: checkpoint.Raw[field][cell]);
            }
        }

        ClearDeltas();
        m_fullResync = true;
        m_revision++;
    }
    /// <summary>Describes the lattice for a console read-back.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        var parts = new List<string>(capacity: FieldCount);

        for (var field = 0; (field < FieldCount); field++) {
            var sum = 0.0;
            var nonzero = 0;

            foreach (var value in m_values[field]) {
                sum += ((double)value);

                if (value != FixedQ4816.Zero) {
                    nonzero++;
                }
            }

            var color = (((m_document.Fields[field] is { HeightScale: > 0f } row) && (row.Color is { } token))
                ? $" color={token}"
                : string.Empty
            );

            parts.Add(item: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{m_names[field]} nonzero={nonzero} mean={(sum / CellCount):0.###}{color}"
            ));
        }

        var plan = new StringBuilder();

        for (var index = 0; (index < m_program.Nodes.Count); index++) {
            if (index > 0) {
                plan.Append(value: ',');
            }

            plan.Append(value: index).Append(value: ':').Append(value: m_program.Nodes[index] switch {
                WorldFieldNode.Diffuse => "diffuse",
                WorldFieldNode.Decay => "decay",
                WorldFieldNode.Transform => "transform",
                WorldFieldNode.Emit => "emit",
                WorldFieldNode.Expose => "expose",
                WorldFieldNode.Flow => "flow",
                _ => "unknown",
            });
        }

        var dependencies = new StringBuilder();

        foreach (var dependency in m_program.Dependencies) {
            if (dependencies.Length > 0) {
                dependencies.Append(value: ',');
            }

            dependencies.Append(value: dependency.Before.Ordinal).Append(value: '>').Append(value: dependency.After.Ordinal);
        }

        return $"lattice {m_width}x{m_layers}x{m_depth} @ {((double)m_cellSize)} every {m_stepEveryTicks} ticks: {string.Join(
            separator: " | ",
            values: parts
        )} | plan nodes={m_program.Nodes.Count} cellPasses={m_program.CellPassCount} bodyPasses={m_program.BodyPassCount} order=[{plan}] dependencies=[{dependencies}]";
    }
}
/// <summary>A contact field over a <see cref="WorldFieldLattice"/>'s height columns: the signed distance to the union
/// of column boxes, exact within two cells of a column and a conservative lower bound beyond.</summary>
public sealed class WorldFieldLatticeSolid : IFieldEvaluator {
    private const int Reach = 2;

    private readonly WorldFieldLattice m_lattice;

    public WorldFieldLatticeSolid(WorldFieldLattice lattice) {
        ArgumentNullException.ThrowIfNull(argument: lattice);

        m_lattice = lattice;
    }

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => new(WarpFree: true);

    private static FixedQ4816 BoxDistance(in FixedVector3 point, in FixedVector3 min, in FixedVector3 max) {
        var dx = FixedQ4816.Max(
            x: (min.X - point.X),
            y: (point.X - max.X)
        );
        var dy = FixedQ4816.Max(
            x: (min.Y - point.Y),
            y: (point.Y - max.Y)
        );
        var dz = FixedQ4816.Max(
            x: (min.Z - point.Z),
            y: (point.Z - max.Z)
        );
        var outside = new FixedVector3(
            X: FixedQ4816.Max(x: dx, y: FixedQ4816.Zero),
            Y: FixedQ4816.Max(x: dy, y: FixedQ4816.Zero),
            Z: FixedQ4816.Max(x: dz, y: FixedQ4816.Zero)
        );
        var inside = FixedQ4816.Min(
            x: FixedQ4816.Max(
                x: dx,
                y: FixedQ4816.Max(x: dy, y: dz)
            ),
            y: FixedQ4816.Zero
        );

        return (outside.Length + inside);
    }
    private FixedQ4816 Distance(in FixedVector3 point) {
        var cell = m_lattice.CellSize;
        var origin = m_lattice.Origin;
        var fx = ((int)(FixedQ4816.Floor(value: ((point.X - origin.X) / cell)).Value >> 16));
        var fz = ((int)(FixedQ4816.Floor(value: ((point.Z - origin.Z) / cell)).Value >> 16));
        var best = (cell * FixedQ4816.FromInteger(value: Reach));

        for (var z = (fz - Reach); (z <= (fz + Reach)); z++) {
            if (
                (z < 0) ||
                (z >= m_lattice.Depth)
            ) {
                continue;
            }

            for (var x = (fx - Reach); (x <= (fx + Reach)); x++) {
                if (
                    (x < 0) ||
                    (x >= m_lattice.Width) ||
                    (m_lattice.ColumnHeight(x: x, z: z) is not { } top)
                ) {
                    continue;
                }

                var min = new FixedVector3(
                    X: (origin.X + (cell * FixedQ4816.FromInteger(value: x))),
                    Y: (origin.Y - cell),
                    Z: (origin.Z + (cell * FixedQ4816.FromInteger(value: z)))
                );
                var max = new FixedVector3(
                    X: (min.X + cell),
                    Y: top,
                    Z: (min.Z + cell)
                );
                var distance = BoxDistance(
                    max: in max,
                    min: in min,
                    point: in point
                );

                if (distance < best) {
                    best = distance;
                }
            }
        }

        return best;
    }

    /// <inheritdoc/>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
        material = 0;

        if (!position.TryDelta(
            delta: out var point,
            origin: FixedPosition.Zero
        )) {
            distance = FixedQ4816.Zero;

            return false;
        }

        distance = Distance(point: in point);

        return true;
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) =>
        TryFieldGradient(
            epsilon: FixedQ4816.FromDouble(value: 0.01),
            gradient: out gradient,
            position: position
        );
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) {
        gradient = default;

        if (!position.TryDelta(
            delta: out var point,
            origin: FixedPosition.Zero
        )) {
            return false;
        }

        // A zero probe asks for the analytic gradient this sampled field has no closed form for; the fallback probe
        // is the solver's own default scale.
        if (epsilon <= FixedQ4816.Zero) {
            epsilon = FixedQ4816.FromDouble(value: 0.01);
        }

        var ex = new FixedVector3(X: epsilon, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var ey = new FixedVector3(X: FixedQ4816.Zero, Y: epsilon, Z: FixedQ4816.Zero);
        var ez = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: epsilon);
        var two = (epsilon + epsilon);
        var px = (point + ex); var mx = (point - ex);
        var py = (point + ey); var my = (point - ey);
        var pz = (point + ez); var mz = (point - ez);

        gradient = new FixedVector3(
            X: ((Distance(point: in px) - Distance(point: in mx)) / two),
            Y: ((Distance(point: in py) - Distance(point: in my)) / two),
            Z: ((Distance(point: in pz) - Distance(point: in mz)) / two)
        );

        return true;
    }
}
/// <summary>The union of two fields: the lesser distance, and that field's gradient and material.</summary>
public sealed class WorldUnionField : IFieldEvaluator {
    private readonly IFieldEvaluator m_a;
    private readonly IFieldEvaluator m_b;

    public WorldUnionField(IFieldEvaluator a, IFieldEvaluator b) {
        ArgumentNullException.ThrowIfNull(argument: a);
        ArgumentNullException.ThrowIfNull(argument: b);

        m_a = a;
        m_b = b;
    }

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => new(WarpFree: (m_a.Capabilities.WarpFree && m_b.Capabilities.WarpFree));

    private bool Nearer(FixedPosition position, out bool useB) {
        var hasA = m_a.TryDistance(
            distance: out var da,
            material: out _,
            position: position
        );
        var hasB = m_b.TryDistance(
            distance: out var db,
            material: out _,
            position: position
        );

        useB = (hasB && (!hasA || (db < da)));

        return (hasA || hasB);
    }

    /// <inheritdoc/>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
        var hasA = m_a.TryDistance(
            distance: out var da,
            material: out var ma,
            position: position
        );
        var hasB = m_b.TryDistance(
            distance: out var db,
            material: out var mb,
            position: position
        );

        if (hasA && hasB) {
            var useB = (db < da);

            distance = (useB ? db : da);
            material = (useB ? mb : ma);

            return true;
        }

        distance = (hasA ? da : db);
        material = (hasA ? ma : mb);

        return (hasA || hasB);
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) {
        if (!Nearer(position: position, useB: out var useB)) {
            gradient = default;

            return false;
        }

        return (useB ? m_b : m_a).TryFieldGradient(
            gradient: out gradient,
            position: position
        );
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) {
        if (!Nearer(position: position, useB: out var useB)) {
            gradient = default;

            return false;
        }

        return (useB ? m_b : m_a).TryFieldGradient(
            epsilon: epsilon,
            gradient: out gradient,
            position: position
        );
    }
}
