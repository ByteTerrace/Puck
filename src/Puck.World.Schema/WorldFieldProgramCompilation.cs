using System.Collections.ObjectModel;
using System.Collections.Immutable;

using Puck.Maths;

namespace Puck.World;

/// <summary>Identifies the iteration domain a compiled field-program node traverses.</summary>
public enum WorldFieldWorkKind : byte {
    /// <summary>The node visits every lattice cell.</summary>
    Cells,

    /// <summary>The node visits every active body and its corresponding lattice cell.</summary>
    Bodies,
}
/// <summary>Identifies one field in the <see cref="WorldFieldProgram"/> that minted it.</summary>
/// <remarks>Handles are bound to one compiled program. The default value is invalid.</remarks>
public readonly record struct WorldFieldHandle {
    private readonly object? m_programIdentity;
    private readonly int m_encodedOrdinal;

    internal WorldFieldHandle(int ordinal, object programIdentity) {
        m_programIdentity = programIdentity;
        m_encodedOrdinal = checked((ordinal + 1));
    }

    /// <summary>Gets whether this handle addresses a compiled field.</summary>
    public bool IsValid => (m_encodedOrdinal > 0);
    /// <summary>Gets the field's stable declaration ordinal, or <c>-1</c> for the default handle.</summary>
    public int Ordinal => (m_encodedOrdinal - 1);

    internal bool BelongsTo(object programIdentity) => ReferenceEquals(objA: m_programIdentity, objB: programIdentity);
}
/// <summary>Identifies one node in the <see cref="WorldFieldProgram"/> that minted it.</summary>
/// <remarks>Handles are bound to one compiled program. Node ordinals are reaction document order, which is also
/// execution order. The default value is invalid.</remarks>
public readonly record struct WorldFieldNodeHandle {
    private readonly object? m_programIdentity;
    private readonly int m_encodedOrdinal;

    internal WorldFieldNodeHandle(int ordinal, object programIdentity) {
        m_programIdentity = programIdentity;
        m_encodedOrdinal = checked((ordinal + 1));
    }

    /// <summary>Gets whether this handle addresses a compiled node.</summary>
    public bool IsValid => (m_encodedOrdinal > 0);
    /// <summary>Gets the node's stable execution ordinal, or <c>-1</c> for the default handle.</summary>
    public int Ordinal => (m_encodedOrdinal - 1);
}
/// <summary>One fixed-point scalar input to a compiled field node.</summary>
/// <param name="Literal">The compiled literal. Ignored when <paramref name="State"/> is valid.</param>
/// <param name="State">The typed state-row handle resolved by the executing reaction, or the invalid handle for a
/// literal.</param>
public readonly record struct WorldFieldScalarInput(FixedQ4816 Literal, WorldStateHandle State) {
    /// <summary>Gets whether this input reads a live state row.</summary>
    public bool IsState => State.IsValid;
}
/// <summary>One compiled lattice field and its deterministic fixed-point envelope.</summary>
/// <param name="Handle">The program-instance-relative field handle.</param>
/// <param name="State">The lattice-shaped state declaration that owns the field.</param>
/// <param name="Name">The authored stable field name.</param>
/// <param name="Initial">The initial value applied before paint.</param>
/// <param name="Minimum">The inclusive cell-value floor.</param>
/// <param name="Maximum">The inclusive cell-value ceiling.</param>
/// <param name="HeightScale">World units of geometry height per field unit.</param>
/// <param name="IsMedium">Whether this field is a fluid medium (see <see cref="WorldStateFieldTrait.Medium"/>) —
/// its value times <paramref name="HeightScale"/> over the lattice origin is a free surface bodies sample.</param>
public readonly record struct WorldFieldDescriptor(
    WorldFieldHandle Handle,
    WorldStateHandle State,
    string Name,
    FixedQ4816 Initial,
    FixedQ4816 Minimum,
    FixedQ4816 Maximum,
    FixedQ4816 HeightScale,
    bool IsMedium
);
/// <summary>One typed condition in a compiled <see cref="WorldFieldNode.Transform"/> node.</summary>
/// <param name="Field">The field sampled at the current cell.</param>
/// <param name="Comparison">The comparison applied to the sampled field value.</param>
/// <param name="Value">The fixed literal or typed state input compared against.</param>
public readonly record struct WorldFieldProgramCondition(
    WorldFieldHandle Field,
    WorldFieldComparison Comparison,
    WorldFieldScalarInput Value
);
/// <summary>One typed write in a compiled <see cref="WorldFieldNode.Transform"/> node.</summary>
/// <param name="Field">The field written at the current cell.</param>
/// <param name="Op">Whether the value replaces or adds to the current field value.</param>
/// <param name="Value">The fixed literal or typed state input written.</param>
public readonly record struct WorldFieldProgramWrite(
    WorldFieldHandle Field,
    WorldFieldWriteOp Op,
    WorldFieldScalarInput Value
);
/// <summary>One required ordering edge between two field-program nodes.</summary>
/// <param name="Before">The earlier node whose reads or writes conflict with <paramref name="After"/>.</param>
/// <param name="After">The later node that must observe document order.</param>
public readonly record struct WorldFieldDependency(WorldFieldNodeHandle Before, WorldFieldNodeHandle After);
/// <summary>One node in the deterministic field program. Nodes execute in <see cref="Handle"/> order.</summary>
/// <param name="Handle">The program-bound node handle.</param>
/// <param name="Work">The iteration domain traversed by this node.</param>
/// <param name="FieldReads">The canonical immutable field-read set.</param>
/// <param name="FieldWrites">The canonical immutable field-write set.</param>
/// <param name="StateReads">The canonical immutable state-read set.</param>
/// <param name="StateWrites">The canonical immutable state-write set.</param>
public abstract record WorldFieldNode(
    WorldFieldNodeHandle Handle,
    WorldFieldWorkKind Work,
    ImmutableArray<WorldFieldHandle> FieldReads,
    ImmutableArray<WorldFieldHandle> FieldWrites,
    ImmutableArray<WorldStateHandle> StateReads,
    ImmutableArray<WorldStateHandle> StateWrites
) {
    /// <summary>Diffuses one field toward its face-neighbour mean.</summary>
    /// <param name="Handle">The program-bound node handle.</param>
    /// <param name="Field">The field read and written.</param>
    /// <param name="Rate">The fixed literal or typed state input controlling diffusion.</param>
    /// <param name="StateReads">The immutable state-read set implied by <paramref name="Rate"/>.</param>
    public sealed record Diffuse(
        WorldFieldNodeHandle Handle,
        WorldFieldHandle Field,
        WorldFieldScalarInput Rate,
        ImmutableArray<WorldStateHandle> StateReads
    ) : WorldFieldNode(Handle, WorldFieldWorkKind.Cells, [Field], [Field], StateReads, []);
    /// <summary>Decays one field toward zero.</summary>
    /// <param name="Handle">The program-bound node handle.</param>
    /// <param name="Field">The field read and written.</param>
    /// <param name="Rate">The fixed literal or typed state input controlling decay.</param>
    /// <param name="StateReads">The immutable state-read set implied by <paramref name="Rate"/>.</param>
    public sealed record Decay(
        WorldFieldNodeHandle Handle,
        WorldFieldHandle Field,
        WorldFieldScalarInput Rate,
        ImmutableArray<WorldStateHandle> StateReads
    ) : WorldFieldNode(Handle, WorldFieldWorkKind.Cells, [Field], [Field], StateReads, []);
    /// <summary>Applies ordered writes where every condition holds.</summary>
    /// <param name="Handle">The program-bound node handle.</param>
    /// <param name="When">The immutable ordered condition list.</param>
    /// <param name="Then">The immutable ordered write list.</param>
    /// <param name="FieldReads">The canonical immutable condition and additive-write read set.</param>
    /// <param name="FieldWrites">The canonical immutable write set.</param>
    /// <param name="StateReads">The canonical immutable scalar-input read set.</param>
    public sealed record Transform(
        WorldFieldNodeHandle Handle,
        ImmutableArray<WorldFieldProgramCondition> When,
        ImmutableArray<WorldFieldProgramWrite> Then,
        ImmutableArray<WorldFieldHandle> FieldReads,
        ImmutableArray<WorldFieldHandle> FieldWrites,
        ImmutableArray<WorldStateHandle> StateReads
    ) : WorldFieldNode(Handle, WorldFieldWorkKind.Cells, FieldReads, FieldWrites, StateReads, []);
    /// <summary>Deposits into a field for every body carrying a nonzero keyed tag.</summary>
    /// <param name="Handle">The program-bound node handle.</param>
    /// <param name="Tag">The keyed state row selecting emitting bodies.</param>
    /// <param name="Field">The field read and written at each emitting body's cell.</param>
    /// <param name="Amount">The fixed literal or typed state input deposited.</param>
    /// <param name="StateReads">The canonical immutable tag and scalar-input read set.</param>
    public sealed record Emit(
        WorldFieldNodeHandle Handle,
        WorldStateHandle Tag,
        WorldFieldHandle Field,
        WorldFieldScalarInput Amount,
        ImmutableArray<WorldStateHandle> StateReads
    ) : WorldFieldNode(Handle, WorldFieldWorkKind.Bodies, [Field], [Field], StateReads, []);
    /// <summary>Writes a keyed body row from a field test at each active body's cell.</summary>
    /// <param name="Handle">The program-bound node handle.</param>
    /// <param name="Field">The field sampled at each body's cell.</param>
    /// <param name="Comparison">The comparison applied to the sampled value.</param>
    /// <param name="Value">The fixed literal or typed state input compared against.</param>
    /// <param name="Row">The keyed state row written per body.</param>
    /// <param name="StateReads">The canonical immutable scalar-input read set.</param>
    public sealed record Expose(
        WorldFieldNodeHandle Handle,
        WorldFieldHandle Field,
        WorldFieldComparison Comparison,
        WorldFieldScalarInput Value,
        WorldStateHandle Row,
        ImmutableArray<WorldStateHandle> StateReads
    ) : WorldFieldNode(Handle, WorldFieldWorkKind.Bodies, [Field], [], StateReads, [Row]);
    /// <summary>Moves one field downhill over a combined surface height, with an optional boundary spill. See
    /// <see cref="WorldReaction.Flow"/>.</summary>
    /// <param name="Handle">The program-bound node handle.</param>
    /// <param name="Field">The field read and written.</param>
    /// <param name="Rate">The fixed literal or typed state input controlling the per-direction share that
    /// moves.</param>
    /// <param name="Over">The other fields forming the terrain basis, in authored order.</param>
    /// <param name="SpillRow">The scalar state row an edge cell's outward share accumulates into, or the invalid
    /// handle when edges are walls.</param>
    /// <param name="FieldReads">The canonical immutable read set: <paramref name="Field"/> plus every
    /// <paramref name="Over"/> field.</param>
    /// <param name="StateReads">The canonical immutable state-read set implied by <paramref name="Rate"/> and, when
    /// declared, <paramref name="SpillRow"/>'s current value.</param>
    /// <param name="StateWrites">The canonical immutable state-write set: <paramref name="SpillRow"/> when
    /// declared.</param>
    public sealed record Flow(
        WorldFieldNodeHandle Handle,
        WorldFieldHandle Field,
        WorldFieldScalarInput Rate,
        ImmutableArray<WorldFieldHandle> Over,
        WorldStateHandle SpillRow,
        ImmutableArray<WorldFieldHandle> FieldReads,
        ImmutableArray<WorldStateHandle> StateReads,
        ImmutableArray<WorldStateHandle> StateWrites
    ) : WorldFieldNode(Handle, WorldFieldWorkKind.Cells, FieldReads, [Field], StateReads, StateWrites);
}
/// <summary>The typed, deterministic reaction program compiled from one lattice topology and its ordered reactions.</summary>
/// <remarks>This is the reaction inspection, scheduling, and future lowering boundary used alongside the complete
/// <see cref="WorldFieldsSection"/> composite. It introduces no second authoring language: every node is a typed view
/// of an existing reaction, with no hidden mutable state or random stream. Runtime values remain in lane-specific
/// stores, document cells, and lattice cells; the state catalog describes their types and addresses only.</remarks>
public sealed class WorldFieldProgram {
    // Kept only as an immutable compatibility witness for the reaction-owned inputs. The live lattice must keep
    // receiving its complete companion document: HasSameProgram deliberately ignores origin, cell size, cadence,
    // colour, and paint, so this snapshot is never a topology or initialization source.
    private readonly WorldFieldsSection m_programSource;
    private readonly object m_identity;
    private readonly WorldFieldDescriptor[] m_fields;
    private readonly ReadOnlyCollection<WorldFieldDescriptor> m_readOnlyFields;
    private readonly ReadOnlyCollection<WorldFieldDependency> m_readOnlyDependencies;
    private readonly ReadOnlyCollection<WorldFieldNode> m_readOnlyNodes;

