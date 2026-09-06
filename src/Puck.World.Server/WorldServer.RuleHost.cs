using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The server as the rule evaluator's host: the state library's <see cref="IRuleHost"/> over the installed
/// definition — its reader forwards the evaluation in flight from the evaluator, its mutation door maps a
/// <see cref="StateMutation"/> onto the world's own, and it widens the reader to the world facts only the server can
/// answer.</summary>
public sealed partial class WorldServer : IWorldRuleReader, IRuleHost {
    private long[] m_boardScratch = [];

    ulong IRuleReader.Tick => m_evaluator.Tick;
    // While EvaluateWorldRules holds the frame active, a read answers from it (EnsureRuleFrame's own reference
    // check keeps this cheap between writes). Every other reader (flock affinity, and anything else that reaches
    // IRuleReader outside EvaluateWorldRules) reads the installed document instead — the frame-commit contract: a
    // read outside the tick's own rule evaluation sees the document, never a mid-evaluation frame that this
    // tick's own fold has not yet installed.
    StateStore IRuleReader.Store => (m_ruleFrameActive ? EnsureRuleFrame() : (m_ruleFrameFallbackStore ??= new RowStore(rows: () => m_definition.State)));
    StateCatalog IRuleReader.Catalog => m_definition.StateCatalog;
    CompiledPatterns IRuleReader.Patterns => m_patterns;
    string? IRuleReader.BoundEachKey => m_evaluator.BoundEachKey;
    string? IRuleReader.BoundTokenKey {
        get => m_patternTokenKey;
        set => m_patternTokenKey = value;
    }
    string? IRuleReader.BoundPreviousKey {
        get => m_patternPreviousKey;
        set => m_patternPreviousKey = value;
    }
    bool IRuleReader.TableKeyMissing {
        get => m_tableKeyMissing;
        set => m_tableKeyMissing = value;
    }
    Span<long> IRuleReader.PatternWord => m_patternWord;

    int IRuleReader.BoundIndex(BoundKey key) => m_evaluator.BoundIndex(key: key);
    long IRuleReader.BindingValue(int ordinal) => m_evaluator.BindingValue(ordinal: ordinal);
    CompiledTable IRuleReader.Table(int ordinal) => m_tables[ordinal];
    void IRuleReader.ReportTableKeyMissing(string table, long key) => m_evaluator.ReportTableKeyMissing(table: table, key: key);
    Span<long> IRuleReader.BoardScratch(int cells) => BoardScratch(count: cells);

