using AddressUse = Puck.State.Rules.AddressUse;
using OperandAddress = Puck.State.Rules.OperandAddress;
using CompiledCellRef = Puck.State.Rules.CompiledCellRef;
using IRuleKey = Puck.State.Rules.IRuleKey;
using IRuleOperand = Puck.State.Rules.IRuleOperand;
using RuleReads = Puck.State.Rules.RuleReads;
using WriteKey = Puck.State.Rules.WriteKey;

namespace Puck.World;

/// <summary>A body a world fact addresses, by ordinal: a literal index, a bound participant, a cell whose value is
/// the index, the extremum of a keyed row, or a placement's inhabitant.</summary>
/// <param name="Kind">How the reference resolves.</param>
/// <param name="Index">The literal index, the bound key's ordinal, or the placement ordinal.</param>
/// <param name="RowOrdinal">The catalog ordinal of the row a cell or extremum reference reads, or <c>-1</c>.</param>
/// <param name="Key">The interned cell key a cell reference reads, or the invalid default.</param>
/// <param name="PlacementOrdinals">The placement ordinal per cell of the enclosing rule's <c>forEach</c> row, for a
/// <c>placement:$each</c> reference.</param>
public readonly record struct WorldBodyRef(CompiledBodyRefKind Kind, int Index, int RowOrdinal = -1, CellKey Key = default, IReadOnlyList<int>? PlacementOrdinals = null);
/// <summary>The operand facts only a world answers. The facet is the type argument, so the read receives
/// <see cref="IWorldFacts"/> as an argument and a fact cannot reach a capability it did not declare.</summary>
public abstract class WorldFactOperand : OperandFact<IWorldFacts>, IRuleOperand {
    private protected WorldFactOperand(CellKind valueKind) : base(valueKind: valueKind) { }

    /// <summary>Returns the population capacity the context's document declares — the per-tick cost of a read that
    /// scans every population slot.</summary>
    /// <param name="context">The compile context.</param>
    /// <returns>The capacity.</returns>
    protected static long PopulationCapacity(IRuleCostContext context) => ((WorldFactsCompileContext)context).Definition.Population.Capacity;

    /// <inheritdoc/>
    public virtual ReadOnlySpan<OperandAddress> Addresses => default;

    /// <summary>Reads the fact once the addresses it declares are resolved. An operand with no addresses answers
    /// through <see cref="OperandFact{TFacet}.Read"/>.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="facet">The facet the evaluator resolved from the host.</param>
    /// <param name="keys">What each of <see cref="Addresses"/> named.</param>
    /// <returns>The fact.</returns>
    public virtual Fact Read(IStateReader reader, IWorldFacts facet, ReadOnlySpan<ResolvedKey> keys) => Read(
        facet: facet,
        reader: reader
    );

    Fact IRuleOperand.Read(IStateReader reader, ReadOnlySpan<ResolvedKey> keys) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        // Admission proved the host serves the facet before any rule naming it ran.
        return Read(
            facet: ((IWorldFacts)reader),
            keys: keys,
            reader: reader
        );
    }
}
/// <summary>The active body count (<see cref="WorldRuleFacts.Population"/>).</summary>
public sealed class WorldPopulationOperand : WorldFactOperand {
    /// <summary>The shared instance.</summary>
    public static readonly WorldPopulationOperand Instance = new();

    private WorldPopulationOperand() : base(valueKind: CellKind.Int) { }

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return facet.Read(operand: PopulationOperand.Instance);
    }
}
/// <summary>Whether every selected rigid body rests uncarried (<see cref="WorldRuleFacts.PhysicsQuiescent"/>).
/// The work is priced at the selected scope's body count, or population capacity for the global census.</summary>
public sealed class WorldPhysicsQuiescentOperand : WorldFactOperand {
    /// <summary>The shared instance.</summary>
    public static readonly WorldPhysicsQuiescentOperand Instance = new();

    private readonly PhysicsQuiescentOperand m_operand;

