using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The world host's side of <see cref="IRuleHost.TryApply(StateMutation, ulong, bool, out string)"/>: a
/// <see cref="StateFrame"/> over the installed section that every state effect writes during
/// <see cref="WorldServer.EvaluateWorldRules"/>, so a tick's cell writes and transforms cost a value-array write
/// instead of a whole-document compose. What the frame accumulated folds into ONE real mutation, applied through
/// the ordinary door exactly once, at the end of the tick's rule evaluation.</summary>
public sealed partial class WorldServer {
    // Rebuilds only when the installed rows no longer fit the frame's layout, or a document swap minted a fresh
    // StateCatalog (a checkpoint restore mints one even when the row structure is byte-for-byte unchanged) — the
    // same rebuild-or-rebind rule SearchRuntime.Rebuild follows for its own frame. m_definition.State is a new
    // reference only on an install, so a repeat call between two installs (every gate and binding read during one
    // tick) short-circuits on that reference alone rather than re-deriving every row's layout to prove nothing moved.
    private StateFrame EnsureRuleFrame() {
        var rows = m_definition.State;

        if (ReferenceEquals(objA: rows, objB: m_ruleFrameFitRows)) {
            return m_ruleFrame!;
        }

        var catalog = RuleReadCatalog;

        if ((m_ruleFrameLayout is null) || !m_ruleFrameLayout.Fits(rows: rows) || !ReferenceEquals(objA: m_ruleFrameCatalog, objB: catalog)) {
            m_ruleFrameLayout = new FrameLayout(rows: rows, topology: name => WorldTopologyCompilation.Find(m_definition, name));
            m_ruleFrame = new StateFrame(layout: m_ruleFrameLayout, rows: rows);
            m_ruleFrameCatalog = catalog;
        } else {
            m_ruleFrame!.Rebind(rows: rows);
        }

        m_ruleFrameFitRows = rows;

        return m_ruleFrame!;
    }
    // Loads the frame fresh from the installed document — once per tick, before any rule evaluates — remembers the
    // tick's starting document as the fold's replay baseline, holds the frame active for IRuleReader.Store
    // until EvaluateWorldRules releases it, so a read outside the tick's own rule evaluation never reaches it, and
    // mirrors every moved identity binding into the fact lane so this tick's rules read it.
    private void LoadRuleFrame(ulong tick) {
        EnsureRuleFrame();
        m_ruleFrameStore ??= new RowStore(rows: () => m_definition.State);
        m_ruleFrame!.Load(source: m_ruleFrameStore);
        m_ruleFrameTickBaseline = m_definition;
        m_ruleFrameActive = true;
        SyncIdentityFactLanes(tick: tick);
    }
    private void BeginRuleFrameScope() {
        m_ruleFrameJournalMarks.Push(item: EnsureRuleFrame().BeginJournalScope());
        m_ruleFrameMutationMarks.Push(item: m_ruleFrameMutations.Count);
        m_ruleFrameReloadMarks.Push(item: m_ruleFrameReloadStamp);
        BeginIdentityFactScope();
    }
    private void EndRuleFrameScope() {
        var journalMark = m_ruleFrameJournalMarks.Pop();
        var mutationMark = m_ruleFrameMutationMarks.Pop();
        var reloadMark = m_ruleFrameReloadMarks.Pop();

        EndIdentityFactScope();
        m_ruleFrameMutations.RemoveRange(index: mutationMark, count: (m_ruleFrameMutations.Count - mutationMark));

        // A cross-row mutation (TryApplyCrossRowStateMutation) reloaded the frame from a candidate this scope is
        // now discarding — that reload bypassed the journal, so rewinding it cannot undo the reload; the frame is
        // re-derived instead, from m_definition as the caller (EndPreflight) already restored it.
        if (m_ruleFrameReloadStamp != reloadMark) {
            // The restored document may predate fast numeric writes before this scope. Replaying the retained
            // queue restores those values too, without retaining any speculative document or text write.
            if (!TryComposeRuleFrameCandidate(next: null, tick: m_evaluator.Tick, candidate: out var restored, reason: out var reason)) {
                throw new InvalidOperationException($"The retained rule prefix could not be restored: {reason}");
            }
            m_definition = restored;
            ReloadRuleFrame(extraScopesToClose: 1);
        } else {
            m_ruleFrame!.RewindJournalScope(mark: journalMark);
        }
    }
    // Keeps this scope's speculative state and document writes. The frame already contains its latest values;
    // the ordered queue remains the source for the eventual installation and any enclosing scope's rollback.
    private bool CommitRuleFrameScope() {
        var mutationMark = m_ruleFrameMutationMarks.Pop();

        _ = m_ruleFrameReloadMarks.Pop();
        m_ruleFrame!.CommitJournalScope();
        _ = m_ruleFrameJournalMarks.Pop();
        CommitIdentityFactScope();

        return (m_ruleFrameMutations.Count > mutationMark);
    }
    // Records every rule mutation in authored order. State writes use the frame for same-tick reads; document writes
    // use the speculative definition, and both are folded through the same ordinary mutation door at tick end.
    private void QueueRuleFrameMutation(WorldMutation mutation) {
        m_ruleFrameMutations.Add(item: mutation);
    }
    // Re-derives the frame from the CURRENT m_definition without disturbing how many preflight scopes remain
    // logically open: a reload needs zero open journal scopes (StateFrame.Load's own contract), so extraScopesToClose
    // (the scope currently closing, already popped from m_ruleFrameJournalMarks by its own caller — 0 from
    // TryApplyCrossRowStateMutation's own mid-effect reload, which closes none) plus every scope this tracks as
    // still open are committed, then every remaining one reopens, fresh, right after — every mark becomes the
    // frame's post-load journal length (0) either way, so reopening needs no per-scope bookkeeping beyond the count.
    private void ReloadRuleFrame(int extraScopesToClose) {
        for (var index = 0; index < extraScopesToClose; index++) {
            m_ruleFrame!.CommitJournalScope();
        }

        var depth = m_ruleFrameJournalMarks.Count;

        for (var index = 0; index < depth; index++) {
            m_ruleFrame!.CommitJournalScope();
        }

        EnsureRuleFrame();
        m_ruleFrameStore ??= new RowStore(rows: () => m_definition.State);
        m_ruleFrame!.Load(source: m_ruleFrameStore);
        m_ruleFrameJournalMarks.Clear();

        for (var index = 0; index < depth; index++) {
            m_ruleFrameJournalMarks.Push(item: m_ruleFrame!.BeginJournalScope());
        }

        m_ruleFrameReloadStamp++;
    }
    // Every StateMutation shape mapped onto the world's own WorldMutation vocabulary — the identical switch
    // IRuleHost.TryApply used before this frame existed, factored out so both the frame path and the cross-row
    // path below queue the same replay-ready shape.
    private static WorldMutation MapStateMutation(StateMutation mutation) => mutation switch {
        StateMutation.UpsertCell cell => new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.World,
            Row: cell.Row,
            Key: cell.Key,
            Value: cell.Value,
            Kind: ((cell.Write == StateWriteKind.Add) ? WorldDocumentWriteKind.Add : WorldDocumentWriteKind.Set),
            Text: cell.Text
        ),
        StateMutation.RemoveCell cell => new WorldMutation.RemoveStateCell(Principal: WorldPrincipal.World, Row: cell.Row, Key: cell.Key),
        StateMutation.Generate generate => new WorldMutation.Generate(Principal: WorldPrincipal.World, Row: generate.Row),
        StateMutation.Apply apply => new WorldMutation.TransformState(WorldPrincipal.World, apply.Transform),
        _ => throw new InvalidOperationException(message: $"state mutation '{mutation.GetType().Name}' has no world mapping."),
    };
    // Scratch for TryComposeRuleFrameCandidate's own combined member list — cleared and refilled on every call
    // rather than allocated fresh, so a tick with many cross-row writes (a Klondike deal) pays for the list once
    // per call, never once per member replayed.
    private readonly List<WorldMutation> m_ruleFrameReplayScratch = [];
    // Replays the one authored rule queue from the clean tick baseline through the batch workspace — the same
    // vehicle an externally submitted WorldMutation.Batch composes through, which guarantees the document it hands
    // back is byte-identical to composing the same members one by one (TryComposeBatch's own contract). A cell
    // write or removal among the replayed members then shares ONE workspace row-list copy instead of paying for a
    // fresh whole-document compose per member, so a tick with many cross-row writes (a Klondike deal) composes its
    // prefix once per call rather than once per member replayed. `next` is appended to the replay rather than
    // queued first, so a caller sees whether the WHOLE candidate — prefix plus the pending member — composes before
    // deciding to keep it.
    private bool TryComposeRuleFrameCandidate(WorldMutation? next, ulong tick, out WorldDefinition candidate, out string reason) {
        var baseline = m_ruleFrameTickBaseline!;

        reason = string.Empty;

        if ((m_ruleFrameMutations.Count == 0) && (next is null)) {
            candidate = baseline;

            return true;
        }

        IReadOnlyList<WorldMutation> members;

        if (next is { } mutation) {
            m_ruleFrameReplayScratch.Clear();
            m_ruleFrameReplayScratch.AddRange(collection: m_ruleFrameMutations);
            m_ruleFrameReplayScratch.Add(item: mutation);
            members = m_ruleFrameReplayScratch;
        } else {
            members = m_ruleFrameMutations;
        }

        return TryComposeBatch(
            batch: new WorldMutation.Batch(Principal: WorldPrincipal.World, Mutations: members),
            candidate: out candidate,
            current: baseline,
            evictedKey: out _,
            instanceIdentity: InstanceIdentity,
            reason: out reason,
            tick: tick,
            patterns: m_patterns
        );
    }
    // The mutation kinds a value frame cannot answer on its own: a text cell (the frame holds only raw longs), a
    // cell removal (structural — no row shrinks in place), a generator draw (advances the row's own DrawCursor/
    // DrawnMasks, not a cell value), and a shuffle (permutes a keyed row's member order through a generator
    // stream keyed to a SEPARATE row). Each composes against a throwaway document built from this tick's own
    // baseline plus every mutation queued so far, so it sees this tick's earlier writes exactly as the real
    // install eventually will; on success the frame is re-derived from the result (ReloadRuleFrame) so a later
    // same-tick read (a gate, or another effect) sees it too — and a scope that later discards or commits this
    // call resyncs the frame back to whatever m_definition it restores, rather than trusting a journal rewind the
    // reload already bypassed (EndRuleFrameScope/CommitRuleFrameScope).
    private bool TryApplyCrossRowStateMutation(WorldMutation mapped, ulong tick, out string reason) {
        if (!TryComposeRuleFrameCandidate(next: mapped, tick: tick, candidate: out var candidate, reason: out reason)) {
            return false;
        }

        m_definition = candidate;
        QueueRuleFrameMutation(mutation: mapped);
        ReloadRuleFrame(extraScopesToClose: 0);

        return true;
    }
    // Installs this tick's queued rule mutations as ONE real mutation through the ordinary door — one compose, one
    // touched-row validation, one install, one journal entry, one echo — replayed from the document the tick
    // started on, never from whatever TryApplyCrossRowStateMutation left m_definition at. A tick that wrote nothing
    // composes nothing.
    private void FoldRuleFrameMutations(ulong tick) {
        if (m_ruleFrameMutations.Count == 0) {
            return;
        }

        m_definition = m_ruleFrameTickBaseline!;

        var mutation = ((m_ruleFrameMutations.Count == 1)
            ? m_ruleFrameMutations[0]
            : new WorldMutation.Batch(Principal: WorldPrincipal.World, Mutations: m_ruleFrameMutations)
        );

        if (!TryApplyMutation(mutation: mutation, tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false)) {
            if (m_output.HasNarrationSink) {
                m_output.Narrate(channel: "world.rule", text: $"[world.rule: this tick's {m_ruleFrameMutations.Count} rule-written mutations were refused as one; the document stays at its prior tick]");
            }
        }

        m_ruleFrameMutations = [];
    }
}