    // Every state effect (cell write, transfer, transform, draw) lands on WorldServer.RuleFrame.cs's value frame
    // instead of composing a whole candidate document: a text cell, a cell removal, a generator draw, and a
    // shuffle need cross-row bookkeeping a frame cannot hold, so those four fall to the cross-row path (which
    // still composes, but replays from this tick's own baseline rather than installing on the spot); the rest
    // answer through the frame's own value array. Either way the mutation queues for EvaluateWorldRules' own
    // end-of-tick fold — nothing here installs, journals, or delivers.
    bool IRuleHost.TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
        switch (mutation) {
            case StateMutation.UpsertCell { Text: not null }:
                return TryApplyCrossRowStateMutation(mapped: MapStateMutation(mutation: mutation), tick: tick, reason: out reason);
            case StateMutation.UpsertCell cell: {
                var frame = EnsureRuleFrame();

                // A frame's Keyed layout is fixed-size at construction, so a keyed row's key that is not already
                // stored means minting a cell — the one shape that needs the cross-row path, since only a keyed
                // row ever grows this way (a board's cells are topology-fixed, a slot has exactly one). Once the
                // frame already holds (or could never mint) the cell, a TryWrite refusal is a real one (envelope,
                // kind, an off-topology key) — trusted as-is rather than retried, so a later effect's refusal never
                // resurrects an earlier sibling's write by routing it through a batch that could fail as a whole.
                if (
                    (frame.Find(name: cell.Row) is { } row) &&
                    CellName.TryParse(candidate: cell.Key, name: out var key, reason: out reason)
                ) {
                    var keyed = (frame.Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal) && (frame.Layout[ordinal].Kind == FrameRowKind.Keyed));

                    if (keyed && !frame.TryStored(row: row, key: key, value: out _, text: out _)) {
                        return TryApplyCrossRowStateMutation(mapped: MapStateMutation(mutation: mutation), tick: tick, reason: out reason);
                    }
                    if (!frame.TryWrite(row: row, key: key, value: cell.Value, write: cell.Write, reason: out reason)) {
                        return false;
                    }

                    m_ruleFrameMutations.Add(item: MapStateMutation(mutation: mutation));

                    return true;
                }

                return TryApplyCrossRowStateMutation(mapped: MapStateMutation(mutation: mutation), tick: tick, reason: out reason);
            }
            case StateMutation.Apply { Transform: StateTransform.BoardCombine combine }: {
                if (!EnsureRuleFrame().TryBoardCombine(combine: combine, reason: out reason)) {
                    return false;
                }

                m_ruleFrameMutations.Add(item: MapStateMutation(mutation: mutation));

                return true;
            }
            case StateMutation.Apply { Transform: StateTransform.WriteSet writeSet }: {
                if (!EnsureRuleFrame().TryWriteSet(writeSet: writeSet, reason: out reason)) {
                    return false;
                }

                m_ruleFrameMutations.Add(item: MapStateMutation(mutation: mutation));

                return true;
            }
            case StateMutation.Apply { Transform: StateTransform.Push push }: {
                var frame = EnsureRuleFrame();

                if (frame.Find(name: push.Row) is not { } ring) {
                    reason = $"row '{push.Row}' is not in the frame";

                    return false;
                }
                if (!frame.TryPush(row: ring, value: push.Value, reason: out reason)) {
                    return false;
                }

                m_ruleFrameMutations.Add(item: MapStateMutation(mutation: mutation));

                return true;
            }
            case StateMutation.Apply { Transform: StateTransform.ClearEnclosed enclosed }: {
                if (!EnsureRuleFrame().TryClearEnclosed(enclosed: enclosed, reason: out reason)) {
                    return false;
                }

                m_ruleFrameMutations.Add(item: MapStateMutation(mutation: mutation));

                return true;
            }
            case StateMutation.Apply { Transform: StateTransform.Transfer { Selector: ZoneSelector.First or ZoneSelector.Last or ZoneSelector.Key } transfer }: {
                if (!EnsureRuleFrame().TryTransfer(transfer: transfer, reason: out reason)) {
                    return false;
                }

                m_ruleFrameMutations.Add(item: MapStateMutation(mutation: mutation));

                return true;
            }
            default:
                // A frame answers a transfer by first/last/key alone; StateTransform.Transfer's Random and Slice
                // selectors, like Shuffle, fall to the cross-row path below.
                return TryApplyCrossRowStateMutation(mapped: MapStateMutation(mutation: mutation), tick: tick, reason: out reason);
        }
    }
    void IRuleHost.BeginPreflight() {
        m_preflightScopes.Push(item: m_definition);
        m_preflightMutations.Push(item: []);
        BeginRuleFrameScope();
    }
    void IRuleHost.EndPreflight() {
        m_definition = m_preflightScopes.Pop();
        _ = m_preflightMutations.Pop();
        EndRuleFrameScope();
    }
    // One member installs as itself; several install as one Batch — one admission, validation, journal entry, and
    // delivery for the whole transaction. A transaction whose members were all state mutations composes nothing
    // here (the document-mechanism's own composed list stays empty) — its mutations already queued on the frame's
    // flat list, folded once at the end of the tick alongside every other rule's, so a caller ORs this call's
    // return against CommitRuleFrameScope's to learn whether the transaction applied at all.
    bool IRuleHost.TryCommitPreflight(ulong tick, out string reason) {
        m_definition = m_preflightScopes.Pop();
        var composed = m_preflightMutations.Pop();
        var stateApplied = CommitRuleFrameScope();
        reason = string.Empty;

        if (composed.Count == 0) {
            return stateApplied;
        }

        var mutation = ((composed.Count == 1) ? composed[0] : new WorldMutation.Batch(Principal: WorldPrincipal.World, Mutations: composed));

        if (TryApplyMutation(mutation: mutation, tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false)) {
            return true;
        }

        reason = "the ordinary mutation door refused the transaction; its mutation rejection names the concrete reason";

        return false;
    }
    // A decision evaluates on its own timers; an interaction evaluates once per bound carrier or pair, each through
    // the evaluator's own gate-and-fire under the latch binding the sweep chooses.
    bool IRuleHost.TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) {
        var world = (CompiledWorldRule)rule;

        if (world.Decision is not null) {
            applied = EvaluateDecisionRule(rule: world, tick: tick, stepTicks: stepTicks);

            return true;
        }

        if (world.Interaction is { } interaction) {
            applied = EvaluateInteraction(rule: world, interaction: interaction, latch: latch, bindings: latch.Bindings(name: rule.Name), tick: tick, stepTicks: stepTicks);

            return true;
        }

        applied = false;

        return false;
    }
    // One line per category per server lifetime; the ledger keeps the exact count.
    void IRuleHost.RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) =>
        Console.Error.WriteLine(value: $"[world.rule: effect refused ({diagnostic.Refusal}) — rule '{diagnostic.Rule}', '{diagnostic.Effect}': {diagnostic.Detail}; world.rule.failures carries the running count]");

    RuleFact IWorldRuleReader.Read(PopulationOperand operand) => RuleFact.Finite(value: m_population.ActiveCount(), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(PhysicsQuiescentOperand operand) => RuleFact.Finite(value: (m_population.RigidBodiesQuiescent() ? 1L : 0L), kind: CellKind.Bool);
    RuleFact IWorldRuleReader.Read(ClockOperand operand) => RuleFact.Finite(value: ReadClockPhaseError(), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(RegionOccupancyOperand operand) => RuleFact.Finite(value: m_events.OccupantCount(placementId: operand.PlacementId), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(MachineMemoryOperand operand) => RuleFact.Finite(value: (Machines.TryPeek(screen: operand.Screen, address: operand.Address, out var raw) ? raw : (byte)0), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(ArgBodyOperand operand) => RuleFact.Finite(
        value: ResolveArgBody(handle: operand.StateHandle, op: operand.Reduce, tick: m_evaluator.Tick, filterHandle: operand.FilterHandle, hasFilter: (operand.FilterRow is not null)),
        kind: CellKind.Int
    );
    RuleFact IWorldRuleReader.Read(BodyDistanceOperand operand) => RuleFact.Finite(value: ReadBodyDistance(bodyA: operand.BodyA, bodyB: operand.BodyB, tick: m_evaluator.Tick));
    RuleFact IWorldRuleReader.Read(LineOfSightOperand operand) => RuleFact.Finite(value: (ReadBodyLineOfSight(bodyA: operand.BodyA, bodyB: operand.BodyB, tick: m_evaluator.Tick) ? 1L : 0L), kind: CellKind.Bool);
    RuleFact IWorldRuleReader.Read(ParkedOperand operand) => ((ReadParkedRemaining(bodyRef: operand.BodyA, tick: m_evaluator.Tick) is { } remaining)
        ? RuleFact.Finite(value: remaining, kind: CellKind.Int)
        : RuleFact.Forever(kind: CellKind.Int));
    RuleFact IWorldRuleReader.Read(UprightOperand operand) => RuleFact.Finite(value: ReadBodyUpright(bodyRef: operand.BodyA, tick: m_evaluator.Tick));
    RuleFact IWorldRuleReader.Read(LinkStalenessOperand operand) => RuleFact.Finite(value: m_events.LinkStalenessTicks(adjacencyName: operand.AdjacencyName), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(ChannelOperand operand) => RuleFact.Finite(value: ReadChannelValue(seat: operand.Seat, ordinal: operand.ChannelOrdinal));
    RuleFact IWorldRuleReader.Read(NearestOperand operand) => RuleFact.Finite(value: ResolveNearestBody(from: operand.BodyA, tagRowHandle: operand.StateHandle, tick: m_evaluator.Tick), kind: CellKind.Int);
    RuleFact IWorldRuleReader.Read(NavigationOperand operand) => RuleFact.Finite(
        value: m_population.NavigationFact(index: ResolveBodyRef(bodyRef: operand.BodyA, tick: m_evaluator.Tick), facet: operand.Facet),
        kind: CellKind.Int
    );
    RuleFact IWorldRuleReader.Read(BoardCellOfOperand operand) {
        var index = ResolveBodyRef(bodyRef: operand.BodyA, tick: m_evaluator.Tick);

        return RuleFact.Finite(
            value: (((Body(index: index) is { } body) && operand.Topology.TryCellOf(position: body.FixedPosition, cell: out var cell)) ? cell : -1),
            kind: CellKind.Int
        );
    }
    int IWorldRuleReader.ResolveBody(in CompiledBodyRef bodyRef) => ResolveBodyRef(bodyRef: bodyRef, tick: m_evaluator.Tick);
    string IWorldRuleReader.PairKey(PairKeyFact key) => ResolvePairKey(a: ResolveBodyRef(bodyRef: key.BodyA, tick: m_evaluator.Tick), b: ResolveBodyRef(bodyRef: key.BodyB, tick: m_evaluator.Tick));

    private Span<long> BoardScratch(int count) {
        if (m_boardScratch.Length < count) {
            m_boardScratch = new long[Math.Max(val1: count, val2: BoardMask.MaxCells)];
        }

        return m_boardScratch.AsSpan(start: 0, length: count);
    }

    private RuleFact ReadWorldFact(OperandFact operand, ulong tick) => m_evaluator.Read(operand: operand, tick: tick);
    private string ResolveOperandKey(string? key, CompiledCellRef? keyFrom, ulong tick) => m_evaluator.ResolveKey(key: key, keyFrom: keyFrom, tick: tick);
    private bool TryEvaluateExpression(CompiledExpressionToken[] program, CellKind kind, ulong tick, out long value) => m_evaluator.TryEvaluateExpression(program: program, kind: kind, tick: tick, value: out value);
    // A gate read outside the evaluator's own loop (a decision's option or interrupt) reports against the rule the
    // decision path named.
    private bool RuleGateOpen(GateToken[] gate, ulong tick) => m_evaluator.GateOpen(gate: gate, tick: tick, ruleName: m_evaluator.RuleName);
}
