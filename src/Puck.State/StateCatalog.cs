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
/// <summary>Identifies the storage shape selected by a compiled state descriptor.</summary>
public enum StateStorageShape : byte {
    /// <summary>The state is addressed as one scalar slot.</summary>
    Slot,

    /// <summary>The state is addressed as a keyed table.</summary>
    Keyed,

    /// <summary>The state is addressed as one scalar per lattice cell.</summary>
    Lattice,
}
/// <summary>Identifies the deterministic value domain selected by a compiled state descriptor.</summary>
public enum StateValueKind : byte {
    /// <summary>A whole signed 64-bit integer, carried and compared as a raw <see cref="long"/>.</summary>
    Int = ((byte)CellKind.Int),

    /// <summary>A Q48.16 fixed-point value carried as raw deterministic bits.</summary>
    Fixed = ((byte)CellKind.Fixed),

    /// <summary>A boolean value.</summary>
    Bool = ((byte)CellKind.Bool),

    /// <summary>A bounded text value.</summary>
    Text = ((byte)CellKind.Text),

    /// <summary>A per-participant Q48.16 counter.</summary>
    Counter = 4,

    /// <summary>A per-participant duration stored in engine ticks.</summary>
    Timer,
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
/// <summary>Describes one authored state declaration after its ownership, storage, and value domains are compiled.</summary>
/// <param name="Handle">The catalog-instance-relative typed handle for this declaration.</param>
/// <param name="Name">The authored stable name.</param>
/// <param name="Ownership">The lane that owns the state.</param>
/// <param name="Storage">The storage shape selected by the authored declaration.</param>
/// <param name="ValueKind">The deterministic value domain selected by the authored declaration.</param>
/// <param name="LaneOrdinal">The declaration's zero-based document-order ordinal within <paramref name="Ownership"/>.</param>
public readonly record struct StateDescriptor(
    StateHandle Handle,
    string Name,
    StateLane Ownership,
    StateStorageShape Storage,
    StateValueKind ValueKind,
    int LaneOrdinal
);
/// <summary>Compiles an <see cref="IStateSection"/> into immutable typed descriptors and catalog-instance-relative
/// handles. Descriptor ordinals are assigned deterministically in document, participant, then identity declaration
/// order.</summary>
/// <remarks>The authored section remains the serialization source. This catalog is a runtime compiler product and
/// carries no mutable state values.</remarks>
public sealed class StateCatalog {
    private readonly object m_identity;
    private readonly StateDescriptor[] m_descriptors;
    private readonly ReadOnlyCollection<StateDescriptor> m_readOnlyDescriptors;
    private readonly Dictionary<string, StateHandle>[] m_handlesByLane;

    private StateCatalog(object identity, StateDescriptor[] descriptors, Dictionary<string, StateHandle>[] handlesByLane) {
        m_identity = identity;
        m_descriptors = descriptors;
        m_readOnlyDescriptors = Array.AsReadOnly(array: descriptors);
        m_handlesByLane = handlesByLane;
    }

    /// <summary>Gets the compiled descriptors in stable handle order.</summary>
    public IReadOnlyList<StateDescriptor> Descriptors => m_readOnlyDescriptors;
    /// <summary>Gets the number of compiled state declarations.</summary>
    public int Count => m_descriptors.Length;

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

