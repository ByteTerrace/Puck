using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace Puck.State;

/// <summary>Identifies which ownership lane declares a compiled state descriptor.</summary>
public enum StateLane : byte {
    /// <summary>The document owns the state — one of the section's rows.</summary>
    Document,

    /// <summary>One participant owns the ephemeral state.</summary>
    Participant,

    /// <summary>One durable identity owns the state.</summary>
    Identity,
}
/// <summary>Identifies the semantic role a per-participant or per-identity slot carries, independent of the
/// <see cref="CellKind"/> it is stored in.</summary>
public enum StateParticipantRole : byte {
    /// <summary>A document row, or a slot carrying no specialized role.</summary>
    None = 0,

    /// <summary>A per-participant tally, stored as <see cref="CellKind.Fixed"/>.</summary>
    Counter = 1,

    /// <summary>A per-participant duration in engine ticks, stored as <see cref="CellKind.Int"/>.</summary>
    Timer = 2,
}
/// <summary>Identifies one descriptor in the <see cref="StateCatalog"/> that minted it.</summary>
/// <remarks>Handles are bound to one catalog instance. A processor resolves a name during compilation, retains the
/// handle while that catalog is current, and uses the catalog indexer during execution instead of repeating a string
/// lookup. Value-only definition updates retain the catalog and its handles; a declaration-shape change produces a
/// replacement catalog and refuses the old handles. The default value is invalid.</remarks>
public readonly record struct StateHandle {
    private readonly object? m_catalogIdentity;
    private readonly int m_encodedOrdinal;

    internal StateHandle(int ordinal, object catalogIdentity) {
        m_catalogIdentity = catalogIdentity;
        m_encodedOrdinal = checked((ordinal + 1));
    }

    /// <summary>Gets whether this handle was minted by a state catalog.</summary>
    public bool IsValid => (m_encodedOrdinal > 0);
    /// <summary>Gets the descriptor's stable ordinal in its catalog, or <c>-1</c> for the default invalid handle.</summary>
    public int Ordinal => (m_encodedOrdinal - 1);

    internal bool BelongsTo(object catalogIdentity) => ReferenceEquals(
        objA: m_catalogIdentity,
        objB: catalogIdentity
    );
}
/// <summary>Describes one authored state declaration after its lane, shape, kind, and role are compiled.</summary>
/// <remarks><paramref name="Kind"/> says how the declaration is stored and <paramref name="Role"/> what it means;
/// the two are independent, so two slots stored alike can carry different roles and a document row carries no role
/// whatever its kind.</remarks>
/// <param name="Handle">The catalog-instance-relative typed handle for this declaration.</param>
/// <param name="Name">The authored stable name.</param>
/// <param name="Lane">The lane that owns the state.</param>
/// <param name="Shape">The storage shape derived from the authored declaration.</param>
/// <param name="Kind">The cell kind every one of the declaration's values is stored in.</param>
/// <param name="Role">The semantic participant role, or <see cref="StateParticipantRole.None"/> for a document
/// row.</param>
/// <param name="Generated">Whether a lowering or the runtime synthesized the declaration rather than an author
/// writing it.</param>
/// <param name="HostOwned">Whether a host facet serves the declaration instead of the store.</param>
/// <param name="LaneOrdinal">The declaration's zero-based document-order ordinal within <paramref name="Lane"/>.</param>
public readonly record struct StateDescriptor(
    StateHandle Handle,
    string Name,
    StateLane Lane,
    RowShape Shape,
    CellKind Kind,
    StateParticipantRole Role,
    bool Generated,
    bool HostOwned,
    int LaneOrdinal
) {
    /// <summary>Gets the declaration's stable ordinal in its catalog, or <c>-1</c> for a default descriptor.</summary>
    public int Ordinal => Handle.Ordinal;
}
/// <summary>Compiles an <see cref="IStateSection"/> into immutable typed descriptors and catalog-instance-relative
/// handles. Descriptor ordinals are assigned deterministically in document, participant, then identity declaration
/// order.</summary>
/// <remarks>The authored section remains the serialization source. This catalog is a runtime compiler product and
/// carries no mutable state values.</remarks>
public sealed class StateCatalog {
    private static long ShapeWalkCountValue;

    private static readonly ConditionalWeakTable<IStateSection, Lazy<IReadOnlyList<StateRow>>> ExpandedRows = new();

    private readonly StateDescriptor[] m_descriptors;
    private readonly Dictionary<string, StateEnum> m_enumsByName;
    private readonly Dictionary<string, RowFamily> m_familiesByName;
    private readonly Dictionary<string, StateHandle>[] m_handlesByLane;
    private readonly object m_identity;
    private readonly CellKeyTable m_keys;
    private readonly StateLaneDescriptor[] m_lanes;
    private readonly ReadOnlyCollection<StateDescriptor> m_readOnlyDescriptors;
    private readonly ReadOnlyCollection<RowFamily> m_readOnlyFamilies;
    private readonly StateEnum?[] m_rowEnums;
    private readonly ReadOnlyCollection<StateRow> m_rows;
    private readonly ReadOnlyCollection<StatePoolDescriptor> m_pools;
    private readonly Dictionary<string, StatePoolDescriptor> m_poolsByName;
    private readonly bool[] m_poolRows;
    private readonly bool[] m_poolFieldRows;
    private readonly CellKey[] m_poolKeys;
    private readonly CellKey[] m_ringKeys;
    private readonly int[] m_poolSlotsByKeyOrdinal;

