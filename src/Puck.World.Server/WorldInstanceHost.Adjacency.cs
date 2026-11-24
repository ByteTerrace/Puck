using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    private static void CloseAdjacency(WorldInstance instance, in WorldAdjacencyFaceHit hit, string reason) {
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
    // A portal crossing its destination refused for good holds each member it sent from the face it walked into, until
    // the member stands clear of the face (ScanPortalFace). A portal transfer's border is "<placement>/<face>"; an
    // adjacency's carries its counterpart, and a verb's transfer has no source frame.
    private void HoldRefusedPortalCrossing(in PendingTransfer transfer, IReadOnlyList<int> members) {
        if (
            (transfer.AdjacencyCounterpart is not null) ||
            (transfer.SourceFrame is null) ||
            (transfer.Border.LastIndexOf(value: '/') is not (> 0 and var cut)) ||
            !m_instances.TryGetValue(
            key: transfer.SourceInstance,
            value: out var instance
        ) ||
            (instance is null)
        ) {
            return;
        }

        var placementId = transfer.Border[..cut];
        var faceName = transfer.Border[(cut + 1)..];

        foreach (var slot in members) {
            instance.PortalOccupancy.Refuse(faceName: faceName, placementId: placementId, seat: slot);
        }
    }
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
            adjacencies: instance.Server.Configuration.Adjacencies,
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
    // The authored `unavailable: closed` treatment, applied wherever a crossing fails for good: press the authored
    // channel once and name the refusal, and on a wall clamp the body one raw fixed-point unit inside the boundary it
    // tried to leave through and drop its velocity. Clamping is what makes a wall's refusal terminal — a body left
    // standing beyond the ownership threshold re-satisfies the same edge on the very next scan
    // (WorldAdjacencyRegion.Sweep answers Crossed for a body already outside), so a refusal that does not move it is
    // a refusal per tick.
    //
    // A boundary the body leaves through vertically gets no clamp, because closed terrain has no meaning under a
    // falling body: holding it level at the plane with its velocity dropped leaves it standing on nothing, in mid
    // air, with no way back to the terrain it left. It stays the source authority's problem and keeps falling, which
    // its own floor or kill plane answers for. The clamp would not make the refusal terminal there either — gravity
    // re-crosses the vertical deadband within a few ticks.
    private static void CloseAdjacencyEdge(WorldInstance instance, string adjacencyName, string? onUnavailable, int seat, in WorldFaceFrame frame, FixedVector3 boundaryPoint, string reason) {
        if (instance.Server.Population.EntryBody(index: seat) is not { } body) {
            return;
        }

        if (frame.IsYawOnly) {
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
        }

        body.ClearPendingContinuum();
        var binding = "engine-only";

        if (
            (onUnavailable is { } channel) &&
            body.Channels.TryGetOrdinal(
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
        if (instance.Server.Output.HasNarrationSink) {
            instance.Server.Output.Narrate(
                channel: "world.adjacency",
                text: $"[world.adjacency: '{instance.Name}/{adjacencyName}' seat {(seat + 1)} CLOSED ({reason}); response={binding}]"
            );
        }
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
            definition: () => instance.Server.Configuration,
            submissions: instance.Link,
            observe: instance.Server.AttachSink,
            adjacencies: () => instance.Server.Adjacencies,
            nextInputTick: () => instance.Server.NextInputTick,
            clockOwnedHere: true,
            drivesDevice: index => instance.Server.Engagement.DrivesDevice(bodyIndex: index)
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
    private static string TravellerName(int index, int localSeatCount) => ((index < localSeatCount)
        ? $"seat {(index + 1)}"
        : $"body:{index}");
    // Whether the world's own program authors this body, the one pairing a World principal may leave a body under.
    private static bool IsWorldAuthoredBody(WorldServer server, int slot) =>
        ((slot >= server.Population.LocalSeatCount) && !server.Population.IsAdmittedPeer(bodyIndex: slot));
    // Adjacencies are ownership topology, not portal furniture. Every local body is tested against the authored
    // invisible rectangles after its authority step; geometry selects the earliest edge. A genuine equal-parameter
    // junction tie is distributed by a stable hash of the entity generation and authority identity over the sorted
    // eligible edges—not document order. Each body transfers independently, which is what keeps a melee straddling a
    // seam live instead of sweeping unrelated party members through it.
    private void ScanInstanceAdjacencies(WorldInstance instance, bool decides) {
        if (instance.Server.Configuration.Adjacencies is not { Count: > 0 }) {
            return;
        }

        var population = instance.Server.Population;
        var heldSeats = HeldCrossingSeats(instance: instance);

        for (var seat = 0; (seat < population.Capacity); seat++) {
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
                if (instance.Server.Output.HasNarrationSink) {
                    instance.Server.Output.Narrate(
                        channel: "world.adjacency",
                        text: $"[world.adjacency: '{instance.Name}' seat {(seat + 1)} crossing HELD (transfer={heldTransferId} is queued or in flight); no further crossing is minted until it resolves]"
                    );
                }
            }
        }

        var candidates = WorldAdjacencyOwnership.Sweep(
            held: heldSeats.ContainsKey,
            pendingOnly: false,
            server: instance.Server
        );

        for (var seat = 0; (seat < candidates.Length); seat++) {
            if (candidates[seat] is not { Count: > 0 } hits) {
                population.EntryBody(index: seat)?.ClearPendingContinuum();
                continue;
            }

            // A rider crosses with the body it rides (EnqueueAdjacencyTransfer queues it behind that body), never on
            // its own while that body stands here.
            if ((population.EntryBody(index: seat)?.MountedOn is { } ridden) && population.IsActive(index: ridden)) {
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
                if (instance.Server.Output.HasNarrationSink) {
                    instance.Server.Output.Narrate(
                        channel: "world.adjacency",
                        text: $"[world.adjacency: '{instance.Name}/{winner.Adjacency.Name}' body:{seat} continuum safety-clamped after {pending.BoundaryEvents} boundary events]"
                    );
                }
                continue;
            }

            if (decides) {
                EnqueueAdjacencyTransfer(
                    hit: in winner,
                    instance: instance
                );
            }
        }
    }
    private void ScanInstanceBoundaries(WorldInstance instance) {
        instance.Server.ExecuteAuthorityAction(state: (Host: this, Instance: instance),
            operation: static state => state.Host.ScanInstanceBoundariesCore(instance: state.Instance));
    }
    // An instance driving its own tape takes every crossing from the tape: each recorded in-place crossing re-applies at
    // its own position among the tick's authority inputs (WorldReplaySnapshot.ApplyRecordedTick), as the offline
    // re-drive applies it, and a tape that departs travelers does not drive live (WorldReplayTape.TryBeginDrive). A crossing the scan minted as well would move the body a second time, so a replaying
    // instance's scan still observes every face (its occupancy latch follows the driven bodies and is current when live
    // input returns) but mints nothing.
    private void ScanInstanceBoundariesCore(WorldInstance instance) {
        var decides = (instance.Tape is not { Mode: WorldReplayMode.Replaying });

        ScanInstanceAdjacencies(
            decides: decides,
            instance: instance
        );
        ScanInstancePortals(
            decides: decides,
            instance: instance
        );
    }

    // One instance's own portal scan: every placement's every portal-carrying face, against every body that can
    // travel (TryTraveller, the set the seam scan reads). Placement/face iteration order is the document's own declared
    // order; body order is ascending 0..Capacity-1 — deterministic within one process run, though this scan's queue
    // sits outside the boot-only replay tape (see m_freshCounters).
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
    private PortalEdgeHit?[]? m_portalWinners;

    private void ScanInstancePortals(WorldInstance instance, bool decides) {
        SeedFederatedArrivalOccupancy(instance: instance);

        var definition = instance.Server.Configuration;
        var population = instance.Server.Population;
        var catalog = WorldFaceCatalog.For(definition: definition);
        // Every tick scans every instance, so the per-body winners reuse one buffer; a scan entered from inside
        // another takes a fresh one.
        var winners = (((m_portalWinners is { } held) && (held.Length >= population.Capacity))
            ? held
            : new PortalEdgeHit?[population.Capacity]);

        m_portalWinners = null;
        Array.Clear(
            array: winners,
            index: 0,
            length: population.Capacity
        );

        // Indexed loops: this runs every tick, and enumerating the lists through their interfaces allocates.
        var placements = definition.Placements;

        for (var row = 0; (row < placements.Count); row++) {
            var placement = placements[row];

            if (
                (placement is null) ||
                (placement.FaceSources is not { Count: > 0 } faces)
            ) {
                continue;
            }

            for (var index = 0; (index < faces.Count); index++) {
                var face = faces[index];

                if (
                    (face.Portal is not { } portal) ||
                    !catalog.TryFindAperture(
                    aperture: out var aperture,
                    definition: definition,
                    faceName: face.Face,
                    placementId: placement.Id
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

        List<PortalEdgeHit>? hits = null;

        for (var seat = 0; (seat < population.Capacity); seat++) {
            if (winners[seat] is { } hit) {
                (hits ??= []).Add(item: hit);
            }
        }

        m_portalWinners = winners;

        if (
            decides &&
            (hits is not null)
        ) {
            ResolveAndEnqueueCoalescedTransfers(
                hits: hits,
                instance: instance
            );
        }
    }
    // One portal face against every body that can travel. The face's geometry is the shared per-revision derivation
    // (WorldFaceCatalog) — the SAME frame rendering draws and arrival maps through, so a rotated or shape-offset door
    // triggers exactly where it is drawn. The region test sweeps the segment from the body's previous scan origin
    // (WorldBody.FixedPreviousPosition) to its current one, so no speed, rate, or motion program can tunnel a body
    // through a face between two samples.
    //
    // A body whose segment's bounds miss the face's bounds (WorldFaceAperture.Box.Bounds) can neither end inside the face
    // nor cross it, so it takes the outside observation without the region test, and only a body that holds a latch
    // somewhere is observed at all: the scan costs a bounds compare per body and face, and a region test only near a
    // face.
    //
    // A door authored `open` fires only while its condition holds: closed, every body reads as outside it, so a body that
    // stands in it when it closes keeps the side it is on and a body walking into it meets the frame alone. A door's
    // `gate` reads the traveller's own cell of its row once a crossing fires; a body it refuses passes the frame without
    // crossing, and its latch keeps that one crossing from firing again until it leaves the face.
    private void ScanPortalFace(WorldInstance instance, WorldPopulation population, WorldPlacement placement, WorldPlacementFace face, WorldPlacementPortal portal, WorldFaceAperture aperture, PortalEdgeHit?[] winners) {
        var bounds = ((aperture is WorldFaceAperture.Box box) ? box.Bounds : FixedAabb.Everywhere);
        var occupancy = instance.PortalOccupancy;
        var server = instance.Server;

        var closed = ((portal.Open is WorldPlacementResponseCondition.StateCondition open) && !open.Holds(arena: server.Arena, definition: server.Configuration, engineTick: server.CompletedEngineTicks, tick: server.CompletedTick));

        population.PortalClosures.Set(closed: closed, face: face.Face, placementId: placement.Id);
        if (closed) {
            for (var seat = 0; (seat < population.Capacity); seat++) {
                if (occupancy.Holds(seat: seat)) {
                    _ = occupancy.Observe(placementId: placement.Id, faceName: face.Face, seat: seat, inside: false, crossed: false);
                }
            }

            return;
        }

        for (var seat = 0; (seat < population.Capacity); seat++) {
            if (!WorldAdjacencyOwnership.TryTraveller(
                body: out var body,
                index: seat,
                population: population
            )) {
                occupancy.Forget(
                    placementId: placement.Id,
                    faceName: face.Face,
                    seat: seat
                );
                occupancy.Release(placementId: placement.Id, faceName: face.Face, seat: seat);

                continue;
            }

            var from = body.FixedPreviousPosition;
            var to = body.FixedPosition;

            // A body this face's destination refused for good asks again only once it has stood a body's radius clear of
            // the face, so one walk into the glass is one attempt however often its origin crosses the region's edge.
            if (occupancy.IsRefused(placementId: placement.Id, faceName: face.Face, seat: seat)) {
                if (bounds.Grow(margin: body.BroadphaseRadius).Contains(point: to)) {
                    continue;
                }
                occupancy.Release(placementId: placement.Id, faceName: face.Face, seat: seat);
            }
            if (!bounds.Intersects(other: FixedAabb.Empty.Enclose(point: from).Enclose(point: to))) {
                if (occupancy.Holds(seat: seat)) {
                    _ = occupancy.Observe(placementId: placement.Id, faceName: face.Face, seat: seat, inside: false, crossed: false);
                }

                continue;
            }

            var crossing = WorldFaceRegion.Sweep(
                aperture: aperture,
                from: from,
                to: to
            );
            var fired = occupancy.Observe(
                placementId: placement.Id,
                faceName: face.Face,
                seat: seat,
                inside: crossing.Inside,
                crossed: crossing.Crossed
            );

            if (!fired) {
                continue;
            }
            if ((portal.Gate is { } gate) && !gate.Admits(arena: server.Arena, bodyIndex: seat, definition: server.Configuration, engineTick: server.CompletedEngineTicks, tick: server.CompletedTick)) {
                if (server.Output.HasNarrationSink) {
                    server.Output.Narrate(
                        channel: "world.portal",
                        text: $"[world.portal: '{instance.Name}/{placement.Id}/{face.Face}' gate '{gate.State}' refuses {TravellerName(index: seat, localSeatCount: population.LocalSeatCount)}; it passes the frame without crossing]"
                    );
                }

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
    public bool HasOnwardRoute(WorldServer source, in WorldMobilityIdentity mobility) => m_forwardedBodies.ContainsKey(key: (source, mobility.Incarnation));
    private bool TryFindOnwardRoute(WorldServer source, in WorldMobilityIdentity mobility, out ForwardedBody route, out string reason) {
        if (m_forwardedBodies.TryGetValue(
            key: (source, mobility.Incarnation),
            value: out route
        )) {
            reason = string.Empty;

            return true;
        }

        reason = $"traveler {mobility.Incarnation} has no committed onward route";

        return false;
    }
    /// <inheritdoc/>
    public ValueTask<(WorldAuthorityRouteDescription? Route, string Reason)> DescribeForwardingAsync(WorldServer source, WorldMobilityIdentity mobility) =>
        TryFindOnwardRoute(source: source, mobility: in mobility, reason: out var reason, route: out var route)
            ? route.Authority.DescribeRouteAsync() : new((null, reason));
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

        var rateHz = instance.Server.Configuration.SimulationRateHz;
        var stopped = (rateHz <= 0);

        status = new WorldInstanceRateStatus(
            RateHz: rateHz,
            Stopped: stopped,
            Paused: instance.IsPaused,
            StepWidthTicks: (stopped
            ? (ulong?)null
            : FixedTickConversion.PerRate(ratePerSecond: ((uint)rateHz))),
            CompletedTicks: instance.CompletedTicks
        );
        reason = string.Empty;

        return true;
    }
    /// <summary>Read-back for <c>world.adjacencies</c>: whether one started authority's own document came from a
    /// composed image this process already held, rather than one that start merged itself — the fact naming a
    /// shard's shared basis, or a corner destination reached a second time, as <c>composed=shared</c> instead of
    /// <c>composed=fresh</c>.</summary>
    /// <param name="name">The instance name.</param>
    /// <param name="shared">Whether the instance's document came from a held image.</param>
    /// <returns><see langword="true"/> when the instance is running and its outcome was recorded.</returns>
    public bool TryDescribeDocumentSharing(string name, out bool shared) =>
        m_documentShared.TryGetValue(
            key: name,
            value: out shared
        );
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
        if (!TryFindOnwardRoute(
            mobility: in mobility,
            reason: out reason,
            route: out var route,
            source: source
        )) {
            return false;
        }

        return route.Authority.TryForwardIntent(
            reason: out reason,
            submission: in submission
        );
    }
    /// <inheritdoc/>
    public bool TryForwardSubmission(WorldServer source, in WorldMobilityIdentity mobility, WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: completion);

        if (!TryFindOnwardRoute(
            mobility: in mobility,
            reason: out reason,
            route: out var route,
            source: source
        )) {
            return false;
        }

        var traveler = mobility;

        return route.Authority.TryForwardSubmission(
            completion: result => {
                // An accepted leave retires the forwarded traveler once its answer arrives.
                if (
                    (payload is WorldSubmissionPayload.Session { Value: SessionRequest.Leave }) &&
                    (result is WorldSubmissionResult.Session { Reply.Accepted: true })
                ) {
                    RetireForwardedTraveler(mobility: in traveler);
                }

                completion(obj: result);
            },
            operationId: operationId,
            payload: RebindForwardedPayload(
                payload: payload,
                bodyIndex: route.BodyIndex
            ),
            reason: out reason
        );
    }

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
