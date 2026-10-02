using Puck.Commands;
using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    // Applies one queued transfer as one transaction: resolve the frozen cohort -> reserve the destination's
    // exact target slots (capacity and destination Drive standing, both pre-checked before any member
    // detaches) -> detach every member with its pose captured -> commit every join into its reserved slot ->
    // if any join still fails (a refusal class reservation cannot pre-check, or the test-only injection
    // point below), abort: every already-landed member returns to its exact source pose, nothing partially
    // lands. No Server.Step of any instance runs between the first detach and the last decision, so a party
    // lands together, aborts together, or never leaves at all.
    private void ApplyTransfer(in PendingTransfer transfer) {
        // Idempotence: checked first, before this transfer touches anything — a retry-shaped duplicate (the
        // same transfer id submitted again) refuses by name rather than double-landing. A diegetic crossing
        // can never collide here on its own (it always mints a fresh id); only an explicitly-supplied id
        // (the verification seam) can.
        // Narrate's deferred formatter cannot capture an `in` parameter, so this transfer's id and source name are
        // copied to ordinary locals once, up front, for every narration below to close over.
        var transferId = transfer.TransferId;
        var sourceInstanceName = transfer.SourceInstance;
        var appliedKey = (transfer.SourceInstance, transfer.TransferId);
        var hadAppliedHighWater = m_appliedTransferHighWater.TryGetValue(
            key: transfer.SourceInstance,
            value: out var previousAppliedHighWater
        );

        if (
            (hadAppliedHighWater && (transfer.TransferId <= previousAppliedHighWater)) ||
            !m_appliedTransferIds.Add(item: appliedKey)
        ) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused (already applied — refused rather than double-landing)]"
                );
            }

            return;
        }
        m_appliedTransferHighWater[transfer.SourceInstance] = transfer.TransferId;
        var appliedSource = transfer.SourceInstance;
        var appliedTransferId = transfer.TransferId;

        foreach (var old in m_appliedTransferIds.Where(predicate: candidate => (string.Equals(
            a: candidate.SourceInstance,
            b: appliedSource,
            comparisonType: StringComparison.Ordinal
        ) && (candidate.TransferId < appliedTransferId))).ToArray()) {
            _ = m_appliedTransferIds.Remove(item: old);
        }

        if (!m_instances.TryGetValue(
            key: transfer.SourceInstance,
            value: out var source
        )) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused (no instance named '{sourceInstanceName}')]"
                );
            }

            return;
        }

        // A resolver-driven transfer's membership/authorization was proven once, at scan time, against the
        // cohort the scan read then. Re-deriving the scope key from that same frozen cohort's still-active
        // members, right here, before this transfer touches anything, catches a seat replaced, a membership
        // row mutated, or a party member who joined after the scan — never silently re-resolving (which
        // would land an unproven cohort in the previously resolved scoped session), always refusing the
        // whole transfer by name when the frozen proof no longer holds. Refusing only when every frozen
        // member had departed would let a partial departure silently move the survivor alone — the frozen
        // cohort's own proof was for both of them together, so a proof missing even one member is an
        // expired proof; refuse the whole transfer rather than moving a subset nobody re-verified. A no-op
        // for a non-resolver transfer (console world.transfer's raw forms carry no frozen scope key to
        // re-verify).
        if (
            !transfer.ScopeProofAlreadyVerified &&
            (transfer.ResolvedDestinationRow is { } frozenDestinationRow) &&
            (transfer.FrozenScopeKey is { } frozenScopeKey) &&
            (transfer.FrozenCohortSlots is { } frozenSlotsForScopeCheck)
        ) {
            var liveCohortForScopeCheck = LiveCohortForFrozenSlots(
                server: source.Server,
                frozenSlots: frozenSlotsForScopeCheck
            );
            string driftReason;

            if (liveCohortForScopeCheck.Count == 0) {
                driftReason = "no frozen cohort member is still active to re-verify membership against";
            } else if (liveCohortForScopeCheck.Count != frozenSlotsForScopeCheck.Count) {
                driftReason = $"only {liveCohortForScopeCheck.Count} of the frozen cohort's {frozenSlotsForScopeCheck.Count} member(s) are still active — the frozen proof was for the WHOLE cohort together, so a partial departure expires it rather than moving the survivors alone";
            } else if (!m_resolver.TryDeriveScopeKey(
                sourceDefinition: source.Server.Definition,
                destination: frozenDestinationRow,
                cohort: liveCohortForScopeCheck,
                scopeKey: out var liveScopeKey,
                reason: out var scopeReason
            )) {
                driftReason = scopeReason;
            } else if (!string.Equals(
                a: liveScopeKey,
                b: frozenScopeKey,
                comparisonType: StringComparison.Ordinal
            )) {
                driftReason = $"now resolves scope key '{liveScopeKey}' instead";
            } else {
                driftReason = string.Empty;
            }

            if (driftReason.Length > 0) {
                if (m_narration.HasNarrationSink) {
                    m_narration.Narrate(
                        channel: "world.transfer",
                        text: $"[world.transfer: transfer={transferId} refused (membership drifted between scan and drain in '{sourceInstanceName}' — the cohort no longer proves scope key '{frozenScopeKey}': {driftReason})]"
                    );
                }

                return;
            }
        }

        // The member slots this transfer moves: the frozen cohort whenever one was proven — a resolver-driven
        // crossing's exact scanned slots, `body` or `party` alike (a coalesced group's merged cohort is
        // carried exactly this way too — see ResolveAndEnqueueCoalescedTransfers), never recomputed live
        // here, so a member who joined the source after the scan never rides along unproven. A non-resolver
        // `party` transfer (console world.transfer, which carries no frozen cohort) falls back to the
        // source's whole active local-seat set read live. A non-resolver `body` transfer is always just the
        // one requested seat.
        int[] members;

        if (transfer.FrozenCohortSlots is { } frozenSlots) {
            members = [.. frozenSlots];
        } else if (transfer.Scope == TransferScope.Party) {
            members = ActiveLocalSeats(server: source.Server);
        } else {
            members = [transfer.SourceSlot];
        }

        if (members.Length == 0) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused (no active local seat in '{sourceInstanceName}' to party-transfer)]"
                );
            }

            return;
        }

        // A destination naming the SAME instance as the source is refused up front for Existing/Persistent, both of
        // which know their name before any spawn. A Fresh destination cannot self-target by construction (a freshly
        // minted name is never one already running), so there is nothing to pre-check for it here.
        if (
            (transfer.Destination.Name is { } destinationName) &&
            string.Equals(
            a: transfer.SourceInstance,
            b: destinationName,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused ('{sourceInstanceName}' names both the source and the target)]"
                );
            }

            return;
        }

        if (!TryResolveWorldPeerCall(
            authority: out var targetAuthority,
            reason: out var destinationReason,
            resolvedName: out var targetName,
            source: source,
            spawned: out var spawned,
            transfer: in transfer
        )) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused ({destinationReason})]"
                );
            }

            // A resolve minted this generation's cache entry before this drain ever attempted to start or
            // reuse the instance it names; that attempt just failed outright (an unstartable reference
            // document, a stable-named collision fence), so nothing will ever back this generation — retire
            // it now rather than leaving world.destinations reporting a dead active generation forever. A
            // refusal that fires before this point, like the source-missing check above, never aborts —
            // another pending transfer in the same drain batch may still be racing to make the same name
            // real.
            if (
                (transfer.Destination.Lifetime == TransferLifetime.Resolved) &&
                (transfer.Destination.Name is { } abortedName)
            ) {
                m_resolver.AbortGeneration(instanceName: abortedName);
            }

            NoteTransferOutcome(
                transfer: in transfer,
                sourceName: transfer.SourceInstance,
                outcome: $"refused-destination:{destinationReason}"
            );
            CloseAdjacencyAfterRefusal(
                reason: destinationReason,
                transfer: in transfer
            );

            return;
        }

        // Resolve once, then split BEFORE reserving anything. A parent cohort lease would consume the very
        // slots its children need and incorrectly require capacity for the whole non-atomic party.
        if (
            !transfer.PartyAllOrNothing &&
            (members.Length > 1)
        ) {
            var splitDestination = ((targetAuthority.Remote is { } remote)
                ? TransferDestination.Remote(
                    name: targetName,
                    documentPath: transfer.Destination.DocumentPath!,
                    authority: remote.Endpoint
                )
                : TransferDestination.Existing(name: targetName)
            );

            for (var ordinal = 0; (ordinal < members.Length); ordinal++) {
                var member = members[ordinal];

                ApplyTransfer(transfer: transfer with {
                    TransferId = MintUnappliedTransferId(sourceInstance: transfer.SourceInstance),
                    Scope = TransferScope.Body,
                    SourceSlot = member,
                    Destination = splitDestination,
                    FrozenCohortSlots = [member],
                    PartyAllOrNothing = true,
                    TestForceJoinRefusalOrdinal = ((transfer.TestForceJoinRefusalOrdinal == ordinal)
                    ? 0
                    : null),
                    ScopeProofAlreadyVerified = true,
                });
            }
            if (spawned) { ReapIfEmpty(name: targetName); }
            return;
        }

        // Reserve through the destination authority's escrow even when both authorities happen to be colocated.
        // Loopback is only a transport optimization beneath this contract; it is not a second transfer path.
        var sourceTick = (source.Server.NextInputTick - 1UL);
        var sourceRate = source.Server.Definition.SimulationRateHz;

        if (
            (sourceRate <= 0) ||
            !FixedTickConversion.TryDurationEngineTicksExact(
            seconds: ((decimal)transfer.HoldSeconds),
            ticks: out var holdEngineTicks
        )
        ) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused (source lease cannot be expressed exactly across the {FixedTickConversion.TicksPerSecond} engine-tick bridge)]"
                );
            }

            if (spawned) {
                ReapIfEmpty(name: targetName);
            }

            return;
        }

        var sourceStepTicks = (FixedTickConversion.TicksPerSecond / checked((ulong)sourceRate));
        var holdSourceSteps = Math.Max(
            val1: 1UL,
            val2: checked((((holdEngineTicks + sourceStepTicks) - 1UL) / sourceStepTicks))
        );
        var reservationMembers = new WorldTransferReservationMember[members.Length];
        // Use the source row's authenticated namespace, not a locally invented composite label.
        var sourceAuthority = source.Server.AuthorityIdentity;

        for (var reservationIndex = 0; (reservationIndex < members.Length); reservationIndex++) {
            var sourceSlot = members[reservationIndex];
            var traveler = source.Server.ExecuteAuthorityOperation(operation: () => {
                var body = source.Server.Population.EntryBody(index: sourceSlot);
                var mobility = source.Server.Population.EnsureMobility(
                    index: sourceSlot,
                    authority: source.Server.AuthorityIdentity
                );

                return (Identity: body?.Profile, Source: (body?.Source ?? IntentSource.Idle), BodyColor: source.Server.Population.BodyColor(index: sourceSlot), CatalogRig: source.Server.Population.CatalogRig(index: sourceSlot), Mobility: mobility);
            });

            reservationMembers[reservationIndex] = new WorldTransferReservationMember(
                Principal: MemberTravelPrincipal(
                    server: source.Server,
                    transfer: in transfer,
                    slot: sourceSlot
                ),
                PreferredSlot: sourceSlot,
                Identity: traveler.Identity,
                Source: traveler.Source,
                BodyColor: traveler.BodyColor,
                CatalogRig: traveler.CatalogRig,
                Mobility: traveler.Mobility
            );
        }

        // The wire-facing federation identity is the SOURCE ROW's own authenticated namespace — the value
        // WorldAttestedAuthenticator proves and the receiving door's SourceAuthorityMismatch check compares every
        // subsequent frame against — never a locally-invented composite label the far side never verified.
        var reservationRequest = new WorldTransferReservationRequest(
            TransferId: transfer.TransferId,
            SourceAuthority: sourceAuthority,
            SourceRateHz: sourceRate,
            SourceTick: sourceTick,
            DeadlineSourceTick: checked((sourceTick + holdSourceSteps)),
            Border: transfer.Border,
            BorderCapacity: transfer.BorderCapacity,
            PartyAllOrNothing: transfer.PartyAllOrNothing,
            PeerAdmission: (reservationMembers.Any(predicate: static member => !member.Source.IsLive) || members.Any(predicate: slot => (slot >= source.Server.Population.LocalSeatCount))),
            Members: reservationMembers
        );
        var reservation = targetAuthority.Reserve(request: reservationRequest);

        if (!reservation.Accepted) {
            var capacityRefusal = (reservation.Reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: " is full "
            )
                || reservation.Reason.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "no free body index"
            ));
            var willRetry = (capacityRefusal && (transfer.FullPolicy == WorldTransferFullPolicy.Retry) && (transfer.Attempt < TransferRetryCeiling));
            var retryText = (willRetry
                ? $"client will retry (attempt {(transfer.Attempt + 1)}/{TransferRetryCeiling}); no destination queue was created"
                : "terminal refusal; no queue was created"
            );
            var reserveReason = $"'{targetName}' refused reservation ({reservation.Reason}; {retryText})";

            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused ({reserveReason}) — the whole transfer is held, no reservation leaked]"
                );
            }

            if (spawned) {
                ReapIfEmpty(name: targetName);
            }

            NoteTransferOutcome(
                transfer: in transfer,
                sourceName: transfer.SourceInstance,
                target: targetAuthority,
                outcome: $"refused-reservation:{reserveReason}"
            );

            if (willRetry) {
                _ = m_appliedTransferIds.Remove(item: appliedKey);
                if (hadAppliedHighWater) {
                    m_appliedTransferHighWater[transfer.SourceInstance] = previousAppliedHighWater;
                } else {
                    _ = m_appliedTransferHighWater.Remove(key: transfer.SourceInstance);
                }
                m_pendingTransfers.Enqueue(item: (transfer with { Attempt = (transfer.Attempt + 1) }));
            } else {
                CloseAdjacencyAfterRefusal(
                    reason: reserveReason,
                    transfer: in transfer
                );
            }

            return;
        }

        if (
            (reservation.BodyIndices.Count != members.Length) ||
            (reservation.DestinationDefinition is null)
        ) {
            try {
                targetAuthority.Abort(
                    sourceAuthority: sourceAuthority,
                    transferId: transfer.TransferId
                );
            } catch (Exception exception) when ((exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)) {
                // A malformed remote acceptance is never consumed. Its bounded destination lease is the fallback
                // if this best-effort abort cannot reach the peer.
            }

            var malformedReason = $"'{targetName}' returned a malformed accepted reservation (expected {members.Length} body indices and a destination definition)";

            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} refused ({malformedReason}) — every source member remains attached]"
                );
            }
            if (spawned) { ReapIfEmpty(name: targetName); }
            NoteTransferOutcome(
                transfer: in transfer,
                sourceName: transfer.SourceInstance,
                target: targetAuthority,
                outcome: $"refused-reservation:{malformedReason}"
            );
            CloseAdjacencyAfterRefusal(
                reason: malformedReason,
                transfer: in transfer
            );
            return;
        }

        var reservedSlots = reservation.BodyIndices.ToArray();
        var destinationDefinition = reservation.DestinationDefinition;

        // Whole-transfer ALL-OR-NOTHING across SOURCE-side authorization too (destination standing was just proven
        // by the reservation above): pre-check every member's own LEAVE standing — Drive over its own body under
        // its travelling principal — BEFORE any member leaves, so a member blocked by a drive gate (a revoked grant
        // today, a combat CC/death gate later) refuses the WHOLE party rather than letting the rest split off while
        // it strands at the source. One blocked member names itself and why.
        foreach (var slot in members) {
            var standingPrincipal = MemberTravelPrincipal(
                server: source.Server,
                transfer: in transfer,
                slot: slot
            );

            var standing = source.Server.ExecuteAuthorityOperation(operation: () => {
                var allowed = AllowsLeave(
                    server: source.Server,
                    principal: standingPrincipal,
                    slot: slot,
                    denial: out var denial
                );

                return (Allowed: allowed, Denial: denial);
            });

            if (!standing.Allowed) {
                targetAuthority.Abort(
                    sourceAuthority: sourceAuthority,
                    transferId: transfer.TransferId
                );
                if (m_narration.HasNarrationSink) {
                    m_narration.Narrate(
                        channel: "world.transfer",
                        text: $"[world.transfer: transfer={transferId} refused ({standingPrincipal.Describe()} cannot leave '{sourceInstanceName}' {TravellerName(index: slot, localSeatCount: source.Server.Population.LocalSeatCount)} — {standing.Denial}); the whole transfer is held]"
                    );
                }

                if (spawned) {
                    ReapIfEmpty(name: targetName);
                }

                NoteTransferOutcome(
                    transfer: in transfer,
                    sourceName: transfer.SourceInstance,
                    target: targetAuthority,
                    outcome: $"refused-source-standing:{standingPrincipal.Describe()}"
                );
                CloseAdjacencyAfterRefusal(
                    transfer: in transfer,
                    reason: $"{standingPrincipal.Describe()} cannot leave — {standing.Denial}"
                );

                return;
            }
        }

        // Mapped arrival's own counterpart resolution — a group-level fact (every member of this transfer
        // maps through the same portal pair), resolved once here against the destination's own delivered
        // definition, never at scan time, since cross-document existence cannot be checked at boot. A
        // resolution failure feeds the same abortReason/unwind mechanism the per-member join-refusal path
        // below uses — with nothing yet detached, the unwind loop after it is simply a no-op, and the
        // ABORTED line names why by quoting the exact counterpart string that failed to resolve.
        //
        // The counterpart resolve proves the authored anchor exists; the arrival frame itself is that
        // face's own derived frame (WorldFaceCatalog), exactly like transfer.SourceFrame captured at scan.
        // Both ends read the same derivation rendering draws from, so scan, arrival, and the drawn door can
        // never disagree about where a face sits.
        var counterpartFrame = default(WorldFaceFrame);
        string? abortReason = null;

        if (transfer.Arrival == WorldPortalArrival.Mapped) {
            if (transfer.SourceFrame is null) {
                abortReason = $"mapped arrival into '{targetName}' carries no source boundary frame";
            } else if (transfer.AdjacencyCounterpart is { } adjacencyCounterpart) {
                if (WorldDefinitionRows.FindAdjacency(
                    adjacencies: destinationDefinition.Adjacencies,
                    name: adjacencyCounterpart
                ) is { } destinationAdjacency) {
                    counterpartFrame = destinationAdjacency.Boundary.CompileFrame();
                } else {
                    abortReason = $"mapped adjacency arrival names no counterpart '{adjacencyCounterpart}' in '{targetName}'";
                }
            } else if (WorldPortalCounterpart.TryResolve(
                definition: destinationDefinition,
                counterpart: transfer.Counterpart,
                placement: out var counterpartPlacement,
                face: out var counterpartFace,
                reason: out var counterpartReason
            )) {
                if (WorldFaceCatalog.For(definition: destinationDefinition).TryFind(
                    placementId: counterpartPlacement!.Id,
                    faceName: counterpartFace!.Face,
                    out var counterpartRow
                )) {
                    counterpartFrame = counterpartRow.Frame;
                } else {
                    abortReason = $"mapped arrival's counterpart '{transfer.Counterpart}' names no DECLARED creation face in '{targetName}'";
                }
            } else {
                abortReason = $"mapped arrival's {counterpartReason} in '{targetName}'";
            }
        }

        // Detach every source member first, then send one cohort commit to the destination escrow. The source
        // remains the lease authority until that commit is acknowledged; a refused commit restores every body.
        var landed = new List<LandedMember>(capacity: members.Length);
        var commitMembers = new List<WorldTransferCommitMember>(capacity: members.Length);

        for (var index = 0; ((abortReason is null) && (index < members.Length)); index++) {
            var sourceSlot = members[index];
            var reservedSlot = reservedSlots[index];
            var memberPrincipal = MemberTravelPrincipal(
                server: source.Server,
                transfer: in transfer,
                slot: sourceSlot
            );

            // Read before the detach, which hands the slot back: the turn the traveler has accumulated so far.
            var departureTurn = source.Server.Population.TravelTurn(index: sourceSlot);

            if (!TryDetachAndCaptureMember(
                source: source,
                transferId: transfer.TransferId,
                sourceSlot: sourceSlot,
                sourceName: transfer.SourceInstance,
                actingPrincipal: memberPrincipal,
                profile: out var profile,
                bodyColor: out var bodyColor,
                position: out var position,
                yaw: out var yaw,
                dynamicState: out var dynamicState,
                designations: out var designations,
                peer: out var peer,
                admissionGrants: out var admissionGrants,
                sourceGrants: out var sourceGrants
            )) {
                abortReason = $"source member {TravellerName(index: sourceSlot, localSeatCount: source.Server.Population.LocalSeatCount)} could not detach after reservation";
                break;
            }

            landed.Add(item: new LandedMember(
                SourceSlot: sourceSlot,
                TargetSlot: reservedSlot,
                Profile: profile,
                BodyColor: bodyColor,
                Position: position,
                Yaw: yaw,
                DynamicState: dynamicState,
                Designations: designations,
                Peer: peer,
                AdmissionGrants: admissionGrants,
                SourceGrants: sourceGrants,
                SourcePrincipal: memberPrincipal,
                Mobility: reservationMembers[index].Mobility!.Value,
                FollowedSeatMask: CaptureFollowedSeats(
                    sourceInstance: transfer.SourceInstance,
                    sourceSlot: sourceSlot
                )
            ));
            var actionContinuity = source.Server.Population.NameTransferActionContinuity(
                slot: sourceSlot,
                state: dynamicState
            );
            var arrivalPosition = position;
            var arrivalYaw = yaw;
            var arrivalPlanarVelocity = dynamicState.PlanarVelocity;
            var arrivalVerticalVelocity = dynamicState.VerticalVelocity;
            var arrivalTurn = departureTurn;
            WorldContinuumTrajectory? continuum = null;

            // Overrides the destination's own fresh spawn pose with the positional-continuity mapping
            // (WorldFrameIsometry.MapArrival), then rotates the captured velocity the same way —
            // after the ordinary join above already embodied this member under the destination's own kit. The
            // selected motion-program NAME travels beside these mapped facts and resolves against that destination's
            // own declared program table (appearance/grants/action-track state remain untouched; see
            // WorldPopulation.ApplyMappedArrival). One isometry maps every member from wherever it actually
            // stands, so a party crossing abreast at different lateral offsets needs no per-member seam.
            if (transfer.Arrival == WorldPortalArrival.Mapped) {
                var sourceFrame = transfer.SourceFrame!.Value;
                var mapped = WorldFrameIsometry.MapArrival(
                    travelerPosition: position,
                    travelerYawRadians: yaw,
                    travelerPlanarVelocity: dynamicState.PlanarVelocity,
                    travelerVerticalVelocity: dynamicState.VerticalVelocity,
                    source: in sourceFrame,
                    destination: in counterpartFrame
                );

                arrivalPosition = mapped.Position;
                arrivalYaw = mapped.YawRadians;
                arrivalPlanarVelocity = mapped.PlanarVelocity;
                arrivalVerticalVelocity = mapped.VerticalVelocity;
                arrivalTurn = WorldFrameIsometry.AccumulateTurn(
                    arrivalYaw: mapped.YawRadians,
                    departureYaw: yaw,
                    travelTurn: departureTurn
                );

                // An adjacency is not a teleport: this source step has already evaluated input, actions, authored
                // motion, gravity, and timers. The destination receives only the remaining geometric image from its
                // counterpart seam to the mapped endpoint. If that image crosses another owner, the original source
                // tick/width stays unchanged and only the bounded face count advances. Ordinary portal furniture
                // deliberately carries no continuum payload and retains its historic arrival behavior.
                if (transfer.AdjacencyCounterpart is not null) {
                    var prior = (transfer.Continuum ?? (dynamicState.PendingContinuum
                        ?? throw new InvalidOperationException(message: "adjacency transfer carries no continuum interval")));
                    var boundaryEvents = checked((byte)(prior.BoundaryEvents + 1));

                    continuum = new WorldContinuumTrajectory(
                        PreviousPosition: WorldFrameIsometry.MapPoint(
                            point: transfer.SourceCrossingPoint,
                            source: in sourceFrame,
                            destination: in counterpartFrame
                        ),
                        SourceTick: prior.SourceTick,
                        ContinuumStartEngineTick: prior.ContinuumStartEngineTick,
                        ContinuumEndEngineTick: prior.ContinuumEndEngineTick,
                        ConsumedThroughEngineTick: Math.Max(
                            val1: prior.ConsumedThroughEngineTick,
                            val2: source.Server.CompletedEngineTicks
                        ),
                        BoundaryEvents: boundaryEvents
                    );
                }
            }

            commitMembers.Add(item: new WorldTransferCommitMember(
                Profile: profile,
                HasMappedArrival: (transfer.Arrival == WorldPortalArrival.Mapped),
                BodyMotionProgramName: dynamicState.BodyMotionProgramName,
                Position: arrivalPosition,
                YawRadians: arrivalYaw,
                PlanarVelocity: arrivalPlanarVelocity,
                VerticalVelocity: arrivalVerticalVelocity,
                ActionContinuity: actionContinuity,
                Continuum: continuum,
                TravelTurn: arrivalTurn
            ));
        }

        if (
            (abortReason is null) &&
            (transfer.TestForceJoinRefusalOrdinal is { } forcedOrdinal)
        ) {
            abortReason = $"TEST-ONLY forced refusal before escrow commit at member {forcedOrdinal} (world.transfer ... forcejoinrefusal:<n>)";
        }

        var departed = false;

        if (abortReason is null) {
            if (TryRecordDeparture(
                departure: new InDoubtTransfer(transfer with { FrozenCohortSlots = [.. members] }, targetAuthority, sourceAuthority, targetName, spawned, reservationRequest.DeadlineSourceTick, landed, commitMembers, members.Length),
                reason: out var departureReason
            )) {
                departed = true;
            } else {
                abortReason = $"'{sourceInstanceName}' could not make its departure durable ({departureReason})";
            }
        }

        if (abortReason is null) {
            var step = targetAuthority.Commit(
                sourceAuthority: sourceAuthority,
                transferId: transfer.TransferId,
                members: commitMembers,
                status: out var verdict,
                reason: out var commitReason
            );

            if ((step != WorldTransferStep.Answered) || (verdict == WorldTransferStatus.Uncertain)) {
                // Preserve every source recovery record and the exact commit payload. Subsequent fixed-point drains
                // query the destination's idempotent status and either publish the committed route, retry the live
                // lease, or restore the source after a confirmed missing/expired reservation. Never infer from a
                // failed transport or an uncertain arrival record whether the destination applied the commit.
                m_inDoubtTransfers.Add(item: new InDoubtTransfer(
                    Transfer: transfer with { FrozenCohortSlots = [.. members] },
                    TargetAuthority: targetAuthority,
                    SourceAuthority: sourceAuthority,
                    TargetName: targetName,
                    Spawned: spawned,
                    SourceDeadlineTick: reservationRequest.DeadlineSourceTick,
                    Landed: landed,
                    CommitMembers: commitMembers,
                    MemberCount: members.Length
                ));
                if (m_narration.HasNarrationSink) {
                    m_narration.Narrate(
                        channel: "world.transfer",
                        text: $"[world.transfer: transfer={transferId} IN-DOUBT ('{targetName}' {((step == WorldTransferStep.Answered) ? "answered uncertain" : "commit acknowledgement was lost")}: {commitReason}) — recovery state retained for status reconciliation]"
                    );
                }
                return;
            }

            if (verdict != WorldTransferStatus.Committed) {
                abortReason = $"'{targetName}' refused reserved commit ({commitReason})";
            }
        }

        if (abortReason is not null) {
            try {
                targetAuthority.Abort(
                    sourceAuthority: sourceAuthority,
                    transferId: transfer.TransferId
                );
            } catch (Exception exception) when ((exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)) {
                // No ambiguous commit reaches this arm. A failed abort leaves only the expiring destination lease.
            }
            var rollback = new InDoubtTransfer(transfer with { FrozenCohortSlots = [.. members] }, targetAuthority, sourceAuthority, targetName, spawned, reservationRequest.DeadlineSourceTick, landed, commitMembers, members.Length, RollbackOnly: true);

            // A departure already made durable is settled home before the cohort returns, so a restart redoes the
            // return instead of asking the destination again.
            if (
                (departed && !TrySettle(
                arrived: false,
                pending: rollback
            )) ||
                !RestoreDetachedMembers(
                commits: commitMembers,
                members: landed,
                source: source,
                transferId: transfer.TransferId
            )
            ) {
                m_inDoubtTransfers.Add(item: rollback);
                if (m_narration.HasNarrationSink) {
                    m_narration.Narrate(
                        channel: "world.transfer",
                        text: $"[world.transfer: transfer={transferId} ROLLBACK-PENDING ({abortReason}) — {landed.Count} source member(s) retain recovery state]"
                    );
                }
                return;
            }

            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transferId} ABORTED ({abortReason}) — every landed member returned to '{sourceInstanceName}' at its exact source pose]"
                );
            }

            if (spawned) {
                ReapIfEmpty(name: targetName);
            }

            NoteTransferOutcome(
                transfer: in transfer,
                sourceName: transfer.SourceInstance,
                target: targetAuthority,
                outcome: $"aborted:{abortReason}"
            );
            // The abort above already restored every member to its exact source pose — the party-atomicity contract.
            // A crossing abort then clamps inside on top of that restore, because the restored pose is the pose that
            // just left through the boundary.
            CloseAdjacencyAfterRefusal(
                reason: abortReason,
                transfer: in transfer
            );

            return;
        }

        var confirmed = new InDoubtTransfer(
            transfer with { FrozenCohortSlots = [.. members] },
            targetAuthority,
            sourceAuthority,
            targetName,
            spawned,
            reservationRequest.DeadlineSourceTick,
            landed,
            commitMembers,
            members.Length,
            CommitConfirmed: true
        );

        m_inDoubtTransfers.Add(item: confirmed);
        if (TryPublishCommittedTransfer(pending: confirmed)) {
            m_inDoubtTransfers.Remove(item: confirmed);
            CompleteCommittedTransfer(pending: confirmed);
        }
    }
    // The candidate cohort a set of local seats resolves as — read live off the server, shared by every resolver
    // call this file makes (the per-hit TryDeriveScopeKey probe and the per-group TryResolve mint/reuse alike).
    private static WorldSessionResolver.CohortMember[] BuildCohort(WorldServer server, IReadOnlyList<int> slots) {
        var cohort = new WorldSessionResolver.CohortMember[slots.Count];

        for (var index = 0; (index < slots.Count); index++) {
            var slot = slots[index];

            cohort[index] = new WorldSessionResolver.CohortMember(
                Principal: TravelPrincipal(
                    server: server,
                    slot: slot
                ),
                IdentityId: server.Population.EntryBody(index: slot)?.Profile?.Id
            );
        }

        return cohort;
    }
    private void EnqueueAdjacencyTransfer(WorldInstance instance, in WorldAdjacencyFaceHit hit) {
        var definition = instance.Server.Definition;
        var label = $"{instance.Name}/{hit.Adjacency.Name}";

        if (
            (WorldDefinitionRows.FindDestination(
            destinations: definition.Destinations,
            name: hit.Adjacency.Destination
        ) is not { } destination) ||
            (WorldDefinitionRows.FindReference(
            references: definition.References,
            name: destination.Reference
        ) is not { } reference)
        ) {
            CloseAdjacency(
                hit: in hit,
                instance: instance,
                reason: "its destination rows are no longer resolvable"
            );
            return;
        }

        var slots = new[] { hit.Seat };
        var cohort = BuildCohort(
            server: instance.Server,
            slots: slots
        );
        var referencedDocument = ResolveReferenceDocument(
            source: instance,
            neighbourKey: reference.NeighbourKey
        );
        var canonicalDocument = CanonicalDocumentIdentity(documentPath: referencedDocument);

        if (
            (destination.Durability == WorldDestinationDurability.Persisted) &&
            m_resolver.TryDeriveScopeKey(
            cohort: cohort,
            destination: destination,
            reason: out _,
            scopeKey: out var homeScopeKey,
            sourceDefinition: definition
        ) &&
            !m_resolver.TryGetActive(
            destinationName: destination.Name.Value,
            durability: destination.Durability,
            scopeKey: homeScopeKey,
            referencedDocument: canonicalDocument,
            resolved: out _
        )
        ) {
            if (TryFindRunningInstanceByOrigin(
                ambiguous: out var ambiguousNames,
                documentPath: referencedDocument,
                matchedName: out var matchedName
            )) {
                m_resolver.TryAdopt(
                    destination: destination,
                    instanceName: matchedName,
                    reason: out _,
                    referencedDocument: canonicalDocument,
                    resolved: out _,
                    scopeKey: homeScopeKey
                );
            } else if (ambiguousNames is { Count: > 1 }) {
                CloseAdjacency(
                    instance: instance,
                    hit: in hit,
                    reason: $"its destination document matches several running authorities [{string.Join(
                        separator: ",",
                        values: ambiguousNames
                    )}]"
                );
                return;
            }
        }

        if (!m_resolver.TryResolve(
            cohort: cohort,
            destination: destination,
            reason: out var resolveReason,
            referencedDocument: canonicalDocument,
            resolved: out var resolvedSession,
            sourceDefinition: definition
        )) {
            CloseAdjacency(
                hit: in hit,
                instance: instance,
                reason: resolveReason
            );
            return;
        }

        var transferDestination = TransferDestination.Resolved(
            name: resolvedSession.InstanceName,
            documentPath: referencedDocument,
            retain: true
        );
        var seamPosition = hit.Frame.PointAt(
            u: hit.SeamU,
            v: hit.SeamV
        );
        var sourceBody = instance.Server.Population.EntryBody(index: hit.Seat)!;
        var continuum = sourceBody.PendingContinuum;

        if (continuum is null) {
            var end = instance.Server.CompletedEngineTicks;
            var width = instance.Server.LastStepTicks;

            continuum = new WorldContinuumTrajectory(
                PreviousPosition: seamPosition,
                SourceTick: (instance.Server.NextInputTick - 1UL),
                ContinuumStartEngineTick: (end - width),
                ContinuumEndEngineTick: end,
                ConsumedThroughEngineTick: end,
                BoundaryEvents: 0
            );
        }
        var transferId = EnqueueTransfer(
            sourceInstance: instance.Name,
            scope: TransferScope.Body,
            sourceSlot: hit.Seat,
            destination: transferDestination,
            actingPrincipal: TravelPrincipal(
                server: instance.Server,
                slot: hit.Seat
            ),
            resolvedDestinationRow: destination,
            frozenCohortSlots: slots,
            frozenScopeKey: resolvedSession.ScopeKey,
            frozenGenerationId: resolvedSession.GenerationId,
            arrival: WorldPortalArrival.Mapped,
            adjacencyCounterpart: hit.Adjacency.Counterpart,
            sourceCrossingPoint: seamPosition,
            sourceFrame: hit.Frame,
            continuum: continuum,
            fullPolicy: WorldTransferFullPolicy.Retry,
            borderCapacity: hit.Adjacency.Capacity,
            border: $"adjacency/{hit.Adjacency.Name.Value}"
        );

        var traveller = TravellerName(index: hit.Seat, localSeatCount: instance.Server.Population.LocalSeatCount);

        if (m_narration.HasNarrationSink) {
            m_narration.Narrate(
                stream: WorldNarrationStream.Output,
                channel: "world.adjacency",
                text: $"[world.adjacency: '{label}' {traveller} crossed -> queued transfer={transferId} generation={resolvedSession.GenerationId} instance={resolvedSession.InstanceName}]"
            );
        }
    }
    // One (destination, scope key) group's own single resolve+enqueue — the ONE resolver call and ONE
    // EnqueueTransfer call the whole merged cohort shares, whether the cohort came from one hit or several.
    private void EnqueueCoalescedGroup(WorldInstance instance, CoalescedPortalGroup group) {
        var cohortSlots = group.Slots.ToArray();
        var cohort = BuildCohort(
            server: instance.Server,
            slots: cohortSlots
        );

        // The resolver's own cache-key identity is canonical, resolved once here — never the raw
        // group.ReferenceDocument string (TryFindRunningInstanceByOrigin below still takes the raw path since
        // it already canonicalizes both sides internally).
        // A references row is document-relative: resolve it beside the source instance before assigning a
        // resolver identity or starting a preview/transfer, rather than falling back to AppContext's copied
        // Assets tree, which can make an explicitly booted document and its own return reference look like
        // different origins.
        var referencedDocument = ResolveReferenceDocument(
            source: instance,
            neighbourKey: group.ReferenceDocument
        );
        var canonicalDocument = CanonicalDocumentIdentity(documentPath: referencedDocument);

        // A destination whose resolved document is the same document a RUNNING instance was already started
        // from — the boot instance especially — resolves to THAT instance, never minting a second one. Runs
        // before the ordinary resolve below, and only changes anything on a pair's first resolution:
        // WorldSessionResolver.TryGetActive gates it, so a key with an active generation is left alone (the
        // resolver's own cache always wins). A scope-key derivation failure here is not reported — the
        // ordinary TryResolve call below re-derives the identical key and reports the same refusal.
        //
        // Persisted-only: an EPHEMERAL destination's generations are resolver-minted by definition (the
        // first scoped resolution mints a fresh instance), so adopting a foreign already-running instance
        // for one would hand an ephemeral traveler someone else's live session purely because it shares the
        // destination's document. Only a PERSISTED destination's stable identity legitimately means the
        // same document, already running, is this destination — so this origin scan is narrowed to
        // persisted rows; ephemeral crossings use the ordinary TryResolve mint-or-reuse path below.
        if (
            (group.Destination.Durability == WorldDestinationDurability.Persisted) &&
            m_resolver.TryDeriveScopeKey(
            sourceDefinition: instance.Server.Definition,
            destination: group.Destination,
            cohort: cohort,
            scopeKey: out var homeScopeKey,
            reason: out _
        ) &&
            !m_resolver.TryGetActive(
            destinationName: group.Destination.Name.Value,
            durability: group.Destination.Durability,
            scopeKey: homeScopeKey,
            referencedDocument: canonicalDocument,
            resolved: out _
        )
        ) {
            if (TryFindRunningInstanceByOrigin(
                ambiguous: out var ambiguousNames,
                documentPath: referencedDocument,
                matchedName: out var matchedName
            )) {
                m_resolver.TryAdopt(
                    destination: group.Destination,
                    scopeKey: homeScopeKey,
                    referencedDocument: canonicalDocument,
                    instanceName: matchedName,
                    resolved: out _,
                    reason: out _
                );
            } else if (ambiguousNames is { Count: > 1 }) {
                if (m_narration.HasNarrationSink) {
                    m_narration.Narrate(
                        channel: "world.portal",
                        text: $"[world.portal: '{instance.Name}' {string.Join(
                            separator: ", ",
                            values: group.Descriptions
                        )} refused (destination '{group.Destination.Name}' resolves document '{group.ReferenceDocument}', matching {ambiguousNames.Count} running instances [{string.Join(
                            separator: ",",
                            values: ambiguousNames
                        )}] by origin — ambiguous, refused rather than adopting one arbitrarily)]"
                    );
                }

                return;
            }
        }

        if (!m_resolver.TryResolve(
            sourceDefinition: instance.Server.Definition,
            destination: group.Destination,
            referencedDocument: canonicalDocument,
            cohort: cohort,
            resolved: out var resolvedSession,
            reason: out var resolveReason
        )) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.portal",
                    text: $"[world.portal: '{instance.Name}' {string.Join(
                        separator: ", ",
                        values: group.Descriptions
                    )} refused (destination '{group.Destination.Name}' — {resolveReason})]"
                );
            }

            return;
        }

        // The resolver already decided which scoped instance this cohort lands in (reused if a generation
        // is already active, freshly minted otherwise); TransferLifetime.Resolved's job is only
        // start-if-absent-else-reuse against that name — retained (never auto-reaped) exactly when the
        // destination's own durability is Persisted, mirroring the Persistent lifetime.
        var transferDestination = TransferDestination.Resolved(
            name: resolvedSession.InstanceName,
            documentPath: referencedDocument,
            retain: (group.Destination.Durability == WorldDestinationDurability.Persisted)
        );

        // The lowest triggering body's travel principal, as a seam names; MemberTravelPrincipal re-derives every other
        // member's own, so the choice among a merged group's triggering bodies only affects that body.
        var actingPrincipal = TravelPrincipal(
            server: instance.Server,
            slot: cohortSlots[0]
        );
        var transferId = EnqueueTransfer(
            sourceInstance: instance.Name,
            scope: group.Scope,
            sourceSlot: cohortSlots[0],
            destination: transferDestination,
            actingPrincipal: actingPrincipal,
            resolvedDestinationRow: group.Destination,
            frozenCohortSlots: cohortSlots,
            frozenScopeKey: resolvedSession.ScopeKey,
            frozenGenerationId: resolvedSession.GenerationId,
            arrival: group.Arrival,
            counterpart: group.Counterpart,
            sourceFrame: group.SourceFrame,
            holdSeconds: group.HoldSeconds,
            fullPolicy: group.FullPolicy,
            partyAllOrNothing: group.PartyAllOrNothing,
            borderCapacity: group.BorderCapacity,
            border: group.Border
        );

        if (m_narration.HasNarrationSink) {
            m_narration.Narrate(
                stream: WorldNarrationStream.Output,
                channel: "world.portal",
                text: $"[world.portal: '{instance.Name}' {string.Join(
                    separator: ", ",
                    values: group.Descriptions
                )} entered -> queued transfer={transferId} to '{group.Destination.Name}' (durability={WorldDestinationTokens.DurabilityToken(durability: group.Destination.Durability)} scope={WorldDestinationTokens.ScopeToken(scope: group.Destination.Scope)} travel={WorldDestinationTokens.TravelToken(travel: group.Travel)} arrival={WorldDestinationTokens.ArrivalToken(arrival: group.Arrival)} generation={resolvedSession.GenerationId}{(resolvedSession.IsNewGeneration
                ? " (new)"
                : "")} instance={resolvedSession.InstanceName} cohort=[{string.Join(
                    separator: ",",
                    values: cohortSlots.Select(selector: static slot => (slot + 1))
                )}])]"
            );
        }
    }
    // Only after all route/roster publication returned successfully may recovery and the destination's exact-retry
    // receipt be retired. No external publication callback runs during retirement.
    private void CompleteCommittedTransfer(InDoubtTransfer pending) {
        var transfer = pending.Transfer;
        var targetAuthority = pending.TargetAuthority!.Value;
        var targetName = pending.TargetName;
        var spawned = pending.Spawned;
        var landed = pending.Landed;
        var memberCount = pending.MemberCount;
        var sourceInstance = m_instances[transfer.SourceInstance];

        // A freshly spawned destination that seated NOBODY (every member skipped at detach — see the defense-in-
        // depth branch above) is worth cleaning up rather than leaking an empty one-shot instance. ReapIfEmpty
        // already refuses a RETAINED (persistent) name.
        if (
            spawned &&
            (landed.Count == 0)
        ) {
            ReapIfEmpty(name: targetName);
        }

        // Source publication is now complete; the destination can discard transaction-shaped exact-retry payload.
        // Until it observes this acknowledgement it retains the exact committed outcome; a later mobility epoch for
        // the same incarnation supersedes that tombstone, so lost acknowledgements remain bounded by live travelers
        // rather than accumulating once per seam crossing.
        try {
            targetAuthority.Acknowledge(
                sourceAuthority: sourceInstance!.Server.AuthorityIdentity,
                transferId: transfer.TransferId
            );
        } catch (Exception exception) when ((exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)) {
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={transfer.TransferId} cleanup acknowledgement deferred ({exception.GetType().Name}: {exception.Message})]"
                );
            }
        }

        // The source's tape re-applies exactly these departures, closing before an emptied source is reaped; the
        // destination's tape carries the arrival itself.
        NoteTransferOutcome(
            transfer: in transfer,
            sourceName: transfer.SourceInstance,
            outcome: $"committed:{landed.Count}/{memberCount}",
            departedSlots: landed.ConvertAll(converter: static member => member.SourceSlot),
            target: targetAuthority
        );

        // A SOURCE that this transfer just emptied is reaped by the SAME rule as any other departure.
        ReapIfEmpty(name: transfer.SourceInstance);
    }
    // Every seat of `instance` that already has a transfer queued or in flight, mapped to that transfer's id.
    // WorldAdjacencyRegion.Sweep answers Crossed for a body ALREADY beyond the ownership threshold (parameter
    // zero, so a multi-edge corner traversal can continue), which means a traveler waiting for its own queued
    // transfer to drain re-satisfies the same edge on every scan. One traversal must mint one crossing, so a
    // seat holding a transfer is not scanned again until that transfer resolves. Derived from the two live
    // collections rather than latched, so no resolution path can leak a stale hold: ApplyTransfer runs at the
    // drain point, never between a scan's read and its use.
    private Dictionary<int, ulong> HeldCrossingSeats(WorldInstance instance) {
        var held = new Dictionary<int, ulong>();

        void Hold(in PendingTransfer transfer) {
            if (!string.Equals(
                a: transfer.SourceInstance,
                b: instance.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                return;
            }

            if (transfer.FrozenCohortSlots is { } frozen) {
                foreach (var slot in frozen) {
                    held[slot] = transfer.TransferId;
                }
            } else if (transfer.Scope == TransferScope.Party) {
                foreach (var slot in ActiveLocalSeats(server: instance.Server)) {
                    held[slot] = transfer.TransferId;
                }
            } else {
                held[transfer.SourceSlot] = transfer.TransferId;
            }
        }

        foreach (var transfer in m_pendingTransfers) {
            Hold(transfer: in transfer);
        }

        foreach (var pending in m_inDoubtTransfers) {
            var transfer = pending.Transfer;

            Hold(transfer: in transfer);
        }

        return held;
    }
    // Re-derives the FROZEN cohort's current membership from the source instance's live state — for the members
    // STILL ACTIVE only. A member no longer active contributes nothing here (it is refused individually, by name, at
    // the ordinary per-member transfer step — never folded into a re-verification it can no longer prove anything
    // for).
    private static IReadOnlyList<WorldSessionResolver.CohortMember> LiveCohortForFrozenSlots(WorldServer server, IReadOnlyList<int> frozenSlots) {
        var members = new List<WorldSessionResolver.CohortMember>(capacity: frozenSlots.Count);

        foreach (var slot in frozenSlots) {
            if (!server.Population.IsActive(index: slot)) {
                continue;
            }

            members.Add(item: new WorldSessionResolver.CohortMember(
                Principal: TravelPrincipal(
                    server: server,
                    slot: slot
                ),
                IdentityId: server.Population.EntryBody(index: slot)?.Profile?.Id
            ));
        }

        return members;
    }
    // A portal's frozen cohort travels under each body's own principal, including coalesced peers and census bodies.
    // An explicit seat-driven party also derives each other member's principal; other explicit actors retain their
    // submitted authority. Reservation, standing and detach all use this decision.
    private static Principal MemberTravelPrincipal(WorldServer server, in PendingTransfer transfer, int slot) =>
        (((transfer.ResolvedDestinationRow is not null) || ((transfer.ActingPrincipal.Kind == PrincipalKind.Seat) && (transfer.ActingPrincipal.Index != slot)))
            ? TravelPrincipal(
                server: server,
                slot: slot
            )
            : transfer.ActingPrincipal
        );
    // The next deterministic fresh-instance name for a SITE: the generated "<site>~<n>", n the site's own draw
    // counter (see
    // m_freshCounters). Never wall-clock, RNG, or tick-of-entry — see that field's own remarks for why this is
    // deterministic within one process run rather than "replay-stable" (the tape does not cover this queue).
    private string MintFreshInstanceName(string site) {
        var ordinal = m_freshCounters.GetValueOrDefault(key: site);

        m_freshCounters[site] = (ordinal + 1);

        return WorldSessionResolver.FreshInstanceName(
            ordinal: ordinal,
            site: site
        );
    }
    // Deterministic, resolver-ordered — the ONE place a transfer id is minted, scoped to the SOURCE ROW's own
    // counter (ids are already scoped (SourceInstance, TransferId) in m_appliedTransferIds and by sourceAuthority on
    // the wire, so a per-row counter loses nothing and keeps a row's checkpoint self-contained).
    private ulong MintTransferId(string sourceInstance) {
        if (!m_instances.TryGetValue(
            key: sourceInstance,
            value: out var row
        )) {
            throw new InvalidOperationException(message: $"cannot mint a transfer id for unknown source instance '{sourceInstance}'");
        }

        return row.NextTransferId++;
    }
    private ulong MintUnappliedTransferId(string sourceInstance) {
        ulong transferId;

        do {
            transferId = MintTransferId(sourceInstance: sourceInstance);
        } while (m_appliedTransferIds.Contains(item: (sourceInstance, transferId)) ||
            (m_appliedTransferHighWater.TryGetValue(
            key: sourceInstance,
            value: out var highWater
        ) && (transferId <= highWater)));

        return transferId;
    }
    private static WorldSubmissionPayload RebindForwardedPayload(WorldSubmissionPayload payload, int bodyIndex) => payload switch {
        WorldSubmissionPayload.Command command => new WorldSubmissionPayload.Command(Value: command.Value with { EntityIndex = bodyIndex }),
        WorldSubmissionPayload.Designation designation => new WorldSubmissionPayload.Designation(Value: designation.Value with { EntityIndex = bodyIndex }),
        WorldSubmissionPayload.Session { Value: SessionRequest.Join join } => new WorldSubmissionPayload.Session(Value: join with { Slot = bodyIndex }),
        WorldSubmissionPayload.Session { Value: SessionRequest.Leave leave } => new WorldSubmissionPayload.Session(Value: leave with { Slot = bodyIndex }),
        WorldSubmissionPayload.Session { Value: SessionRequest.SetIdentity identity } => new WorldSubmissionPayload.Session(Value: identity with { Slot = bodyIndex }),
        WorldSubmissionPayload.Query { Value: WorldQuery.PlayerWhere } => new WorldSubmissionPayload.Query(Value: new WorldQuery.PlayerWhere(Index: bodyIndex)),
        WorldSubmissionPayload.Query { Value: WorldQuery.PlayerChannels } => new WorldSubmissionPayload.Query(Value: new WorldQuery.PlayerChannels(Index: bodyIndex)),
        WorldSubmissionPayload.Query { Value: WorldQuery.PlayerState } => new WorldSubmissionPayload.Query(Value: new WorldQuery.PlayerState(Index: bodyIndex)),
        WorldSubmissionPayload.Query { Value: WorldQuery.PlayerTargets } => new WorldSubmissionPayload.Query(Value: new WorldQuery.PlayerTargets(Index: bodyIndex)),
        WorldSubmissionPayload.Query { Value: WorldQuery.Contacts } => new WorldSubmissionPayload.Query(Value: new WorldQuery.Contacts(Index: (bodyIndex + 1))),
        WorldSubmissionPayload.Query { Value: WorldQuery.Properties properties } when (properties.BodyIndex is not null) => new WorldSubmissionPayload.Query(Value: properties with { BodyIndex = bodyIndex }),
        _ => payload,
    };
    private static bool RestoreDetachedMember(WorldInstance source, ulong transferId, LandedMember member, WorldTransferCommitMember commit) => source.Server.ExecuteAuthorityOperation(operation: () => source.Server.RestoreDetachedForTransfer(
        detached: new WorldDetachedBody(
            AdmissionGrants: member.AdmissionGrants,
            BodyColor: member.BodyColor,
            Designations: member.Designations,
            DynamicState: member.DynamicState,
            Peer: member.Peer,
            Position: member.Position,
            Profile: member.Profile,
            Slot: member.SourceSlot,
            SourceGrants: member.SourceGrants,
            Yaw: member.Yaw
        ),
        mobility: member.Mobility,
        transferId: transferId,
        // Inactive slots are absent from population checkpoints and can be reused while recovery waits, so the
        // departure turn is recovered from the retained commit, undoing only this attempted arrival.
        travelTurn: (commit.HasMappedArrival
            ? WorldFrameIsometry.AccumulateTurn(
                travelTurn: commit.TravelTurn,
                departureYaw: commit.YawRadians,
                arrivalYaw: member.Yaw
            )
            : commit.TravelTurn)
    ));
    // Remove successful restores from BOTH lists so checkpoint profile ordinals still match. Failed restores keep
    // their recovery record; no retry may overwrite an occupied source slot.
    private static bool RestoreDetachedMembers(WorldInstance source, ulong transferId, List<LandedMember> members, List<WorldTransferCommitMember> commits) {
        for (var index = 0; (index < members.Count);) {
            if (!RestoreDetachedMember(
                commit: commits[index],
                member: members[index],
                source: source,
                transferId: transferId
            )) { index++; continue; }
            members.RemoveAt(index: index);
            commits.RemoveAt(index: index);
        }
        return (members.Count == 0);
    }
    private static void SeedArrivalOccupancy(WorldInstance instance, int seat) {
        if (
            (seat < 0) ||
            (seat >= instance.Server.Population.LocalSeatCount) ||
            (instance.Server.Population.EntryBody(index: seat) is not { } body)
        ) {
            return;
        }

        var definition = instance.Server.Definition;
        var catalog = WorldFaceCatalog.For(definition: definition);
        var crossingFloor = WorldFacePortalPolicy.CrossingFloor(definition: definition);

        foreach (var placement in definition.Placements) {
            if (
                (placement is null) ||
                (placement.FaceSources is not { Count: > 0 } faces)
            ) {
                continue;
            }

            foreach (var face in faces) {
                if (
                    (face.Portal is null) ||
                    !catalog.TryFind(
                    placementId: placement.Id,
                    faceName: face.Face,
                    out var row
                ) ||
                    !WorldFacePortalPolicy.TryAperture(
                    aperture: out var aperture,
                    crossingFloor: crossingFloor,
                    row: in row
                )
                ) {
                    continue;
                }

                if (WorldFaceRegion.Sweep(
                    aperture: aperture!,
                    from: body.FixedPosition,
                    to: body.FixedPosition
                ).Inside) {
                    instance.PortalOccupancy.SeedInside(
                        placementId: placement.Id,
                        faceName: face.Face,
                        seat: seat
                    );
                }
            }
        }
    }
    // An arriving traveler's own occupancy, latched at the instant it lands rather than discovered by the
    // next scan. The mapped isometry sets a traveler down against its counterpart's threshold, and a Spawn
    // arrival can land a seat inside any door the spawn point happens to sit in front of; either way, the
    // body did not walk in, so its first scan there must not read as an entry edge. A degenerate segment
    // (the landing collapses previous to current — WorldBody.Pose) makes the swept test the point test, so
    // the region's own Inside answer is exactly what the next scan would latch.
    // The same seed for an arrival this host did not itself land: a traveler committed into one of this process's
    // instances over the wire reaches the destination through the escrow, never through PublishCommittedTransfer,
    // so the commit-time seed there covers colocated arrivals only. The escrow's own border admission is written by
    // the destination for both topologies, which is what makes this reachable at all.
    private void SeedFederatedArrivalOccupancy(WorldInstance instance) {
        for (var seat = 0; (seat < instance.Server.Population.Capacity); seat++) {
            var key = (instance.Name, seat);

            if (!instance.Server.TryTransferArrivalBorder(
                bodyIndex: seat,
                border: out var border
            )) {
                _ = m_seededArrivals.Remove(key: key);

                continue;
            }

            if (
                m_seededArrivals.TryGetValue(
                key: key,
                value: out var seeded
            ) &&
                string.Equals(
                a: seeded,
                b: border,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }

            m_seededArrivals[key] = border;
            SeedArrivalOccupancy(
                instance: instance,
                seat: seat
            );
        }
    }
    private static ulong StableJunctionSeed(string authority, int entity, int generation) {
        var hash = Puck.Maths.Fnv1aHash.Create();

        WorldDeterministicHash.AddUtf8(
            hash: ref hash,
            value: authority
        );
        hash.Add(value: unchecked((uint)entity));
        hash.Add(value: unchecked((uint)generation));
        return hash.Value;
    }
    // A body that is neither a local seat nor an admitted peer has no external driver: the world's own authored
    // program moves it, so it travels as Principal.World, which holds no grant row at all. Console would also
    // resolve here and would carry the table's only Drive/all — authority over every seat and peer besides.
    private static Principal TravelPrincipal(WorldServer server, int slot) =>
        ((slot < server.Population.LocalSeatCount)
            ? Principal.Seat(slot: slot)
            : (server.Population.IsAdmittedPeer(bodyIndex: slot)
                ? server.Population.PeerPrincipal(index: slot)
                : Principal.World
        ));
    // Leave(source) with its pose captured before the detach discards it — the abort-restoration half of an
    // atomic body transfer. Never player.leave <slot> instance:<name> / ReapIfEmpty / ApplySession(Leave):
    // those are destructive (park-with-grace still advances a parked body, and ReapIfEmpty would retire the
    // source out from under a transfer still in flight) — see WorldPopulation.TryDetachSeatForTransfer. The
    // Drive/leave standing re-check here is defensive: ApplyTransfer's own pre-check loop already proved it
    // for every still-active member immediately before this runs, and is never load-bearing on its own.
    private static bool TryDetachAndCaptureMember(WorldInstance source, ulong transferId, int sourceSlot, string sourceName, Principal actingPrincipal, out WorldIdentity? profile, out Vector3 bodyColor, out FixedVector3 position, out FixedQ4816 yaw, out WorldBodyTransferState dynamicState, out WorldTargetDesignation[] designations, out WorldPeerEventEntry? peer, out IReadOnlyList<WorldAdmissionGrant> admissionGrants, out IReadOnlyList<WorldGrant> sourceGrants) {
        var captured = source.Server.ExecuteAuthorityOperation(operation: () => {
            var success = TryDetachAndCaptureMemberCore(
                actingPrincipal: actingPrincipal,
                admissionGrants: out var capturedAdmissionGrants,
                bodyColor: out var capturedBodyColor,
                designations: out var capturedDesignations,
                dynamicState: out var capturedState,
                peer: out var capturedPeer,
                position: out var capturedPosition,
                profile: out var capturedProfile,
                source: source,
                sourceGrants: out var capturedSourceGrants,
                sourceName: sourceName,
                sourceSlot: sourceSlot,
                transferId: transferId,
                yaw: out var capturedYaw
            );

            return (Success: success, Profile: capturedProfile, BodyColor: capturedBodyColor, Position: capturedPosition, Yaw: capturedYaw, State: capturedState, Designations: capturedDesignations, Peer: capturedPeer, AdmissionGrants: capturedAdmissionGrants, SourceGrants: capturedSourceGrants);
        });

        profile = captured.Profile;
        bodyColor = captured.BodyColor;
        position = captured.Position;
        yaw = captured.Yaw;
        dynamicState = captured.State;
        designations = captured.Designations;
        peer = captured.Peer;
        admissionGrants = captured.AdmissionGrants;
        sourceGrants = captured.SourceGrants;
        return captured.Success;
    }
    private static bool TryDetachAndCaptureMemberCore(WorldInstance source, ulong transferId, int sourceSlot, string sourceName, Principal actingPrincipal, out WorldIdentity? profile, out Vector3 bodyColor, out FixedVector3 position, out FixedQ4816 yaw, out WorldBodyTransferState dynamicState, out WorldTargetDesignation[] designations, out WorldPeerEventEntry? peer, out IReadOnlyList<WorldAdmissionGrant> admissionGrants, out IReadOnlyList<WorldGrant> sourceGrants) {
        profile = null;
        bodyColor = default;
        position = default;
        yaw = default;
        dynamicState = default;
        designations = [];
        peer = null;
        admissionGrants = [];
        sourceGrants = [];

        if (((uint)sourceSlot) >= ((uint)source.Server.Population.Capacity)) {
            if (source.Server.Output.HasNarrationSink) {
                source.Server.Output.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: refused (body:{sourceSlot} out of range in '{sourceName}')]"
                );
            }

            return false;
        }

        if (
            !source.Server.Population.IsActive(index: sourceSlot) ||
            (source.Server.Population.EntryBody(index: sourceSlot) is not { } body)
        ) {
            if (source.Server.Output.HasNarrationSink) {
                source.Server.Output.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: refused ({TravellerName(index: sourceSlot, localSeatCount: source.Server.Population.LocalSeatCount)} is not active in '{sourceName}')]"
                );
            }

            return false;
        }

        // A rigid kit's mass/inertia and momentum are meaningless without the source authority's own contact field
        // and manifold state; carrying one across is out of scope, refused by name here rather than transferred
        // silently as an inert avatar.
        if (body.IsRigid) {
            if (source.Server.Output.HasNarrationSink) {
                source.Server.Output.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: refused ({TravellerName(index: sourceSlot, localSeatCount: source.Server.Population.LocalSeatCount)} in '{sourceName}' wears a rigid kit — cross-world transfer of a rigid body is not supported)]"
                );
            }

            return false;
        }

        // A carried rigid body's pose is derived from the carrier's own frame every tick (WorldBody.Carry.cs) —
        // there is no destination authority for it to keep deriving from once the carrier lands elsewhere, and the
        // rigid-kit refusal above already blocks transferring the passenger directly. Refuse the carrier's own
        // transfer instead of silently dropping or orphaning what it holds.
        if (body.Carrying is not null) {
            if (source.Server.Output.HasNarrationSink) {
                source.Server.Output.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: refused ({TravellerName(index: sourceSlot, localSeatCount: source.Server.Population.LocalSeatCount)} in '{sourceName}' is carrying body:{body.Carrying} — cross-world transfer while carrying is not supported)]"
                );
            }

            return false;
        }

        if (!AllowsLeave(
            server: source.Server,
            principal: actingPrincipal,
            slot: sourceSlot,
            denial: out var leaveDenial
        )) {
            if (source.Server.Output.HasNarrationSink) {
                source.Server.Output.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: refused ({actingPrincipal.Describe()} cannot leave '{sourceName}' {TravellerName(index: sourceSlot, localSeatCount: source.Server.Population.LocalSeatCount)} — {leaveDenial})]"
                );
            }

            return false;
        }

        if (source.Server.DetachForTransfer(
            slot: sourceSlot,
            transferId: transferId
        ) is not { } detached) {
            if (source.Server.Output.HasNarrationSink) {
                source.Server.Output.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: refused ({TravellerName(index: sourceSlot, localSeatCount: source.Server.Population.LocalSeatCount)} in '{sourceName}' has no body to transfer)]"
                );
            }

            return false;
        }

        profile = detached.Profile;
        bodyColor = detached.BodyColor;
        position = detached.Position;
        yaw = detached.Yaw;
        dynamicState = detached.DynamicState;
        designations = detached.Designations;
        peer = detached.Peer;
        admissionGrants = detached.AdmissionGrants;
        sourceGrants = detached.SourceGrants;
        return true;
    }

    /// <summary>Drains every queued transfer at this host's one fixed point in its per-tick driving sequence —
    /// <c>WorldSimulation</c>/<c>HeadlessWorldSimulation</c> call this before stepping the boot instance or any other
    /// instance this tick (mirroring where <c>WorldServer.DrainPendingOps</c> sits relative to the rest of
    /// <c>WorldServer.Step</c>'s own body). An adjacency arrival carries the source step's consumed-through
    /// engine-time fence: destination topology resolves before population advance, and any overlapping destination
    /// step skips that traveler without evaluating input, motion, or dynamic contact a second time.</summary>
    public void DrainPendingTransfers() {
        ReconcileInDoubtTransfers();

        // A Retry refusal deliberately re-enqueues the same transfer. Drain only the batch that existed at this
        // tick's entry: consuming a retry again in this same loop would spin forever inside the host act and prevent
        // the authority from reaching Server.Step—the refused body (and every unrelated body) would appear frozen.
        // FIFO is preserved, and the retried request becomes the next tick's ordinary first-class attempt.
        var batchCount = m_pendingTransfers.Count;

        for (var index = 0; ((index < batchCount) && m_pendingTransfers.TryDequeue(result: out var transfer)); index++) {
            WorldNarrationScope.Current = transfer.SourceInstance;

            try {
                ApplyTransfer(transfer: in transfer);
            } finally {
                WorldNarrationScope.Current = null;
            }
        }
    }
    /// <summary>Queues a same-process transfer for this host's next <see cref="DrainPendingTransfers"/> call —
    /// <c>world.transfer</c> is the only caller today. Enqueuing never fails: every check that can refuse (an
    /// unknown or unstartable instance, an out-of-range/empty/absent source seat, no free destination seat, a denied
    /// Drive grant) runs at drain time, so a refusal is reported once, at the same fixed point the transfer would
    /// otherwise have applied at — exactly like a rejected <see cref="Server.WorldServer"/> mutation.</summary>
    /// <param name="sourceInstance">The console-facing name of the instance the seat(s) currently occupy.</param>
    /// <param name="scope">Whether this moves one named seat or the source's whole active local-seat set.</param>
    /// <param name="sourceSlot">The source instance's 0-based local seat — ignored when <paramref name="scope"/> is
    /// <see cref="TransferScope.Party"/> (the member set is read live at drain time instead).</param>
    /// <param name="destination">How the destination instance resolves — see <see cref="TransferDestination"/>.</param>
    /// <param name="actingPrincipal">The principal that submitted the transfer — threaded unchanged through both the
    /// leave-side Drive check and the destination's own <c>ApplySession(Join)</c> for every member, so each
    /// arrival's authority is attributed to the same principal that left rather than a principal this door
    /// invents.</param>
    /// <param name="resolvedDestinationRow">The destination row a <see cref="WorldSessionResolver.TryResolve"/> call
    /// proved <paramref name="frozenCohortSlots"/> against — see <see cref="PendingTransfer.ResolvedDestinationRow"/>.
    /// Omit for a non-resolver transfer (console <c>world.transfer</c>'s raw forms).</param>
    /// <param name="frozenCohortSlots">The exact local-seat slots the resolve proved — see
    /// <see cref="PendingTransfer.FrozenCohortSlots"/>. Omit for a non-resolver transfer.</param>
    /// <param name="frozenScopeKey">The scope key the resolve produced — see
    /// <see cref="PendingTransfer.FrozenScopeKey"/>. Omit for a non-resolver transfer.</param>
    /// <param name="frozenGenerationId">The resolver-issued generation id the resolve produced — see
    /// <see cref="PendingTransfer.FrozenGenerationId"/>. Omit for a non-resolver transfer.</param>
    /// <param name="explicitTransferId">An explicit transfer id to carry instead of minting a fresh one — the
    /// retry/idempotence verification seam (console <c>world.transfer</c>'s <c>transfer:&lt;id&gt;</c> token only; a
    /// diegetic portal crossing never supplies this). Omit to mint a fresh, deterministically-ordered id.</param>
    /// <param name="testForceJoinRefusalOrdinal">Test-only — see <see cref="PendingTransfer.TestForceJoinRefusalOrdinal"/>.
    /// Omit outside verification.</param>
    /// <param name="arrival">Where each landed member's own pose lands — see <see cref="PendingTransfer.Arrival"/>.
    /// Omit for the ordinary spawn arrival (console <c>world.transfer</c>'s own form).</param>
    /// <param name="counterpart">The destination document's border placementId/face a <c>Mapped</c> arrival maps
    /// onto — see <see cref="PendingTransfer.Counterpart"/>. Omit for the ordinary spawn arrival.</param>
    /// <param name="adjacencyCounterpart">The destination document's reciprocal adjacency row for an automatic
    /// ownership handoff. Omit for portal and console transfers.</param>
    /// <param name="sourceCrossingPoint">The crossing seat's own swept boundary point — see
    /// <see cref="PendingTransfer.SourceCrossingPoint"/>. Omit outside an adjacency crossing.</param>
    /// <param name="sourceFrame">The source boundary or portal face frame — see
    /// <see cref="PendingTransfer.SourceFrame"/>. Omit for the ordinary spawn arrival.</param>
    /// <param name="continuum">The source-step interval captured by an adjacency scan. Omit for portals and spawns.</param>
    /// <param name="holdSeconds">The binding lease duration authored by the source world.</param>
    /// <param name="fullPolicy">Whether a full refusal remains retryable.</param>
    /// <param name="partyAllOrNothing">Whether the cohort binds as one transaction.</param>
    /// <param name="borderCapacity">The optional capacity authored on the crossed border.</param>
    /// <param name="border">The stable border identity used for destination admission accounting.</param>
    /// <returns>The transfer id this call's queued crossing will carry (freshly minted unless
    /// <paramref name="explicitTransferId"/> was supplied) — so a caller that wants to echo or later retry it has the
    /// value without re-deriving the enqueue order itself.</returns>
    public ulong EnqueueTransfer(string sourceInstance, TransferScope scope, int sourceSlot, TransferDestination destination, Principal actingPrincipal, WorldDestination? resolvedDestinationRow = null, IReadOnlyList<int>? frozenCohortSlots = null, string? frozenScopeKey = null, ulong? frozenGenerationId = null, ulong? explicitTransferId = null, int? testForceJoinRefusalOrdinal = null, WorldPortalArrival arrival = WorldPortalArrival.Spawn, string? counterpart = null, string? adjacencyCounterpart = null, FixedVector3 sourceCrossingPoint = default, WorldFaceFrame? sourceFrame = null, WorldContinuumTrajectory? continuum = null, double holdSeconds = 2.0, WorldTransferFullPolicy fullPolicy = WorldTransferFullPolicy.Retry, bool partyAllOrNothing = true, int? borderCapacity = null, string? border = null) {
        var transferId = (explicitTransferId ?? MintTransferId(sourceInstance: sourceInstance));

        m_pendingTransfers.Enqueue(item: new PendingTransfer(
            SourceInstance: sourceInstance,
            Scope: scope,
            SourceSlot: sourceSlot,
            Destination: destination,
            ActingPrincipal: actingPrincipal,
            ResolvedDestinationRow: resolvedDestinationRow,
            FrozenCohortSlots: frozenCohortSlots,
            FrozenScopeKey: frozenScopeKey,
            FrozenGenerationId: frozenGenerationId,
            TransferId: transferId,
            TestForceJoinRefusalOrdinal: testForceJoinRefusalOrdinal,
            Arrival: arrival,
            Counterpart: counterpart,
            AdjacencyCounterpart: adjacencyCounterpart,
            SourceCrossingPoint: sourceCrossingPoint,
            SourceFrame: sourceFrame,
            Continuum: continuum,
            HoldSeconds: holdSeconds,
            FullPolicy: fullPolicy,
            PartyAllOrNothing: partyAllOrNothing,
            BorderCapacity: borderCapacity,
            Border: (border ?? "transfer")
        ));

        return transferId;
    }

    /// <summary>How a queued transfer's destination instance is resolved at drain time — see
    /// <see cref="TransferDestination"/> for the per-case payload and <see cref="TryResolveDestination"/> for the
    /// resolution itself.</summary>
    public enum TransferLifetime {
        /// <summary>The target must already be running under a given name (<c>world.instance.start</c> first) — the
        /// original, step-1 form. Refused by name when no instance answers to it.</summary>
        Existing,

        /// <summary>A brand-new instance, deterministically named from a site plus this host's per-site draw counter
        /// (see <see cref="MintFreshInstanceName"/>) — a fresh transfer is a new draw roll for that destination.
        /// Reaped like any other transient instance once its last occupant leaves (never retained).</summary>
        Fresh,

        /// <summary>A stable, caller-named instance: started from the destination document if not already running,
        /// else reused as-is. Retained (see <see cref="m_retainedInstances"/>) from the moment a transfer resolves
        /// it — two transfers naming the same persistent instance are two doors into one place, and the second must
        /// find the first traveler's instance still standing even if it is momentarily empty.</summary>
        Persistent,

        /// <summary>A name already computed by <see cref="WorldSessionResolver.TryResolve"/> — started from the
        /// destination document if not already running, else reused as-is, exactly like <see cref="Persistent"/>,
        /// but retained only when the resolved destination's own <see cref="WorldDestinationDurability"/> is
        /// <see cref="WorldDestinationDurability.Persisted"/> (see <see cref="TransferDestination.Retain"/>) — an
        /// Ephemeral-durability resolution reaps normally through the ordinary <see cref="ReapIfEmpty"/> rule the
        /// moment its occupancy hits zero, which is what lets <see cref="WorldSessionResolver.NotifyInstanceRetired"/>
        /// observe the generation actually ending (docs/architecture/worlds.md "Durability, scope and generation").</summary>
        Resolved,
    }
    /// <summary>A queued transfer's destination, as the console verb expressed it — resolved to a live
    /// <see cref="WorldInstance"/> exactly once per transfer by <see cref="TryResolveDestination"/> (a <c>party</c>
    /// transfer's whole member set shares that one resolution, so a <see cref="TransferLifetime.Fresh"/> destination
    /// mints its name once for the whole party, never once per body).</summary>
    public readonly record struct TransferDestination {
        private TransferDestination(TransferLifetime lifetime, string? name, string? documentPath, string? site, bool retain, string? authority) {
            Lifetime = lifetime;
            Name = name;
            DocumentPath = documentPath;
            Site = site;
            Retain = retain;
            Authority = authority;
        }

        /// <summary>An operator-selected remote authority for this run, overriding the document endpoint.</summary>
        public string? Authority { get; }
        /// <summary>The world document to start the instance from if it is not already running — set for
        /// <see cref="TransferLifetime.Fresh"/>, <see cref="TransferLifetime.Persistent"/>, and
        /// <see cref="TransferLifetime.Resolved"/>.</summary>
        public string? DocumentPath { get; }
        /// <summary>How this destination resolves.</summary>
        public TransferLifetime Lifetime { get; }
        /// <summary>The caller-named instance name — set for <see cref="TransferLifetime.Existing"/>,
        /// <see cref="TransferLifetime.Persistent"/>, and <see cref="TransferLifetime.Resolved"/>,
        /// <see langword="null"/> for <see cref="TransferLifetime.Fresh"/> (whose name is minted, never named).</summary>
        public string? Name { get; }
        /// <summary>Whether a <see cref="TransferLifetime.Resolved"/> destination is retained through an occupancy
        /// dip to zero (see <see cref="m_retainedInstances"/>) — ignored for every other lifetime, which each carry
        /// their own fixed retention rule.</summary>
        public bool Retain { get; }
        /// <summary>The site identifier a <see cref="TransferLifetime.Fresh"/> destination's name is drawn under —
        /// see <see cref="MintFreshInstanceName"/>.</summary>
        public string? Site { get; }

        /// <summary>An already-running instance named <paramref name="name"/> — refused at resolve time if none
        /// answers to it.</summary>
        public static TransferDestination Existing(string name) => new(
            authority: null,
            documentPath: null,
            lifetime: TransferLifetime.Existing,
            name: name,
            retain: false,
            site: null
        );
        /// <summary>A brand-new instance, deterministically named from <paramref name="site"/>'s draw counter and
        /// started from <paramref name="documentPath"/>.</summary>
        public static TransferDestination Fresh(string site, string documentPath) => new(
            authority: null,
            documentPath: documentPath,
            lifetime: TransferLifetime.Fresh,
            name: null,
            retain: false,
            site: site
        );
        /// <summary>A stable instance named <paramref name="name"/> — reused if already running, else started from
        /// <paramref name="documentPath"/>.</summary>
        public static TransferDestination Persistent(string name, string documentPath) => new(
            authority: null,
            documentPath: documentPath,
            lifetime: TransferLifetime.Persistent,
            name: name,
            retain: false,
            site: null
        );
        /// <summary>The normal boot composition routed through a remote authority selected for this run.</summary>
        public static TransferDestination Remote(string name, string documentPath, string authority) => new(
            authority: authority,
            documentPath: documentPath,
            lifetime: TransferLifetime.Resolved,
            name: name,
            retain: true,
            site: null
        );
        /// <summary>A name already computed by <see cref="WorldSessionResolver.TryResolve"/> — reused if already
        /// running, else started from <paramref name="documentPath"/>; retained through an occupancy dip to zero
        /// only when <paramref name="retain"/> (the resolved destination's own durability being Persisted).</summary>
        public static TransferDestination Resolved(string name, string documentPath, bool retain) => new(
            authority: null,
            documentPath: documentPath,
            lifetime: TransferLifetime.Resolved,
            name: name,
            retain: retain,
            site: null
        );
    }
    /// <summary>Which of a source instance's local seats a queued transfer moves — see
    /// <see cref="PendingTransfer.Scope"/>.</summary>
    public enum TransferScope {
        /// <summary>One body by its source-authority index, including peers and creatures.</summary>
        Body,

        /// <summary>The source instance's whole active local-seat set (0..<see cref="Server.WorldPopulation.LocalSeatCount"/>-1),
        /// computed from live state at drain time, landing together in one destination — never one instance per
        /// member.</summary>
        Party,
    }

    /// <summary>One same-process body (or party) transfer queued for this host's one fixed drain point (see
    /// <see cref="DrainPendingTransfers"/>) — captured at enqueue time as the request shape only. Every live-state
    /// check (both instances still running, the source seat(s) still active, a free destination seat, Drive
    /// authority) runs at drain time against whatever state that tick actually holds, mirroring
    /// <see cref="Server.WorldServer"/>'s own pending-ops FIFO (compose/validate at apply, never at submit).</summary>
    /// <param name="SourceInstance">The console-facing name of the instance the seat(s) currently occupy.</param>
    /// <param name="Scope">Whether this moves one named seat or the source's whole active local-seat set.</param>
    /// <param name="SourceSlot">The source instance's 0-based body index — ignored when <paramref name="Scope"/> is
    /// <see cref="TransferScope.Party"/>.</param>
    /// <param name="Destination">How the destination instance resolves.</param>
    /// <param name="ActingPrincipal">The principal that submitted the transfer.</param>
    /// <param name="ResolvedDestinationRow">The destination row a <see cref="WorldSessionResolver.TryResolve"/> call
    /// proved this cohort against at scan time — populated only for <see cref="TransferLifetime.Resolved"/> (a
    /// diegetic portal crossing; <c>Puck.World.WorldInstanceCommandModule</c>'s console <c>world.transfer</c>
    /// never touches the resolver at all). This is what lets <see cref="ApplyTransfer"/>
    /// re-verify the frozen scope key and, if the cached instance no longer runs, re-resolve through the resolver
    /// rather than guessing.</param>
    /// <param name="FrozenCohortSlots">The exact local-seat slots the resolve proved — a <c>body</c> crossing's own
    /// single entering seat, or a <c>party</c> crossing's whole active local-seat set as it stood at scan time.
    /// <see cref="ApplyTransfer"/> applies to exactly this frozen set rather than recomputing it live at drain
    /// (a cohort TOCTOU fix) — a member no longer active by drain time still refuses that
    /// member's own move by name, exactly as before; nothing here changes who is allowed to travel, only where the
    /// set of "who" is read from. <see langword="null"/> for a non-resolver transfer.</param>
    /// <param name="FrozenScopeKey">The scope key the resolve produced — re-derived live from
    /// <see cref="FrozenCohortSlots"/>' still-active members immediately before this transfer applies; a mismatch
    /// refuses the whole transfer (membership drifted between scan and drain, so the frozen proof no longer holds).
    /// <see langword="null"/> for a non-resolver transfer.</param>
    /// <param name="FrozenGenerationId">The resolver-issued generation id the resolve produced — carried purely for
    /// <see cref="ApplyTransfer"/>'s own tape narration (<see cref="WorldReplayTape.NoteTransfer"/>); never
    /// re-verified (the scope-key re-derivation above is what proves the resolution still holds).
    /// <see langword="null"/> for a non-resolver transfer.</param>
    /// <param name="TransferId">The transfer id this particular queued crossing carries — minted deterministically at
    /// enqueue time (docs/architecture/worlds.md) unless a caller supplied one explicitly (console
    /// <c>world.transfer</c>'s <c>transfer:&lt;id&gt;</c> token, the retry/idempotence verification seam). Threaded
    /// through every echo this transfer produces and checked against <see cref="m_appliedTransferIds"/> before
    /// anything else at drain time.</param>
    /// <param name="TestForceJoinRefusalOrdinal">Test-only (see <see cref="ApplyTransfer"/>'s own remarks on why a
    /// live document-authored join refusal is unreachable once reservation pre-checks capacity and destination Drive
    /// standing): when set, the N-th (1-based, in <see cref="FrozenCohortSlots"/>/member order) member's destination
    /// join is forced to refuse once, exercising the abort/rollback path directly. Only ever set by console
    /// <c>world.transfer</c>'s <c>forcejoinrefusal:&lt;n&gt;</c> token — never by a diegetic portal crossing.</param>
    /// <param name="Arrival">Where each landed member's own pose lands (see <c>Puck.World.WorldPlacementPortal.Arrival</c>).
    /// Default <c>Spawn</c> — the destination's ordinary seat spawn point, unchanged for a non-resolver transfer
    /// (console <c>world.transfer</c> never authors mapped arrival).</param>
    /// <param name="Counterpart">The destination document's border placementId/face a <c>Mapped</c> arrival maps
    /// onto (see <c>Puck.World.WorldPortalCounterpart</c>) — resolved against the destination's own delivered
    /// definition at drain time, never at scan time. <see langword="null"/> for <c>Spawn</c>.</param>
    /// <param name="AdjacencyCounterpart">The destination document's reciprocal adjacency row for an automatic
    /// ownership handoff. Mutually exclusive with <paramref name="Counterpart"/>.</param>
    /// <param name="SourceCrossingPoint">The exact world point the crossing seat's swept segment met the source
    /// boundary. Consumed only as the mapped continuum cursor's origin, never as the arrival anchor —
    /// <see cref="WorldFrameIsometry.MapArrival"/> maps a traveler from wherever it actually stands. Default for
    /// <c>Spawn</c> and for portal furniture, which carries no continuum.</param>
    /// <param name="SourceFrame">The source boundary or portal face's own frame, captured at scan so later document
    /// mutation cannot move it. <see langword="null"/> for an ordinary spawn arrival.</param>
    /// <param name="Continuum">The source-step interval and geometric cursor captured by an adjacency scan. Null
    /// for portal furniture and ordinary spawn transfers.</param>
    /// <param name="HoldSeconds">The authored binding lease duration.</param>
    /// <param name="FullPolicy">The authored full-border retry policy.</param>
    /// <param name="PartyAllOrNothing">Whether the cohort binds as one transaction.</param>
    /// <param name="BorderCapacity">The optional authored capacity for this border.</param>
    /// <param name="Border">The stable source border identity used by destination admission.</param>
    /// <param name="ScopeProofAlreadyVerified">Internal split-party marker: the parent already re-verified the
    /// frozen cohort's membership proof before creating one-member transactions against its one resolved target.</param>
    /// <param name="Attempt">How many times a retryable capacity refusal has already re-queued this crossing.</param>
    /// <param name="RecoveryDestinationName">The captured resolver destination used only for outcome narration
    /// after restart. Recovery never resolves or re-enqueues this already-detached crossing.</param>
    private readonly record struct PendingTransfer(
        string SourceInstance,
        TransferScope Scope,
        int SourceSlot,
        TransferDestination Destination,
        Principal ActingPrincipal,
        WorldDestination? ResolvedDestinationRow,
        IReadOnlyList<int>? FrozenCohortSlots,
        string? FrozenScopeKey,
        ulong? FrozenGenerationId,
        ulong TransferId,
        int? TestForceJoinRefusalOrdinal,
        WorldPortalArrival Arrival,
        string? Counterpart,
        string? AdjacencyCounterpart,
        FixedVector3 SourceCrossingPoint,
        WorldFaceFrame? SourceFrame,
        WorldContinuumTrajectory? Continuum,
        double HoldSeconds,
        WorldTransferFullPolicy FullPolicy,
        bool PartyAllOrNothing,
        int? BorderCapacity,
        string Border,
        bool ScopeProofAlreadyVerified = false,
        int Attempt = 0,
        string? RecoveryDestinationName = null
    );
    // One landed member's captured state — enough to restore it exactly at the source if the transfer
    // aborts after it already joined the destination. Body color/position/yaw/dynamic state/designations are all
    // captured before TryDetachSeatForTransfer runs (which discards them). See
    // WorldPopulation.RestoreDetachedSeat for why position+yaw alone reconstructs a grounded-model body's
    // orientation bit-for-bit, WorldBodyTransferState for what dynamic state carries (velocity, dash
    // overlay, in-flight timed presses), and WorldPopulation.CaptureDesignations for why designations need
    // their own separate capture.
    private readonly record struct LandedMember(
        int SourceSlot,
        int TargetSlot,
        WorldIdentity? Profile,
        Vector3 BodyColor,
        FixedVector3 Position,
        FixedQ4816 Yaw,
        WorldBodyTransferState DynamicState,
        WorldTargetDesignation[] Designations,
        WorldPeerEventEntry? Peer,
        IReadOnlyList<WorldAdmissionGrant> AdmissionGrants,
        IReadOnlyList<WorldGrant> SourceGrants,
        Principal SourcePrincipal,
        WorldMobilityIdentity Mobility,
        byte FollowedSeatMask = 0
    );
    private sealed record InDoubtTransfer(
        PendingTransfer Transfer,
        WorldPeerCall? TargetAuthority,
        string SourceAuthority,
        string TargetName,
        bool Spawned,
        ulong SourceDeadlineTick,
        List<LandedMember> Landed,
        List<WorldTransferCommitMember> CommitMembers,
        int MemberCount,
        bool RollbackOnly = false,
        bool ConflictingCommitReported = false,
        string? RecoveryAuthority = null,
        string? RecoveryEndpoint = null,
        WorldDefinition? RecoveryDefinition = null,
        bool CommitConfirmed = false
    ) {
        public bool PublicationFailureReported { get; set; }
        // The settlement this source wrote ahead of acknowledging or restoring the cohort.
        public bool SettlementRecorded { get; set; }
        public bool SettlementFailureReported { get; set; }
    }
}
