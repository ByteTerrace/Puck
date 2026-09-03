using Puck.Hosting;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    private static void CloseAdjacency(WorldInstance instance, in AdjacencyEdgeHit hit, string reason) {
        CloseAdjacencyEdge(
            instance: instance,
            adjacencyName: hit.Adjacency.Name.Value,
            onUnavailable: hit.Adjacency.OnUnavailable,
            seat: hit.Seat,
            frame: hit.Frame,
            boundaryPoint: hit.Frame.PointAt(
                u: hit.SeamU,
                v: hit.SeamV
            ),
            reason: reason
        );
    }
    // A drain-time refusal against an adjacency border, routed back to that border's own authored treatment. Portal
    // furniture has no such treatment: a refused portal crossing leaves the traveler standing where it was, and the
    // occupancy latch already stops the same door firing again.
    private void CloseAdjacencyAfterRefusal(in PendingTransfer transfer, string reason) {
        if (
            (transfer.AdjacencyCounterpart is null) ||
            (transfer.SourceFrame is not { } frame) ||
            !m_instances.TryGetValue(
            key: transfer.SourceInstance,
            value: out var instance
        ) ||
            (instance is null)
        ) {
            return;
        }

        const string Prefix = "adjacency/";
        var adjacencyName = (transfer.Border.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        )
            ? transfer.Border[Prefix.Length..]
            : transfer.Border
        );
        var onUnavailable = WorldDefinitionRows.FindAdjacency(
            adjacencies: instance.Server.Definition.Adjacencies,
            name: adjacencyName
        )?.OnUnavailable;

        foreach (var slot in (transfer.FrozenCohortSlots ?? [transfer.SourceSlot])) {
            CloseAdjacencyEdge(
                instance: instance,
                adjacencyName: adjacencyName,
                onUnavailable: onUnavailable,
                seat: slot,
                frame: in frame,
                boundaryPoint: transfer.SourceCrossingPoint,
                reason: reason
            );
        }
    }
    // The authored `unavailable: closed` treatment, applied wherever a crossing fails for good: clamp the body one
    // raw fixed-point unit inside the boundary it tried to leave through, drop outward velocity, press the authored
    // channel once, and name the refusal. Clamping is what makes the refusal terminal — a body left standing beyond
    // the ownership threshold re-satisfies the same edge on the very next scan (WorldAdjacencyRegion.Sweep answers
    // Crossed for a body already outside), so a refusal that does not move it is a refusal per tick.
    private static void CloseAdjacencyEdge(WorldInstance instance, string adjacencyName, string? onUnavailable, int seat, in WorldFaceFrame frame, FixedVector3 boundaryPoint, string reason) {
        if (instance.Server.Population.EntryBody(index: seat) is not { } body) {
            return;
        }

        var inward = FixedQ4816.FromRawBits(value: 1L);

        body.Pose(
            position: (boundaryPoint - (frame.Normal * inward)),
            yawRadians: body.FixedYaw,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        body.SetArrivalVelocity(
            planarVelocity: FixedVector3.Zero,
            verticalVelocity: FixedQ4816.Zero
        );
        body.ClearPendingContinuum();
        var binding = "engine-only";

        if (
            (onUnavailable is { } channel) &&
            instance.Server.Population.Channels.TryGetOrdinal(
            name: channel,
            ordinal: out var ordinal
        )
        ) {
            body.PressChannel(
                ordinal: ordinal,
                value: FixedQ4816.One
            );
            binding = $"channel:{channel}";
        }
        Console.Error.WriteLine(value: $"[world.adjacency: '{instance.Name}/{adjacencyName}' seat {(seat + 1)} CLOSED ({reason}); response={binding}]");
    }
    private WorldAuthorityEndpoint EndpointFor(WorldInstance instance) {
        if (m_authorityEndpoints.TryGetValue(
            key: instance.Name,
            value: out var endpoint
        )) {
            return endpoint;
        }

        endpoint = new WorldAuthorityEndpoint(
            identity: instance.Name,
            definition: () => instance.Server.Definition,
            submissions: instance.Link,
            observe: instance.Server.AttachSink,
            adjacencies: () => instance.Server.Adjacencies,
            nextInputTick: () => instance.Server.NextInputTick,
            clockOwnedHere: true
        );
        m_authorityEndpoints[instance.Name] = endpoint;
        return endpoint;
    }
    private WorldAuthorityEndpoint EndpointFor(string identity, WorldRemoteAuthority authority, WorldAuthorityRouteDescription? seed = null) {
        if (m_authorityEndpoints.TryGetValue(
            key: identity,
            value: out var endpoint
        )) {
            if (seed is { } existingSeed) {
                endpoint.SeedRoute(route: in existingSeed);
            }
            return endpoint;
        }

        endpoint = new WorldAuthorityEndpoint(
            identity: identity,
            definition: () => authority.Definition,
            submissions: authority.Link,
            observe: authority.AttachSink,
            adjacencies: static () => null,
            nextInputTick: () => authority.NextInputTick,
            clockOwnedHere: false,
            seed: seed
        );
        m_authorityEndpoints[identity] = endpoint;
        return endpoint;
    }
    // Whether the world's own program authors this body, the one pairing a World principal may leave a body under.
    private static bool IsWorldAuthoredBody(WorldServer server, int slot) =>
        ((slot >= server.Population.LocalSeatCount) && !server.Population.IsAdmittedPeer(bodyIndex: slot));
    // Adjacencies are ownership topology, not portal furniture. Every local body is tested against the authored
    // invisible rectangles after its authority step; geometry selects the earliest edge. A genuine equal-parameter
    // junction tie is distributed by a stable hash of the entity generation and authority identity over the sorted
    // eligible edges—not document order. Each body transfers independently, which is what keeps a melee straddling a
    // seam live instead of sweeping unrelated party members through it.
    private void ScanInstanceAdjacencies(WorldInstance instance, bool resolveOnly = false) {
        if (instance.Server.Definition.Adjacencies is not { Count: > 0 } adjacencies) {
            if (resolveOnly) {
                for (var index = 0; (index < instance.Server.Population.Capacity); index++) {
                    instance.Server.Population.EntryBody(index: index)?.ClearPendingContinuum();
                }
            }
            return;
        }

        var population = instance.Server.Population;
        var candidates = new List<AdjacencyEdgeHit>[population.Capacity];
        var heldSeats = HeldCrossingSeats(instance: instance);

        _ = WorldAdjacencyPolicy.TryReciprocalHysteresis(
            definition: instance.Server.Definition,
            depth: out var reciprocalHysteresis,
            reason: out _
        );
        _ = WorldAdjacencyPolicy.TryVerticalSettleDeadband(
            definition: instance.Server.Definition,
            depth: out var verticalSettleDeadband,
            reason: out _
        );

        for (var seat = 0; (!resolveOnly && (seat < population.Capacity)); seat++) {
            var announced = m_announcedCrossingHolds.TryGetValue(
                key: (instance.Name, seat),
                value: out var announcedTransferId
            );

            if (!heldSeats.TryGetValue(
                key: seat,
                value: out var heldTransferId
            )) {
                if (announced) {
                    _ = m_announcedCrossingHolds.Remove(key: (instance.Name, seat));
                }

                continue;
            }

            if (
                !announced ||
                (announcedTransferId != heldTransferId)
            ) {
                m_announcedCrossingHolds[(instance.Name, seat)] = heldTransferId;
                Console.Error.WriteLine(value: $"[world.adjacency: '{instance.Name}' seat {(seat + 1)} crossing HELD (transfer={heldTransferId} is queued or in flight); no further crossing is minted until it resolves]");
            }
        }

        foreach (var adjacency in adjacencies) {
            if (adjacency is null) {
                continue;
            }

            var frame = adjacency.Boundary.CompileFrame();
            var ownershipThreshold = WorldAdjacencyPolicy.OwnershipThreshold(
                frame: in frame,
                reciprocalHysteresis: reciprocalHysteresis,
                verticalSettleDeadband: verticalSettleDeadband
            );

            for (var seat = 0; (seat < population.Capacity); seat++) {
                if (
                    !population.IsActive(index: seat) ||
                    (population.EntryBody(index: seat) is not { } body)
                ) {
                    continue;
                }
                if (
                    resolveOnly &&
                    (body.PendingContinuum is null)
                ) {
                    continue;
                }
                if (
                    !resolveOnly &&
                    heldSeats.ContainsKey(key: seat)
                ) {
                    continue;
                }

                // A remotely committed arrival bypasses this host's PublishCommittedTransfer path, but escrow
                // retains the authenticated source border on the destination authority. Handoff occurs at the far
                // side of the boundary's own ownership threshold, so a mapped arrival starts at least that far
                // inside the new owner: a wall carries the reciprocal contact hysteresis, a floor/ceiling the much
                // smaller vertical settle deadband. Test both ends of this step: a genuine reversal may cross the
                // whole deadband in one tick and must not be stranded outside its owner merely because its settled
                // endpoint is outward again.
                if (
                    instance.Server.TryTransferArrivalBorder(
                    bodyIndex: seat,
                    border: out var arrivalBorder
                ) &&
                    string.Equals(
                    a: arrivalBorder,
                    b: $"adjacency/{adjacency.Counterpart}",
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    var previousOutward = FixedVector3.Dot(
                        left: (body.FixedPreviousPosition - frame.Origin),
                        right: frame.Normal
                    );
                    var outward = FixedVector3.Dot(
                        left: (body.FixedPosition - frame.Origin),
                        right: frame.Normal
                    );

                    if (
                        (previousOutward > -ownershipThreshold) &&
                        (outward > -ownershipThreshold)
                    ) {
                        continue;
                    }

                    _ = instance.Server.ClearTransferArrivalBorder(
                        bodyIndex: seat,
                        expectedBorder: arrivalBorder
                    );
                }

                var crossing = WorldAdjacencyRegion.Sweep(
                    frame: frame,
                    from: body.FixedPreviousPosition,
                    to: body.FixedPosition,
                    outwardThreshold: ownershipThreshold
                );

                if (!crossing.Crossed) {
                    continue;
                }

                (candidates[seat] ??= []).Add(item: new AdjacencyEdgeHit(
                    Adjacency: adjacency,
                    Seat: seat,
                    Frame: frame,
                    SeamU: crossing.SeamU,
                    SeamV: crossing.SeamV,
                    Parameter: crossing.Parameter
                ));
            }
        }

        for (var seat = 0; (seat < candidates.Length); seat++) {
            if (
                resolveOnly &&
                (population.EntryBody(index: seat)?.PendingContinuum is null)
            ) {
                continue;
            }
            if (candidates[seat] is not { Count: > 0 } hits) {
                population.EntryBody(index: seat)?.ClearPendingContinuum();
                continue;
            }

            if (resolveOnly) {
                continue;
            }

            var earliest = hits.Min(selector: static hit => hit.Parameter);
            var eligible = hits.Where(predicate: hit => (hit.Parameter == earliest))
                .OrderBy(
                keySelector: static hit => hit.Adjacency.Name.Value,
                comparer: StringComparer.Ordinal
            )
                .ToArray();
            var choice = ((eligible.Length == 1)
                ? 0
                : (int)(StableJunctionSeed(
                    authority: instance.Server.AuthorityIdentity,
                    entity: seat,
                    generation: population.Generation(index: seat)
                ) % ((ulong)eligible.Length))
            );
            var winner = eligible[choice];

            if (
                (population.EntryBody(index: seat)?.PendingContinuum is { } pending) &&
                (pending.BoundaryEvents >= WorldContinuumTrajectory.MaxBoundaryEvents)
            ) {
                var winnerFrame = winner.Frame;

                population.EntryBody(index: seat)!.ClampContinuum(
                    frame: in winnerFrame,
                    seamU: winner.SeamU,
                    seamV: winner.SeamV
                );
                Console.Error.WriteLine(value: $"[world.adjacency: '{instance.Name}/{winner.Adjacency.Name}' body:{seat} continuum safety-clamped after {pending.BoundaryEvents} boundary events]");
                continue;
            }

            EnqueueAdjacencyTransfer(
                hit: in winner,
                instance: instance
            );
        }
    }
    private void ScanInstanceBoundaries(WorldInstance instance) {
        instance.Server.ExecuteAuthorityOperation(operation: () => ScanInstanceBoundariesCore(instance: instance));
    }
    private void ScanInstanceBoundariesCore(WorldInstance instance) {
        ScanInstanceAdjacencies(instance: instance);
        ScanInstancePortals(instance: instance);
    }
    // One instance's own portal scan: every placement's every portal-carrying face, against every active local
    // seat. Placement/face iteration order is the document's own declared order; seat order is ascending
    // 0..LocalSeatCount-1 — deterministic within one process run, though this scan's queue sits outside the
    // boot-only replay tape (see m_freshCounters).
    //
    // One winner per seat: a step that crosses two doors resolves to the face with the earliest crossing
    // parameter, tie-broken by the face's own document identity (WorldFaceCrossingClaim), never by
    // dictionary enumeration order.
    //
    // Every surviving edge is gathered into `hits` first, never resolved/enqueued inline, because two
    // different edges in the same scan (two seats entering the same party-travel doorway together, or two
    // independent body-travel doors resolving the same destination/scope key) must land as one transfer
    // with one merged cohort. ResolveAndEnqueueCoalescedTransfers does the grouping once the whole scan is
    // in hand.
    private void ScanInstancePortals(WorldInstance instance) {
        SeedFederatedArrivalOccupancy(instance: instance);

        var definition = instance.Server.Definition;
        var population = instance.Server.Population;
        var catalog = WorldFaceCatalog.For(definition: definition);
        var crossingFloor = WorldFacePortalPolicy.CrossingFloor(definition: definition);
        var winners = new PortalEdgeHit?[population.LocalSeatCount];

        foreach (var placement in definition.Placements) {
            if (
                (placement is null) ||
                (placement.FaceSources is not { Count: > 0 } faces)
            ) {
                continue;
            }

            foreach (var face in faces) {
                if (
                    (face.Portal is not { } portal) ||
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
                    // A face with no aperture mapping is refused by name at validation, so reaching here means the
                    // document never declared the face at all — nothing to scan.
                    continue;
                }

                ScanPortalFace(
                    aperture: aperture!,
                    face: face,
                    instance: instance,
                    placement: placement,
                    population: population,
                    portal: portal,
                    winners: winners
                );
            }
        }

        var hits = new List<PortalEdgeHit>();

        foreach (var winner in winners) {
            if (winner is { } hit) {
                hits.Add(item: hit);
            }
        }

        if (hits.Count > 0) {
            ResolveAndEnqueueCoalescedTransfers(
                hits: hits,
                instance: instance
            );
        }
    }
    // One portal face against every local seat. The face's geometry is the shared per-revision derivation
    // (WorldFaceCatalog) — the SAME frame rendering draws and arrival maps through, so a rotated or shape-offset door
    // triggers exactly where it is drawn. The region test sweeps the segment from the body's previous scan origin
    // (WorldBody.FixedPreviousPosition) to its current one, so no speed, rate, or motion program can tunnel a body
    // through a face between two samples.
    private void ScanPortalFace(WorldInstance instance, WorldPopulation population, WorldPlacement placement, WorldPlacementFace face, WorldPlacementPortal portal, WorldFaceAperture aperture, PortalEdgeHit?[] winners) {
        for (var seat = 0; (seat < population.LocalSeatCount); seat++) {
            if (
                !population.IsActive(index: seat) ||
                (population.EntryBody(index: seat) is not { } body)
            ) {
                instance.PortalOccupancy.Forget(
                    placementId: placement.Id,
                    faceName: face.Face,
                    seat: seat
                );

                continue;
            }

            var crossing = WorldFaceRegion.Sweep(
                aperture: aperture,
                from: body.FixedPreviousPosition,
                to: body.FixedPosition
            );
            var fired = instance.PortalOccupancy.Observe(
                placementId: placement.Id,
                faceName: face.Face,
                seat: seat,
                inside: crossing.Inside,
                crossed: crossing.Crossed
            );

            if (!fired) {
                continue;
            }

            var claim = new WorldFaceCrossingClaim(
                PlacementId: placement.Id,
                FaceName: face.Face,
                Parameter: crossing.Parameter
            );

            if (
                (winners[seat] is { } standing) &&
                !claim.Outranks(other: standing.Claim)
            ) {
                continue;
            }

            winners[seat] = new PortalEdgeHit(
                Placement: placement,
                Face: face,
                Portal: portal,
                Seat: seat,
                Frame: crossing.Frame,
                SeamU: crossing.SeamU,
                SeamV: crossing.SeamV,
                Claim: claim
            );
        }
    }

    /// <inheritdoc/>
    public bool TryDescribeForwarding(WorldServer source, in WorldMobilityIdentity mobility, out WorldAuthorityRouteDescription routeDescription, out string reason) {
        if (!m_forwardedBodies.TryGetValue(
            key: (source, mobility.Incarnation),
            value: out var route
        )) {
            routeDescription = default;
            reason = $"traveler {mobility.Incarnation} has no committed onward route";
            return false;
        }

        return route.Authority.TryDescribeRoute(
            reason: out reason,
            route: out routeDescription
        );
    }
    /// <summary>Read-back for <c>world.rate</c>: one instance's declared rate, live schedule state, step width and
    /// completed ticks — the boot instance included, under its reserved name.</summary>
    /// <param name="name">The instance name.</param>
    /// <param name="status">The described status, when found.</param>
    /// <param name="reason">The refusal reason on failure (an unknown name only).</param>
    /// <returns><see langword="true"/> when the instance exists.</returns>
    public bool TryDescribeRate(string name, out WorldInstanceRateStatus status, out string reason) {
        if (!m_instances.TryGetValue(
            key: name,
            value: out var instance
        )) {
            status = default;
            reason = $"no instance named '{name}'";

            return false;
        }

        var rateHz = instance.Server.Definition.SimulationRateHz;
        var stopped = (rateHz <= 0);

        status = new WorldInstanceRateStatus(
            RateHz: rateHz,
            Stopped: stopped,
            Paused: instance.IsPaused,
            StepWidthTicks: (stopped
            ? (ulong?)null
            : EngineTicks.PerRate(ratePerSecond: ((uint)rateHz))),
            CompletedTicks: instance.CompletedTicks
        );
        reason = string.Empty;

        return true;
    }
    /// <summary>Finds the local roster seat currently following one concrete instance-local seat. This is the
    /// instance-addressed <c>player.leave</c> join: a raw instance leave must not bypass the roster/router half when
    /// the named body is the local traveler's current embodiment.</summary>
    public bool TryFindFollowedRosterSlot(string instanceName, int instanceSlot, out int rosterSlot) {
        for (var slot = 0; (slot < m_seats.SeatCount); slot++) {
            if (
                m_seats.IsOccupied(slot: slot) &&
                (m_seats.RoutedEndpoint(slot: slot) is { } endpoint) &&
                string.Equals(
                a: endpoint.Identity,
                b: instanceName,
                comparisonType: StringComparison.Ordinal
            ) &&
                (m_seats.RoutedEntity(slot: slot).Index == instanceSlot)
            ) {
                rosterSlot = slot;

                return true;
            }
        }

        rosterSlot = -1;

        return false;
    }
    /// <inheritdoc/>
    public bool TryForwardIntent(WorldServer source, in WorldMobilityIdentity mobility, in IntentSubmission submission, out string reason) {
        if (!m_forwardedBodies.TryGetValue(
            key: (source, mobility.Incarnation),
            value: out var route
        )) {
            reason = $"traveler {mobility.Incarnation} has no committed onward route";
            return false;
        }

        return route.Authority.TryForwardIntent(
            reason: out reason,
            submission: in submission
        );
    }
    /// <inheritdoc/>
    public bool TryForwardSubmission(WorldServer source, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        if (!m_forwardedBodies.TryGetValue(
            key: (source, mobility.Incarnation),
            value: out var route
        )) {
            result = null;
            reason = $"traveler {mobility.Incarnation} has no committed onward route";
            return false;
        }

        var accepted = route.Authority.TryForwardSubmission(
            payload: RebindForwardedPayload(
                payload: payload,
                bodyIndex: route.BodyIndex
            ),
            result: out result,
            reason: out reason
        );

        if (
            accepted &&
            (payload is WorldSubmissionPayload.Session { Value: SessionRequest.Leave }) &&
            (result is WorldSubmissionResult.Session { Reply.Accepted: true })
        ) {
            RetireForwardedTraveler(in mobility);
        }
        return accepted;
    }

    private readonly record struct AdjacencyEdgeHit(WorldAdjacency Adjacency, int Seat, WorldFaceFrame Frame, FixedQ4816 SeamU, FixedQ4816 SeamV, FixedQ4816 Parameter);
    // One edge-triggered portal hit, collected during a scan rather than acted on immediately — see
    // ScanInstancePortals' own remarks on why every hit in one scan is gathered before any of them resolves. Claim
    // carries the crossing parameter and the face's own identity, which is what decides a seat's ONE winner when its
    // step crosses several faces.
    private readonly record struct PortalEdgeHit(WorldPlacement Placement, WorldPlacementFace Face, WorldPlacementPortal Portal, int Seat, WorldFaceFrame Frame, FixedQ4816 SeamU, FixedQ4816 SeamV, WorldFaceCrossingClaim Claim);
    // The key includes the source door's complete mapping identity. Doors sharing a destination and scope therefore
    // retain their own arrival frames without relying on the document model's one-portal-per-face rule.
    private readonly record struct CoalescedPortalGroupKey(string DestinationName, string ScopeKey, string SourcePlacementId, string SourceFace, WorldPortalArrival Arrival, string? Counterpart);
    // One destination, scope, and source-door group accumulated before enqueueing a transfer.
    private sealed class CoalescedPortalGroup {
        // Captured when the source face is scanned, so later document mutation cannot move it.
        public required WorldPortalArrival Arrival { get; init; }
        public required string Border { get; init; }
        public required int? BorderCapacity { get; init; }
        public required string? Counterpart { get; init; }
        public required WorldDestination Destination { get; init; }
        public required WorldTransferFullPolicy FullPolicy { get; init; }
        public required double HoldSeconds { get; init; }
        public required bool PartyAllOrNothing { get; init; }
        public required string ReferenceDocument { get; init; }
        public TransferScope Scope { get; set; }
        public required WorldFaceFrame SourceFrame { get; init; }
        public required WorldPortalTravel Travel { get; init; }

        public readonly SortedSet<int> Slots = new();
        public readonly List<string> Descriptions = new();
    }
}