    /// <summary>Compiles an authored state section into its typed runtime catalog.</summary>
    /// <param name="section">The authored state section, or <see langword="null"/> for an empty catalog.</param>
    /// <returns>The compiled catalog.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="section"/> contains a null declaration or a
    /// duplicate document-lane name, or a name shared by the participant and identity lanes. Whole-document
    /// validation normally refuses those shapes before runtime compilation.</exception>
    public static StateCatalog Compile(IStateSection? section) {
        var identity = new object();
        var descriptors = new List<StateDescriptor>();
        var handlesByLane = new Dictionary<string, StateHandle>[Enum.GetValues<StateLane>().Length];

        for (var index = 0; (index < handlesByLane.Length); index++) {
            handlesByLane[index] = new Dictionary<string, StateHandle>(comparer: StringComparer.Ordinal);
        }

        void Add(string name, StateLane ownership, StateStorageShape storage, StateValueKind valueKind, int laneOrdinal) {
            var handle = new StateHandle(
                ordinal: descriptors.Count,
                catalogIdentity: identity
            );

            if (!handlesByLane[((int)ownership)].TryAdd(
                key: name,
                value: handle
            )) {
                throw new InvalidOperationException(message: $"State lane '{ownership}' declares duplicate name '{name}'.");
            }

            descriptors.Add(item: new StateDescriptor(
                Handle: handle,
                LaneOrdinal: laneOrdinal,
                Name: name,
                Ownership: ownership,
                Storage: storage,
                ValueKind: valueKind
            ));
        }

        var rows = (section?.Rows ?? []);
        var lattices = section?.Lattices;

        for (var index = 0; (index < rows.Count); index++) {
            var row = (rows[index] ?? throw new InvalidOperationException(message: $"State lane 'Document' contains a null declaration at ordinal {index}."));

            Add(
                name: row.Name,
                ownership: StateLane.Document,
                storage: Storage(row: row, lattices: lattices),
                valueKind: FromCellKind(kind: row.Kind),
                laneOrdinal: index
            );
        }

        var perParticipantNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        AddSlots(
            declarations: (section?.ParticipantSlots ?? []),
            ownership: StateLane.Participant,
            names: perParticipantNames,
            add: Add
        );
        AddSlots(
            declarations: (section?.IdentitySlots ?? []),
            ownership: StateLane.Identity,
            names: perParticipantNames,
            add: Add
        );

        return new StateCatalog(
            identity: identity,
            descriptors: descriptors.ToArray(),
            handlesByLane: handlesByLane
        );
    }
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