    private StateCatalog(
        object identity,
        StateDescriptor[] descriptors,
        Dictionary<string, StateHandle>[] handlesByLane,
        StateLaneDescriptor[] lanes,
        CellKeyTable keys,
        Dictionary<string, StateEnum> enumsByName,
        StateEnum?[] rowEnums,
        Dictionary<string, RowFamily> familiesByName,
        RowFamily[] families,
        StateRow[] rows,
        StatePoolDescriptor[] pools
    ) {
        m_descriptors = descriptors;
        m_enumsByName = enumsByName;
        m_familiesByName = familiesByName;
        m_handlesByLane = handlesByLane;
        m_identity = identity;
        m_keys = keys;
        m_keys.SealSeed();
        // Ring addresses are compiler symbols, not retained runtime names. Prebind them after sealing the
        // declaration seed so iteration neither mints keys nor changes the arena's resource ledger.
        var ringCapacity = rows.Where(predicate: static row => (!row.HostOwned && (row.EffectiveDomain is StateDomain.Ring)))
            .Select(selector: static row => row.CellCeiling).DefaultIfEmpty().Max();

        m_ringKeys = new CellKey[ringCapacity];
        for (var position = 0; (position < ringCapacity); position++) {
            m_ringKeys[position] = m_keys.Intern(name: CellName.Parse(candidate: IndexKeyCache.Get(index: position)));
        }
        m_lanes = lanes;
        m_readOnlyDescriptors = Array.AsReadOnly(array: descriptors);
        m_readOnlyFamilies = Array.AsReadOnly(array: families);
        m_rowEnums = rowEnums;
        m_rows = Array.AsReadOnly(array: rows);
        m_pools = Array.AsReadOnly(array: pools);
        m_poolsByName = pools.ToDictionary(keySelector: static pool => pool.Name.Value, comparer: StringComparer.Ordinal);
        m_poolRows = ((pools.Length == 0) ? [] : new bool[rows.Length]);
        m_poolFieldRows = ((pools.Length == 0) ? [] : new bool[rows.Length]);
        foreach (var pool in pools) {
            m_poolRows[pool.DomainRowOrdinal] = true;
            m_poolRows[pool.GenerationRowOrdinal] = true;
            foreach (var field in pool.Fields) {
                m_poolRows[field.RowOrdinal] = true;
                m_poolFieldRows[field.RowOrdinal] = true;
            }
        }
        m_poolKeys = new CellKey[((pools.Length == 0) ? 0 : pools.Max(selector: static pool => pool.Capacity))];
        for (var slot = 0; (slot < m_poolKeys.Length); slot++) {
            if (!m_keys.TryResolve(name: CellName.Parse(candidate: slot.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)), key: out m_poolKeys[slot])) {
                throw new InvalidOperationException(message: $"State pool slot {slot} has no compiled key.");
            }
        }
        m_poolSlotsByKeyOrdinal = ((m_poolKeys.Length == 0) ? [] : new int[(m_poolKeys.Max(selector: static key => key.Ordinal) + 1)]);
        Array.Fill(array: m_poolSlotsByKeyOrdinal, value: -1);
        for (var slot = 0; (slot < m_poolKeys.Length); slot++) {
            m_poolSlotsByKeyOrdinal[m_poolKeys[slot].Ordinal] = slot;
        }
    }

    /// <summary>Gets the number of compiled state declarations.</summary>
    public int Count => m_descriptors.Length;
    /// <summary>Gets the compiled descriptors in stable handle order.</summary>
    public IReadOnlyList<StateDescriptor> Descriptors => m_readOnlyDescriptors;
    /// <summary>Gets the compiled row families in declaration order.</summary>
    public IReadOnlyList<RowFamily> Families => m_readOnlyFamilies;
    /// <summary>Gets the complete document lane, including catalog-generated pool storage rows.</summary>
    public IReadOnlyList<StateRow> Rows => m_rows;
    /// <summary>Gets the compiled pools in declaration order.</summary>
    public IReadOnlyList<StatePoolDescriptor> Pools => m_pools;
    /// <summary>Gets the compiled key symbols. Runtime names are owned by <see cref="StateArena.Keys"/>.</summary>
    public CellKeyTable Keys => m_keys;
    /// <summary>Gets the running count of calls to <see cref="MatchesShape"/> across every catalog. A
    /// recompilation site calls it once per candidate section; nothing on a warm read of an already-keyed catalog
    /// may call it, so a law reads this to prove that contract without a wall-clock measurement.</summary>
    public static long ShapeWalkCount => Interlocked.Read(location: ref ShapeWalkCountValue);

    /// <summary>Looks up a compiled pool by stable name.</summary>
    public bool TryGetPool(CellName name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StatePoolDescriptor? pool) => m_poolsByName.TryGetValue(key: name.Value, value: out pool);
    /// <summary>Mints a runtime handle bound to this catalog's identity.</summary>
    public StateInstanceHandle CreateInstanceHandle(int poolOrdinal, int slot, long generation) => new(
        Generation: generation,
        PoolOrdinal: poolOrdinal,
        Slot: slot,
        catalogIdentity: m_identity
    );

    internal object Identity => m_identity;

    internal CellKey RingKey(int position) => m_ringKeys[position];
    internal bool TryGetPoolKey(int slot, out CellKey key) {
        if (((uint)slot) < ((uint)m_poolKeys.Length)) {
            key = m_poolKeys[slot];
            return true;
        }
        key = default;
        return false;
    }
    internal bool TryGetPoolSlot(int keyOrdinal, out int slot) {
        if ((((uint)keyOrdinal) < ((uint)m_poolSlotsByKeyOrdinal.Length)) && ((slot = m_poolSlotsByKeyOrdinal[keyOrdinal]) >= 0)) {
            return true;
        }
        slot = -1;
        return false;
    }

    /// <summary>Gets whether a document row is generated storage owned by a pool.</summary>
    public bool IsPoolRow(int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)m_poolRows.Length)) && m_poolRows[rowOrdinal]);

    internal bool IsPoolFieldRow(int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)m_poolFieldRows.Length)) && m_poolFieldRows[rowOrdinal]);

    /// <summary>Gets the descriptor addressed by <paramref name="handle"/>.</summary>
    /// <param name="handle">A handle minted by this catalog.</param>
    /// <returns>The compiled descriptor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="handle"/> is invalid or outside this catalog.</exception>
    public StateDescriptor this[StateHandle handle] => (TryGetDescriptor(
        descriptor: out var descriptor,
        handle: handle
    )
        ? descriptor
        : throw new ArgumentOutOfRangeException(
            paramName: nameof(handle),
            actualValue: handle.Ordinal,
            message: "The state handle is invalid or outside this catalog."
        )
    );

    private static CellKind SlotKind(StateParticipantRole role) => (role switch {
        StateParticipantRole.Counter => CellKind.Fixed,
        StateParticipantRole.Timer => CellKind.Int,
        _ => throw new InvalidOperationException(message: $"A state slot carries role '{role}', which no lane stores."),
    });
    private static void AddSlots(IReadOnlyList<IStateSlot> declarations, StateLane lane, ISet<string> names, Action<string, StateLane, RowShape, CellKind, StateParticipantRole, bool, bool, int> add) {
        for (var index = 0; (index < declarations.Count); index++) {
            var declaration = (declarations[index] ?? throw new InvalidOperationException(message: $"State lane '{lane}' contains a null declaration at ordinal {index}."));

            if (!names.Add(item: declaration.Name)) {
                throw new InvalidOperationException(message: $"State lanes 'Participant' and 'Identity' declare duplicate name '{declaration.Name}'.");
            }

            if (declaration.Role is not (StateParticipantRole.Counter or StateParticipantRole.Timer)) {
                throw new InvalidOperationException(message: $"State lane '{lane}' declaration '{declaration.Name}' carries unknown role '{declaration.Role}'.");
            }

            add(
                declaration.Name,
                lane,
                RowShape.Slot,
                SlotKind(role: declaration.Role),
                declaration.Role,
                false,
                false,
                index
            );
        }
    }
    private static bool MatchesSlotLane(IReadOnlyList<IStateSlot> declarations, StateLane lane, IReadOnlyList<StateDescriptor> descriptors, ref int descriptorIndex) {
        for (var index = 0; (index < declarations.Count); index++) {
            if (
                (declarations[index] is not { } declaration) ||
                (declaration.Role is not (StateParticipantRole.Counter or StateParticipantRole.Timer)) ||
                (((uint)descriptorIndex) >= ((uint)descriptors.Count))
            ) {
                return false;
            }

            var descriptor = descriptors[descriptorIndex++];

            if (
                !string.Equals(
                a: descriptor.Name,
                b: declaration.Name,
                comparisonType: StringComparison.Ordinal
            ) ||
                (descriptor.Lane != lane) ||
                (descriptor.Shape != RowShape.Slot) ||
                (descriptor.Kind != SlotKind(role: declaration.Role)) ||
                (descriptor.Role != declaration.Role) ||
                (descriptor.LaneOrdinal != index)
            ) {
                return false;
            }
        }

        return true;
    }
    // A family's members are the `Size` rows named <family><index>, declared consecutively — the member naming the
    // transpiler's own family lowering emits. A family that declares an index set or its member rows outright is
    // resolved through the ordinal-per-index slot table instead, which neither convention nor contiguity binds.
    private static RowFamily ResolveFamily(StateFamily family, IReadOnlyList<StateRow> rows, IReadOnlyDictionary<string, int> ordinalsByName) {
        if ((family.Size < 1) || (family.Size > StateCapacity.MaxRows)) {
            throw new InvalidOperationException(message: $"State family '{family.Name.Value}' declares size {family.Size}, outside 1..{StateCapacity.MaxRows}.");
        }
        if ((family.Indices is { } declaredIndices) && (declaredIndices.Count != family.Size)) {
            throw new InvalidOperationException(message: $"State family '{family.Name.Value}' declares {declaredIndices.Count} member indices for size {family.Size}.");
        }
        if ((family.Members is { } declaredMembers) && (declaredMembers.Count != family.Size)) {
            throw new InvalidOperationException(message: $"State family '{family.Name.Value}' names {declaredMembers.Count} member rows for size {family.Size}.");
        }

        var explicitly = ((family.Indices is not null) || (family.Members is not null));
        var first = -1;
        var highest = -1;
        var ordinals = new int[family.Size];

        for (var index = 0; (index < family.Size); index++) {
            var member = family.MemberName(index: index);

            if (!ordinalsByName.TryGetValue(
                key: member,
                value: out var ordinal
            )) {
                throw new InvalidOperationException(message: $"State family '{family.Name.Value}' names member row '{member}', which the document lane does not declare.");
            }

            var familyIndex = family.FamilyIndex(index: index);

            if ((familyIndex < 0) || (familyIndex >= StateCapacity.MaxRows)) {
                throw new InvalidOperationException(message: $"State family '{family.Name.Value}' member '{member}' carries family index {familyIndex}, outside 0..{(StateCapacity.MaxRows - 1)}.");
            }

            highest = Math.Max(
                val1: highest,
                val2: familyIndex
            );
            ordinals[index] = ordinal;

            if (index == 0) {
                first = ordinal;
            } else if (!explicitly && (ordinal != (first + index))) {
                throw new InvalidOperationException(message: $"State family '{family.Name.Value}' member '{member}' is declared at ordinal {ordinal}, breaking the family's contiguous range starting at {first}.");
            }
        }

        var kind = rows[first].Kind;

        for (var index = 1; (index < family.Size); index++) {
            if (rows[ordinals[index]].Kind != kind) {
                throw new InvalidOperationException(message: $"State family '{family.Name.Value}' member '{family.MemberName(index: index)}' is {rows[ordinals[index]].Kind}, but member zero is {kind}.");
            }
        }

        var slots = default(int[]);

        if (explicitly) {
            slots = new int[(highest + 1)];

            Array.Fill(
                array: slots,
                value: -1
            );

            for (var index = 0; (index < family.Size); index++) {
                var familyIndex = family.FamilyIndex(index: index);

                if (slots[familyIndex] >= 0) {
                    throw new InvalidOperationException(message: $"State family '{family.Name.Value}' declares family index {familyIndex} twice.");
                }

                slots[familyIndex] = ordinals[index];
            }
        }

        return new RowFamily(
            Count: family.Size,
            FirstOrdinal: first,
            Name: family.Name,
            Slots: slots
        );
    }

    // The first part of every row a pool generates: the pool's live-slot and generation rows are
    // `$pool$<pool>$live` and `$pool$<pool>$generation`, and each record field's row is `$pool$<pool>$field$<field>`,
    // so no pool, field or pair of them can generate another's row. The pool may itself be a generated name, a
    // module instance's `left$pieces`, which is the one part that may carry the joiner. The reserved prefix keeps
    // each one a row no author declares.
    private const string PoolRowHead = "$pool";

    private static CellName PoolRowName(StatePool pool, params ReadOnlySpan<string> parts) => GeneratedPoolRowName(
        kind: "pool",
        parts: parts,
        pool: pool.Name
    );
    private static CellName PairPoolRowName(StatePairPool pool, params ReadOnlySpan<string> parts) => GeneratedPoolRowName(
        kind: "pair pool",
        parts: parts,
        pool: pool.Name
    );
    private static CellName GeneratedPoolRowName(string kind, CellName pool, ReadOnlySpan<string> parts) {
        try {
            var name = GeneratedName.Qualify(
                head: PoolRowHead,
                name: pool.Value
            );

            foreach (var part in parts) {
                name = GeneratedName.Append(
                    name: name,
                    part: part
                );
            }

            return CellName.Parse(candidate: name);
        } catch (ArgumentException exception) {
            throw new InvalidOperationException(message: $"State {kind} '{pool.Value}' cannot generate its '{string.Join(separator: GeneratedName.Joiner, values: parts.ToArray())}' row: {exception.Message} — a record field name may not carry '{GeneratedName.Joiner}'", innerException: exception);
        } catch (FormatException exception) {
            throw new InvalidOperationException(message: $"State {kind} '{pool.Value}' cannot generate its '{string.Join(separator: GeneratedName.Joiner, values: parts.ToArray())}' row: {exception.Message}", innerException: exception);
        }
    }
    private static CellValue DefaultValue(StatePoolField field) {
        if (field.Default.HasValue) {
            return field.Default;
        }

        return (field.Kind switch {
            CellKind.Bool => CellValue.Bool(value: false),
            CellKind.Fixed => CellValue.Fixed(rawBits: 0L),
            CellKind.Int => CellValue.Int(value: 0L),
            CellKind.Text => CellValue.Text(value: string.Empty),
            CellKind.Vector => CellValue.Vector(components: new sbyte[(field.Dimensions ?? 0)]),
            _ => throw new InvalidOperationException(message: $"State pool field '{field.Name.Value}' carries unknown kind '{field.Kind}'."),
        });
    }
    private static void ValidateRecordField(StateRecord record, StatePoolField field, IStateSection? section) {
        if (!Enum.IsDefined(value: field.Kind)) {
            throw new InvalidOperationException(message: $"State record '{record.Name.Value}' field '{field.Name.Value}' carries unknown kind '{field.Kind}'.");
        }
        if (
            (field.Kind == CellKind.Vector) &&
            ((field.Dimensions.GetValueOrDefault() < 0) || (field.Dimensions.GetValueOrDefault() > StateCapacity.MaxVectorDimensions))
        ) {
            throw new InvalidOperationException(message: $"State record '{record.Name.Value}' field '{field.Name.Value}' declares {field.Dimensions.GetValueOrDefault()} vector dimensions, outside 0..{StateCapacity.MaxVectorDimensions}.");
        }

        var value = DefaultValue(field: field);

        // A field draws its default from the enum it names only when that is a declared enum on an Int field; the
        // document's validator is the one refusal of any other (an undeclared enum, a non-Int field).
        if (
            (field.Kind == CellKind.Int) &&
            (field.Enum is { } enumName) &&
            (StateEnum.Named(
                enums: section?.Enums,
                name: enumName
            ) is { } domain)
        ) {
            if (!domain.TryValidate(reason: out var enumReason)) {
                throw new InvalidOperationException(message: enumReason);
            }
            if (!domain.Admits(value: value.Raw)) {
                throw new InvalidOperationException(message: $"State record '{record.Name.Value}' field '{field.Name.Value}' default {value.Raw} is outside enum '{enumName.Value}'.");
            }
        }

        if (!value.HasValue || (value.Kind != field.Kind)) {
            var carried = (value.HasValue ? value.Kind.ToString() : "no value case");

            throw new InvalidOperationException(message: $"State record '{record.Name.Value}' field '{field.Name.Value}' declares kind {field.Kind}, which its {carried} default does not satisfy.");
        }
        if ((field.Kind == CellKind.Text) && (value.AsText.Length > StateCapacity.MaxTextValueLength)) {
            throw new InvalidOperationException(message: $"State record '{record.Name.Value}' field '{field.Name.Value}' default holds {value.AsText.Length} characters, past the {StateCapacity.MaxTextValueLength}-character limit.");
        }
        if ((field.Kind == CellKind.Vector) && (value.AsVector.Length != field.Dimensions.GetValueOrDefault())) {
            throw new InvalidOperationException(message: $"State record '{record.Name.Value}' field '{field.Name.Value}' declares {field.Dimensions.GetValueOrDefault()} dimensions, which its {value.AsVector.Length}-component default does not satisfy.");
        }
        if (
            (field.Kind is CellKind.Int or CellKind.Fixed or CellKind.Bool) &&
            ((field.Min.HasValue && (value.Raw < field.Min.Value)) || (field.Max.HasValue && (value.Raw > field.Max.Value)))
        ) {
            throw new InvalidOperationException(message: $"State record '{record.Name.Value}' field '{field.Name.Value}' default {value.Raw} is outside its declared {(field.Min?.ToString() ?? "unbounded")}..{(field.Max?.ToString() ?? "unbounded")} envelope.");
        }
        if ((field.Advance is { } advance) && ((field.Kind is not (CellKind.Int or CellKind.Fixed)) || (advance.PerSecondDenominator <= 0L))) {
            throw new InvalidOperationException(message: $"State record '{record.Name.Value}' field '{field.Name.Value}' advance requires an Int or Fixed field and a positive denominator.");
        }
    }

    /// <summary>Expands the current authored rows and pool declarations into the complete document lane the arena
    /// stores. Generated pool rows are synthesized once per immutable section identity; caller-supplied generated
    /// flags are never trusted. A replacement section receives its own population, even when it reuses a catalog.</summary>
    public static IReadOnlyList<StateRow> ExpandRows(IStateSection? section) => ((section is null)
        ? []
        : ExpandedRows.GetValue(key: section, createValueCallback: static source => new Lazy<IReadOnlyList<StateRow>>(
            valueFactory: () => ExpandRowsCore(section: source)
        )).Value);

    private static IReadOnlyList<StateRow> ExpandRowsCore(IStateSection section) {
        var rows = new List<StateRow>(collection: (section?.Rows ?? []));

        for (var index = 0; (index < rows.Count); index++) {
            if (rows[index] is null) {
                throw new InvalidOperationException(message: $"State lane 'Document' contains a null declaration at ordinal {index}.");
            }
            if (rows[index].Generated) {
                throw new InvalidOperationException(message: "Authored state rows cannot carry the runtime-generated mark.");
            }
        }
        var names = rows.Select(selector: static row => row.Name.Value).ToHashSet(comparer: StringComparer.Ordinal);
        var records = new Dictionary<string, StateRecord>(comparer: StringComparer.Ordinal);

        foreach (var record in (section?.Records ?? [])) {
            if (record is null) {
                throw new InvalidOperationException(message: "The state section contains a null record declaration.");
            }
            if (!records.TryAdd(key: record.Name.Value, value: record)) {
                throw new InvalidOperationException(message: $"The state section declares duplicate record '{record.Name.Value}'.");
            }

            var fieldNames = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var field in (record.Fields ?? [])) {
                if ((field is null) || !fieldNames.Add(item: field.Name.Value)) {
                    throw new InvalidOperationException(message: $"State record '{record.Name.Value}' contains a null or duplicate field declaration.");
                }
                ValidateRecordField(field: field, record: record, section: section);
            }
        }

        long expandedRowCount = rows.Count;

        foreach (var pool in (section?.Pools ?? [])) {
            if (pool is null) {
                throw new InvalidOperationException(message: "The state section contains a null pool declaration.");
            }
            expandedRowCount += (2L + (records.TryGetValue(key: pool.Record.Value, value: out var record) ? (record.Fields?.Count ?? 0) : 0));
        }
        foreach (var pool in (section?.PairPools ?? [])) {
            if (pool is null) {
                throw new InvalidOperationException(message: "The state section contains a null pair pool declaration.");
            }
            expandedRowCount += (2L + (records.TryGetValue(key: pool.Record.Value, value: out var record) ? (record.Fields?.Count ?? 0) : 0));
        }
        if (expandedRowCount > StateCapacity.MaxRows) {
            throw new InvalidOperationException(message: $"The expanded state section declares {expandedRowCount} rows, past the {StateCapacity.MaxRows}-row limit.");
        }

        var poolNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var pool in (section?.Pools ?? [])) {
            if (pool is null) {
                throw new InvalidOperationException(message: "The state section contains a null pool declaration.");
            }
            if (!poolNames.Add(item: pool.Name.Value)) {
                throw new InvalidOperationException(message: $"The state section declares duplicate pool '{pool.Name.Value}'.");
            }
            if (names.Contains(item: pool.Name.Value)) {
                throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' collides with a state row of the same name.");
            }
            if ((pool.Capacity < 1) || (pool.Capacity > StateCapacity.MaxCellsPerRow)) {
                throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' declares capacity {pool.Capacity}, outside 1..{StateCapacity.MaxCellsPerRow}.");
            }
            if (!records.TryGetValue(key: pool.Record.Value, value: out var record)) {
                throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' names undeclared record '{pool.Record.Value}'.");
            }
            if ((pool.Initial is not null) && (pool.Snapshot is not null)) {
                throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' carries both an initial population and a runtime snapshot.");
            }

            var generations = (pool.Snapshot?.Generations ?? Enumerable.Repeat(element: 0L, count: pool.Capacity).ToArray());

            if (generations.Count != pool.Capacity) {
                throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' snapshot carries {generations.Count} generations for capacity {pool.Capacity}.");
            }
            if (generations.Any(predicate: static generation => (generation < 0L))) {
                throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' snapshot carries a negative generation.");
            }

            var live = (pool.Snapshot?.Live ?? (pool.Initial ?? []));
            var liveBySlot = new SortedDictionary<int, StatePoolSeed>();

            foreach (var seed in live) {
                if ((seed is null) || (seed.Slot < 0) || (seed.Slot >= pool.Capacity) || !liveBySlot.TryAdd(key: seed.Slot, value: seed)) {
                    throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' carries a null, duplicate, or out-of-range live slot.");
                }
                var seededFields = new HashSet<string>(comparer: StringComparer.Ordinal);

                foreach (var value in (seed.Values ?? [])) {
                    if (
                        (value is null) ||
                        !seededFields.Add(item: value.Field.Value) ||
                        !(record.Fields ?? []).Any(predicate: field => string.Equals(a: field.Name.Value, b: value.Field.Value, comparisonType: StringComparison.Ordinal))
                    ) {
                        throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' slot {seed.Slot} carries a null, duplicate, or undeclared field value.");
                    }
                }
                if ((pool.Snapshot is not null) && (seededFields.Count != (record.Fields?.Count ?? 0))) {
                    throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' snapshot slot {seed.Slot} does not carry every record field.");
                }
            }

            var privateVisibility = new StateVisibility(Readers: []);
            var domainName = PoolRowName(pool: pool, "live");
            var generationName = PoolRowName(pool: pool, "generation");
            var domainCells = liveBySlot.Select(selector: pair => new StateCell(
                Key: CellName.Parse(candidate: pair.Key.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
                Value: CellValue.Int(value: generations[pair.Key])
            )).ToArray();
            var generationCells = Enumerable.Range(start: 0, count: pool.Capacity).Select(selector: slot => new StateCell(
                Key: CellName.Parse(candidate: slot.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
                Value: CellValue.Int(value: generations[slot])
            )).ToArray();

            foreach (var generatedName in new[] { domainName.Value, generationName.Value }) {
                if (!names.Add(item: generatedName)) {
                    throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' generated duplicate row '{generatedName}'.");
                }
            }

            rows.Add(item: new StateRow(Name: domainName, Kind: CellKind.Int, Capacity: pool.Capacity, Cells: domainCells, Visibility: privateVisibility) { Generated = true });
            rows.Add(item: new StateRow(Name: generationName, Kind: CellKind.Int, Capacity: pool.Capacity, Cells: generationCells, Min: 0L, Visibility: privateVisibility) { Generated = true });

            foreach (var field in (record.Fields ?? [])) {
                var fieldName = PoolRowName(pool: pool, "field", field.Name.Value);

                if (!names.Add(item: fieldName.Value)) {
                    throw new InvalidOperationException(message: $"State pool '{pool.Name.Value}' generated duplicate row '{fieldName.Value}'.");
                }

                var overrides = liveBySlot.ToDictionary(
                    keySelector: static pair => pair.Key,
                    elementSelector: pair => (pair.Value.Values ?? []).ToDictionary(keySelector: static value => value.Field.Value, comparer: StringComparer.Ordinal)
                );
                var cells = liveBySlot.Select(selector: pair => {
                    var hasOverride = overrides[pair.Key].TryGetValue(key: field.Name.Value, value: out var value);

                    return new StateCell(
                        Key: CellName.Parse(candidate: pair.Key.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
                        Value: (hasOverride ? value!.Value : DefaultValue(field: field)),
                        Clock: (hasOverride ? value!.Clock : null));
                }).ToArray();

                rows.Add(item: new StateRow(
                    Name: fieldName,
                    Kind: field.Kind,
                    Min: field.Min,
                    Max: field.Max,
                    Capacity: pool.Capacity,
                    Overflow: field.Overflow,
                    Advance: field.Advance,
                    Cells: cells,
                    Space: field.Space?.Value,
                    Enum: field.Enum,
                    Visibility: privateVisibility
                ) { Generated = true });
            }
        }

        var ordinaryPools = (section?.Pools ?? []).ToDictionary(keySelector: static pool => pool.Name.Value, comparer: StringComparer.Ordinal);
        var pairPools = (section?.PairPools ?? []);
        var pairDeclarations = new Dictionary<string, StatePairPool>(comparer: StringComparer.Ordinal);

        foreach (var declaration in pairPools) {
            if ((declaration is null) || !pairDeclarations.TryAdd(key: declaration.Name.Value, value: declaration)) {
                throw new InvalidOperationException(message: "The state section contains a null or duplicate pair pool declaration.");
            }
        }
        var visiting = new HashSet<string>(comparer: StringComparer.Ordinal);
        var visited = new HashSet<string>(comparer: StringComparer.Ordinal);

        void VisitPair(string name) {
            if (visited.Contains(item: name) || !pairDeclarations.TryGetValue(key: name, value: out var declaration)) {
                return;
            }
            if (!visiting.Add(item: name)) {
                throw new InvalidOperationException(message: $"State pair pool endpoint dependency graph contains a cycle through '{name}'.");
            }
            VisitPair(name: declaration.LeftPool.Value);
            VisitPair(name: declaration.RightPool.Value);
            _ = visiting.Remove(item: name);
            _ = visited.Add(item: name);
        }
        foreach (var pair in pairPools) {
            VisitPair(name: pair.Name.Value);
        }
        var capacities = ordinaryPools.ToDictionary(keySelector: static item => item.Key, elementSelector: static item => item.Value.Capacity, comparer: StringComparer.Ordinal);

        int PoolCapacity(string name) {
            if (capacities.TryGetValue(key: name, value: out var known)) {
                return known;
            }
            if (!pairDeclarations.TryGetValue(key: name, value: out var declaration)) {
                throw new InvalidOperationException(message: $"State pool endpoint '{name}' is undeclared.");
            }
            var computed = checked((((long)PoolCapacity(name: declaration.LeftPool.Value)) * PoolCapacity(name: declaration.RightPool.Value)));

            if (computed > StateCapacity.MaxCellsPerRow) {
                throw new InvalidOperationException(message: $"State pair pool '{name}' identity universe {computed} exceeds {StateCapacity.MaxCellsPerRow} cells per row.");
            }
            capacities[name] = ((int)computed);
            return ((int)computed);
        }
        IReadOnlyList<StatePoolSeed> LiveSeeds(string name) => (ordinaryPools.TryGetValue(key: name, value: out var ordinary)
            ? (ordinary.Snapshot?.Live ?? (ordinary.Initial ?? []))
            : (pairDeclarations[name].Snapshot?.Live ?? (pairDeclarations[name].Initial ?? [])));
        var pairNames = new HashSet<string>(poolNames, comparer: StringComparer.Ordinal);

        foreach (var pair in pairPools) {
            if ((pair is null) || !pairNames.Add(item: pair.Name.Value)) {
                throw new InvalidOperationException(message: "The state section contains a null or duplicate pair pool declaration.");
            }
            if (names.Contains(item: pair.Name.Value)) {
                throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' collides with a state row of the same name.");
            }
            if (!records.TryGetValue(key: pair.Record.Value, value: out var record)) {
                throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' names undeclared record '{pair.Record.Value}'.");
            }
            var leftCapacity = PoolCapacity(name: pair.LeftPool.Value);
            var rightCapacity = PoolCapacity(name: pair.RightPool.Value);

            if (!pair.Directed && (pair.LeftPool != pair.RightPool)) {
                throw new InvalidOperationException(message: $"Undirected state pair pool '{pair.Name.Value}' must use the same endpoint pool on both sides.");
            }
            var universe = checked((((long)leftCapacity) * rightCapacity));

            if (universe > StateCapacity.MaxCellsPerRow) {
                throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' identity universe {universe} exceeds {StateCapacity.MaxCellsPerRow} cells per row.");
            }
            if ((pair.MaxLive < 1) || (pair.MaxLive > universe)) {
                throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' declares max-live {pair.MaxLive}, outside 1..{universe}.");
            }
            if ((pair.Initial is not null) && (pair.Snapshot is not null)) {
                throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' carries both an initial population and a runtime snapshot.");
            }

            var capacity = ((int)universe);
            var generations = (pair.Snapshot?.Generations ?? Enumerable.Repeat(count: capacity, element: 0L).ToArray());

            if ((generations.Count != capacity) || generations.Any(predicate: static generation => (generation < 0L))) {
                throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' snapshot generations do not match its identity universe.");
            }
            var liveBySlot = new SortedDictionary<int, StatePoolSeed>();

            foreach (var seed in (pair.Snapshot?.Live ?? (pair.Initial ?? []))) {
                if ((seed is null) || (seed.Slot < 0) || (seed.Slot >= capacity) || !liveBySlot.TryAdd(key: seed.Slot, value: seed)) {
                    throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' carries a null, duplicate, or out-of-range live slot.");
                }
                var leftSlot = (seed.Slot / rightCapacity);
                var rightSlot = (seed.Slot % rightCapacity);

                if ((!pair.AllowSelf && (pair.LeftPool == pair.RightPool) && (leftSlot == rightSlot)) || (!pair.Directed && (leftSlot > rightSlot))) {
                    throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' carries a forbidden or noncanonical pair slot {seed.Slot}.");
                }
                var seededFields = new HashSet<string>(comparer: StringComparer.Ordinal);

                foreach (var value in (seed.Values ?? [])) {
                    if ((value is null) || !seededFields.Add(item: value.Field.Value) || !(record.Fields ?? []).Any(predicate: field => (field.Name == value.Field))) {
                        throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' slot {seed.Slot} carries a null, duplicate, or undeclared field value.");
                    }
                }
                if ((pair.Snapshot is not null) && (seededFields.Count != (record.Fields?.Count ?? 0))) {
                    throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' snapshot slot {seed.Slot} does not carry every record field.");
                }
                var leftSeed = LiveSeeds(name: pair.LeftPool.Value);
                var rightSeed = LiveSeeds(name: pair.RightPool.Value);

                if (!leftSeed.Any(predicate: item => (item.Slot == leftSlot)) || !rightSeed.Any(predicate: item => (item.Slot == rightSlot))) {
                    throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' live slot {seed.Slot} names a dead endpoint.");
                }
            }
            if (liveBySlot.Count > pair.MaxLive) {
                throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' snapshot exceeds max-live {pair.MaxLive}.");
            }

            var visibility = new StateVisibility(Readers: []);
            var domainName = PairPoolRowName(pool: pair, "live");
            var generationName = PairPoolRowName(pool: pair, "generation");

            foreach (var generatedName in new[] { domainName.Value, generationName.Value }) {
                if (!names.Add(item: generatedName)) {
                    throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' generated duplicate row '{generatedName}'.");
                }
            }
            StateCell[] Cells(Func<int, CellValue> value) => liveBySlot.Keys.Select(selector: slot => new StateCell(Key: CellName.Parse(candidate: slot.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)), Value: value(slot))).ToArray();
            rows.Add(item: new StateRow(Name: domainName, Kind: CellKind.Int, Capacity: capacity, Cells: Cells(value: slot => CellValue.Int(value: generations[slot])), Visibility: visibility) { Generated = true });
            rows.Add(item: new StateRow(Name: generationName, Kind: CellKind.Int, Capacity: capacity, Cells: Enumerable.Range(count: capacity, start: 0).Select(selector: slot => new StateCell(Key: CellName.Parse(candidate: slot.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)), Value: CellValue.Int(value: generations[slot]))).ToArray(), Min: 0L, Visibility: visibility) { Generated = true });
            foreach (var field in (record.Fields ?? [])) {
                var fieldName = PairPoolRowName(pool: pair, "field", field.Name.Value);

                if (!names.Add(item: fieldName.Value)) {
                    throw new InvalidOperationException(message: $"State pair pool '{pair.Name.Value}' generated duplicate row '{fieldName.Value}'.");
                }
                var overrides = liveBySlot.ToDictionary(keySelector: static item => item.Key, elementSelector: item => (item.Value.Values ?? []).ToDictionary(keySelector: static value => value.Field.Value, comparer: StringComparer.Ordinal));

                rows.Add(item: new StateRow(Name: fieldName, Kind: field.Kind, Min: field.Min, Max: field.Max, Capacity: capacity, Overflow: field.Overflow, Advance: field.Advance, Cells: liveBySlot.Values.Select(selector: seed => {
                    var hasOverride = overrides[seed.Slot].TryGetValue(key: field.Name.Value, value: out var stored);

                    return new StateCell(Key: CellName.Parse(candidate: seed.Slot.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)), Value: (hasOverride ? stored!.Value : DefaultValue(field: field)), Clock: (hasOverride ? stored!.Clock : null));
                }).ToArray(), Space: field.Space?.Value, Enum: field.Enum, Visibility: visibility) { Generated = true });
            }
        }

        if (rows.Count > StateCapacity.MaxRows) {
            throw new InvalidOperationException(message: $"The expanded state section declares {rows.Count} rows, past the {StateCapacity.MaxRows}-row limit.");
        }

        return rows.AsReadOnly();
    }

    /// <summary>Compiles an authored state section into its typed runtime catalog. A row's enum is whole-document
    /// validation's alone to refuse: the catalog draws a row's cells from the enum it names when that is a declared
    /// enum on an Int row, and from no symbolic domain otherwise.</summary>
    /// <param name="section">The authored state section, or <see langword="null"/> for an empty catalog.</param>
    /// <returns>The compiled catalog.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="section"/> contains a null declaration, a
    /// duplicate document-lane name, a name shared by the participant and identity lanes, a malformed or duplicate
    /// enum, a host-owned row whose shape no host can serve, or a family whose member rows are missing,
    /// non-contiguous, or of mixed kind. Whole-document validation normally refuses those shapes before runtime
    /// compilation.</exception>
    public static StateCatalog Compile(IStateSection? section) {
        var identity = new object();
        var descriptors = new List<StateDescriptor>();
        var handlesByLane = new Dictionary<string, StateHandle>[Enum.GetValues<StateLane>().Length];
        var keys = new CellKeyTable();
        var laneCounts = new int[handlesByLane.Length];
        var laneFirsts = new int[handlesByLane.Length];

        for (var index = 0; (index < handlesByLane.Length); index++) {
            handlesByLane[index] = new Dictionary<string, StateHandle>(comparer: StringComparer.Ordinal);
            laneFirsts[index] = -1;
        }

        void Add(string name, StateLane lane, RowShape shape, CellKind kind, StateParticipantRole role, bool generated, bool hostOwned, int laneOrdinal) {
            var handle = new StateHandle(
                ordinal: descriptors.Count,
                catalogIdentity: identity
            );

            if (!handlesByLane[((int)lane)].TryAdd(
                key: name,
                value: handle
            )) {
                throw new InvalidOperationException(message: $"State lane '{lane}' declares duplicate name '{name}'.");
            }

            if (laneFirsts[((int)lane)] < 0) {
                laneFirsts[((int)lane)] = handle.Ordinal;
            }

            laneCounts[((int)lane)]++;

            descriptors.Add(item: new StateDescriptor(
                Generated: generated,
                Handle: handle,
                HostOwned: hostOwned,
                Kind: kind,
                Lane: lane,
                LaneOrdinal: laneOrdinal,
                Name: name,
                Role: role,
                Shape: shape
            ));
        }

        var enumsByName = CompileEnums(section: section);
        var rows = ExpandRows(section: section);
        var ordinalsByName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        var rowEnums = new StateEnum?[rows.Count];

        for (var index = 0; (index < rows.Count); index++) {
            var row = (rows[index] ?? throw new InvalidOperationException(message: $"State lane 'Document' contains a null declaration at ordinal {index}."));
            var shape = row.Shape;

            if (
                row.HostOwned &&
                (shape is not (RowShape.Slot or RowShape.Lattice))
            ) {
                throw new InvalidOperationException(message: $"State row '{row.Name.Value}' is host-owned but {shape}-shaped; only Slot and Lattice rows can be served by a host.");
            }

            rowEnums[index] = ResolveRowEnum(
                enumsByName: enumsByName,
                row: row
            );
            ordinalsByName[row.Name.Value] = descriptors.Count;

            Add(
                name: row.Name,
                lane: StateLane.Document,
                shape: shape,
                kind: row.Kind,
                role: StateParticipantRole.None,
                generated: row.Generated,
                hostOwned: row.HostOwned,
                laneOrdinal: index
            );

            foreach (var cell in (row.Cells ?? [])) {
                // Ring addresses are compiled slot symbols, independent of which slots an export holds.
                // Seeding them here would make export/rebuild spend additional retained-key budget.
                if ((cell is not null) && (shape != RowShape.Ring)) {
                    keys.Intern(name: cell.Key);
                }
            }

            // A board's addressable cells are its topology's, not the ones the document happened to author a value
            // for, so every one of them interns here: an unauthored cell must still be writable, iterable and
            // readable by key. A host-owned lattice stores no cell here and addresses none through the key table.
            if (
                (shape == RowShape.Lattice) &&
                !row.HostOwned &&
                (row.EffectiveDomain is StateDomain.CellsOf board) &&
                (TopologyCompilation.Find(
                lattices: section?.Lattices,
                name: board.Topology
            ) is { } topology)
            ) {
                for (var cell = 0; (cell < topology.CellCount); cell++) {
                    keys.Intern(name: topology.NameOf(cell: cell));
                }
            }
        }

        var families = new List<RowFamily>();
        var familiesByName = new Dictionary<string, RowFamily>(comparer: StringComparer.Ordinal);

        foreach (var declared in (section?.Families ?? [])) {
            var family = (declared ?? throw new InvalidOperationException(message: "The state section contains a null family declaration."));
            var resolved = ResolveFamily(
                family: family,
                ordinalsByName: ordinalsByName,
                rows: rows
            );

            if (!familiesByName.TryAdd(
                key: family.Name.Value,
                value: resolved
            )) {
                throw new InvalidOperationException(message: $"The state section declares duplicate family '{family.Name.Value}'.");
            }

            families.Add(item: resolved);
        }

        if (families.Count > StateCapacity.MaxFamilies) {
            throw new InvalidOperationException(message: $"The state section declares {families.Count} families, past the {StateCapacity.MaxFamilies}-family limit.");
        }

        var perParticipantNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        AddSlots(
            declarations: (section?.ParticipantSlots ?? []),
            lane: StateLane.Participant,
            names: perParticipantNames,
            add: Add
        );
        AddSlots(
            declarations: (section?.IdentitySlots ?? []),
            lane: StateLane.Identity,
            names: perParticipantNames,
            add: Add
        );

        var lanes = new StateLaneDescriptor[handlesByLane.Length];

        for (var index = 0; (index < lanes.Length); index++) {
            lanes[index] = new StateLaneDescriptor(
                Count: laneCounts[index],
                FirstOrdinal: laneFirsts[index],
                Lane: ((StateLane)index)
            );
        }

        var recordsByName = (section?.Records ?? []).ToDictionary(keySelector: static record => record.Name.Value, comparer: StringComparer.Ordinal);
        var pools = new List<StatePoolDescriptor>();
        var allPoolOrdinals = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < (section?.Pools?.Count ?? 0)); index++) {
            allPoolOrdinals[section!.Pools![index].Name.Value] = index;
        }
        for (var index = 0; (index < (section?.PairPools?.Count ?? 0)); index++) {
            allPoolOrdinals[section!.PairPools![index].Name.Value] = ((section.Pools?.Count ?? 0) + index);
        }
        foreach (var declaredPool in (section?.Pools ?? [])) {
            var record = recordsByName[declaredPool.Record.Value];
            var fields = new List<StatePoolFieldDescriptor>();

            for (var fieldOrdinal = 0; (fieldOrdinal < (record.Fields?.Count ?? 0)); fieldOrdinal++) {
                var field = record.Fields![fieldOrdinal];
                var rowName = PoolRowName(pool: declaredPool, "field", field.Name.Value);

                fields.Add(item: new StatePoolFieldDescriptor(
                    Default: DefaultValue(field: field),
                    Kind: field.Kind,
                    Name: field.Name,
                    Ordinal: fieldOrdinal,
                    RowOrdinal: ordinalsByName[rowName.Value],
                    Declaration: field
                ));
            }

            pools.Add(item: new StatePoolDescriptor(
                Capacity: declaredPool.Capacity,
                DomainRowOrdinal: ordinalsByName[PoolRowName(pool: declaredPool, "live").Value],
                Fields: fields.AsReadOnly(),
                GenerationRowOrdinal: ordinalsByName[PoolRowName(pool: declaredPool, "generation").Value],
                Name: declaredPool.Name,
                Ordinal: pools.Count,
                Record: declaredPool.Record
            ));
        }
        foreach (var declaredPool in (section?.PairPools ?? [])) {
            var record = recordsByName[declaredPool.Record.Value];
            var leftOrdinal = allPoolOrdinals[declaredPool.LeftPool.Value];
            var rightOrdinal = allPoolOrdinals[declaredPool.RightPool.Value];
            var capacity = (rows[ordinalsByName[PairPoolRowName(pool: declaredPool, "generation").Value]].Capacity ?? throw new InvalidOperationException(message: "Generated pair generation row has no capacity."));
            var fields = new List<StatePoolFieldDescriptor>();

            for (var fieldOrdinal = 0; (fieldOrdinal < (record.Fields?.Count ?? 0)); fieldOrdinal++) {
                var field = record.Fields![fieldOrdinal];

                fields.Add(item: new StatePoolFieldDescriptor(
                    Default: DefaultValue(field: field), Kind: field.Kind, Name: field.Name, Ordinal: fieldOrdinal,
                    RowOrdinal: ordinalsByName[PairPoolRowName(pool: declaredPool, "field", field.Name.Value).Value], Declaration: field));
            }
            pools.Add(item: new StatePoolDescriptor(
                Ordinal: pools.Count, Name: declaredPool.Name, Record: declaredPool.Record, Capacity: capacity,
                DomainRowOrdinal: ordinalsByName[PairPoolRowName(pool: declaredPool, "live").Value],
                GenerationRowOrdinal: ordinalsByName[PairPoolRowName(pool: declaredPool, "generation").Value],
                Fields: fields.AsReadOnly(), IsPair: true, LeftPoolOrdinal: leftOrdinal, RightPoolOrdinal: rightOrdinal,
                MaxLive: declaredPool.MaxLive, Directed: declaredPool.Directed, AllowSelf: declaredPool.AllowSelf));
        }

        return new StateCatalog(
            descriptors: descriptors.ToArray(),
            enumsByName: enumsByName,
            families: families.ToArray(),
            familiesByName: familiesByName,
            handlesByLane: handlesByLane,
            identity: identity,
            keys: keys,
            lanes: lanes,
            rowEnums: rowEnums,
            rows: [.. rows],
            pools: [.. pools]
        );
    }
    /// <summary>Determines whether another catalog carries the same declaration shape, ignoring instance branding.</summary>
    /// <param name="other">The catalog to compare with.</param>
    /// <returns><see langword="true"/> when every descriptor and every family agrees.</returns>
    public bool HasSameShape(StateCatalog other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        if (
            (m_descriptors.Length != other.m_descriptors.Length) ||
            (m_readOnlyFamilies.Count != other.m_readOnlyFamilies.Count) ||
            (m_pools.Count != other.m_pools.Count)
        ) {
            return false;
        }

        for (var index = 0; (index < m_descriptors.Length); index++) {
            var left = m_descriptors[index];
            var right = other.m_descriptors[index];

            if (
                !string.Equals(
                a: left.Name,
                b: right.Name,
                comparisonType: StringComparison.Ordinal
            ) ||
                (left.Lane != right.Lane) ||
                (left.Shape != right.Shape) ||
                (left.Kind != right.Kind) ||
                (left.Role != right.Role) ||
                (left.Generated != right.Generated) ||
                (left.HostOwned != right.HostOwned) ||
                (left.LaneOrdinal != right.LaneOrdinal)
            ) {
                return false;
            }
        }

        for (var index = 0; (index < m_readOnlyFamilies.Count); index++) {
            if (m_readOnlyFamilies[index] != other.m_readOnlyFamilies[index]) {
                return false;
            }
        }

        for (var index = 0; (index < m_pools.Count); index++) {
            var left = m_pools[index];
            var right = other.m_pools[index];

            if ((left.Name != right.Name) || (left.Record != right.Record) || (left.Capacity != right.Capacity) || (left.Fields.Count != right.Fields.Count) || (left.IsPair != right.IsPair) || (left.LeftPoolOrdinal != right.LeftPoolOrdinal) || (left.RightPoolOrdinal != right.RightPoolOrdinal) || (left.MaxLive != right.MaxLive) || (left.Directed != right.Directed) || (left.AllowSelf != right.AllowSelf)) {
                return false;
            }
            for (var field = 0; (field < left.Fields.Count); field++) {
                if (left.Fields[field].Declaration != right.Fields[field].Declaration) {
                    return false;
                }
            }
        }

        return true;
    }
    /// <summary>Gets the descriptor extent of one ownership lane.</summary>
    /// <param name="lane">The lane to describe.</param>
    /// <returns>The lane's descriptor range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lane"/> is not a declared lane.</exception>
    public StateLaneDescriptor Lane(StateLane lane) => ((((uint)lane) < ((uint)m_lanes.Length))
        ? m_lanes[((int)lane)]
        : throw new ArgumentOutOfRangeException(
            paramName: nameof(lane),
            actualValue: lane,
            message: "The state lane is not declared."
        )
    );
    /// <summary>Determines without allocation whether an authored section still carries this catalog's shape.</summary>
    /// <param name="section">The section to compare with.</param>
    /// <returns><see langword="true"/> when the section compiles to the same shape.</returns>
    public bool MatchesShape(IStateSection? section) {
        Interlocked.Increment(location: ref ShapeWalkCountValue);

        var descriptorIndex = 0;

        bool Match(string name, StateLane lane, RowShape shape, CellKind kind, bool generated, bool hostOwned, int laneOrdinal) {
            if (((uint)descriptorIndex) >= ((uint)m_descriptors.Length)) {
                return false;
            }

            var descriptor = m_descriptors[descriptorIndex++];

            return (
                string.Equals(
                a: descriptor.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            ) &&
                (descriptor.Lane == lane) &&
                (descriptor.Shape == shape) &&
                (descriptor.Kind == kind) &&
                (descriptor.Role == StateParticipantRole.None) &&
                (descriptor.Generated == generated) &&
                (descriptor.HostOwned == hostOwned) &&
                (descriptor.LaneOrdinal == laneOrdinal)
            );
        }

        // Compare declarations, never their expanded populations. A value-only replacement may have entirely
        // different seeds while retaining these handles; population admission belongs to the load path.
        var rows = (section?.Rows ?? []);
        var authoredRowCount = ((m_pools.Count == 0) ? m_rows.Count : m_pools[0].DomainRowOrdinal);

        if (rows.Count != authoredRowCount) {
            return false;
        }

        for (var index = 0; (index < rows.Count); index++) {
            if (
                (rows[index] is not { } row) || row.Generated ||
                !Match(
                name: row.Name,
                lane: StateLane.Document,
                shape: row.Shape,
                kind: row.Kind,
                generated: row.Generated,
                hostOwned: row.HostOwned,
                laneOrdinal: index
            )
            ) {
                return false;
            }
        }

        if (!MatchesFamilies(section: section)) {
            return false;
        }

        descriptorIndex = m_lanes[((int)StateLane.Document)].Count;

        if (!MatchesSlotLane(
            declarations: (section?.ParticipantSlots ?? []),
            lane: StateLane.Participant,
            descriptors: m_descriptors,
            descriptorIndex: ref descriptorIndex
        )) {
            return false;
        }

        if (!(
            MatchesSlotLane(
            declarations: (section?.IdentitySlots ?? []),
            lane: StateLane.Identity,
            descriptors: m_descriptors,
            descriptorIndex: ref descriptorIndex
        ) &&
            (descriptorIndex == m_descriptors.Length)
        )) {
            return false;
        }

        var declaredPools = (section?.Pools ?? []);
        var declaredPairPools = (section?.PairPools ?? []);

        if ((declaredPools.Count + declaredPairPools.Count) != m_pools.Count) {
            return false;
        }
        var records = (section?.Records ?? []);

        for (var index = 0; (index < records.Count); index++) {
            if (records[index] is not { } record) {
                return false;
            }
            for (var prior = 0; (prior < index); prior++) {
                if (records[prior].Name == record.Name) {
                    return false;
                }
            }
        }

        static StateRecord? FindRecord(IReadOnlyList<StateRecord> declarations, CellName name) {
            for (var index = 0; (index < declarations.Count); index++) {
                if (declarations[index].Name == name) {
                    return declarations[index];
                }
            }
            return null;
        }

        for (var index = 0; (index < declaredPools.Count); index++) {
            var compiled = m_pools[index];
            var declared = declaredPools[index];
            var record = ((declared is null) ? null : FindRecord(declarations: records, name: declared.Record));

            if (
                (declared is null) || compiled.IsPair ||
                (declared.Name != compiled.Name) ||
                (declared.Record != compiled.Record) ||
                (declared.Capacity != compiled.Capacity) ||
                (record is null) ||
                ((record.Fields?.Count ?? 0) != compiled.Fields.Count)
            ) {
                return false;
            }
            for (var field = 0; (field < compiled.Fields.Count); field++) {
                if (record.Fields![field] != compiled.Fields[field].Declaration) {
                    return false;
                }
            }
        }
        for (var pairIndex = 0; (pairIndex < declaredPairPools.Count); pairIndex++) {
            var compiled = m_pools[(declaredPools.Count + pairIndex)];
            var declared = declaredPairPools[pairIndex];
            var record = ((declared is null) ? null : FindRecord(declarations: records, name: declared.Record));

            if ((declared is null) || !compiled.IsPair || (declared.Name != compiled.Name) || (declared.Record != compiled.Record) || (declared.MaxLive != compiled.MaxLive) || (declared.Directed != compiled.Directed) || (declared.AllowSelf != compiled.AllowSelf) || (m_pools[compiled.LeftPoolOrdinal].Name != declared.LeftPool) || (m_pools[compiled.RightPoolOrdinal].Name != declared.RightPool) || (record is null) || ((record.Fields?.Count ?? 0) != compiled.Fields.Count)) {
                return false;
            }
            for (var field = 0; (field < compiled.Fields.Count); field++) {
                if (record.Fields![field] != compiled.Fields[field].Declaration) {
                    return false;
                }
            }
        }
        return true;
    }
    /// <summary>Attempts to read a descriptor by its catalog-instance-relative handle.</summary>
    /// <param name="handle">The handle to inspect.</param>
    /// <param name="descriptor">The descriptor on success; otherwise the default descriptor.</param>
    /// <returns><see langword="true"/> when the handle addresses this catalog's current shape.</returns>
    public bool TryGetDescriptor(StateHandle handle, out StateDescriptor descriptor) {
        if (
            handle.IsValid &&
            handle.BelongsTo(catalogIdentity: m_identity) &&
            (handle.Ordinal < m_descriptors.Length)
        ) {
            descriptor = m_descriptors[handle.Ordinal];

            return true;
        }

        descriptor = default;

        return false;
    }
    /// <summary>Attempts to read the enum a declared row names.</summary>
    /// <param name="handle">A handle minted by this catalog.</param>
    /// <param name="symbols">The enum on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the handle addresses a document row that names an enum.</returns>
    public bool TryGetEnum(StateHandle handle, out StateEnum? symbols) {
        if (
            TryGetDescriptor(
            descriptor: out var descriptor,
            handle: handle
        ) &&
            (descriptor.Lane == StateLane.Document) &&
            (descriptor.LaneOrdinal < m_rowEnums.Length)
        ) {
            symbols = m_rowEnums[descriptor.LaneOrdinal];

            return (symbols is not null);
        }

        symbols = null;

        return false;
    }
    /// <summary>Attempts to read a declared enum by name.</summary>
    /// <param name="name">The enum's stable name.</param>
    /// <param name="symbols">The enum on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the section declares the enum.</returns>
    public bool TryGetEnum(CellName name, out StateEnum? symbols) => m_enumsByName.TryGetValue(
        key: name.Value,
        value: out symbols
    );
    /// <summary>Attempts to read a compiled family by name.</summary>
    /// <param name="name">The family's stable name.</param>
    /// <param name="family">The compiled ordinal range on success; otherwise the default.</param>
    /// <returns><see langword="true"/> when the section declares the family.</returns>
    public bool TryGetFamily(CellName name, out RowFamily family) => m_familiesByName.TryGetValue(
        key: name.Value,
        value: out family
    );
    /// <summary>Resolves an authored name once into a typed handle.</summary>
    /// <param name="lane">The ownership lane to search.</param>
    /// <param name="name">The authored stable name.</param>
    /// <param name="handle">The resolved handle, or the invalid default value when no declaration matches.</param>
    /// <returns><see langword="true"/> when the lane declares the name.</returns>
    public bool TryResolve(StateLane lane, string name, out StateHandle handle) {
        // The lane tables are indexed by lane, so their length is the guard. Enum.IsDefined would read a type cache
        // the runtime holds only weakly, and allocate it again after every collection.
        if (
            (((uint)lane) >= ((uint)m_handlesByLane.Length)) ||
            (name is null)
        ) {
            handle = default;

            return false;
        }

        return m_handlesByLane[((int)lane)].TryGetValue(
            key: name,
            value: out handle
        );
    }
    /// <summary>Resolves a validated state name once into a typed handle.</summary>
    /// <param name="lane">The ownership lane to search.</param>
    /// <param name="name">The validated authored name.</param>
    /// <param name="handle">The resolved handle, or the invalid default value when no declaration matches.</param>
    /// <returns><see langword="true"/> when the lane declares the name.</returns>
    public bool TryResolve(StateLane lane, CellName name, out StateHandle handle) => TryResolve(
        lane: lane,
        name: name.Value,
        handle: out handle
    );

    private static Dictionary<string, StateEnum> CompileEnums(IStateSection? section) {
        var enumsByName = new Dictionary<string, StateEnum>(comparer: StringComparer.Ordinal);

        foreach (var declared in (section?.Enums ?? [])) {
            var symbols = (declared ?? throw new InvalidOperationException(message: "The state section contains a null enum declaration."));

            if (!symbols.TryValidate(reason: out var reason)) {
                throw new InvalidOperationException(message: reason);
            }

            if (!enumsByName.TryAdd(
                key: symbols.Name.Value,
                value: symbols
            )) {
                throw new InvalidOperationException(message: $"The state section declares duplicate enum '{symbols.Name.Value}'.");
            }
        }

        if (enumsByName.Count > StateCapacity.MaxEnums) {
            throw new InvalidOperationException(message: $"The state section declares {enumsByName.Count} enums, past the {StateCapacity.MaxEnums}-enum limit.");
        }

        return enumsByName;
    }
    private static StateEnum? ResolveRowEnum(StateRow row, IReadOnlyDictionary<string, StateEnum> enumsByName) => (
        ((row.Enum is { } name) &&
        (row.Kind == CellKind.Int) &&
        enumsByName.TryGetValue(
            key: name.Value,
            value: out var symbols
        ))
            ? symbols
            : null
    );
    private bool MatchesFamilies(IStateSection? section) {
        var declared = (section?.Families ?? []);

        if (declared.Count != m_readOnlyFamilies.Count) {
            return false;
        }

        for (var index = 0; (index < declared.Count); index++) {
            if (
                (declared[index] is not { } family) ||
                (m_readOnlyFamilies[index].Name != family.Name) ||
                (m_readOnlyFamilies[index].Count != family.Size)
            ) {
                return false;
            }

            for (var member = 0; (member < family.Size); member++) {
                if (!m_readOnlyFamilies[index].TryGetOrdinal(
                    index: family.FamilyIndex(index: member),
                    ordinal: out var ordinal
                ) || (TryResolve(
                    lane: StateLane.Document,
                    name: family.MemberName(index: member),
                    handle: out var handle
                ) && (handle.Ordinal != ordinal))) {
                    return false;
                }
            }
        }

        return true;
    }
}
