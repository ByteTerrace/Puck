using Puck.Commands;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    private long? TenureDeadline(string placement) {
        var value = TenureValue(placement);
        return value >= 0L ? value : null;
    }
    private long? TenureValue(string placement) {
        if (!Host.Arena.Catalog.TryResolve(StateLane.Document, WorldContributionState.Row, out var row) ||
            !Host.Arena.Keys.TryResolve(CellName.Parse(placement), out var key) || !Host.Arena.TryRead(row.Ordinal, key, out var value)) { return null; }
        return value.Raw;
    }
    private void StampContribution(WorldPlacement placement, WorldPlacementContribution contribution, ulong tick) {
        WriteTenure(placement.Id, contribution.RetractDeadlineTick ?? -1L);
        Host.Document.InstallPlacementProjection([(placement.Id, placement with { Contribution = contribution })]);
    }
    private void WriteTenure(string placement, long value) {
        if (!Host.Arena.Catalog.TryResolve(StateLane.Document, WorldContributionState.Row, out var row)) { throw new InvalidOperationException("The tenure row was not declared at installation."); }
        var mark = Host.BeginStateTransaction();
        if (!Host.Arena.TryWrite(rowOrdinal: row.Ordinal, key: Host.Arena.Keys.Intern(CellName.Parse(placement)), write: StateWriteKind.Set, operand: value, reason: out var reason)) {
            Host.RewindStateTransaction(mark); throw new InvalidOperationException(reason);
        }
        if (!Host.TryCommitStateTransaction(mark, Principal.World, out reason)) { throw new InvalidOperationException(reason); }
        Host.PublishArena();
    }
    // Retraction stores the released tenure state and projects the slot's authored creation again. The creation
    // declaration remains available; no placement or creation edit is submitted by a lifecycle sweep.
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

        WriteTenure(placement.Id, -2L);
        Host.Document.InstallPlacementProjection([(placement.Id, placement with {
            PrototypeId = contribution.SlotCreationId,
            Contribution = contribution with { Contributor = null, RetractDeadlineTick = null },
        })]);

        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.contribution",
                text: $"[world.contribution: retracted '{placement.Id}' — tenure=presence link={(contribution.Link?.Value ?? "(none)")} contributor={(contributor?.Describe() ?? "(none)")} creation '{retired}' released, slot shows '{contribution.SlotCreationId}']"
            );
        }

    }

    // Presence-tenure slots indexed by watched link. Declaration changes or the live tenure row version
    // rebuild this derived index; deadlines themselves live in the arena.
    private readonly List<string> m_tenureLinks = [];
    private readonly List<bool> m_tenureLinkDropped = [];
    private readonly List<WorldTenureSlot> m_tenureSlots = [];
    // Armed retract deadlines by placement id — the entries a quiet tick pays one comparison for instead of a walk.
    private readonly WorldDeadlineTable<string> m_tenureDeadlines = new();

    private WorldDefinition? m_tenureSource;
    private StateArena? m_tenureArena;
    private ulong m_tenureVersion;
    private int m_tenurePlacementReads;

    /// <summary>Gets how many placement rows the contribution-tenure sweep has read since this server booted — the
    /// read-back behind the sweep's own cost claim.</summary>
    /// <remarks>Diagnostic only: read by the tenure laws alone, off every hashed path, and never fed back into what
    /// the sweep decides.</remarks>
    public int TenurePlacementReads => m_tenurePlacementReads;

    // One placement walk and one liveness read per watched link; deadlines come from the live tenure row.
    private void RebuildTenureIndex() {
        m_tenureDeadlines.Clear();
        m_tenureLinkDropped.Clear();
        m_tenureLinks.Clear();
        m_tenureSlots.Clear();
        m_tenureSource = Host.Document.Definition;
        m_tenureArena = Host.Arena;
        m_tenureVersion = Host.Arena.Catalog.TryResolve(StateLane.Document, WorldContributionState.Row, out var tenure)
            ? Host.Arena.RowVersion(tenure.Ordinal) : 0UL;

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

            if (TenureValue(placement.Id) == -2L) {
                RetractContribution(placement, contribution, Host.NextInputTick);
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

            if (TenureDeadline(placement.Id) is { } armed) {
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
                if (TenureDeadline(placement.Id) is not null) {
                    _ = m_tenureDeadlines.Remove(token: slot.PlacementId);
                    StampContribution(
                        contribution: (contribution with { RetractDeadlineTick = null }),
                        placement: placement,
                        tick: tick
                    );
                }

                continue;
            }

            if (TenureDeadline(placement.Id) is not null) {
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

        var version = Host.Arena.Catalog.TryResolve(StateLane.Document, WorldContributionState.Row, out var tenure)
            ? Host.Arena.RowVersion(tenure.Ordinal) : 0UL;
        if (!ReferenceEquals(m_tenureArena, Host.Arena) || m_tenureVersion != version || !ReferenceEquals(
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
