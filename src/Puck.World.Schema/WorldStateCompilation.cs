using System.Collections.ObjectModel;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>Identifies which ownership lane declares a compiled state descriptor.</summary>
public enum WorldStateOwnershipLane : byte {
    /// <summary>The world document owns the state.</summary>
    World,

    /// <summary>One body owns the ephemeral state.</summary>
    Body,

    /// <summary>One durable identity owns the state.</summary>
    Identity,
}

/// <summary>Identifies the storage shape selected by a compiled state descriptor.</summary>
public enum WorldStateStorageShape : byte {
    /// <summary>The state is addressed as one scalar slot.</summary>
    Slot,

    /// <summary>The state is addressed as a keyed table.</summary>
    Keyed,

    /// <summary>The state is addressed as one scalar per lattice cell.</summary>
    Lattice,
}

/// <summary>Identifies the deterministic value domain selected by a compiled state descriptor.</summary>
public enum WorldStateValueKind : byte {
    /// <summary>A whole signed integer within <see cref="WorldStateCapacity.MinIntCellValue"/> through
    /// <see cref="WorldStateCapacity.MaxIntCellValue"/>, the range every engine read can lift to fixed point.</summary>
    Int = (byte)CellKind.Int,

    /// <summary>A Q48.16 fixed-point value carried as raw deterministic bits.</summary>
    Fixed = (byte)CellKind.Fixed,

    /// <summary>A boolean value.</summary>
    Bool = (byte)CellKind.Bool,

    /// <summary>A bounded text value.</summary>
    Text = (byte)CellKind.Text,

    /// <summary>A per-body Q48.16 action-state counter.</summary>
    Counter = 4,

    /// <summary>A per-body action-state duration stored in engine ticks.</summary>
    Timer,
}

