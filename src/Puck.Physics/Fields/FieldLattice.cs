using System.Globalization;
using System.Text;
using Puck.Maths;
using Puck.State;

namespace Puck.Physics.Fields;

/// <summary>The body-position and state-row seam a <see cref="FieldLattice.Step"/> reaches through — implemented
/// once by the owning host so a step reaches body state through a plain interface call rather than allocating fresh
/// delegates every tick. Every method receives the stepping tick explicitly since the interface itself carries none.</summary>
public interface IFieldLatticeHost {
    /// <summary>Resolves an active body's position, or <see langword="null"/> for an inactive slot.</summary>
    /// <param name="body">The body index.</param>
    FixedVector3? BodyPosition(int body);
    /// <summary>Reads a typed keyed int state row's cell for a body index (0 when absent).</summary>
    /// <param name="row">The compiled state row handle.</param>
    /// <param name="body">The body index used as the cell's key.</param>
    /// <param name="tick">The stepping tick.</param>
    long ReadTag(StateHandle row, int body, ulong tick);
    /// <summary>Writes a typed keyed int state row's cell for a body index.</summary>
    /// <param name="row">The compiled state row handle.</param>
    /// <param name="body">The body index used as the cell's key.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="tick">The stepping tick.</param>
    void WriteTag(StateHandle row, int body, long value, ulong tick);
    /// <summary>Reads a scalar fixed-kind state row's slot cell for a row-referenced reaction scalar (0 when absent
    /// or unwritten).</summary>
    /// <param name="row">The compiled state row handle.</param>
    /// <param name="tick">The stepping tick.</param>
    FixedQ4816 ReadScalar(StateHandle row, ulong tick);
    /// <summary>Adds to a scalar fixed-kind state row's slot cell, clamped to the row's declared envelope (a
    /// <see cref="FieldReactionInput.Flow"/> spill accumulator) — the host resolves and applies the clamp itself, so
    /// this never refuses.</summary>
    /// <param name="row">The compiled state row handle.</param>
    /// <param name="amount">The raw amount to add.</param>
    /// <param name="tick">The stepping tick.</param>
    void AddScalar(StateHandle row, FixedQ4816 amount, ulong tick);
}
/// <summary>A field lattice's own free surface at one column — a point on it and the lattice's own frame normal
/// (always world +Y: the lattice carries no rotation of its own). A medium hold's law projects displacement along
/// the body's own resolved gravity-up rather than this normal, so a medium inside a tilted gravity area still
/// measures depth along the axis that governs it; the lattice's own normal stays world +Y either way, so a curved
/// (planetoid) surface is not expressible this way, only a uniform tilted one.</summary>
/// <param name="Point">A point on the surface, over the sampled position's own column.</param>
/// <param name="Normal">The lattice's own frame normal.</param>
public readonly record struct FixedFieldSurface(FixedVector3 Point, FixedVector3 Normal);
/// <summary>
/// The live cell values of a world's field lattice and the reactions that evolve them — simulation state beside the
/// population: stepped on the lattice's own cadence, checkpointed, and delivered to clients as cell deltas. Values
/// are <see cref="FixedQ4816"/>; every reaction is integer arithmetic in a fixed cell order, so the same input
/// reproduces the same fields bit for bit. The kernel parses no document: it is built and reinstalled from a plain
/// <see cref="FieldLatticeInput"/> the host compiles once from its own document.
/// </summary>
public sealed class FieldLattice {
    private static readonly FixedVector3 UnitY = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    private readonly FixedQ4816 m_cellSize;
    private readonly int m_depth;
    private readonly List<int> m_deltas = [];
    private readonly bool[][] m_deltaDirty;

    private FieldLatticeInput m_input;
    private ReactionSets[] m_reactionSets;

    private readonly FixedQ4816[] m_heightScale;
    private readonly bool[] m_isMedium;
    private readonly int m_layers;
    private readonly FixedQ4816 m_bodyCouplingCeiling;
    private readonly FixedQ4816[] m_max;
    private readonly FixedQ4816[] m_min;
    private readonly string[] m_names;
    private readonly FixedVector3 m_origin;

    private readonly FixedQ4816[] m_scratch;
    private readonly Int128[] m_flowDelta;
    private readonly FixedQ4816[] m_flowHeights;
    private readonly int m_flowDirections;
    private readonly int m_stepEveryTicks;
    private readonly FixedQ4816[][] m_values;
    private readonly ulong[] m_valueRevisions;
    private readonly int m_width;

    private bool m_fullResync = true;
    private int m_revision;
    private int m_cellNodeCount;
    private int m_cellPassCount;
    private int m_bodyPassCount;

