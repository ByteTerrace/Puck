using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>The rule facade of <see cref="WorldServer"/>, and the rule evaluator's host: the compiled rules,
/// groups, interactions and tables, their edge latches, the decision, pattern, identity-fact and influence-fact
/// runtimes, the queries, the trace and the world's own effect arms. The arena is the store every read and write
/// addresses, and the world facts only the server can answer reach a fact as the facet its declaration names.</summary>
public sealed partial class WorldRuleHost : IStateReader, IEffectHost, IArenaTransformHost, IRuleOwner, IRuleRefusalSink, IWorldFacts {
    // The compiled `rules` section, adopted by Install on every recompile from the same WorldFactsCompiler path the
    // validator already ran over the candidate. Recomputed unconditionally: rules and state rows are both
    // small-capacity sections, so there is no AffectsRules classification predicate earning its keep here.
    private CompiledRule[] m_rules = [];
    private CompiledRuleGroup[] m_groups = [];
    // The members of m_rules no group of m_groups claims. A claimed rule runs only under its group's pass or
    // cursor, so the direct evaluation pass walks this array and the group pass walks m_groups; walking m_rules
    // in both would fire every member twice per tick. m_rules stays whole because a group's Members index it,
    // and because admission, the latch prune, the trace and the decision freeze all speak of every rule.
    private CompiledRule[] m_ungroupedRules = [];
    private readonly RuleGroupState m_groupState = new();
    private CompiledTable[] m_tables = [];
    // The EDGE latch, keyed by rule name and deliberately OUTSIDE m_rules: a rule's own effect installs a new
    // definition, which recompiles m_rules, so a latch living in the compiled record would clear itself every time it
    // fired — which is exactly the 503-entries-in-500-ticks shape edge mode exists to close. Surviving names keep
    // their bit across an install; vanished names are dropped.
    private readonly RuleLatch m_ruleGateHeld = new();
    // The compiled `interactions` section — a SECOND compiled array, evaluated after m_rules (see
    // EvaluateWorldRules), never merged into it: an interaction desugars into a synthesized WorldRule and rides the
    // SAME per-rule evaluation, but interactions occupy their OWN name namespace (WorldInteraction.Name), so a
    // shared latch dictionary would risk aliasing a rule and an interaction that happen to share a name.
    private CompiledRule[] m_interactions = [];
    // The interaction family's own EDGE latch — the SAME shape m_ruleGateHeld is, kept separate for the identical
    // aliasing reason m_interactions itself is kept separate from m_rules.
    private readonly RuleLatch m_interactionGateHeld = new();

    // The state library's evaluator over this server as its host : the loop, the edge
    // latching, the trace, the refusal ledger, and every state-neutral effect's firing.
    private readonly RuleEvaluator m_evaluator;

    // Reused carrier/key scratch for rule evaluation: left (and forEach keys) and right, both live during one
    // distance interaction.
    private readonly List<int> m_carrierScratchLeft = [];
    private readonly List<int> m_carrierScratchRight = [];
    // A distance interaction's nearest-neighbour selection, ascending by distance then index (WorldInteraction.Neighbours).
    private readonly FixedQ4816[] m_neighbourDistance = new FixedQ4816[WorldInteractionCapacity.MaxNeighbours];
    private readonly int[] m_neighbourIndex = new int[WorldInteractionCapacity.MaxNeighbours];