    /// <summary>Determines whether another catalog carries the same declaration shape, ignoring instance branding.</summary>
    /// <param name="other">The catalog to compare with.</param>
    public bool HasSameShape(StateCatalog other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        if (m_descriptors.Length != other.m_descriptors.Length) {
            return false;
        }

        for (var index = 0; (index < m_descriptors.Length); index++) {
            var left = m_descriptors[index];
            var right = other.m_descriptors[index];

            if (
                !string.Equals(a: left.Name, b: right.Name, comparisonType: StringComparison.Ordinal) ||
                (left.Ownership != right.Ownership) ||
                (left.Storage != right.Storage) ||
                (left.ValueKind != right.ValueKind) ||
                (left.LaneOrdinal != right.LaneOrdinal)
            ) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Gets the running count of calls to <see cref="MatchesShape"/> across every catalog. A
    /// recompilation site calls it once per candidate section; nothing on a warm read of an already-keyed catalog
    /// may call it, so a law reads this to prove that contract without a wall-clock measurement.</summary>
    public static long ShapeWalkCount => Interlocked.Read(location: ref s_shapeWalkCount);
    private static long s_shapeWalkCount;

    /// <summary>Determines without allocation whether an authored section still carries this catalog's shape.</summary>
    /// <param name="section">The section to compare with.</param>
    public bool MatchesShape(IStateSection? section) {
        Interlocked.Increment(location: ref s_shapeWalkCount);

        var descriptorIndex = 0;

        bool Match(string name, StateLane ownership, StateStorageShape storage, StateValueKind valueKind, int laneOrdinal) {
            if (((uint)descriptorIndex) >= ((uint)m_descriptors.Length)) {
                return false;
            }

            var descriptor = m_descriptors[descriptorIndex++];

            return (
                string.Equals(a: descriptor.Name, b: name, comparisonType: StringComparison.Ordinal) &&
                (descriptor.Ownership == ownership) &&
                (descriptor.Storage == storage) &&
                (descriptor.ValueKind == valueKind) &&
                (descriptor.LaneOrdinal == laneOrdinal)
            );
        }

        var rows = (section?.Rows ?? []);
        var lattices = section?.Lattices;

        for (var index = 0; (index < rows.Count); index++) {
            if (
                (rows[index] is not { } row) ||
                !TryFromCellKind(kind: row.Kind, valueKind: out var valueKind) ||
                !Match(
                    name: row.Name,
                    ownership: StateLane.Document,
                    storage: Storage(row: row, lattices: lattices),
                    valueKind: valueKind,
                    laneOrdinal: index
                )
            ) {
                return false;
            }
        }

        if (!MatchesSlotLane(
            declarations: (section?.ParticipantSlots ?? []),
            ownership: StateLane.Participant,
            descriptors: m_descriptors,
            descriptorIndex: ref descriptorIndex
        )) {
            return false;
        }

        return (
            MatchesSlotLane(
                declarations: (section?.IdentitySlots ?? []),
                ownership: StateLane.Identity,
                descriptors: m_descriptors,
                descriptorIndex: ref descriptorIndex
            ) &&
            (descriptorIndex == m_descriptors.Length)
        );
    }

    // A row lying over a dense (field-kind) topology stores one scalar per cell; every other shape is the sparse
    // slot or keyed store.
    private static StateStorageShape Storage(StateRow row, IReadOnlyList<LatticeTopology>? lattices) {
        if (row.EffectiveDomain is StateDomain.CellsOf cellsOf) {
            for (var index = 0; (index < (lattices?.Count ?? 0)); index++) {
                var topology = lattices![index];

                if ((topology is not null) && (topology.Kind == TopologyKind.Field) && string.Equals(a: topology.Name, b: cellsOf.Topology, comparisonType: StringComparison.Ordinal)) {
                    return StateStorageShape.Lattice;
                }
            }
        }

        return (row.IsKeyed ? StateStorageShape.Keyed : StateStorageShape.Slot);
    }
    private static void AddSlots(IReadOnlyList<IStateSlot> declarations, StateLane ownership, ISet<string> names, Action<string, StateLane, StateStorageShape, StateValueKind, int> add) {
        for (var index = 0; (index < declarations.Count); index++) {
            var declaration = (declarations[index] ?? throw new InvalidOperationException(message: $"State lane '{ownership}' contains a null declaration at ordinal {index}."));

            if (!names.Add(item: declaration.Name)) {
                throw new InvalidOperationException(message: $"State lanes 'Participant' and 'Identity' declare duplicate name '{declaration.Name}'.");
            }

            add(
                declaration.Name,
                ownership,
                StateStorageShape.Slot,
                declaration.ValueKind switch {
                    StateValueKind.Counter => StateValueKind.Counter,
                    StateValueKind.Timer => StateValueKind.Timer,
                    _ => throw new InvalidOperationException(message: $"State lane '{ownership}' declaration '{declaration.Name}' carries unknown value kind '{declaration.ValueKind}'."),
                },
                index
            );
        }
    }
    private static StateValueKind FromCellKind(CellKind kind) => kind switch {
        CellKind.Int => StateValueKind.Int,
        CellKind.Fixed => StateValueKind.Fixed,
        CellKind.Bool => StateValueKind.Bool,
        CellKind.Text => StateValueKind.Text,
        _ => throw new InvalidOperationException(message: $"Unknown state cell kind '{kind}'."),
    };
    private static bool MatchesSlotLane(IReadOnlyList<IStateSlot> declarations, StateLane ownership, IReadOnlyList<StateDescriptor> descriptors, ref int descriptorIndex) {
        for (var index = 0; (index < declarations.Count); index++) {
            if (declarations[index] is not { } declaration) {
                return false;
            }

            var valueKind = declaration.ValueKind switch {
                StateValueKind.Counter => StateValueKind.Counter,
                StateValueKind.Timer => StateValueKind.Timer,
                _ => ((StateValueKind?)null),
            };

            if (
                (valueKind is not { } kind) ||
                (((uint)descriptorIndex) >= ((uint)descriptors.Count))
            ) {
                return false;
            }

            var descriptor = descriptors[descriptorIndex++];

            if (
                !string.Equals(a: descriptor.Name, b: declaration.Name, comparisonType: StringComparison.Ordinal) ||
                (descriptor.Ownership != ownership) ||
                (descriptor.Storage != StateStorageShape.Slot) ||
                (descriptor.ValueKind != kind) ||
                (descriptor.LaneOrdinal != index)
            ) {
                return false;
            }
        }

        return true;
    }
    private static bool TryFromCellKind(CellKind kind, out StateValueKind valueKind) {
        switch (kind) {
            case CellKind.Int:
            case CellKind.Fixed:
            case CellKind.Bool:
            case CellKind.Text:
                valueKind = ((StateValueKind)kind);

                return true;
            default:
                valueKind = default;

                return false;
        }
    }
}
