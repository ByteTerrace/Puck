using System.Collections.ObjectModel;

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

    private StateCatalog(
        object identity,
        StateDescriptor[] descriptors,
        Dictionary<string, StateHandle>[] handlesByLane,
        StateLaneDescriptor[] lanes,
        CellKeyTable keys,
        Dictionary<string, StateEnum> enumsByName,
        StateEnum?[] rowEnums,
        Dictionary<string, RowFamily> familiesByName,
        RowFamily[] families
    ) {
        m_descriptors = descriptors;
        m_enumsByName = enumsByName;
        m_familiesByName = familiesByName;
        m_handlesByLane = handlesByLane;
        m_identity = identity;
        m_keys = keys;
        m_lanes = lanes;
        m_readOnlyDescriptors = Array.AsReadOnly(array: descriptors);
        m_readOnlyFamilies = Array.AsReadOnly(array: families);
        m_rowEnums = rowEnums;
    }

    /// <summary>Gets the number of compiled state declarations.</summary>
    public int Count => m_descriptors.Length;
    /// <summary>Gets the compiled descriptors in stable handle order.</summary>
    public IReadOnlyList<StateDescriptor> Descriptors => m_readOnlyDescriptors;
    /// <summary>Gets the compiled row families in declaration order.</summary>
    public IReadOnlyList<RowFamily> Families => m_readOnlyFamilies;
    /// <summary>Gets the compiled key symbols. Runtime names are owned by <see cref="StateArena.Keys"/>.</summary>
    public CellKeyTable Keys => m_keys;
    /// <summary>Gets the running count of calls to <see cref="MatchesShape"/> across every catalog. A
    /// recompilation site calls it once per candidate section; nothing on a warm read of an already-keyed catalog
    /// may call it, so a law reads this to prove that contract without a wall-clock measurement.</summary>
    public static long ShapeWalkCount => Interlocked.Read(location: ref ShapeWalkCountValue);

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

    /// <summary>Compiles an authored state section into its typed runtime catalog.</summary>
    /// <param name="section">The authored state section, or <see langword="null"/> for an empty catalog.</param>
    /// <returns>The compiled catalog.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="section"/> contains a null declaration, a
    /// duplicate document-lane name, a name shared by the participant and identity lanes, a malformed or duplicate
    /// enum, a row naming an undeclared enum or naming one on a kind that cannot carry it, a host-owned row whose
    /// shape no host can serve, or a family whose member rows are missing, non-contiguous, or of mixed kind.
    /// Whole-document validation normally refuses those shapes before runtime compilation.</exception>
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
        var rows = (section?.Rows ?? []);
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
                if (cell is not null) {
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

        return new StateCatalog(
            descriptors: descriptors.ToArray(),
            enumsByName: enumsByName,
            families: families.ToArray(),
            familiesByName: familiesByName,
            handlesByLane: handlesByLane,
            identity: identity,
            keys: keys,
            lanes: lanes,
            rowEnums: rowEnums
        );
    }
    /// <summary>Determines whether another catalog carries the same declaration shape, ignoring instance branding.</summary>
    /// <param name="other">The catalog to compare with.</param>
    /// <returns><see langword="true"/> when every descriptor and every family agrees.</returns>
    public bool HasSameShape(StateCatalog other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        if (
            (m_descriptors.Length != other.m_descriptors.Length) ||
            (m_readOnlyFamilies.Count != other.m_readOnlyFamilies.Count)
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

        return true;
    }
    /// <summary>Gets the descriptor extent of one ownership lane.</summary>
    /// <param name="lane">The lane to describe.</param>
    /// <returns>The lane's descriptor range.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lane"/> is not a declared lane.</exception>
    public StateLaneDescriptor Lane(StateLane lane) => (Enum.IsDefined(value: lane)
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

        var rows = (section?.Rows ?? []);

        for (var index = 0; (index < rows.Count); index++) {
            if (
                (rows[index] is not { } row) ||
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

        if (!MatchesSlotLane(
            declarations: (section?.ParticipantSlots ?? []),
            lane: StateLane.Participant,
            descriptors: m_descriptors,
            descriptorIndex: ref descriptorIndex
        )) {
            return false;
        }

        return (
            MatchesSlotLane(
            declarations: (section?.IdentitySlots ?? []),
            lane: StateLane.Identity,
            descriptors: m_descriptors,
            descriptorIndex: ref descriptorIndex
        ) &&
            (descriptorIndex == m_descriptors.Length)
        );
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
        if (
            !Enum.IsDefined(value: lane) ||
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
    private static StateEnum? ResolveRowEnum(StateRow row, IReadOnlyDictionary<string, StateEnum> enumsByName) {
        if (row.Enum is not { } name) {
            return null;
        }

        if (row.Kind != CellKind.Int) {
            throw new InvalidOperationException(message: $"State row '{row.Name.Value}' names enum '{name.Value}' but is {row.Kind}; only Int rows carry a symbolic domain.");
        }

        return (enumsByName.TryGetValue(
            key: name.Value,
            value: out var symbols
        )
            ? symbols
            : throw new InvalidOperationException(message: $"State row '{row.Name.Value}' names undeclared enum '{name.Value}'.")
        );
    }
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