    // The engine's largest representable magnitude — the DELIBERATELY-INVERTED sentinel WorldRuleFacts.DistancePrefix's
    // own remarks explain: unlike $machine:/$region:, where zero is a correct neutral count for "nothing there",
    // distance's neutral-for-absence value must never read as "close", or a within-range gate (compareState against
    // lessOrEqual) would spuriously OPEN for a body reference that resolved to nothing.
    private static readonly FixedQ4816 NoBodyDistance = FixedQ4816.MaxValue;
    // WorldRuleFacts.UprightPrefix's rotated axis: a body's own local +Y before FixedOrientation is applied.
    private static readonly FixedVector3 LocalUp = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );

    private readonly WorldServer m_host;

    /// <inheritdoc/>
    public StateArena Arena => m_host.Arena;
    /// <summary>Gets the compiled rule groups.</summary>
    internal CompiledRuleGroup[] Groups => m_groups;
    /// <summary>Gets each open group's pass or step progress.</summary>
    internal RuleGroupState GroupState => m_groupState;
    /// <summary>Gets the compiled <c>interactions</c> section — a second array evaluated after the rules, never
    /// merged into them: an interaction occupies its own name namespace.</summary>
    internal CompiledRule[] Interactions => m_interactions;
    /// <summary>Gets the interaction family's own edge latch.</summary>
    internal RuleLatch InteractionGateHeld => m_interactionGateHeld;
    /// <summary>Gets the edge latch keyed by rule name, deliberately outside the compiled rules so an install that
    /// recompiles them cannot clear it.</summary>
    internal RuleLatch RuleGateHeld => m_ruleGateHeld;
    /// <summary>Gets the compiled <c>rules</c> section, including every member a group claims.</summary>
    internal CompiledRule[] Rules => m_rules;
    /// <summary>Gets the compiled <c>tables</c> section.</summary>
    private CompiledTable[] Tables => m_tables;
    /// <summary>Gets the server whose document, arena, entity table and narration every rule read and effect arm
    /// addresses.</summary>
    private WorldServer Host => m_host;

    private readonly long[] m_localValues = new long[RuleCapacity.MaxLocalsPerRule];
    private readonly long[] m_hostPatternWord = new long[PatternCapacity.MaxWord];

    private ulong m_hostEngineTick;
    private ulong m_hostTick;

    private int m_boundLeft = -1;
    private int m_boundRight = -1;

    /// <inheritdoc/>
    public Span<long> Locals => m_localValues;
    /// <inheritdoc/>
    public CellKey BoundEachKey { get; set; }
    /// <inheritdoc/>
    public CellKey BoundPreviousKey { get; set; }
    /// <inheritdoc/>
    public CellKey BoundTokenKey { get; set; }
    /// <inheritdoc/>
    public ulong EngineTick => m_hostEngineTick;
    /// <inheritdoc/>
    public Span<long> PatternWord => m_hostPatternWord;
    /// <inheritdoc/>
    public ulong Tick => m_hostTick;
    // IStateReader.Time carries a default body, so a narrower accessibility here rebinds silently to it and loses
    // this world's dynamics and rate.
    /// <inheritdoc/>
    public ArenaTime Time => new(
        Dynamics: Host.Definition.Dynamics,
        EngineTick: m_hostEngineTick,
        Tick: m_hostTick,
        TicksPerSecond: Host.Definition.SimulationRateHz
    );

    // Moves the tick pair every read answers as of, on this host and on the arena host that shares its arena.
    private void AdvanceHost(ulong tick, ulong engineTick) {
        m_hostEngineTick = engineTick;
        m_hostTick = tick;
        Host.ArenaHost.Advance(
            engineTick: engineTick,
            tick: tick
        );
    }

    /// <inheritdoc/>
    public int BoundIndex(BoundKey key) => (key switch {
        BoundKey.Each => BoundEachIndex(),
        BoundKey.Left => m_boundLeft,
        BoundKey.Right => m_boundRight,
        _ => -1,
    });

    /// <inheritdoc/>
    void IRuleRefusalSink.RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) {
        var recorded = diagnostic;

        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.rule",
                text: $"[world.rule: effect refused ({recorded.Refusal}) — rule '{recorded.Rule}', '{recorded.Effect}': {recorded.Detail}; world.rule.failures carries the running count]"
            );
        }
    }
    // A decision evaluates on its own timers; an interaction evaluates once per bound carrier or pair, each through
    // the evaluator's own gate-and-fire under the latch binding the sweep chooses.
    bool IRuleOwner.TryEvaluateOwn(Puck.State.Rules.RuleEvaluator evaluator, CompiledRule rule, RuleLatch latch, ulong stepTicks, out bool applied) {
        var world = ((CompiledWorldFactsRule)rule);

        if (world.Decision is not null) {
            applied = EvaluateDecisionRule(
                rule: world,
                stepTicks: stepTicks
            );

            return true;
        }
        if (world.Interaction is { } interaction) {
            applied = EvaluateInteraction(
                bindings: latch.Bindings(name: rule.Name),
                interaction: interaction,
                latch: latch,
                rule: world,
                stepTicks: stepTicks
            );

            return true;
        }

        applied = false;

        return false;
    }

    /// <inheritdoc/>
    public RuleFact Read(PopulationOperand operand) => RuleFact.Finite(
        kind: CellKind.Int,
        value: Host.Population.ActiveCount()
    );
    /// <inheritdoc/>
    public RuleFact Read(PhysicsQuiescentOperand operand) => RuleFact.Finite(
        kind: CellKind.Bool,
        value: (Host.Population.RigidBodiesQuiescent()
        ? 1L
        : 0L)
    );
    /// <inheritdoc/>
    public RuleFact Read(ClockOperand operand) => RuleFact.Finite(
        kind: CellKind.Int,
        value: Host.ReadClockPhaseError()
    );
    /// <inheritdoc/>
    public RuleFact Read(RegionOccupancyOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: Host.Events.OccupantCount(placementId: operand.PlacementId)
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(MachineMemoryOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: (Host.Machines.TryPeek(
                address: operand.Address,
                screen: operand.Screen,
                value: out var raw
            )
                ? raw
                : (byte)0)
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(ArgBodyOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: Host.ResolveArgBodyOrdinal(
                filterRowOrdinal: ((operand.FilterRow is not null)
                    ? OrdinalOf(handle: operand.FilterHandle)
                    : -1),
                op: operand.Reduce,
                rowOrdinal: OrdinalOf(handle: operand.StateHandle)
            )
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(BodyDistanceOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(value: ReadBodyDistance(
            bodyA: ResolveBodyRef(bodyRef: operand.BodyA),
            bodyB: ResolveBodyRef(bodyRef: operand.BodyB)
        ));
    }
    /// <inheritdoc/>
    public RuleFact Read(LineOfSightOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(
            kind: CellKind.Bool,
            value: (ReadBodyLineOfSight(
                indexA: ResolveBodyRef(bodyRef: operand.BodyA),
                indexB: ResolveBodyRef(bodyRef: operand.BodyB)
            )
                ? 1L
                : 0L)
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(ParkedOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return ((Host.Population.ParkedRemainingTicks(
            index: ResolveBodyRef(bodyRef: operand.BodyA),
            tick: Tick
        ) is { } remaining)
            ? RuleFact.Finite(
                kind: CellKind.Int,
                value: remaining
            )
            : RuleFact.Forever(kind: CellKind.Int)
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(UprightOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(value: ReadBodyUpright(index: ResolveBodyRef(bodyRef: operand.BodyA)));
    }
    /// <inheritdoc/>
    public RuleFact Read(BodyFactOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: (((Host.Body(index: ResolveBodyRef(bodyRef: operand.Body)) is { } factBody) && BodyFactVocabulary.Holds(
                facts: factBody.Facts,
                gate: operand.Fact
            ))
                ? 1L
                : 0L)
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(LinkStalenessOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: Host.Events.LinkStalenessTicks(adjacencyName: operand.AdjacencyName)
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(ChannelOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(value: ReadChannelValue(
            ordinal: operand.ChannelOrdinal,
            seat: operand.Seat
        ));
    }
    /// <inheritdoc/>
    public RuleFact Read(NearestOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: ResolveNearestBody(
                origin: ResolveBodyRef(bodyRef: operand.BodyA),
                tagRowOrdinal: OrdinalOf(handle: operand.StateHandle)
            )
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(NavigationOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: Host.Population.NavigationFact(
                facet: operand.Facet,
                index: ResolveBodyRef(bodyRef: operand.BodyA)
            )
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(BoardCellOfOperand operand) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        var index = ResolveBodyRef(bodyRef: operand.BodyA);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: (((Host.Body(index: index) is { } body) && operand.Topology.TryCellOf(
                cell: out var cell,
                position: body.FixedPosition
            ))
                ? cell
                : -1)
        );
    }
    /// <inheritdoc/>
    public int ResolveBody(in CompiledBodyRef bodyRef) => ResolveBodyRef(bodyRef: in bodyRef);
    /// <inheritdoc/>
    public CellKey PairKey(PairKeyFact key) {
        ArgumentNullException.ThrowIfNull(argument: key);

        return Host.Arena.Catalog.Keys.Intern(name: CellName.Parse(candidate: ResolvePairKey(
            a: ResolveBodyRef(bodyRef: key.BodyA),
            b: ResolveBodyRef(bodyRef: key.BodyB)
        )));
    }
    /// <inheritdoc/>
    public bool TryReadHostOwnedCell(int rowOrdinal, int cell, out long value) {
        value = 0L;

        if (
            (Host.Population.Fields is not { } lattice) ||
            !TryHostOwnedField(
            field: out var field,
            rowOrdinal: rowOrdinal
        ) ||
            !lattice.TryFieldIndex(
            field: out var index,
            name: field
        )
        ) {
            return false;
        }

        value = lattice.Value(
            cell: cell,
            field: index
        ).Value;

        return true;
    }
    /// <inheritdoc/>
    public bool TryReadHostOwnedSlot(int rowOrdinal, out CellValue value) {
        value = default;

        return false;
    }

    // The field a host-owned row names, or null when the ordinal is not one this host serves.
    private bool TryHostOwnedField(int rowOrdinal, out string field) {
        field = string.Empty;

        if (
            (((uint)rowOrdinal) >= ((uint)Host.Arena.Catalog.Count)) ||
            !Host.Arena.Catalog.Descriptors[rowOrdinal].HostOwned
        ) {
            return false;
        }

        var descriptor = Host.Arena.Catalog.Descriptors[rowOrdinal];

        if (
            (descriptor.Lane != StateLane.Document) ||
            (((uint)descriptor.LaneOrdinal) >= ((uint)Host.Definition.State.Count)) ||
            (Host.Definition.State[descriptor.LaneOrdinal].Field is null)
        ) {
            return false;
        }

        field = descriptor.Name;

        return true;
    }
    // The participant index bound to $each: the iterated key spelled as a body index, or -1 when it is not one.
    private int BoundEachIndex() => (RuleReads.TryKeyIndex(
        catalog: Host.Arena.Catalog,
        index: out var index,
        key: BoundEachKey
    )
        ? ((int)index)
        : -1
    );
    private static int OrdinalOf(StateHandle handle) => (handle.IsValid
        ? handle.Ordinal
        : -1
    );
    private int ResolveBodyRef(in CompiledBodyRef bodyRef) => (bodyRef.Kind switch {
        CompiledBodyRefKind.Literal => bodyRef.Index,
        CompiledBodyRefKind.Binding => BoundIndex(key: ((BoundKey)bodyRef.Index)),
        CompiledBodyRefKind.Cell => (((IntegerOf(value: Host.ReadArenaCell(
        key: Host.Arena.Catalog.Keys.Intern(name: CellName.Parse(candidate: bodyRef.Key!)),
        rowOrdinal: OrdinalOf(handle: bodyRef.Handle)
    )) is var cellIndex) && (cellIndex >= 0) && (cellIndex < Host.Population.Capacity))
        ? ((int)cellIndex)
        : -1),
        // 'placement:$each' names the placement whose id is the iterated key, which is what the compile-time
        // ordinal list is keyed by; a key naming no declared placement resolves no body.
        CompiledBodyRefKind.Placement => ((bodyRef.PlacementOrdinals is not null)
        ? Host.Population.BodyForPlacementOrdinal(ordinal: Host.PlacementOrdinalOf(id: (Host.Arena.Catalog.Keys.TryGetName(
            key: BoundEachKey,
            name: out var placementName
        )
            ? placementName.Value
            : string.Empty)))
        : Host.Population.BodyForPlacementOrdinal(ordinal: bodyRef.Index)),
        _ => Host.ResolveArgBodyOrdinal(
        filterRowOrdinal: -1,
        op: ((bodyRef.Kind == CompiledBodyRefKind.ArgMax)
        ? StateReduceOp.Max
        : StateReduceOp.Min),
        rowOrdinal: OrdinalOf(handle: bodyRef.Handle)
    ),
    });

    /// <summary>Initializes the rule host over the server it evaluates against.</summary>
    /// <param name="host">The owning server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    internal WorldRuleHost(WorldServer host) {
        ArgumentNullException.ThrowIfNull(argument: host);

        m_host = host;
        m_evaluator = new RuleEvaluator(host: this);
    }

    /// <summary>Adopts a fresh rule compilation — the rules, the ungrouped subset, the interactions, the groups and
    /// the tables — leaving the edge latches alone, and admits every adopted rule, interaction and group trigger
    /// against this host.</summary>
    /// <param name="compilation">The compilation to adopt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="compilation"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An adopted rule, interaction or group names a need this host
    /// cannot answer.</exception>
    internal void Install(WorldRuleCompilation compilation) {
        ArgumentNullException.ThrowIfNull(argument: compilation);

        m_groups = compilation.Groups;
        m_interactions = compilation.Interactions;
        m_rules = compilation.Rules;
        m_tables = compilation.Tables;
        m_ungroupedRules = compilation.Ungrouped;

        AdmitRules(
            rules: m_rules,
            subject: "rule"
        );
        AdmitRules(
            rules: m_interactions,
            subject: "interaction"
        );
        AdmitGroups(groups: m_groups);
    }
    /// <summary>Drops every latched edge and group cursor whose compiled name the current compilation no longer
    /// carries. A surviving name keeps its bit.</summary>
    internal void PruneLatches() {
        m_groupState.Prune(compiled: m_groups);
        m_interactionGateHeld.Prune(compiled: m_interactions);
        m_ruleGateHeld.Prune(compiled: m_rules);
    }
}