    /// <summary>One captured lattice: raw Q48.16 cell values per field, field-major.</summary>
    /// <param name="Raw">The raw values, one array per declared field.</param>
    public sealed record Checkpoint(IReadOnlyList<long[]> Raw);
    /// <summary>One cell whose value changed since the last take.</summary>
    /// <param name="Cell">The cell index.</param>
    /// <param name="Field">The field ordinal.</param>
    /// <param name="Raw">The cell's raw <see cref="FixedQ4816"/> bits after the change.</param>
    public readonly record struct Delta(int Cell, byte Field, long Raw);
    // The canonical field/state read and write sets one compiled reaction carries — computed once per install, from
    // the plain reaction records, the same shape the document-side compiler derives for its own dependency plan.
    private readonly record struct ReactionSets(bool IsCellWork, int[] FieldReads, int[] FieldWrites, StateHandle[] StateReads, StateHandle[] StateWrites);

    /// <summary>Creates the live lattice from its complete input. The constructor recomputes nothing the host has
    /// already resolved.</summary>
    /// <param name="input">The complete field-lattice input.</param>
    /// <param name="worldSeed">The deterministic paint seed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public FieldLattice(FieldLatticeInput input, ulong worldSeed = 0UL) {
        ArgumentNullException.ThrowIfNull(argument: input);

        m_input = input;
        m_width = input.Lattice.Width;
        m_depth = input.Lattice.Depth;
        m_layers = input.Lattice.Layers;
        m_cellSize = input.Lattice.CellSize;
        m_origin = input.Lattice.Origin;
        m_stepEveryTicks = input.Lattice.StepEveryTicks;

        var fields = input.Fields;

        m_names = new string[fields.Count];
        m_min = new FixedQ4816[fields.Count];
        m_max = new FixedQ4816[fields.Count];
        m_heightScale = new FixedQ4816[fields.Count];
        m_isMedium = new bool[fields.Count];
        m_values = new FixedQ4816[fields.Count][];
        m_valueRevisions = new ulong[fields.Count];
        m_deltaDirty = new bool[fields.Count][];
        m_scratch = new FixedQ4816[CellCount];
        m_flowDelta = new Int128[CellCount];
        m_flowHeights = new FixedQ4816[CellCount];
        // Every cell donates an equal share to each of the lattice's active-axis directions -- an axis with a
        // single cell (Layers = 1 on a ground lattice) has no directions at all, never a "missing neighbour".
        m_flowDirections = (
            (((m_width > 1) ? 2 : 0) +
            ((m_depth > 1) ? 2 : 0)) +
            ((m_layers > 1) ? 2 : 0)
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

            Array.Fill(
                array: m_values[field],
                value: row.Initial
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

        m_reactionSets = CompileReactionSets(reactions: input.Reactions);
        RecomputePassCounts();

        foreach (var fill in input.Paint) {
            if (fill is not FieldFillInput.DrawMarker) {
                ApplyPaintFill(
                    field: fill.Field,
                    fill: fill,
                    trackDeltas: false,
                    worldSeed: worldSeed
                );
            }
        }

    }

    /// <summary>Gets the declared reaction count.</summary>
    public int ReactionCount => m_input.Reactions.Count;
    /// <summary>Gets the declared step cadence in simulation ticks.</summary>
    public int StepEveryTicks => m_stepEveryTicks;
    /// <summary>Gets the lattice's cell count (width × layers × depth).</summary>
    public int CellCount => ((m_width * m_layers) * m_depth);
    /// <summary>Gets the declared cubic cell edge.</summary>
    public FixedQ4816 CellSize => m_cellSize;
    /// <summary>Gets the installed input.</summary>
    public FieldLatticeInput Input => m_input;
    /// <summary>Gets the number of declared fields.</summary>
    public int FieldCount => m_values.Length;
    /// <summary>Gets the lattice's minimum corner.</summary>
    public FixedVector3 Origin => m_origin;
    /// <summary>Gets a counter that moves on every cell write.</summary>
    public int Revision => m_revision;
    /// <summary>Gets a derived invalidation stamp for one field, not simulation truth — a caller compares it against
    /// a value it last observed rather than reading it as a value in its own right. Restore stamps it anew after
    /// installing the saved values.</summary>
    /// <param name="field">The field index.</param>
    public ulong ValueRevision(int field) => m_valueRevisions[field];

    // The field portion of the host's authoritative state-hash boundary. Field-major/cell-major is the same
    // canonical order Capture and the checkpoint codec use, without allocating a checkpoint-shaped jagged array.
    /// <summary>Folds every field's declared name and cell value into a running hash, in field-major/cell-major
    /// order.</summary>
    /// <param name="hash">The running hash.</param>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)FieldCount));
        hash.Add(value: ((uint)CellCount));

        for (var field = 0; (field < FieldCount); field++) {
            hash.Add(value: Fnv1aHash.Compute(values: m_names[field].AsSpan()));

            for (var cell = 0; (cell < CellCount); cell++) {
                hash.Add(value: m_values[field][cell].Value);
            }
        }
    }

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