    /// <summary>Creates a census over the selected bodies, or the population when omitted.</summary>
    /// <param name="bodies">Stable body ordinals, or null for the whole population.</param>
    public WorldPhysicsQuiescentOperand(IReadOnlyList<int>? bodies = null) : base(valueKind: CellKind.Bool) => m_operand = ((bodies is null)
        ? PhysicsQuiescentOperand.Instance
        : PhysicsQuiescentOperand.ForBodies(bodies: bodies));

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => ((m_operand.Bodies is { } bodies) ? bodies.Count : PopulationCapacity(context: context));
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return facet.Read(operand: m_operand);
    }
}
/// <summary>The music clock's phase error (<see cref="WorldRuleFacts.ClockPrefix"/>).</summary>
public sealed class WorldClockOperand : WorldFactOperand {
    /// <summary>The shared instance.</summary>
    public static readonly WorldClockOperand Instance = new();

    private WorldClockOperand() : base(valueKind: CellKind.Int) { }

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return facet.Read(operand: ClockOperand.Instance);
    }
}
/// <summary>The occupant count of a region placement (<see cref="WorldRuleFacts.RegionPrefix"/>).</summary>
public sealed class WorldRegionOccupancyOperand : WorldFactOperand {
    private readonly RegionOccupancyOperand m_operand;

    /// <summary>Initializes the operand.</summary>
    /// <param name="placementId">The region placement's id.</param>
    public WorldRegionOccupancyOperand(string placementId) : base(valueKind: CellKind.Int) => m_operand = new RegionOccupancyOperand(placementId: placementId);

    /// <summary>Gets the region placement's id.</summary>
    public string PlacementId => m_operand.PlacementId;

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => PopulationCapacity(context: context);
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return facet.Read(operand: m_operand);
    }
}
/// <summary>The influence a placement's channel carries (<see cref="WorldRuleFacts.InfluencePrefix"/>).</summary>
public sealed class WorldPlacementInfluenceOperand : WorldFactOperand {
    // One volume's fixed-point frame composition, quaternion rotation and extent checks, priced as a small fixed multiple of a scan step.
    private const long BoundVolumeCompileWork = 32L;

    // Child operands are semantic names. Caching a runtime CellKey here would retain one arena's address when the
    // compiled rule later serves a replacement arena.
    private readonly Dictionary<string, PlacementInfluenceOperand> m_resolved = new(comparer: StringComparer.Ordinal);
    private readonly PlacementInfluenceOperand m_operand;

    /// <summary>Initializes the operand.</summary>
    /// <param name="channel">The channel name.</param>
    /// <param name="placementId">The placement's id.</param>
    /// <param name="childKey">The dealt child's literal key, or <see langword="null"/>.</param>
    /// <param name="keyFrom">The live child-key indirection, or <see langword="null"/>.</param>
    public WorldPlacementInfluenceOperand(string channel, string placementId, string? childKey, CompiledCellRef? keyFrom) : base(valueKind: CellKind.Int) {
        KeyFrom = keyFrom;
        m_addresses = OperandAddress.List(
            first: keyFrom,
            use: AddressUse.Key
        );
        m_operand = new PlacementInfluenceOperand(
            channel: channel,
            key: childKey,
            placementId: placementId
        );
    }

