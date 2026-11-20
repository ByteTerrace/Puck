using System.Diagnostics.CodeAnalysis;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    /// <summary>A validated mutation awaiting commit. The transaction that admitted and staged it owns
    /// the journal scope or document resources until installation or disposal.</summary>
    internal sealed class WorldPreparedMutation : IDisposable {
        private readonly WorldDocument m_document;
        private readonly WorldMutation m_mutation;
        private readonly ulong m_tick;
        private readonly ulong m_engineTick;
        private readonly int? m_stateMark;
        private readonly CellName? m_evicted;
        private readonly DocumentStage? m_stage;
        private bool m_pending = true;
        private MutationDrawHistory? m_privateWords;
        private Action? m_commitJournal;

        private WorldPreparedMutation(WorldDocument document, WorldMutation mutation, ulong tick, ulong engineTick,
            int? stateMark, CellName? evicted, DocumentStage? stage) {
            m_document = document;
            m_mutation = mutation;
            m_tick = tick;
            m_engineTick = engineTick;
            m_stateMark = stateMark;
            m_evicted = evicted;
            m_stage = stage;
        }

        internal static bool TryPrepare(WorldDocument document, WorldMutation mutation, ulong tick, ulong engineTick,
            bool preMetered, [NotNullWhen(true)] out WorldPreparedMutation? prepared, out string reason, out bool denied, bool derived) {
            prepared = null;
            denied = !document.TryAdmitCompleteMutation(mutation, preMetered, out var admission);
            if (denied) {
                reason = admission.Describe();
                return false;
            }

            var host = document.Host;
            var view = host.WriteView(mutation.Principal);
            var reads = host.ReadForWrite(view);
            int? mark = null;
            CellName? evicted = null;
            // Only real inputs to an accepted mutation enter its journal entry.
            MutationDrawHistory? words = null;
            using var capture = WorldJournalEntry.MayDraw(host.Configuration, mutation)
                ? host.DrawKeys.Capture(word => (words ??= new()).Record(host.Arena, word)) : null;
            DocumentStage? stage = null;
            try {
                bool accepted;
                if (document.IsStateCommand(mutation)) {
                    mark = host.BeginStateTransaction();
                    var time = ComposeTime(document.m_definition, engineTick: engineTick, tick: tick);
                    accepted = document.TryStateMembers(reads, mutation, in time, out reason, out evicted) &&
                        host.ValidateStateTransaction(mutation.Principal, out reason, reads);
                } else {
                    accepted = document.TryStageDocument(mutation, tick, engineTick, out stage, out evicted, out reason);
                }
                if (!reads.Failed) {
                    if (!accepted) { return false; }
                    Action? commitJournal = null;
                    if (!derived && mutation.Principal != Principal.World && host.PrepareMutationJournal is { } prepareJournal) {
                        (commitJournal, var refusal) = prepareJournal(new WorldJournalEntry(tick, engineTick, mutation) { DrawHistory = words });
                        if (refusal is not null) {
                            document.m_lastMutationFailureCode = "world.mutation.journal-unavailable";
                            reason = refusal;
                            return false;
                        }
                    }
                    prepared = new WorldPreparedMutation(document, mutation, tick, engineTick, mark, evicted, stage) {
                        m_privateWords = words,
                        m_commitJournal = commitJournal,
                    };
                    return true;
                }
            } catch (StateUnavailableException) when (reads.Failed) {
                // The transaction publishes the same refusal whether the reader's exception propagated or was caught.
            } finally {
                if (prepared is null) {
                    if (mark is { } open) { host.RewindStateTransaction(open); }
                    else if (words is not null) { host.DrawKeys.AdvanceGeneration(); }
                    stage?.Dispose();
                }
            }
            document.m_lastMutationFailureCode = WorldRecipientMutations.NotDisclosedCode;
            reason = view.Refusal("mutation", WorldRecipientMutations.RefusalSubject(mutation));
            denied = true;
            return false;
        }

        internal void Install(int connectionId, long correlationId, bool derived = false) {
            if (!m_pending) { throw new InvalidOperationException("The prepared mutation was already settled."); }
            m_pending = false;
            if (m_stateMark is { } mark) {
                m_document.Host.CommitStateTransaction(mark);
                m_document.Host.PublishArena();
            } else {
                m_document.CommitInstall(stage: m_stage!);
            }
            m_document.RecordCommittedMutation(m_mutation, m_tick, m_engineTick, connectionId, correlationId, m_evicted, m_stage, derived, m_privateWords, m_commitJournal);
        }

        public void Dispose() {
            if (!m_pending) { return; }
            m_pending = false;
            if (m_stateMark is { } mark) { m_document.Host.RewindStateTransaction(mark); }
            else {
                if (m_privateWords is not null) { m_document.Host.DrawKeys.AdvanceGeneration(); }
                m_stage?.Dispose();
            }
        }
    }
}
