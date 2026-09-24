using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    // Re-stamps one slot's server-owned half through the ordinary pipeline under Principal.World — the same
    // structural-exemption door a rule effect's own writes use, so the arm/disarm is journalled and undoable.
    private void StampContribution(WorldPlacement placement, WorldPlacementContribution contribution, ulong tick) {
        _ = Host.TryApplyMutation(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            mutation: new WorldMutation.UpsertPlacement(
                Placement: (placement with { Contribution = contribution }),
                Principal: Principal.World
            ),
            preMetered: false,
            tick: tick,
            engineTick: CompletedEngineTicks
        );
    }
    // Retraction: the host's frame stands, the piece goes. One UpsertPlacement re-points prototypeId back at the
    // authored slotCreationId and clears the stamped half, and — only when nothing else still names it — one
    // RemoveCreation releases the contributed row. Both are ordinary mutations, so world.undo puts the piece back.
    //
    // A slot whose Inhabit facet is bound to a possessed body defers instead: the deadline stays stamped and the next
    // tick's sweep tries again, so a retraction can never destroy a concrete drive grant's binding out from under it
    // (the same refusal a rule-fired despawn takes, reached through the lifetime rule instead of a rule effect).
    private void RetractContribution(WorldPlacement placement, WorldPlacementContribution contribution, ulong tick) {
        if (TryFindPossessedInhabitant(
            bodyIndex: out var possessedBody,
            holder: out var possessor,
            placementId: placement.Id
        )) {
            // The dequeue that reached here took this slot out of the deadline table, so the retry the stamped
            // deadline promises is an explicit re-arm at the next tick: this tick's own pass is already past it,
            // which is what keeps the retry from spinning inside the dequeue loop.
            m_tenureDeadlines.Add(
                dueTick: unchecked((((long)tick) + 1L)),
                token: placement.Id
            );

            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(
                    channel: "world.contribution",
                    text: $"[world.contribution: retraction deferred ({WorldRuleEffectRefusal.CarrierPossessed}) — slot '{placement.Id}' carries inhabitant body:{possessedBody}, possessed by {possessor.Describe()}; the deadline stands and the sweep retries next tick]"
                );
            }

            return;
        }

        var retired = placement.PrototypeId;
        var contributor = contribution.Contributor;

        if (!Host.TryApplyMutation(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            mutation: new WorldMutation.UpsertPlacement(
                Placement: (placement with {
                    PrototypeId = contribution.SlotCreationId,
                    Contribution = (contribution with { Contributor = null, RetractDeadlineTick = null }),
                }),
                Principal: Principal.World
            ),
            preMetered: false,
            tick: tick,
            engineTick: CompletedEngineTicks
        )) {
            return;
        }

        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.contribution",
                text: $"[world.contribution: retracted '{placement.Id}' — tenure=presence link={(contribution.Link?.Value ?? "(none)")} contributor={(contributor?.Describe() ?? "(none)")} creation '{retired}' released, slot shows '{contribution.SlotCreationId}']"
            );
        }

        // A creation some surviving row still names stays: its removal would be refused, and the stamp this sweep just
        // cleared would not bring it back to retry.
        if (WorldDefinitionRows.FindCreationReference(
            creationId: retired,
            definition: Host.Document.Definition
        ) is not null) {
            return;
        }

        _ = Host.TryApplyMutation(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            mutation: new WorldMutation.RemoveCreation(
                Id: retired,
                Principal: Principal.World
            ),
            preMetered: false,
            tick: tick,
            engineTick: CompletedEngineTicks
        );
    }

    // Presence-tenure slots, indexed by their watched link, rebuilt only when the document itself has changed since
    // the last rebuild (every mutation swaps the live definition for a new instance — this project never mutates a
    // document in place — so a reference compare is an exact "did anything change" test). m_tenureLinkDropped is the
    // liveness each link read AT THAT REBUILD, not the last tick's: a stamp write that is refused leaves the document
    // unchanged, so the disagreement it was reacting to still stands next tick and the pass retries. A successful
    // stamp swaps the document, which rebuilds both.
    private readonly List<string> m_tenureLinks = [];
    private readonly List<bool> m_tenureLinkDropped = [];
    private readonly List<WorldTenureSlot> m_tenureSlots = [];
    // Armed retract deadlines by placement id — the entries a quiet tick pays one comparison for instead of a walk.
    private readonly WorldDeadlineTable<string> m_tenureDeadlines = new();

    private WorldDefinition? m_tenureSource;
    private int m_tenurePlacementReads;

    /// <summary>Gets how many placement rows the contribution-tenure sweep has read since this server booted — the
    /// read-back behind the sweep's own cost claim.</summary>
    /// <remarks>Diagnostic only: read by the tenure laws alone, off every hashed path, and never fed back into what
    /// the sweep decides.</remarks>
    public int TenurePlacementReads => m_tenurePlacementReads;

    // Rebuilds the presence-tenure index from the live document: one walk of `placements`, one liveness read per
    // distinct watched link, and the deadline table refilled from whatever stamps the document already carries.
    private void RebuildTenureIndex() {
        m_tenureDeadlines.Clear();
        m_tenureLinkDropped.Clear();
        m_tenureLinks.Clear();
        m_tenureSlots.Clear();
        m_tenureSource = Host.Document.Definition;

        var placements = m_tenureSource.Placements;

        // Indexed, not foreach: Placements is declared IReadOnlyList, and its backing row array's own
        // IEnumerable<T>.GetEnumerator boxes on every call — indexing is the zero-allocation walk of an interface-
        // typed list regardless of what concrete type backs it.
        for (var index = 0; (index < placements.Count); index++) {
            var placement = placements[index];

            m_tenurePlacementReads++;
            if (
                (placement.Contribution is not { Tenure: WorldContributionTenure.Presence } contribution) ||
                (contribution.Contributor is null) ||
                (contribution.Link is not { } link)
            ) {
                continue;
            }

            var linkIndex = m_tenureLinks.IndexOf(item: link.Value);

            if (linkIndex < 0) {
                linkIndex = m_tenureLinks.Count;

                m_tenureLinkDropped.Add(item: (TryLinkLiveness(
                    adjacencyName: link.Value,
                    dropped: out var dropped,
                    staleTicks: out _
                ) && dropped));
                m_tenureLinks.Add(item: link.Value);
            }

            m_tenureSlots.Add(item: new WorldTenureSlot(
                LinkIndex: linkIndex,
                PlacementId: placement.Id
            ));

            if (contribution.RetractDeadlineTick is { } armed) {
                m_tenureDeadlines.Add(
                    dueTick: armed,
                    token: placement.Id
                );
            }
        }
    }
    // Brings every slot on one link into agreement with that link's current drop verdict: a reconnect clears the
    // stamp outright, a drop arms the authored grace. A zero grace arms and expires on the same observation, which
    // the deadline table's own dequeue below settles in the same tick.
    private void ReconcileTenureLink(int linkIndex, bool dropped, int rate, ulong tick) {
        for (var index = 0; (index < m_tenureSlots.Count); index++) {
            var slot = m_tenureSlots[index];

            if (slot.LinkIndex != linkIndex) {
                continue;
            }

            m_tenurePlacementReads++;
            if (WorldDefinitionRows.FindPlacement(
                id: slot.PlacementId,
                placements: Host.Document.Definition.Placements
            ) is not { Contribution: { } contribution } placement) {
                continue;
            }

            if (!dropped) {
                // Reconnect-within-grace is nothing happening at all: clear the stamp and leave the piece standing.
                if (contribution.RetractDeadlineTick is not null) {
                    _ = m_tenureDeadlines.Remove(token: slot.PlacementId);
                    StampContribution(
                        contribution: (contribution with { RetractDeadlineTick = null }),
                        placement: placement,
                        tick: tick
                    );
                }

                continue;
            }

            if (contribution.RetractDeadlineTick is not null) {
                continue;
            }

            var grace = contribution.CompiledGrace(simulationRateHz: rate);

            if (grace.IsNever) {
                continue;
            }

            var armed = unchecked((((long)tick) + ((long)grace.Ticks)));

            m_tenureDeadlines.Add(
                dueTick: armed,
                token: slot.PlacementId
            );
            StampContribution(
                contribution: (contribution with { RetractDeadlineTick = armed }),
                placement: placement,
                tick: tick
            );
        }
    }
    // CONTRIBUTION TENURE RECOVERY — the same tick-driven, replay-deterministic shape ReclaimExpiredEscrows
    // establishes, for a presence-tenure slot whose contributor's link went away instead of an unaccepted ownership
    // offer. A tick on an unchanged document whose watched links read as they did at the last rebuild, with nothing
    // due, reads no placement at all.
    private void SweepContributionTenure(ulong tick) {
        var rate = Host.Document.Definition.SimulationRateHz;

        if (rate <= 0) {
            return;
        }

        if (!ReferenceEquals(
            objA: Host.Document.Definition,
            objB: m_tenureSource
        )) {
            RebuildTenureIndex();
        }

        for (var index = 0; (index < m_tenureLinks.Count); index++) {
            var dropped = (TryLinkLiveness(
                adjacencyName: m_tenureLinks[index],
                dropped: out var live,
                staleTicks: out _
            ) && live);

            if (dropped == m_tenureLinkDropped[index]) {
                continue;
            }

            ReconcileTenureLink(
                dropped: dropped,
                linkIndex: index,
                rate: rate,
                tick: tick
            );
        }

        var signedTick = unchecked((long)tick);

        while (m_tenureDeadlines.TryDequeueDue(
            tick: signedTick,
            out var placementId
        )) {
            m_tenurePlacementReads++;
            // Re-read from the live definition: an earlier dequeue in this same pass may already have rewritten the
            // document this one is retracting against.
            if (WorldDefinitionRows.FindPlacement(
                id: placementId,
                placements: Host.Document.Definition.Placements
            ) is not { Contribution: { } stamped } stampedPlacement) {
                continue;
            }

            RetractContribution(
                contribution: stamped,
                placement: stampedPlacement,
                tick: tick
            );
        }
    }

    /// <summary>Returns one authored <c>adjacencies</c> row's live liveness — the event feed's own staleness count
    /// and its own latched drop verdict (<see cref="WorldEventFeed.LinkDropped"/>), never a second spelling of the
    /// grace comparison that pass owns.</summary>
    /// <param name="adjacencyName">The document's stable adjacency row name.</param>
    /// <param name="staleTicks">Simulation ticks since that row last took a delivered neighbour refresh; 0 on the
    /// tick one landed, and 0 for a row whose liveness sensing is disabled.</param>
    /// <param name="dropped">Whether the link pass currently calls the row dropped.</param>
    /// <returns><see langword="true"/> when <paramref name="adjacencyName"/> names an authored adjacency row.</returns>
    internal bool TryLinkLiveness(string adjacencyName, out long staleTicks, out bool dropped) {
        staleTicks = 0L;
        dropped = false;

        if (WorldDefinitionRows.FindAdjacency(
            adjacencies: Host.Document.Definition.Adjacencies,
            name: adjacencyName
        ) is null) {
            return false;
        }

        staleTicks = Host.Events.LinkStalenessTicks(adjacencyName: adjacencyName);
        dropped = Host.Events.LinkDropped(adjacencyName: adjacencyName);

        return true;
    }
}