    private readonly OperandAddress[] m_addresses;

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the channel name.</summary>
    public string Channel => m_operand.Channel;
    /// <summary>Gets the dealt child's literal key, or <see langword="null"/>.</summary>
    public string? ChildKey => m_operand.Key;
    /// <summary>Gets the live child-key indirection, or <see langword="null"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the placement's id.</summary>
    public string PlacementId => m_operand.PlacementId;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: KeyFrom
    );
    /// <summary>Returns the placement scan the read is bounded by: every spatial volume the document declares, which
    /// is what an influence count walks. A document with volumes that read a state-bound value adds the compile a read
    /// can owe after a write moved one: each volume copied twice (the index and its occupation targets) and each bound
    /// volume's geometry compiled again.</summary>
    /// <param name="context">The compile context.</param>
    /// <returns>The work units.</returns>
    public override RuleWork Cost(IRuleCostContext context) {
        var placements = ((WorldFactsCompileContext)context).Definition.Placements;
        var volumes = Math.Max(
            val1: 1L,
            val2: placements.Sum(selector: static placement => ((long)(placement.Spatial?.Count ?? 0)))
        );
        var bound = WorldSpatialQueryCompilation.BoundVolumeCount(placements: placements);

        return ((bound == 0L)
            ? volumes
            : ((volumes + (2L * volumes)) + (BoundVolumeCompileWork * bound))
        );
    }
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) => ((KeyFrom is null)
        ? Read(
            facet: facet,
            keys: default,
            reader: reader
        )
        : Puck.State.Rules.RuleOperandReads.Read(operand: this, reader: reader));
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet, ReadOnlySpan<ResolvedKey> keys) {
        ArgumentNullException.ThrowIfNull(argument: facet);
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (KeyFrom is null) {
            return facet.Read(operand: m_operand);
        }

        var key = keys[0].Key;

        if (
            !keys[0].Named ||
            !key.IsValid ||
            !reader.Arena.Keys.TryGetName(
            key: key,
            name: out var name
        )) {
            // The indirection named no cell, so no dealt child answers.
            return Fact.Absent(kind: CellKind.Int);
        }

        if (!m_resolved.TryGetValue(
            key: name.Value,
            value: out var child
        )) {
            // A speculative arena can mint and rewind arbitrarily many distinct names while retaining only the
            // bounded live ledger. Keep this compiled-operand memo within that same bound.
            if (m_resolved.Count >= StateCapacity.MaxCellKeys) {
                m_resolved.Clear();
            }
            child = new PlacementInfluenceOperand(
                channel: m_operand.Channel,
                key: name.Value,
                placementId: m_operand.PlacementId
            );
            m_resolved[name.Value] = child;
        }

        return facet.Read(operand: child);
    }
}
/// <summary>One byte of a screen's machine memory (<see cref="WorldRuleFacts.MachinePrefix"/>).</summary>
public sealed class WorldMachineMemoryOperand : WorldFactOperand {
    private readonly MachineMemoryOperand m_operand;

    /// <summary>Initializes the operand.</summary>
    /// <param name="screen">The screen index.</param>
    /// <param name="address">The memory address.</param>
    public WorldMachineMemoryOperand(int screen, int address) : base(valueKind: CellKind.Int) => m_operand = new MachineMemoryOperand(
        address: address,
        screen: screen
    );

    /// <summary>Gets the memory address.</summary>
    public int Address => m_operand.Address;
    /// <summary>Gets the screen index.</summary>
    public int Screen => m_operand.Screen;

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return facet.Read(operand: m_operand);
    }
}
/// <summary>The body whose keyed-row cell is the extreme (<see cref="WorldRuleFacts.ArgMaxPrefix"/> /
/// <see cref="WorldRuleFacts.ArgMinPrefix"/>), optionally filtered by a second keyed row.</summary>
public sealed class WorldArgBodyOperand : WorldFactOperand {
    private readonly ArgBodyOperand m_operand;

    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The aggregated row's catalog ordinal.</param>
    /// <param name="filterOrdinal">The filter row's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="reduce">Max or min.</param>
    /// <param name="operand">The world reader's own compiled form of the same read.</param>
    public WorldArgBodyOperand(int rowOrdinal, int filterOrdinal, StateReduceOp reduce, ArgBodyOperand operand) : base(valueKind: CellKind.Int) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        FilterOrdinal = filterOrdinal;
        Reduce = reduce;
        RowOrdinal = rowOrdinal;
        m_operand = operand;
    }

    /// <summary>Gets the filter row's catalog ordinal, or <c>-1</c>.</summary>
    public int FilterOrdinal { get; }
    /// <summary>Gets the aggregate.</summary>
    public StateReduceOp Reduce { get; }
    /// <summary>Gets the aggregated row's catalog ordinal.</summary>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: RowOrdinal
        ));
        if (FilterOrdinal >= 0) {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: FilterOrdinal
            ));
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        return context.RowCapacity(rowOrdinal: RowOrdinal);
    }
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return facet.Read(operand: m_operand);
    }
}
/// <summary>A world fact over one or two body references — distance, line of sight, park state, uprightness, a body
/// fact bit, a navigation facet, the nearest tagged body, or the board cell under a body.</summary>
public sealed class WorldBodyFactOperand : WorldFactOperand {
    private readonly Func<IWorldFacts, Fact> m_read;
    private readonly long m_cost;

