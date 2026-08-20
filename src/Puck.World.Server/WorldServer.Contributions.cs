using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Whether any surviving row still names `creationId` — the pre-check the retraction runs before submitting a
    // RemoveCreation, so a shared creation is left alone instead of driving a loud refusal the sweep would repeat
    // every tick. RemoveCreation's own compose arm re-checks placements; looks are covered by whole-document
    // revalidation, and are checked here for the same pre-filter reason.
    private static bool IsCreationReferenced(WorldDefinition definition, string creationId) {
        foreach (var placement in definition.Placements) {
            if (string.Equals(
                a: placement.CreationId,
                b: creationId,
                comparisonType: StringComparison.Ordinal
            )) {
                return true;
            }

            if (
                (placement.Contribution is { } contribution) &&
                string.Equals(
                a: contribution.SlotCreationId,
                b: creationId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return true;
            }
        }

        foreach (var look in definition.Looks) {
            if (
                (look.Source is WorldLookSource.Creation creationLook) &&
                string.Equals(
                a: creationLook.CreationId.Value,
                b: creationId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return true;
            }
        }

        return false;
    }
    // Re-stamps one slot's server-owned half through the ordinary pipeline under WorldPrincipal.World — the same
    // structural-exemption door a rule effect's own writes use, so the arm/disarm is journalled and undoable.
    private void StampContribution(WorldPlacement placement, WorldPlacementContribution contribution, ulong tick) {
        _ = TryApplyMutation(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            mutation: new WorldMutation.UpsertPlacement(
                Placement: (placement with { Contribution = contribution }),
                Principal: WorldPrincipal.World
            ),
            preMetered: false,
            tick: tick
        );
    }
    // Retraction: the host's frame stands, the piece goes. One UpsertPlacement re-points creationId back at the
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
            Console.Error.WriteLine(value: $"[world.contribution: retraction deferred ({WorldRuleEffectRefusal.CarrierPossessed}) — slot '{placement.Id}' carries inhabitant body:{possessedBody}, possessed by {possessor.Describe()}; the deadline stands and the sweep retries next tick]");

            return;
        }

        var retired = placement.CreationId;
        var contributor = contribution.Contributor;

        if (!TryApplyMutation(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            mutation: new WorldMutation.UpsertPlacement(
                Placement: (placement with {
                CreationId = contribution.SlotCreationId,
                Contribution = (contribution with { Contributor = null, RetractDeadlineTick = null }),
            }),
                Principal: WorldPrincipal.World
            ),
            preMetered: false,
            tick: tick
        )) {
            return;
        }

        Console.Error.WriteLine(value: $"[world.contribution: retracted '{placement.Id}' — tenure=presence link={contribution.Link?.Value ?? "(none)"} contributor={contributor?.Describe() ?? "(none)"} creation '{retired}' released, slot shows '{contribution.SlotCreationId}']");

        if (
            string.Equals(
            a: retired,
            b: contribution.SlotCreationId,
            comparisonType: StringComparison.Ordinal
        ) ||
            IsCreationReferenced(
            creationId: retired,
            definition: m_definition
        )
        ) {
            return;
        }

        _ = TryApplyMutation(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            mutation: new WorldMutation.RemoveCreation(
                Id: retired,
                Principal: WorldPrincipal.World
            ),
            preMetered: false,
            tick: tick
        );
    }
    // The placement upsert arm. Everything here is about the contribution facet's server-stamped half; a row carrying
    // no facet composes exactly as it always did.
    //
    // WorldPrincipal.World takes the submitted row verbatim: the per-tick sweep and a world rule's own effects are the
    // engine composing its own bookkeeping, and re-deriving it here would overwrite the arm/disarm/retract the sweep
    // just computed. Every other principal has the stamped half DERIVED, never read from the payload — filling the
    // slot stamps the acting principal the ingress door already put on the envelope, so there is no spelling of this
    // mutation that lets a submitter name who contributed.
    private static bool TryComposeUpsertPlacement(WorldDefinition current, WorldMutation.UpsertPlacement mutation, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        var incoming = mutation.Placement;

        if (mutation.Principal.Kind != PrincipalKind.World) {
            var existing = WorldDefinitionRows.FindPlacement(
                id: incoming.Id,
                placements: current.Placements
            );

            if (incoming.Contribution is { } submitted) {
                if (submitted.Contributor is { } named) {
                    reason = $"contribution.contributor {named.Describe()} is server-stamped — the compose boundary reads it off the submitting principal; drop it from the payload";

                    return false;
                }

                if (submitted.RetractDeadlineTick is { } namedDeadline) {
                    reason = $"contribution.retractDeadlineTick {namedDeadline} is server-stamped — only the presence sweep arms it; drop it from the payload";

                    return false;
                }

                var carried = (existing?.Contribution?.Contributor);
                var carriedDeadline = (existing?.Contribution?.RetractDeadlineTick);
                var fills = !string.Equals(
                    a: incoming.CreationId,
                    b: submitted.SlotCreationId,
                    comparisonType: StringComparison.Ordinal
                );

                if (
                    fills &&
                    (carried is null)
                ) {
                    carried = mutation.Principal;
                    carriedDeadline = null;
                } else if (!fills) {
                    carried = null;
                    carriedDeadline = null;
                }

                incoming = (incoming with { Contribution = (submitted with { Contributor = carried, RetractDeadlineTick = carriedDeadline }) });
            } else if (existing?.Contribution is { Contributor: { } occupant }) {
                reason = $"placement '{incoming.Id}' is a contribution slot filled by {occupant.Describe()} — retract it (re-point creationId at its slotCreationId) before dropping the facet";

                return false;
            }
        }

        candidate = (current with {
            PlacementsRaw = Upsert(
            list: current.Placements,
            item: incoming,
            keyOf: static placement => placement.Id
        ),
        });

        return true;
    }
    // CONTRIBUTION TENURE RECOVERY — the same tick-driven, replay-deterministic shape ReclaimExpiredEscrows and
    // SettleExpiredMarketListings establish, for a presence-tenure slot whose contributor's link went away instead of
    // an unaccepted ownership offer. `placements` is read once, before any mutation in this pass swaps m_definition,
    // matching those sweeps' own safe-iteration remark; a row an earlier iteration already rewrote is re-read from the
    // live definition where the pass needs its post-write state.
    private void SweepContributionTenure(ulong tick) {
        var rate = m_definition.SimulationRateHz;

        if (rate <= 0) {
            return;
        }

        var placements = m_definition.Placements;

        foreach (var placement in placements) {
            if (
                (placement.Contribution is not { Tenure: WorldContributionTenure.Presence } contribution) ||
                (contribution.Contributor is null) ||
                (contribution.Link is not { } link)
            ) {
                continue;
            }

            if (!TryLinkLiveness(
                adjacencyName: link.Value,
                dropped: out var dropped,
                staleTicks: out _
            )) {
                continue;
            }

            if (!dropped) {
                // Reconnect-within-grace is nothing happening at all: clear the stamp and leave the piece standing.
                if (contribution.RetractDeadlineTick is not null) {
                    StampContribution(
                        contribution: (contribution with { RetractDeadlineTick = null }),
                        placement: placement,
                        tick: tick
                    );
                }

                continue;
            }

            var grace = contribution.CompiledGrace(simulationRateHz: rate);

            if (grace.IsNever) {
                continue;
            }

            if (contribution.RetractDeadlineTick is { } deadline) {
                if (unchecked((long)tick) >= deadline) {
                    RetractContribution(
                        contribution: contribution,
                        placement: placement,
                        tick: tick
                    );
                }

                continue;
            }

            var armed = unchecked((((long)tick) + ((long)grace.Ticks)));

            StampContribution(
                contribution: (contribution with { RetractDeadlineTick = armed }),
                placement: placement,
                tick: tick
            );

            // A zero grace arms and expires on the same observation. Re-read the row so the retraction carries the
            // stamp that was just installed rather than the pre-arm snapshot this loop is walking.
            if (
                (armed > unchecked((long)tick)) ||
                (WorldDefinitionRows.FindPlacement(
                placements: m_definition.Placements,
                id: placement.Id
            ) is not { Contribution: { } stamped } stampedPlacement)
            ) {
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
    public bool TryLinkLiveness(string adjacencyName, out long staleTicks, out bool dropped) {
        staleTicks = 0L;
        dropped = false;

        if (WorldDefinitionRows.FindAdjacency(
            adjacencies: m_definition.Adjacencies,
            name: adjacencyName
        ) is null) {
            return false;
        }

        staleTicks = m_events.LinkStalenessTicks(adjacencyName: adjacencyName);
        dropped = m_events.LinkDropped(adjacencyName: adjacencyName);

        return true;
    }
}