        var cellVisits = checked((((long)CellCount) * m_cellPassCount));
        var bodySlotVisits = checked((((long)bodyCapacity) * m_bodyPassCount));

        return $"lattice {m_input.Reactions.Count} node(s) every {m_stepEveryTicks} tick(s): {CellCount} cell(s) x {m_cellPassCount} pass(es) = {cellVisits} cell visit(s); bodies {activeBodyCount}/{bodyCapacity} active/capacity x {m_bodyPassCount} pass(es) = {bodySlotVisits} slot visit(s)";
    }
    /// <summary>Checks whether a replacement input can be installed without reallocating or reseeding cell storage.
    /// Reaction-only, colour, and paint changes are compatible; topology, cadence, and field envelope changes
    /// require a host restart.</summary>
    /// <param name="input">The candidate input.</param>
    /// <param name="reason">The named incompatibility on refusal; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the candidate can replace the live input without migrating cells.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public bool CanInstallInput(FieldLatticeInput input, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: input);

        if (
            (input.Lattice != m_input.Lattice) ||
            (input.Fields.Count != m_input.Fields.Count)
        ) {
            reason = "the field lattice topology or cadence differs from the live allocation; restart the host to load it";

            return false;
        }

        for (var index = 0; (index < input.Fields.Count); index++) {
            var current = m_input.Fields[index];
            var candidate = input.Fields[index];

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
    /// <summary>Installs a compatible replacement input while retaining every live cell, pending delta, revision,
    /// and checkpoint shape.</summary>
    /// <param name="input">The replacement input.</param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The input requires a live lattice allocation migration.</exception>
    public void InstallInput(FieldLatticeInput input) {
        if (!CanInstallInput(
            input: input,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: reason);
        }

        m_input = input;
        m_reactionSets = CompileReactionSets(reactions: input.Reactions);
        RecomputePassCounts();
    }

    private void RecomputePassCounts() {
        m_cellNodeCount = m_reactionSets.Count(predicate: static sets => sets.IsCellWork);
        m_cellPassCount = 0;

        for (var index = 0; (index < m_input.Reactions.Count); index++) {
            m_cellPassCount += m_input.Reactions[index] switch {
                FieldReactionInput.Diffuse => 2,
                FieldReactionInput.Flow => 2,
                _ when m_reactionSets[index].IsCellWork => 1,
                _ => 0,
            };
        }

        m_bodyPassCount = (m_reactionSets.Length - m_cellNodeCount);
    }
    // Mirrors the document-side compiler's canonical read/write set derivation over the plain reaction records, so
    // the dependency plan a read-back reports never depends on which layer compiled the reactions.
    private static ReactionSets[] CompileReactionSets(IReadOnlyList<FieldReactionInput> reactions) {
        var sets = new ReactionSets[reactions.Count];

        for (var index = 0; (index < reactions.Count); index++) {
            sets[index] = reactions[index] switch {
                FieldReactionInput.Diffuse diffuse => new ReactionSets(true, [diffuse.Field], [diffuse.Field], StateReads(input: diffuse.Rate), []),
                FieldReactionInput.Decay decay => new ReactionSets(true, [decay.Field], [decay.Field], StateReads(input: decay.Rate), []),
                FieldReactionInput.Transform transform => CompileTransformSets(transform: transform),
                FieldReactionInput.Emit emit => new ReactionSets(
                    false,
                    [emit.Field],
                    [emit.Field],
                    CanonicalStates(inputs: [new FieldScalarInput(Literal: default, State: emit.Tag), emit.Amount]),
                    []
                ),
                FieldReactionInput.Expose expose => new ReactionSets(false, [expose.Field], [], StateReads(input: expose.Value), [expose.Row]),
                FieldReactionInput.Flow flow => CompileFlowSets(flow: flow),
                _ => throw new InvalidOperationException(message: "unknown field reaction kind."),
            };
        }

        return sets;
    }
    private static ReactionSets CompileTransformSets(FieldReactionInput.Transform transform) {
        var fieldReads = transform.When
            .Select(selector: static condition => condition.Field)
            .Concat(second: transform.Then
                .Where(predicate: static write => (write.Op == FieldWriteOp.Add))
                .Select(selector: static write => write.Field));
        var stateReads = transform.When.Select(selector: static condition => condition.Value)
            .Concat(second: transform.Then.Select(selector: static write => write.Value));

        return new ReactionSets(
            true,
            CanonicalFields(fields: fieldReads),
            CanonicalFields(fields: transform.Then.Select(selector: static write => write.Field)),
            CanonicalStates(inputs: stateReads),
            []
        );
    }
    private static ReactionSets CompileFlowSets(FieldReactionInput.Flow flow) {
        var stateInputs = new List<FieldScalarInput> { flow.Rate };

        if (flow.SpillRow.IsValid) {
            stateInputs.Add(item: new FieldScalarInput(Literal: default, State: flow.SpillRow));
        }

        return new ReactionSets(
            true,
            CanonicalFields(fields: flow.Over.Append(element: flow.Field)),
            [flow.Field],
            CanonicalStates(inputs: stateInputs),
            (flow.SpillRow.IsValid ? [flow.SpillRow] : [])
        );
    }
    private static int[] CanonicalFields(IEnumerable<int> fields) => [.. fields.Distinct().OrderBy(keySelector: static field => field)];
    private static StateHandle[] CanonicalStates(IEnumerable<FieldScalarInput> inputs) => [.. inputs
        .Where(predicate: static input => input.IsState)
        .Select(selector: static input => input.State)
        .Distinct()
        .OrderBy(keySelector: static handle => handle.Ordinal)];
    private static StateHandle[] StateReads(FieldScalarInput input) => (input.IsState ? [input.State] : []);
    private static bool Conflicts(ReactionSets earlier, ReactionSets later) => (
        Intersects(left: earlier.FieldWrites, right: later.FieldReads) ||
        Intersects(left: earlier.FieldWrites, right: later.FieldWrites) ||
        Intersects(left: earlier.FieldReads, right: later.FieldWrites) ||
        Intersects(left: earlier.StateWrites, right: later.StateReads) ||
        Intersects(left: earlier.StateWrites, right: later.StateWrites) ||
        Intersects(left: earlier.StateReads, right: later.StateWrites)
    );
    private static bool Intersects<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) where T : IEquatable<T> {
        for (var leftIndex = 0; (leftIndex < left.Count); leftIndex++) {
            for (var rightIndex = 0; (rightIndex < right.Count); rightIndex++) {
                if (left[leftIndex].Equals(other: right[rightIndex])) {
                    return true;
                }
            }
        }

        return false;
    }

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
    private void Write(int field, int cell, FixedQ4816 value) {
        var clamped = Clamp(
            field: field,
            value: value
        );

        if (m_values[field][cell] == clamped) {
            return;
        }

        m_values[field][cell] = clamped;
        m_valueRevisions[field]++;

        if (!m_deltaDirty[field][cell]) {
            m_deltaDirty[field][cell] = true;
            m_deltas.Add(item: ((field * CellCount) + cell));
        }

        m_revision++;
    }

    /// <summary>Sets or adds one bounded spherical neighborhood. Coordinates and radius are lattice-cell units;
    /// cells outside the topology are clipped and every written value is clamped to the field envelope.</summary>
    /// <param name="fieldName">The declared field row to paint.</param>
    /// <param name="centerX">The sphere center on the lattice X axis, in cells.</param>
    /// <param name="centerY">The sphere center on the lattice Y axis, in cells.</param>
    /// <param name="centerZ">The sphere center on the lattice Z axis, in cells.</param>
    /// <param name="radius">The non-negative sphere radius, in cells.</param>
    /// <param name="operation">The set or add operation.</param>
    /// <param name="value">The value to set or add before the field envelope is applied.</param>
    /// <returns>The number of cells whose value changed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or
    /// <paramref name="operation"/> is not defined.</exception>
    public int PaintSphere(string fieldName, int centerX, int centerY, int centerZ, int radius, FieldWriteOp operation, FixedQ4816 value) {
        if (!TryFieldIndex(name: fieldName, field: out var field)) {
            return 0;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(value: radius);
        if (!Enum.IsDefined(value: operation)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(operation), actualValue: operation, message: "The field write operation is not defined.");
        }

        var minimumX = Math.Max(0, centerX - radius);
        var maximumX = Math.Min(m_width - 1, centerX + radius);
        var minimumY = Math.Max(0, centerY - radius);
        var maximumY = Math.Min(m_layers - 1, centerY + radius);
        var minimumZ = Math.Max(0, centerZ - radius);
        var maximumZ = Math.Min(m_depth - 1, centerZ + radius);
        var radiusSquared = checked(radius * radius);
        var changed = 0;

        for (var z = minimumZ; z <= maximumZ; z++) {
            var dz = (z - centerZ);
            for (var y = minimumY; y <= maximumY; y++) {
                var dy = (y - centerY);
                for (var x = minimumX; x <= maximumX; x++) {
                    var dx = (x - centerX);
                    if (checked((dx * dx) + (dy * dy) + (dz * dz)) > radiusSquared) {
                        continue;
                    }

                    var cell = CellIndex(x: x, y: y, z: z);
                    var before = m_values[field][cell];
                    var requested = ((operation == FieldWriteOp.Add)
                        ? AddClamped(field: field, x: before, y: value)
                        : value
                    );
                    Write(field: field, cell: cell, value: requested);
                    if (m_values[field][cell] != before) {
                        changed++;
                    }
                }
            }
        }

        return changed;
    }

    /// <summary>Resolves the cell a BODY couples to for the <see cref="FieldReactionInput.Emit"/>/
    /// <see cref="FieldReactionInput.Expose"/> reactions: the column under the body, with Y admitted up to the
    /// lattice's derived coupling ceiling (the volume's top plus the tallest surface any height-bearing field can
    /// raise) and clamped onto the top layer. A bare <see cref="TryCellOf"/> requires the position INSIDE the voxel
    /// volume, which no body standing ON a one-layer ground lattice ever is — its feet rest on the raised surface,
    /// above the half-unit slab — so body-coupled reactions would never fire on the documented ground-lattice
    /// shape.</summary>
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
    /// coupling <see cref="TryBodyCellOf"/> resolves for <see cref="FieldReactionInput.Emit"/>/<see cref="FieldReactionInput.Expose"/>).
    /// <see langword="null"/> when the body lies outside the lattice's coupling ceiling, or every medium field reads
    /// zero or less there. Returns a point over <paramref name="position"/>'s own column at that height, and the
    /// lattice's own frame normal — a caller measuring depth under a tilted gravity area projects along its OWN
    /// resolved up instead of this normal.</summary>
    /// <param name="position">The body's world position.</param>
    /// <returns>The surface, or <see langword="null"/> for no medium.</returns>
    public FixedFieldSurface? MediumSurface(in FixedVector3 position) {
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

        return ((best is { } height)
            ? new FixedFieldSurface(
                Normal: UnitY,
                Point: new FixedVector3(X: position.X, Y: height, Z: position.Z)
            )
            : null
        );
    }
    /// <summary>Reports whether a point lies inside one named live medium field: the same authored-field identity
    /// and body-coupling ceiling <see cref="MediumSurface"/> resolves through, narrowed to one field rather than
    /// the tallest wet one; medium-constrained navigation uses it to keep swimmer routes in their fluid.</summary>
    public bool IsInsideMedium(string name, in FixedVector3 position) {
        return TryFieldIndex(name: name, field: out var field) && IsInsideMedium(field: field, position: in position);
    }
    /// <summary>Reports whether a point lies inside one compiled live medium-field ordinal. This is the hot-path
    /// counterpart of <see cref="IsInsideMedium(string, in FixedVector3)"/>: navigation resolves the authored name
    /// once when its domain is built, then avoids a name-table scan for every node an A* search examines.</summary>
    public bool IsInsideMedium(int field, in FixedVector3 position) {
        return IsInsideMediumPoint(field: field, position: in position);
    }
    /// <summary>Reports whether an axis-aligned clearance cube around a point remains inside a live medium. Clearance
    /// must be in [0, half a lattice cell]. Every intersected voxel and its local free surface are checked; wet corners
    /// alone cannot prove the interior wet. The cube conservatively encloses an agent sphere.</summary>
    public bool IsInsideMedium(int field, in FixedVector3 position, FixedQ4816 clearance) {
        if (clearance < FixedQ4816.Zero || clearance.Value > m_cellSize.Value / 2) {
            return false;
        }
        return IsMediumBox(field, (Int128)position.X.Value - clearance.Value, (Int128)position.Y.Value - clearance.Value,
            (Int128)position.Z.Value - clearance.Value, (Int128)position.X.Value + clearance.Value,
            (Int128)position.Y.Value + clearance.Value, (Int128)position.Z.Value + clearance.Value);
    }
    // A field's free surface (value * heightScale over the origin) is unbounded by its own topology's layer count
    // — the same reach TryBodyCellOf already admits for reaction coupling — so the coupled cell is resolved through
    // it (clamped onto the top layer) rather than through a bare TryCellOf, which would refuse any column whose
    // surface rises past one voxel.
    private bool IsInsideMediumPoint(int field, in FixedVector3 position) {
        if ((uint)field >= (uint)m_isMedium.Length || !m_isMedium[field] || !TryBodyCellOf(position: in position, cell: out var cell)) {
            return false;
        }
        var value = m_values[field][cell];
        return value > FixedQ4816.Zero && position.Y <= (m_origin.Y + (value * m_heightScale[field]));
    }
    /// <summary>Conservatively proves an entire clearance-cube sweep inside one live medium's free surface. Each
    /// half-cell-or-shorter piece checks its swept bounding box against the coupled column's surface height, not
    /// just sample points. Clearance above half a cell, an invalid field, or a segment exceeding the caller's
    /// subdivision ceiling refuses. Outward-rounded endpoints cannot leave a sub-quantum gap in the proof.</summary>
    public bool IsSegmentInsideMedium(int field, in FixedVector3 from, in FixedVector3 to, FixedQ4816 clearance, int maximumSubdivisions) {
        if (maximumSubdivisions <= 0 || clearance < FixedQ4816.Zero || clearance.Value > m_cellSize.Value / 2 ||
            (uint)field >= (uint)m_isMedium.Length || !m_isMedium[field]) {
            return false;
        }
        var maximum = Int128.Max(Int128.Abs((Int128)to.X.Value - from.X.Value),
            Int128.Max(Int128.Abs((Int128)to.Y.Value - from.Y.Value), Int128.Abs((Int128)to.Z.Value - from.Z.Value)));
        var interval = Math.Max(1, m_cellSize.Value / 2);
        var subdivisions = maximum / interval + (maximum % interval == 0 ? 0 : 1);
        if (subdivisions > maximumSubdivisions) {
            return false;
        }
        var count = Math.Max(1, checked((int)subdivisions));
        for (var index = 0; index < count; index++) {
            SegmentAxisBounds(from.X.Value, to.X.Value, index, count, clearance.Value, out var minX, out var maxX);
            SegmentAxisBounds(from.Y.Value, to.Y.Value, index, count, clearance.Value, out var minY, out var maxY);
            SegmentAxisBounds(from.Z.Value, to.Z.Value, index, count, clearance.Value, out var minZ, out var maxZ);
            if (!IsMediumBox(field, minX, minY, minZ, maxX, maxY, maxZ)) {
                return false;
            }
        }
        return true;
    }

    private static void SegmentAxisBounds(long from, long to, int piece, int count, long clearance, out Int128 minimum, out Int128 maximum) {
        // Keep the segment parameter rational until the final outward round. Multiplication is at most 96 bits.
        var delta = (Int128)to - from;
        var first = (Int128)from * count + delta * piece;
        var second = first + delta;
        var low = Int128.Min(first, second);
        var high = Int128.Max(first, second);
        minimum = low / count - (low % count < 0 ? 1 : 0) - clearance;
        maximum = high / count + (high % count > 0 ? 1 : 0) + clearance;
    }

    // Every voxel layer the box actually spans is checked on its own value: a dry cap between wet layers must
    // still refuse. Only the TOP visited layer is special: a field's free surface (value * heightScale over the
    // origin) is unbounded by its own topology's layer count — the same reach TryBodyCellOf admits for reaction
    // coupling — so a box whose top rises past the voxel grid (up to the body-coupling ceiling) checks that
    // layer's value against the box's true top rather than the layer's own slab top; every layer below it must
    // be wet clear to ITS own slab top, since the box continues past it regardless of what lies above.
    private bool IsMediumBox(int field, Int128 minX, Int128 minY, Int128 minZ, Int128 maxX, Int128 maxY, Int128 maxZ) {
        if ((uint)field >= (uint)m_isMedium.Length || !m_isMedium[field]) { return false; }
        minX -= m_origin.X.Value; maxX -= m_origin.X.Value;
        minY -= m_origin.Y.Value; maxY -= m_origin.Y.Value;
        minZ -= m_origin.Z.Value; maxZ -= m_origin.Z.Value;
        var size = m_cellSize.Value;
        if (minX < 0 || minY < 0 || minZ < 0 || maxX >= (Int128)size * m_width ||
            maxY > m_bodyCouplingCeiling.Value || maxZ >= (Int128)size * m_depth) { return false; }
        var x0 = (int)(minX / size); var x1 = (int)(maxX / size);
        var z0 = (int)(minZ / size); var z1 = (int)(maxZ / size);
        var y0 = (int)Int128.Min(minY / size, m_layers - 1);
        var y1 = (int)Int128.Min(maxY / size, m_layers - 1);
        for (var z = z0; z <= z1; z++) {
            for (var y = y0; y <= y1; y++) {
                var requiredHeight = (y == m_layers - 1) ? maxY : Int128.Min(maxY, (Int128)(y + 1) * size);
                for (var x = x0; x <= x1; x++) {
                    var value = m_values[field][CellIndex(x, y, z)];
                    if (value <= FixedQ4816.Zero || requiredHeight > (value * m_heightScale[field]).Value) { return false; }
                }
            }
        }
        return true;
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

        foreach (var fill in m_input.Paint) {
            if (fill.Field != field) {
                continue;
            }

            if (fill is FieldFillInput.DrawMarker) {
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

    private void ApplyPaintFill(int field, FieldFillInput fill, bool trackDeltas, ulong worldSeed) {
        switch (fill) {
            case FieldFillInput.Noise noise:
                ApplyNoiseFill(
                    field: field,
                    fill: noise,
                    trackDeltas: trackDeltas,
                    worldSeed: worldSeed
                );
                break;
            case FieldFillInput.Scatter scatter:
                ApplyScatterFill(
                    field: field,
                    fill: scatter,
                    trackDeltas: trackDeltas,
                    worldSeed: worldSeed
                );
                break;
            case FieldFillInput.Rect rect:
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
            m_valueRevisions[field]++;
        }
    }
    private void ApplyRectFill(int field, FieldFillInput.Rect fill, bool trackDeltas) {
        var value = Clamp(
            field: field,
            value: fill.Value
        );
        var half = (m_cellSize / FixedQ4816.FromInteger(value: 2));

        for (var z = 0; (z < m_depth); z++) {
            var centreZ = ((m_origin.Z + (m_cellSize * FixedQ4816.FromInteger(value: z))) + half);

            if ((centreZ < fill.MinZ) || (centreZ > fill.MaxZ)) {
                continue;
            }

            for (var x = 0; (x < m_width); x++) {
                var centreX = ((m_origin.X + (m_cellSize * FixedQ4816.FromInteger(value: x))) + half);

                if ((centreX < fill.MinX) || (centreX > fill.MaxX)) {
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
    private void ApplyNoiseFill(int field, FieldFillInput.Noise fill, bool trackDeltas, ulong worldSeed) {
        var value = Clamp(
            field: field,
            value: fill.Value
        );
        var one = FixedQ4816.One;
        var span = (one - fill.Threshold);
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

                if (n < fill.Threshold) {
                    continue;
                }

                var scaled = ((span.Value > 0) ? (value * ((n - fill.Threshold) / span)) : value);

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
    private void ApplyScatterFill(int field, FieldFillInput.Scatter fill, bool trackDeltas, ulong worldSeed) {
        var value = Clamp(
            field: field,
            value: fill.Value
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
    /// host is invoked directly (no per-call delegate is allocated) so a lattice pays nothing beyond the cadence
    /// check on the ticks it does not react on.</summary>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="bodyCount">The entity-table capacity; bodies are visited by index.</param>
    /// <param name="host">The body-position and state-row seam.</param>
    public void Step(ulong tick, int bodyCount, IFieldLatticeHost host) {
        if ((tick % ((ulong)m_stepEveryTicks)) != 0UL) {
            return;
        }

        ArgumentNullException.ThrowIfNull(argument: host);

        foreach (var reaction in m_input.Reactions) {
            switch (reaction) {
                case FieldReactionInput.Diffuse diffuse:
                    StepDiffuse(
                        field: diffuse.Field,
                        rate: ClampRate(rate: Resolve(host: host, input: diffuse.Rate, tick: tick))
                    );
                    break;
                case FieldReactionInput.Decay decay:
                    StepDecay(
                        field: decay.Field,
                        rate: ClampRate(rate: Resolve(host: host, input: decay.Rate, tick: tick))
                    );
                    break;
                case FieldReactionInput.Transform transform:
                    StepTransform(
                        host: host,
                        reaction: transform,
                        tick: tick
                    );
                    break;
                case FieldReactionInput.Emit emit:
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
                            field: emit.Field,
                            value: AddClamped(
                                field: emit.Field,
                                x: m_values[emit.Field][cell],
                                y: Resolve(host: host, input: emit.Amount, tick: tick)
                            )
                        );
                    }

                    break;
                case FieldReactionInput.Flow flow:
                    StepFlow(
                        reaction: flow,
                        rate: ClampRate(rate: Resolve(host: host, input: flow.Rate, tick: tick)),
                        host: host,
                        tick: tick
                    );
                    break;
                case FieldReactionInput.Expose expose:
                    for (var body = 0; (body < bodyCount); body++) {
                        if (host.BodyPosition(body: body) is not { } position) {
                            continue;
                        }

                        var exposed = (TryBodyCellOf(
                            cell: out var cell,
                            position: in position
                        ) && expose.Comparison.Holds(
                            expected: Resolve(host: host, input: expose.Value, tick: tick),
                            value: m_values[expose.Field][cell]
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

    private static FixedQ4816 Resolve(IFieldLatticeHost host, FieldScalarInput input, ulong tick) => (input.IsState
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
    private void StepTransform(FieldReactionInput.Transform reaction, IFieldLatticeHost host, ulong tick) {
        // Row-referenced terms resolve ONCE per step, before the cell loop — a season row's value is a step-wide
        // constant, never a per-cell read.
        var whenValues = new FixedQ4816[reaction.When.Count];
        var thenValues = new FixedQ4816[reaction.Then.Count];

        for (var index = 0; (index < reaction.When.Count); index++) {
            whenValues[index] = Resolve(host: host, input: reaction.When[index].Value, tick: tick);
        }
        for (var index = 0; (index < reaction.Then.Count); index++) {
            thenValues[index] = Resolve(host: host, input: reaction.Then[index].Value, tick: tick);
        }

        for (var cell = 0; (cell < CellCount); cell++) {
            var holds = true;

            for (var index = 0; (index < reaction.When.Count); index++) {
                var condition = reaction.When[index];

                if (!condition.Comparison.Holds(
                    expected: whenValues[index],
                    value: m_values[condition.Field][cell]
                )) {
                    holds = false;
                    break;
                }
            }

            if (!holds) {
                continue;
            }

            for (var index = 0; (index < reaction.Then.Count); index++) {
                var write = reaction.Then[index];

                Write(
                    cell: cell,
                    field: write.Field,
                    value: ((write.Op == FieldWriteOp.Add)
                        ? AddClamped(
                            field: write.Field,
                            x: m_values[write.Field][cell],
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
    private void StepFlow(FieldReactionInput.Flow reaction, FixedQ4816 rate, IFieldLatticeHost host, ulong tick) {
        if (m_flowDirections == 0) {
            return;
        }

        var field = reaction.Field;
        var values = m_values[field];

        Array.Copy(
            sourceArray: values,
            destinationArray: m_scratch,
            length: values.Length
        );

        for (var cell = 0; (cell < CellCount); cell++) {
            var height = (m_scratch[cell] * m_heightScale[field]);

            foreach (var over in reaction.Over) {
                height += (m_values[over][cell] * m_heightScale[over]);
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
    public Delta[] TakeDeltas(bool full, out bool isFull) {
        if (full || m_fullResync) {
            var all = new Delta[(FieldCount * CellCount)];
            var index = 0;

            for (var field = 0; (field < FieldCount); field++) {
                for (var cell = 0; (cell < CellCount); cell++) {
                    all[index++] = new Delta(
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

        var taken = new Delta[m_deltas.Count];

        for (var index = 0; (index < m_deltas.Count); index++) {
            var key = m_deltas[index];
            var field = (key / CellCount);
            var cell = (key - (field * CellCount));

            taken[index] = new Delta(
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
    public Checkpoint Capture() {
        var raw = new long[FieldCount][];

        for (var field = 0; (field < FieldCount); field++) {
            raw[field] = new long[CellCount];

            for (var cell = 0; (cell < CellCount); cell++) {
                raw[field][cell] = m_values[field][cell].Value;
            }
        }

        return new Checkpoint(Raw: raw);
    }
    /// <summary>Validates that a checkpoint has this lattice's shape and declared value ranges.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    public void ValidateCheckpoint(Checkpoint checkpoint) {
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
    public void Restore(Checkpoint checkpoint) {
        ValidateCheckpoint(checkpoint: checkpoint);

        for (var field = 0; (field < FieldCount); field++) {
            for (var cell = 0; (cell < CellCount); cell++) {
                m_values[field][cell] = FixedQ4816.FromRawBits(value: checkpoint.Raw[field][cell]);
            }
        }

        ClearDeltas();
        m_fullResync = true;
        for (var field = 0; field < m_valueRevisions.Length; field++) { m_valueRevisions[field]++; }
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

            var color = (((m_heightScale[field] > FixedQ4816.Zero) && (m_input.Fields[field].Color is { } token))
                ? $" color={token}"
                : string.Empty
            );

            parts.Add(item: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{m_names[field]} nonzero={nonzero} mean={(sum / CellCount):0.###}{color}"
            ));
        }

        var plan = new StringBuilder();

        for (var index = 0; (index < m_input.Reactions.Count); index++) {
            if (index > 0) {
                plan.Append(value: ',');
            }

            plan.Append(value: index).Append(value: ':').Append(value: m_input.Reactions[index] switch {
                FieldReactionInput.Diffuse => "diffuse",
                FieldReactionInput.Decay => "decay",
                FieldReactionInput.Transform => "transform",
                FieldReactionInput.Emit => "emit",
                FieldReactionInput.Expose => "expose",
                FieldReactionInput.Flow => "flow",
                _ => "unknown",
            });
        }

        var dependencies = new StringBuilder();

        for (var after = 0; (after < m_reactionSets.Length); after++) {
            for (var before = 0; (before < after); before++) {
                if (Conflicts(earlier: m_reactionSets[before], later: m_reactionSets[after])) {
                    if (dependencies.Length > 0) {
                        dependencies.Append(value: ',');
                    }

                    dependencies.Append(value: before).Append(value: '>').Append(value: after);
                }
            }
        }

        return $"lattice {m_width}x{m_layers}x{m_depth} @ {((double)m_cellSize)} every {m_stepEveryTicks} ticks: {string.Join(
            separator: " | ",
            values: parts
        )} | plan nodes={m_input.Reactions.Count} cellPasses={m_cellPassCount} bodyPasses={m_bodyPassCount} order=[{plan}] dependencies=[{dependencies}]";
    }
}
/// <summary>A contact field over a <see cref="FieldLattice"/>'s height columns: the signed distance to the union
/// of column boxes, exact within two cells of a column and a conservative lower bound beyond.</summary>
public sealed class FieldLatticeSolid : IFieldEvaluator {
    private const int Reach = 2;

    private readonly FieldLattice m_lattice;

    public FieldLatticeSolid(FieldLattice lattice) {
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
public sealed class UnionField : IFieldEvaluator {
    private readonly IFieldEvaluator m_a;
    private readonly IFieldEvaluator m_b;

    public UnionField(IFieldEvaluator a, IFieldEvaluator b) {
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