    /// <summary>Initializes the operand.</summary>
    /// <param name="valueKind">The encoding the read answers in.</param>
    /// <param name="bodyA">The first body reference.</param>
    /// <param name="bodyB">The second body reference, or the default.</param>
    /// <param name="rowOrdinal">The catalog ordinal of the row the read scans, or <c>-1</c>.</param>
    /// <param name="cost">The conservative work units one read costs.</param>
    /// <param name="read">The world reader's own read of the same fact.</param>
    /// <param name="describe">The authored channel, for the read-back.</param>
    /// <param name="payload">The payload <paramref name="read"/> hands the world reader, when it is one, so a host can
    /// name what the read depends on (<see cref="IStateReader.TryScheduleFact"/>); <see langword="null"/> otherwise.</param>
    public WorldBodyFactOperand(CellKind valueKind, WorldBodyRef bodyA, WorldBodyRef bodyB, int rowOrdinal, long cost, Func<IWorldFacts, Fact> read, string describe, WorldOperandFact? payload = null) : base(valueKind: valueKind) {
        ArgumentNullException.ThrowIfNull(argument: read);

        BodyA = bodyA;
        BodyB = bodyB;
        Describe = describe;
        Payload = payload;
        RowOrdinal = rowOrdinal;
        m_cost = cost;
        m_read = read;
    }

    /// <summary>Gets the payload the read hands the world reader, or <see langword="null"/> when the read builds its own.</summary>
    public WorldOperandFact? Payload { get; }

    /// <inheritdoc/>
    public override string ToString() => Describe;

    /// <summary>Gets the first body reference.</summary>
    public WorldBodyRef BodyA { get; }
    /// <summary>Gets the second body reference.</summary>
    public WorldBodyRef BodyB { get; }
    /// <summary>Gets the authored channel, for the read-back.</summary>
    public string Describe { get; }
    /// <summary>Gets the catalog ordinal of the row the read scans, or <c>-1</c>.</summary>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var body in new[] { BodyA, BodyB }) {
            if (body.RowOrdinal >= 0) {
                into.Add(item: new CellAccess(
                    Key: default,
                    RowOrdinal: body.RowOrdinal
                ));
            }
        }
        if (RowOrdinal >= 0) {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: RowOrdinal
            ));
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => m_cost;
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return m_read(arg: facet);
    }
}
/// <summary>One fact on the identity a body drives under: the live value, at the evaluation's time, of the body's lane
/// cell in the world's reserved <see cref="WorldIdentityFactLane"/> row (<see cref="WorldRuleFacts.IdentityPrefix"/>);
/// a body driving under no identity, or a fact the identity never wrote, reads 0.</summary>
public sealed class WorldIdentityFactOperand : WorldFactOperand {
    private readonly CompiledBodyRef m_body;
    private readonly CellKey[] m_laneKeys;
    private readonly string m_fact;

    private CellKeyTable? m_laneKeyTable;

    /// <summary>Initializes the operand.</summary>
    /// <param name="body">The body reference, in the world reader's own form.</param>
    /// <param name="fact">The fact key on the identity's row.</param>
    /// <param name="laneOrdinal">The identity lane row's catalog ordinal.</param>
    /// <param name="capacity">The population capacity the lane-key cache is sized to.</param>
    public WorldIdentityFactOperand(CompiledBodyRef body, string fact, int laneOrdinal, int capacity) : base(valueKind: CellKind.Int) {
        LaneOrdinal = laneOrdinal;
        m_body = body;
        m_fact = fact;
        m_laneKeys = new CellKey[capacity];
    }

    /// <summary>Gets the identity lane row's catalog ordinal.</summary>
    public int LaneOrdinal { get; }