/// <summary>Identifies one descriptor in the <see cref="WorldStateCatalog"/> that minted it.</summary>
/// <remarks>Handles are bound to one catalog instance. A processor resolves a name during compilation, retains the
/// handle while that catalog is current, and uses the catalog indexer during execution instead of repeating a string
/// lookup. Value-only definition updates retain the catalog and its handles; a declaration-shape change produces a
/// replacement catalog and refuses the old handles. The default value is invalid.</remarks>
public readonly record struct WorldStateHandle {
    private readonly object? m_catalogIdentity;
    private readonly int m_encodedOrdinal;

    internal WorldStateHandle(int ordinal, object catalogIdentity) {
        m_catalogIdentity = catalogIdentity;
        m_encodedOrdinal = checked(ordinal + 1);
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
public readonly record struct WorldStateDescriptor(
    WorldStateHandle Handle,
    string Name,
    WorldStateOwnershipLane Ownership,
    WorldStateStorageShape Storage,
    WorldStateValueKind ValueKind,
    int LaneOrdinal
);

/// <summary>Compiles a <see cref="WorldStateSection"/> into immutable typed descriptors and catalog-instance-relative
/// handles. Descriptor ordinals are assigned deterministically in world, body, then identity document order.</summary>
/// <remarks>The authored section remains the serialization source. This catalog is a runtime compiler product and
/// carries no mutable state values.</remarks>
public sealed class WorldStateCatalog {
    private readonly object m_identity;
    private readonly WorldStateDescriptor[] m_descriptors;
    private readonly ReadOnlyCollection<WorldStateDescriptor> m_readOnlyDescriptors;
    private readonly Dictionary<string, WorldStateHandle>[] m_handlesByLane;

    private WorldStateCatalog(object identity, WorldStateDescriptor[] descriptors, Dictionary<string, WorldStateHandle>[] handlesByLane) {
        m_identity = identity;
        m_descriptors = descriptors;
        m_readOnlyDescriptors = Array.AsReadOnly(array: descriptors);
        m_handlesByLane = handlesByLane;
    }

    /// <summary>Gets the compiled descriptors in stable handle order.</summary>
    public IReadOnlyList<WorldStateDescriptor> Descriptors => m_readOnlyDescriptors;

    /// <summary>Gets the number of compiled state declarations.</summary>
    public int Count => m_descriptors.Length;

    /// <summary>Gets the descriptor addressed by <paramref name="handle"/>.</summary>
    /// <param name="handle">A handle minted by this catalog.</param>
    /// <returns>The compiled descriptor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="handle"/> is invalid or outside this catalog.</exception>
    public WorldStateDescriptor this[WorldStateHandle handle] => (TryGetDescriptor(
        handle: handle,
        descriptor: out var descriptor
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
    /// duplicate world-lane name, or a name shared by the body and identity lanes. Whole-document validation
    /// normally refuses those shapes before runtime compilation.</exception>
    public static WorldStateCatalog Compile(WorldStateSection? section) {
        var identity = new object();
        var descriptors = new List<WorldStateDescriptor>();
        var handlesByLane = new Dictionary<string, WorldStateHandle>[Enum.GetValues<WorldStateOwnershipLane>().Length];

        for (var index = 0; (index < handlesByLane.Length); index++) {
            handlesByLane[index] = new Dictionary<string, WorldStateHandle>(comparer: StringComparer.Ordinal);
        }

        void Add(string name, WorldStateOwnershipLane ownership, WorldStateStorageShape storage, WorldStateValueKind valueKind, int laneOrdinal) {
            var handle = new WorldStateHandle(
                ordinal: descriptors.Count,
                catalogIdentity: identity
            );

            if (!handlesByLane[(int)ownership].TryAdd(
                key: name,
                value: handle
            )) {
                throw new InvalidOperationException(message: $"State lane '{ownership}' declares duplicate name '{name}'.");
            }

            descriptors.Add(item: new WorldStateDescriptor(
                Handle: handle,
                Name: name,
                Ownership: ownership,
                Storage: storage,
                ValueKind: valueKind,
                LaneOrdinal: laneOrdinal
            ));
        }

        var worldRows = (section?.World ?? []);

        for (var index = 0; (index < worldRows.Count); index++) {
            var row = (worldRows[index] ?? throw new InvalidOperationException(message: $"State lane 'World' contains a null declaration at ordinal {index}."));

            Add(
                name: row.Name,
                ownership: WorldStateOwnershipLane.World,
                storage: ((row.Lattice is not null)
                    ? WorldStateStorageShape.Lattice
                    : (row.IsKeyed
                        ? WorldStateStorageShape.Keyed
                        : WorldStateStorageShape.Slot
                )),
                valueKind: FromCellKind(kind: row.Kind),
                laneOrdinal: index
            );
        }

        var perBodyNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        AddActionState(
            declarations: (section?.Body ?? []),
            ownership: WorldStateOwnershipLane.Body,
            names: perBodyNames,
            add: Add
        );
        AddActionState(
            declarations: (section?.Identity ?? []),
            ownership: WorldStateOwnershipLane.Identity,
            names: perBodyNames,
            add: Add
        );

        return new WorldStateCatalog(
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
    public bool TryResolve(WorldStateOwnershipLane lane, string name, out WorldStateHandle handle) {
        if (
            !Enum.IsDefined(value: lane) ||
            (name is null)
        ) {
            handle = default;

            return false;
        }

        return m_handlesByLane[(int)lane].TryGetValue(
            key: name,
            value: out handle
        );
    }

    /// <summary>Resolves a validated state name once into a typed handle.</summary>
    /// <param name="lane">The ownership lane to search.</param>
    /// <param name="name">The validated authored name.</param>
    /// <param name="handle">The resolved handle, or the invalid default value when no declaration matches.</param>
    /// <returns><see langword="true"/> when the lane declares the name.</returns>
    public bool TryResolve(WorldStateOwnershipLane lane, WorldCellName name, out WorldStateHandle handle) => TryResolve(
        lane: lane,
        name: name.Value,
        handle: out handle
    );

    /// <summary>Attempts to read a descriptor by its catalog-instance-relative handle.</summary>
    /// <param name="handle">The handle to inspect.</param>
    /// <param name="descriptor">The descriptor on success; otherwise the default descriptor.</param>
    /// <returns><see langword="true"/> when the handle addresses this catalog's current shape.</returns>
    public bool TryGetDescriptor(WorldStateHandle handle, out WorldStateDescriptor descriptor) {
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
    internal bool HasSameShape(WorldStateCatalog other) {
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

    /// <summary>Determines without allocation whether an authored section still carries this catalog's shape.</summary>
    internal bool MatchesShape(WorldStateSection? section) {
        var descriptorIndex = 0;

        bool Match(string name, WorldStateOwnershipLane ownership, WorldStateStorageShape storage, WorldStateValueKind valueKind, int laneOrdinal) {
            if ((uint)descriptorIndex >= (uint)m_descriptors.Length) {
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

        var worldRows = (section?.World ?? []);

        for (var index = 0; (index < worldRows.Count); index++) {
            if (
                (worldRows[index] is not { } row) ||
                !TryFromCellKind(kind: row.Kind, valueKind: out var valueKind) ||
                !Match(
                    name: row.Name,
                    ownership: WorldStateOwnershipLane.World,
                    storage: ((row.Lattice is not null)
                        ? WorldStateStorageShape.Lattice
                        : (row.IsKeyed
                            ? WorldStateStorageShape.Keyed
                            : WorldStateStorageShape.Slot
                    )),
                    valueKind: valueKind,
                    laneOrdinal: index
                )
            ) {
                return false;
            }
        }

        if (!MatchesActionLane(
            declarations: (section?.Body ?? []),
            ownership: WorldStateOwnershipLane.Body,
            descriptors: m_descriptors,
            descriptorIndex: ref descriptorIndex
        )) {
            return false;
        }

        return (
            MatchesActionLane(
                declarations: (section?.Identity ?? []),
                ownership: WorldStateOwnershipLane.Identity,
                descriptors: m_descriptors,
                descriptorIndex: ref descriptorIndex
            ) &&
            (descriptorIndex == m_descriptors.Length)
        );
    }

    private static void AddActionState(IReadOnlyList<ActionStateSlot> declarations, WorldStateOwnershipLane ownership, ISet<string> names, Action<string, WorldStateOwnershipLane, WorldStateStorageShape, WorldStateValueKind, int> add) {
        for (var index = 0; (index < declarations.Count); index++) {
            var declaration = (declarations[index] ?? throw new InvalidOperationException(message: $"State lane '{ownership}' contains a null declaration at ordinal {index}."));

            if (!names.Add(item: declaration.Name)) {
                throw new InvalidOperationException(message: $"State lanes 'Body' and 'Identity' declare duplicate name '{declaration.Name}'.");
            }

            add(
                declaration.Name,
                ownership,
                WorldStateStorageShape.Slot,
                declaration.Kind switch {
                    ActionStateKind.Counter => WorldStateValueKind.Counter,
                    ActionStateKind.Timer => WorldStateValueKind.Timer,
                    _ => throw new InvalidOperationException(message: $"State lane '{ownership}' declaration '{declaration.Name}' carries unknown value kind '{declaration.Kind}'."),
                },
                index
            );
        }
    }

    private static WorldStateValueKind FromCellKind(CellKind kind) => kind switch {
        CellKind.Int => WorldStateValueKind.Int,
        CellKind.Fixed => WorldStateValueKind.Fixed,
        CellKind.Bool => WorldStateValueKind.Bool,
        CellKind.Text => WorldStateValueKind.Text,
        _ => throw new InvalidOperationException(message: $"Unknown state cell kind '{kind}'."),
    };

    private static bool MatchesActionLane(IReadOnlyList<ActionStateSlot> declarations, WorldStateOwnershipLane ownership, IReadOnlyList<WorldStateDescriptor> descriptors, ref int descriptorIndex) {
        for (var index = 0; (index < declarations.Count); index++) {
            if (declarations[index] is not { } declaration) {
                return false;
            }

            var valueKind = declaration.Kind switch {
                ActionStateKind.Counter => WorldStateValueKind.Counter,
                ActionStateKind.Timer => WorldStateValueKind.Timer,
                _ => (WorldStateValueKind?)null,
            };

            if (
                (valueKind is not { } kind) ||
                ((uint)descriptorIndex >= (uint)descriptors.Count)
            ) {
                return false;
            }

            var descriptor = descriptors[descriptorIndex++];

            if (
                !string.Equals(a: descriptor.Name, b: declaration.Name, comparisonType: StringComparison.Ordinal) ||
                (descriptor.Ownership != ownership) ||
                (descriptor.Storage != WorldStateStorageShape.Slot) ||
                (descriptor.ValueKind != kind) ||
                (descriptor.LaneOrdinal != index)
            ) {
                return false;
            }
        }

        return true;
    }

    private static bool TryFromCellKind(CellKind kind, out WorldStateValueKind valueKind) {
        switch (kind) {
            case CellKind.Int:
            case CellKind.Fixed:
            case CellKind.Bool:
            case CellKind.Text:
                valueKind = (WorldStateValueKind)kind;

                return true;
            default:
                valueKind = default;

                return false;
        }
    }
}