    private WorldFieldProgram(object identity, WorldStateCatalog stateCatalog, WorldFieldsSection programSource, WorldFieldDescriptor[] fields, WorldFieldNode[] nodes, int cellCount) {
        m_identity = identity;
        m_programSource = programSource;
        StateCatalog = stateCatalog;
        m_fields = fields;
        m_readOnlyFields = Array.AsReadOnly(array: fields);
        m_readOnlyNodes = Array.AsReadOnly(array: nodes);
        m_readOnlyDependencies = Array.AsReadOnly(array: CompileDependencies(nodes: nodes));
        CellCount = cellCount;
        CellNodeCount = nodes.Count(predicate: static node => (node.Work == WorldFieldWorkKind.Cells));
        CellPassCount = nodes.Sum(selector: static node => node switch {
            WorldFieldNode.Diffuse => 2,
            WorldFieldNode.Flow => 2,
            { Work: WorldFieldWorkKind.Cells } => 1,
            _ => 0,
        });
        BodyPassCount = (nodes.Length - CellNodeCount);
    }

    /// <summary>Gets the canonical typed state catalog that resolves every state handle carried by this program.</summary>
    public WorldStateCatalog StateCatalog { get; }
    /// <summary>Gets the number of cell-work reaction nodes.</summary>
    public int CellNodeCount { get; }
    /// <summary>Gets the lattice cell count traversed by each cell-work node.</summary>
    public int CellCount { get; }
    /// <summary>Gets the number of full-cell traversals per lattice step. Diffusion counts twice because it first
    /// snapshots the field, then visits every cell.</summary>
    public int CellPassCount { get; }
    /// <summary>Gets the number of active-body passes per lattice step.</summary>
    public int BodyPassCount { get; }
    /// <summary>Gets the fields in stable handle order.</summary>
    public IReadOnlyList<WorldFieldDescriptor> Fields => m_readOnlyFields;
    /// <summary>Gets every document-order edge required by typed read/write conflicts. A scheduler may run nodes
    /// without an edge concurrently, but commits each edge in <see cref="WorldFieldDependency.Before"/> →
    /// <see cref="WorldFieldDependency.After"/> order.</summary>
    public IReadOnlyList<WorldFieldDependency> Dependencies => m_readOnlyDependencies;
    /// <summary>Gets the nodes in deterministic execution order.</summary>
    public IReadOnlyList<WorldFieldNode> Nodes => m_readOnlyNodes;