    // Compiled operands may serve a relayout that replaces an arena's runtime table. Cached addresses also cease
    // to resolve after a speculative mint rewinds, so validate them before reuse.
    private CellKey LaneKey(StateArena arena, int bodyIndex) {
        var keys = arena.Keys;

        if (!ReferenceEquals(objA: m_laneKeyTable, objB: keys)) {
            Array.Clear(array: m_laneKeys);
            m_laneKeyTable = keys;
        }
        if (((uint)bodyIndex) >= ((uint)m_laneKeys.Length)) {
            return Mint(
                bodyIndex: bodyIndex,
                keys: keys
            );
        }
        if (!keys.TryGetName(key: m_laneKeys[bodyIndex], name: out _)) {
            m_laneKeys[bodyIndex] = Mint(
                bodyIndex: bodyIndex,
                keys: keys
            );
        }

        return m_laneKeys[bodyIndex];
    }
    private CellKey Mint(CellKeyTable keys, int bodyIndex) => (keys.TryResolve(
        key: out var key,
        name: CellName.Parse(candidate: WorldIdentityFactLane.Key(
            bodyIndex: bodyIndex,
            fact: m_fact
        ))
    )
        ? key
        : default
    );

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: LaneOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);
        ArgumentNullException.ThrowIfNull(argument: reader);

        var body = m_body;
        var index = facet.ResolveBody(bodyRef: in body);

        if (index < 0) {
            return Fact.Finite(
                kind: CellKind.Int,
                value: 0L
            );
        }

        var key = LaneKey(
            bodyIndex: index,
            arena: reader.Arena
        );
        var time = reader.Time;

        return Fact.Finite(
            kind: CellKind.Int,
            value: ((key.IsValid && reader.Arena.TryReadLiveNumber(
                key: key,
                rowOrdinal: LaneOrdinal,
                time: in time,
                value: out var value
            ))
            ? value
            : 0L)
        );
    }
}
/// <summary>A <c>$pair:&lt;bodyRefA&gt;:&lt;bodyRefB&gt;</c> cell key, resolved from the two bodies' indices at
/// evaluation. Reads resolve only an already-admitted key; a state-writing effect admits its pair key inside the
/// firing's journal scope.</summary>
public sealed class WorldPairKeyFact : KeyFact<IWorldFacts>, IRuleKey {
    private readonly PairKeyFact m_key;

    /// <summary>Initializes the key.</summary>
    /// <param name="key">The world reader's own compiled form of the same key.</param>
    public WorldPairKeyFact(PairKeyFact key) {
        ArgumentNullException.ThrowIfNull(argument: key);

        m_key = key;
    }

    /// <inheritdoc/>
    public override CellKey Resolve(IStateReader reader, IWorldFacts facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        return facet.PairKey(key: m_key);
    }

    Resolved<CellKey> IRuleKey.Resolve(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var facet = ((IWorldFacts)reader);
        var key = Resolve(
            facet: facet,
            reader: reader
        );

        // An absent but addressable pair reads its state cell's default, just like any other missing keyed cell.
        // Only a missing participant is unnamed. PairKey itself does not intern while resolving a read.
        return (((facet.ResolveBody(bodyRef: m_key.BodyA) >= 0) && (facet.ResolveBody(bodyRef: m_key.BodyB) >= 0))
            ? Resolved<CellKey>.Named(value: key)
            : Resolved<CellKey>.Nothing);
    }
    WriteKey IRuleKey.ResolveForWrite(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var facet = ((IWorldFacts)reader);
        var existing = facet.PairKey(key: m_key);

        if (existing.IsValid) {
            return WriteKey.Resolved(key: existing);
        }

        var a = facet.ResolveBody(bodyRef: m_key.BodyA);
        var b = facet.ResolveBody(bodyRef: m_key.BodyB);

        if ((a < 0) || (b < 0)) {
            return WriteKey.Resolved(key: default);
        }

        return (reader.Arena.Keys.TryIntern(
            key: out var interned,
            name: CellName.Parse(candidate: $"{a}_{b}"),
            reason: out var reason
        )
            ? WriteKey.Resolved(key: interned)
            : WriteKey.Refused(reason: reason));
    }
    Resolved<long> IRuleKey.ResolveIndex(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var resolved = ((IRuleKey)this).Resolve(reader: reader);

        if (!resolved.TryNamed(value: out var key)) {
            return ((resolved.Case == FaultCase.Faulted)
                ? Resolved<long>.Faulted
                : Resolved<long>.Nothing);
        }

        return (RuleReads.TryKeyIndex(
            keys: reader.Arena.Keys,
            index: out var index,
            key: key
        )
            ? Resolved<long>.Named(value: index)
            : Resolved<long>.Nothing);
    }
}
