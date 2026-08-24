using System.Globalization;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

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
    private readonly WorldFieldsSection m_document;
    private readonly FixedQ4816[] m_heightScale;
    private readonly int m_layers;
    private readonly FixedQ4816 m_bodyCouplingCeiling;
    private readonly FixedQ4816[] m_max;
    private readonly FixedQ4816[] m_min;
    private readonly string[] m_names;
    private readonly FixedVector3 m_origin;
    private readonly CompiledReaction[] m_reactions;
    private readonly FixedQ4816[] m_scratch;
    private readonly int m_stepEveryTicks;
    private readonly FixedQ4816[][] m_values;
    private readonly int m_width;
    private bool m_fullResync = true;
    private int m_revision;

    /// <summary>One captured lattice: raw Q48.16 cell values per field, field-major.</summary>
    /// <param name="Raw">The raw values, one array per declared field.</param>
    public sealed record WorldFieldCheckpoint(IReadOnlyList<long[]> Raw);

    private readonly record struct CompiledCondition(int Field, WorldFieldComparison Comparison, FixedQ4816 Value);
    private readonly record struct CompiledWrite(int Field, WorldFieldWriteOp Op, FixedQ4816 Value);
    private readonly record struct CompiledReaction(
        WorldReaction Kind,
        int Field,
        FixedQ4816 Rate,
        CompiledCondition[] When,
        CompiledWrite[] Then,
        string? Row,
        WorldFieldComparison Comparison,
        FixedQ4816 Value
    );

    public WorldFieldLattice(WorldFieldsSection document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        m_document = document;
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

        var fields = document.Fields;

        m_names = new string[fields.Count];
        m_min = new FixedQ4816[fields.Count];
        m_max = new FixedQ4816[fields.Count];
        m_heightScale = new FixedQ4816[fields.Count];
        m_values = new FixedQ4816[fields.Count][];
        m_deltaDirty = new bool[fields.Count][];
        m_scratch = new FixedQ4816[CellCount];

        for (var field = 0; (field < fields.Count); field++) {
            var row = fields[field];

            m_names[field] = row.Name;
            m_min[field] = FixedQ4816.FromDouble(value: row.Min);
            m_max[field] = FixedQ4816.FromDouble(value: row.Max);
            m_heightScale[field] = FixedQ4816.FromDouble(value: row.HeightScale);
            m_values[field] = new FixedQ4816[CellCount];
            m_deltaDirty[field] = new bool[CellCount];

            var initial = FixedQ4816.FromDouble(value: row.Initial);

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

        foreach (var paint in (document.Paint ?? [])) {
            var field = FieldIndex(name: paint.Field);
            var value = Clamp(
                field: field,
                value: FixedQ4816.FromDouble(value: paint.Value)
            );
            var minX = FixedQ4816.FromDouble(value: paint.MinX);
            var maxX = FixedQ4816.FromDouble(value: paint.MaxX);
            var minZ = FixedQ4816.FromDouble(value: paint.MinZ);
            var maxZ = FixedQ4816.FromDouble(value: paint.MaxZ);
            var half = (m_cellSize / FixedQ4816.FromInteger(value: 2));

            for (var z = 0; (z < m_depth); z++) {
                var centreZ = ((m_origin.Z + (m_cellSize * FixedQ4816.FromInteger(value: z))) + half);

                if (
                    (centreZ < minZ) ||
                    (centreZ > maxZ)
                ) {
                    continue;
                }

                for (var x = 0; (x < m_width); x++) {
                    var centreX = ((m_origin.X + (m_cellSize * FixedQ4816.FromInteger(value: x))) + half);

                    if (
                        (centreX < minX) ||
                        (centreX > maxX)
                    ) {
                        continue;
                    }

                    for (var y = 0; (y < m_layers); y++) {
                        m_values[field][CellIndex(x: x, y: y, z: z)] = value;
                    }
                }
            }
        }

        var reactions = (document.Reactions ?? []);

        m_reactions = new CompiledReaction[reactions.Count];

        for (var index = 0; (index < reactions.Count); index++) {
            m_reactions[index] = reactions[index] switch {
                WorldReaction.Diffuse diffuse => new CompiledReaction(
                Kind: diffuse,
                Field: FieldIndex(name: diffuse.Field),
                Rate: FixedQ4816.FromDouble(value: diffuse.Rate),
                When: [],
                Then: [],
                Row: null,
                Comparison: default,
                Value: default
            ),
                WorldReaction.Decay decay => new CompiledReaction(
                Kind: decay,
                Field: FieldIndex(name: decay.Field),
                Rate: FixedQ4816.FromDouble(value: decay.Rate),
                When: [],
                Then: [],
                Row: null,
                Comparison: default,
                Value: default
            ),
                WorldReaction.Transform transform => new CompiledReaction(
                Kind: transform,
                Field: -1,
                Rate: default,
                When: (transform.When ?? []).Select(selector: c => new CompiledCondition(
                    Field: FieldIndex(name: c.Field),
                    Comparison: c.Comparison,
                    Value: FixedQ4816.FromDouble(value: c.Value)
                )).ToArray(),
                Then: (transform.Then ?? []).Select(selector: w => new CompiledWrite(
                    Field: FieldIndex(name: w.Field),
                    Op: w.Op,
                    Value: FixedQ4816.FromDouble(value: w.Value)
                )).ToArray(),
                Row: null,
                Comparison: default,
                Value: default
            ),
                WorldReaction.Emit emit => new CompiledReaction(
                Kind: emit,
                Field: FieldIndex(name: emit.Field),
                Rate: FixedQ4816.FromDouble(value: emit.Amount),
                When: [],
                Then: [],
                Row: emit.Tag,
                Comparison: default,
                Value: default
            ),
                WorldReaction.Expose expose => new CompiledReaction(
                Kind: expose,
                Field: FieldIndex(name: expose.Field),
                Rate: default,
                When: [],
                Then: [],
                Row: expose.Row,
                Comparison: expose.Comparison,
                Value: FixedQ4816.FromDouble(value: expose.Value)
            ),
                _ => throw new InvalidOperationException(message: $"fields.reactions[{index}] is an unknown reaction kind."),
            };
        }
    }

    /// <summary>Gets the lattice's cell count (width × layers × depth).</summary>
    public int CellCount => ((m_width * m_layers) * m_depth);
    /// <summary>Gets the declared cubic cell edge.</summary>
    public FixedQ4816 CellSize => m_cellSize;
    /// <summary>Gets the authored section.</summary>
    public WorldFieldsSection Document => m_document;
    /// <summary>Gets the number of declared fields.</summary>
    public int FieldCount => m_values.Length;
    /// <summary>Gets the lattice's minimum corner.</summary>
    public FixedVector3 Origin => m_origin;
    /// <summary>Gets a counter that moves on every cell write.</summary>
    public int Revision => m_revision;

    private static bool Holds(WorldFieldComparison comparison, FixedQ4816 value, FixedQ4816 expected) => comparison switch {
        WorldFieldComparison.Equal => (value == expected),
        WorldFieldComparison.NotEqual => (value != expected),
        WorldFieldComparison.Less => (value < expected),
        WorldFieldComparison.LessOrEqual => (value <= expected),
        WorldFieldComparison.Greater => (value > expected),
        _ => (value >= expected),
    };
    private int CellIndex(int x, int y, int z) => (((z * m_layers) + y) * m_width + x);
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
    private static FixedQ4816 Mean(Int128 rawSum, int count) {
        var negative = (rawSum < Int128.Zero);
        var magnitude = ((UInt128)(negative ? -rawSum : rawSum));
        var divisor = ((UInt128)(uint)count);
        var quotient = (magnitude / divisor);
        var remainder = (magnitude % divisor);

        if (
            ((remainder * 2U) > divisor) ||
            (((remainder * 2U) == divisor) && ((quotient & 1U) != 0U))
        ) {
            quotient++;
        }

        var raw = (negative
            ? ((quotient == (((UInt128)1U) << 63)) ? long.MinValue : -((long)quotient))
            : ((long)quotient)
        );

        return FixedQ4816.FromRawBits(value: raw);
    }
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
    /// <summary>Gets the lattice's width in cells.</summary>
    public int Width => m_width;
    /// <summary>Gets the lattice's depth in cells.</summary>
    public int Depth => m_depth;
    /// <summary>Gets the lattice's layer count.</summary>
    public int Layers => m_layers;

    /// <summary>Steps the reactions once when <paramref name="tick"/> falls on the cadence; a no-op otherwise.</summary>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="bodyCount">The entity-table capacity; bodies are visited by index.</param>
    /// <param name="bodyPosition">Resolves an active body's position, or <see langword="null"/> for an inactive slot.</param>
    /// <param name="readTag">Reads a keyed int state row's cell for a body index (0 when absent).</param>
    /// <param name="writeTag">Writes a keyed int state row's cell for a body index.</param>
    public void Step(ulong tick, int bodyCount, Func<int, FixedVector3?> bodyPosition, Func<string, int, long> readTag, Action<string, int, long> writeTag) {
        if ((tick % ((ulong)m_stepEveryTicks)) != 0UL) {
            return;
        }

        foreach (var reaction in m_reactions) {
            switch (reaction.Kind) {
                case WorldReaction.Diffuse:
                    StepDiffuse(reaction: in reaction);
                    break;
                case WorldReaction.Decay:
                    StepDecay(reaction: in reaction);
                    break;
                case WorldReaction.Transform:
                    StepTransform(reaction: in reaction);
                    break;
                case WorldReaction.Emit:
                    for (var body = 0; (body < bodyCount); body++) {
                        if (
                            (bodyPosition(arg: body) is not { } position) ||
                            (readTag(arg1: reaction.Row!, arg2: body) == 0L) ||
                            !TryBodyCellOf(
                            position: in position,
                            cell: out var cell
                        )
                        ) {
                            continue;
                        }

                        Write(
                            cell: cell,
                            field: reaction.Field,
                            value: AddClamped(
                                field: reaction.Field,
                                x: m_values[reaction.Field][cell],
                                y: reaction.Rate
                            )
                        );
                    }

                    break;
                case WorldReaction.Expose:
                    for (var body = 0; (body < bodyCount); body++) {
                        if (bodyPosition(arg: body) is not { } position) {
                            continue;
                        }

                        var exposed = (TryBodyCellOf(
                            position: in position,
                            cell: out var cell
                        ) && Holds(
                            comparison: reaction.Comparison,
                            expected: reaction.Value,
                            value: m_values[reaction.Field][cell]
                        ));

                        writeTag(
                            arg1: reaction.Row!,
                            arg2: body,
                            arg3: (exposed
                                ? 1L
                                : 0L)
                        );
                    }

                    break;
            }
        }
    }
    private void StepDiffuse(in CompiledReaction reaction) {
        var values = m_values[reaction.Field];

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

                    var mean = Mean(rawSum: rawSum, count: count);
                    var current = m_scratch[cell];

                    Write(
                        cell: cell,
                        field: reaction.Field,
                        value: (current + ((mean - current) * reaction.Rate))
                    );
                }
            }
        }
    }
    private void StepDecay(in CompiledReaction reaction) {
        var values = m_values[reaction.Field];

        for (var cell = 0; (cell < values.Length); cell++) {
            var current = values[cell];

            if (current == FixedQ4816.Zero) {
                continue;
            }

            Write(
                cell: cell,
                field: reaction.Field,
                value: (current - (current * reaction.Rate))
            );
        }
    }
    private void StepTransform(in CompiledReaction reaction) {
        for (var cell = 0; (cell < CellCount); cell++) {
            var holds = true;

            foreach (var condition in reaction.When) {
                if (!Holds(
                    comparison: condition.Comparison,
                    expected: condition.Value,
                    value: m_values[condition.Field][cell]
                )) {
                    holds = false;
                    break;
                }
            }

            if (!holds) {
                continue;
            }

            foreach (var write in reaction.Then) {
                Write(
                    cell: cell,
                    field: write.Field,
                    value: ((write.Op == WorldFieldWriteOp.Add)
                        ? AddClamped(
                            field: write.Field,
                            x: m_values[write.Field][cell],
                            y: write.Value
                        )
                        : write.Value)
                );
            }
        }
    }

    /// <summary>Takes the cell deltas written since the last take — or every cell, when a full resync is owed
    /// (construction, restore, or a primer snapshot).</summary>
    /// <param name="full">Whether to send every cell rather than the pending deltas.</param>
    /// <param name="isFull">Whether the returned set covers every cell.</param>
    /// <returns>The deltas.</returns>
    public FieldCellDelta[] TakeDeltas(bool full, out bool isFull) {
        if (full || m_fullResync) {
            var all = new FieldCellDelta[FieldCount * CellCount];
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
                sum += (double)value;

                if (value != FixedQ4816.Zero) {
                    nonzero++;
                }
            }

            parts.Add(item: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{m_names[field]} nonzero={nonzero} mean={(sum / CellCount):0.###}"
            ));
        }

        return $"lattice {m_width}x{m_layers}x{m_depth} @ {(double)m_cellSize} every {m_stepEveryTicks} ticks: {string.Join(
            separator: " | ",
            values: parts
        )}";
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
        var fx = (int)(FixedQ4816.Floor(value: ((point.X - origin.X) / cell)).Value >> 16);
        var fz = (int)(FixedQ4816.Floor(value: ((point.Z - origin.Z) / cell)).Value >> 16);
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
            position: position,
            distance: out var da,
            material: out _
        );
        var hasB = m_b.TryDistance(
            position: position,
            distance: out var db,
            material: out _
        );

        useB = (hasB && (!hasA || (db < da)));

        return (hasA || hasB);
    }
    /// <inheritdoc/>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
        var hasA = m_a.TryDistance(
            position: position,
            distance: out var da,
            material: out var ma
        );
        var hasB = m_b.TryDistance(
            position: position,
            distance: out var db,
            material: out var mb
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