    /// <summary>Gets a compiled field descriptor by handle.</summary>
    /// <param name="handle">A handle minted by this program instance.</param>
    /// <returns>The compiled field descriptor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="handle"/> is invalid, outside this program, or
    /// was minted by another program instance.</exception>
    public WorldFieldDescriptor this[WorldFieldHandle handle] => (
        (handle.IsValid && handle.BelongsTo(programIdentity: m_identity) && (handle.Ordinal < m_fields.Length))
            ? m_fields[handle.Ordinal]
            : throw new ArgumentOutOfRangeException(
                paramName: nameof(handle),
                actualValue: handle.Ordinal,
                message: "The field handle is invalid or belongs to a different program instance."
            )
    );

    /// <summary>Compiles an already-validated lattice composite into a typed deterministic reaction program.</summary>
    /// <param name="document">The complete compiled lattice composite that remains the source for topology, paint,
    /// and presentation metadata.</param>
    /// <param name="state">The catalog used to resolve and brand every state dependency.</param>
    /// <returns>The immutable typed reaction program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="state"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A field or state dependency cannot be resolved to its required
    /// storage and value shape.</exception>
    public static WorldFieldProgram Compile(WorldFieldsSection document, WorldStateCatalog state) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: state);

        var identity = new object();
        var fields = new WorldFieldDescriptor[document.Fields.Count];
        var fieldsByName = new Dictionary<string, WorldFieldHandle>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < fields.Length); index++) {
            var row = document.Fields[index];
            var handle = new WorldFieldHandle(
                ordinal: index,
                programIdentity: identity
            );

            if (!fieldsByName.TryAdd(key: row.Name, value: handle)) {
                throw new InvalidOperationException(message: $"fields declares duplicate row '{row.Name}'.");
            }

            var stateHandle = RequireState(
                state: state,
                name: row.Name,
                storage: WorldStateStorageShape.Lattice,
                valueKind: WorldStateValueKind.Fixed,
                location: $"fields[{index}]"
            );

            fields[index] = new WorldFieldDescriptor(
                Handle: handle,
                State: stateHandle,
                Name: row.Name,
                Initial: FixedQ4816.FromDouble(value: row.Initial),
                Minimum: FixedQ4816.FromDouble(value: row.Min),
                Maximum: FixedQ4816.FromDouble(value: row.Max),
                HeightScale: FixedQ4816.FromDouble(value: row.HeightScale),
                IsMedium: row.Medium
            );
        }

        WorldFieldHandle Field(string name, int reaction) => (fieldsByName.TryGetValue(key: name, value: out var handle)
            ? handle
            : throw new InvalidOperationException(message: $"fields.reactions[{reaction}] names undeclared field '{name}'.")
        );

        WorldFieldScalarInput Scalar(WorldLatticeScalar scalar, int reaction) {
            if (scalar.Row is not { } row) {
                return new WorldFieldScalarInput(
                    Literal: FixedQ4816.FromDouble(value: (scalar.Literal ?? 0f)),
                    State: default
                );
            }

            return new WorldFieldScalarInput(
                Literal: default,
                State: RequireState(
                    location: $"fields.reactions[{reaction}] scalar",
                    name: row,
                    state: state,
                    storage: WorldStateStorageShape.Slot,
                    valueKind: WorldStateValueKind.Fixed
                )
            );
        }

        var reactions = (document.Reactions ?? []);
        var nodes = new WorldFieldNode[reactions.Count];

        for (var index = 0; (index < reactions.Count); index++) {
            var nodeHandle = new WorldFieldNodeHandle(
                ordinal: index,
                programIdentity: identity
            );

            nodes[index] = reactions[index] switch {
                WorldReaction.Diffuse reaction => CompileDiffuse(field: Field, handle: nodeHandle, index: index, reaction: reaction, scalar: Scalar),
                WorldReaction.Decay reaction => CompileDecay(field: Field, handle: nodeHandle, index: index, reaction: reaction, scalar: Scalar),
                WorldReaction.Transform reaction => CompileTransform(field: Field, handle: nodeHandle, index: index, reaction: reaction, scalar: Scalar),
                WorldReaction.Emit reaction => CompileEmit(field: Field, handle: nodeHandle, index: index, reaction: reaction, scalar: Scalar, state: state),
                WorldReaction.Expose reaction => CompileExpose(field: Field, handle: nodeHandle, index: index, reaction: reaction, scalar: Scalar, state: state),
                WorldReaction.Flow reaction => CompileFlow(field: Field, handle: nodeHandle, index: index, reaction: reaction, scalar: Scalar, state: state),
                _ => throw new InvalidOperationException(message: $"fields.reactions[{index}] carries an unknown reaction kind."),
            };
        }

        return new WorldFieldProgram(
            identity: identity,
            stateCatalog: state,
            programSource: document,
            fields: fields,
            nodes: nodes,
            cellCount: checked(((document.Lattice.Width * document.Lattice.Depth) * document.Lattice.Layers))
        );
    }
    /// <summary>Determines whether a complete companion composite still carries exactly the field declarations and
    /// ordered reactions represented by this program. Topology placement, cadence, colour, and paint remain owned by
    /// the companion and therefore do not participate.</summary>
    /// <param name="document">The complete companion composite to inspect.</param>
    /// <returns><see langword="true"/> when the companion's program-owned inputs match.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public bool MatchesProgram(WorldFieldsSection document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return m_programSource.HasSameProgram(other: document);
    }

    private static WorldFieldNode CompileDiffuse(WorldReaction.Diffuse reaction, WorldFieldNodeHandle handle, int index, Func<string, int, WorldFieldHandle> field, Func<WorldLatticeScalar, int, WorldFieldScalarInput> scalar) {
        var rate = scalar(reaction.Rate, index);

        return new WorldFieldNode.Diffuse(handle, field(reaction.Field, index), rate, StateReads(input: rate));
    }
    private static WorldFieldNode CompileDecay(WorldReaction.Decay reaction, WorldFieldNodeHandle handle, int index, Func<string, int, WorldFieldHandle> field, Func<WorldLatticeScalar, int, WorldFieldScalarInput> scalar) {
        var rate = scalar(reaction.Rate, index);

        return new WorldFieldNode.Decay(handle, field(reaction.Field, index), rate, StateReads(input: rate));
    }
    private static WorldFieldNode CompileTransform(WorldReaction.Transform reaction, WorldFieldNodeHandle handle, int index, Func<string, int, WorldFieldHandle> field, Func<WorldLatticeScalar, int, WorldFieldScalarInput> scalar) {
        var conditions = (reaction.When ?? []).Select(selector: condition => new WorldFieldProgramCondition(
            Field: field(condition.Field, index),
            Comparison: condition.Comparison,
            Value: scalar(condition.Value, index)
        )).ToImmutableArray();
        var writes = (reaction.Then ?? []).Select(selector: write => new WorldFieldProgramWrite(
            Field: field(write.Field, index),
            Op: write.Op,
            Value: scalar(write.Value, index)
        )).ToImmutableArray();

        var fieldReads = conditions
            .Select(selector: static condition => condition.Field)
            .Concat(second: writes
                .Where(predicate: static write => (write.Op == WorldFieldWriteOp.Add))
                .Select(selector: static write => write.Field));

        return new WorldFieldNode.Transform(
            handle,
            conditions,
            writes,
            CanonicalFields(handles: fieldReads),
            CanonicalFields(handles: writes.Select(selector: static write => write.Field)),
            CanonicalStates(inputs: conditions.Select(selector: static condition => condition.Value).Concat(second: writes.Select(selector: static write => write.Value)))
        );
    }
    private static WorldFieldNode CompileEmit(WorldReaction.Emit reaction, WorldFieldNodeHandle handle, int index, Func<string, int, WorldFieldHandle> field, Func<WorldLatticeScalar, int, WorldFieldScalarInput> scalar, WorldStateCatalog state) {
        var amount = scalar(reaction.Amount, index);
        var tag = RequireState(state, reaction.Tag, WorldStateStorageShape.Keyed, WorldStateValueKind.Int, $"fields.reactions[{index}].tag");
        var target = field(reaction.Field, index);

        return new WorldFieldNode.Emit(
            handle,
            tag,
            target,
            amount,
            CanonicalStates(inputs: [new WorldFieldScalarInput(Literal: default, State: tag), amount])
        );
    }
    private static WorldFieldNode CompileExpose(WorldReaction.Expose reaction, WorldFieldNodeHandle handle, int index, Func<string, int, WorldFieldHandle> field, Func<WorldLatticeScalar, int, WorldFieldScalarInput> scalar, WorldStateCatalog state) {
        var value = scalar(reaction.Value, index);
        var row = RequireState(state, reaction.Row, WorldStateStorageShape.Keyed, WorldStateValueKind.Int, $"fields.reactions[{index}].row");

        return new WorldFieldNode.Expose(
            handle,
            field(reaction.Field, index),
            reaction.Comparison,
            value,
            row,
            StateReads(input: value)
        );
    }
    private static WorldFieldNode CompileFlow(WorldReaction.Flow reaction, WorldFieldNodeHandle handle, int index, Func<string, int, WorldFieldHandle> field, Func<WorldLatticeScalar, int, WorldFieldScalarInput> scalar, WorldStateCatalog state) {
        var target = field(reaction.Field, index);
        var rate = scalar(reaction.Rate, index);
        var over = (reaction.Over ?? []).Select(selector: name => field(name, index)).ToImmutableArray();
        var spillRow = ((reaction.SpillRow is { } spillName)
            ? RequireState(location: $"fields.reactions[{index}].spillRow", name: spillName, state: state, storage: WorldStateStorageShape.Slot, valueKind: WorldStateValueKind.Fixed)
            : default
        );
        var stateInputs = new List<WorldFieldScalarInput> { rate };

        if (spillRow.IsValid) {
            stateInputs.Add(item: new WorldFieldScalarInput(Literal: default, State: spillRow));
        }

        return new WorldFieldNode.Flow(
            handle,
            target,
            rate,
            over,
            spillRow,
            CanonicalFields(handles: over.Append(element: target)),
            CanonicalStates(inputs: stateInputs),
            (spillRow.IsValid ? ImmutableArray.Create(item: spillRow) : ImmutableArray<WorldStateHandle>.Empty)
        );
    }
    private static ImmutableArray<WorldFieldHandle> CanonicalFields(IEnumerable<WorldFieldHandle> handles) => handles
        .Distinct()
        .OrderBy(keySelector: static handle => handle.Ordinal)
        .ToImmutableArray();
    private static WorldFieldDependency[] CompileDependencies(IReadOnlyList<WorldFieldNode> nodes) {
        var dependencies = new List<WorldFieldDependency>();

        for (var after = 0; (after < nodes.Count); after++) {
            for (var before = 0; (before < after); before++) {
                if (Conflicts(earlier: nodes[before], later: nodes[after])) {
                    dependencies.Add(item: new WorldFieldDependency(
                        Before: nodes[before].Handle,
                        After: nodes[after].Handle
                    ));
                }
            }
        }

        return dependencies.ToArray();
    }
    private static bool Conflicts(WorldFieldNode earlier, WorldFieldNode later) => (
        Intersects(left: earlier.FieldWrites, right: later.FieldReads) ||
        Intersects(left: earlier.FieldWrites, right: later.FieldWrites) ||
        Intersects(left: earlier.FieldReads, right: later.FieldWrites) ||
        Intersects(left: earlier.StateWrites, right: later.StateReads) ||
        Intersects(left: earlier.StateWrites, right: later.StateWrites) ||
        Intersects(left: earlier.StateReads, right: later.StateWrites)
    );
    private static bool Intersects<T>(IReadOnlyList<T> left, IReadOnlyList<T> right)
        where T : IEquatable<T> {
        for (var leftIndex = 0; (leftIndex < left.Count); leftIndex++) {
            for (var rightIndex = 0; (rightIndex < right.Count); rightIndex++) {
                if (left[leftIndex].Equals(other: right[rightIndex])) {
                    return true;
                }
            }
        }

        return false;
    }
    private static ImmutableArray<WorldStateHandle> CanonicalStates(IEnumerable<WorldFieldScalarInput> inputs) => inputs
        .Where(predicate: static input => input.IsState)
        .Select(selector: static input => input.State)
        .Distinct()
        .OrderBy(keySelector: static handle => handle.Ordinal)
        .ToImmutableArray();
    private static ImmutableArray<WorldStateHandle> StateReads(WorldFieldScalarInput input) => (input.IsState
        ? [input.State]
        : []
    );
    private static WorldStateHandle RequireState(WorldStateCatalog state, string name, WorldStateStorageShape storage, WorldStateValueKind valueKind, string location) {
        if (!state.TryResolve(handle: out var handle, lane: WorldStateOwnershipLane.World, name: name)) {
            throw new InvalidOperationException(message: $"{location} names undeclared world state row '{name}'.");
        }

        var descriptor = state[handle];

        if ((descriptor.Storage != storage) || (descriptor.ValueKind != valueKind)) {
            throw new InvalidOperationException(message: $"{location} requires a {storage} {valueKind} world state row, but '{name}' is {descriptor.Storage} {descriptor.ValueKind}.");
        }

        return handle;
    }
}
