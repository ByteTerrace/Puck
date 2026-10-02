using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    private byte CaptureFollowedSeats(string sourceInstance, int sourceSlot) {
        byte mask = 0;

        for (var slot = 0; (slot < m_seats.SeatCount); slot++) {
            if (
                (m_seats.RoutedEndpoint(slot: slot)?.Identity == sourceInstance) &&
                (m_seats.RoutedEntity(slot: slot).Index == sourceSlot)
            ) {
                mask |= checked((byte)(1 << slot));
            }
        }
        return mask;
    }
    private bool TryPublishCommittedTransfer(InDoubtTransfer pending) {
        if (!m_instances.ContainsKey(key: pending.Transfer.SourceInstance)) { return false; }
        try {
            var transfer = pending.Transfer;

            PublishCommittedTransfer(
                in transfer,
                pending.TargetAuthority!.Value,
                pending.TargetName,
                pending.Landed,
                pending.CommitMembers
            );
            return true;
        } catch (Exception exception) when ((exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)) {
            if (!pending.PublicationFailureReported) {
                pending.PublicationFailureReported = true;
                if (m_narration.HasNarrationSink) {
                    m_narration.Narrate(
                        channel: "world.transfer",
                        text: $"[world.transfer: transfer={pending.Transfer.TransferId} PUBLICATION-PENDING — commit confirmed; source completion retained ({exception.GetType().Name}: {exception.Message})]"
                    );
                }
            }
            return false;
        }
    }
    private void PublishCommittedTransfer(in PendingTransfer transfer, WorldPeerCall targetAuthority, string targetName, List<LandedMember> landed, List<WorldTransferCommitMember> commits) {
        // Copied for the same reason ApplyTransfer copies it — an `in` parameter cannot be captured into Narrate's
        // deferred formatter.
        var transferId = transfer.TransferId;

        _ = m_instances.TryGetValue(
            key: transfer.SourceInstance,
            value: out var sourceInstance
        );

        // A traveler set down on a door's own threshold reads as a fresh entry edge on the destination's
        // next scan and is bounced straight back, so every face an arriving body already stands inside is
        // latched rather than discovered as a crossing. Seeded here, for the whole cohort at once, rather
        // than per member as each lands — the landing loop can still abort, and its unwind restores bodies,
        // not latches, so a per-member seed would outlive its own member. Commit-time seeding has nothing for
        // a rollback to undo.
        for (var memberOrdinal = 0; (memberOrdinal < landed.Count); memberOrdinal++) {
            var member = landed[memberOrdinal];
            // A live peer's control follows its body to whichever authority now owns it. The colocated arm is
            // registered on the same rule as the socket arm: without it, an in-process onward crossing leaves the
            // client's intents and submissions naming a body this source no longer has.
            if (
                (member.Peer is { Source.IsLive: true }) &&
                (sourceInstance is not null)
            ) {
                IWorldForwardedAuthority? onward = null;
                var onwardSlot = member.TargetSlot;

                if (targetAuthority.Remote is { } forwardedAuthority) {
                    // Recovery opens a fresh remote link, with no reservation cache. The confirmed commit's
                    // retained member is the credential's authority: a slot-keyed cache could also name a later
                    // occupant by the time an ambiguous handoff is resolved.
                    var credential = new WorldRemoteRouteCredential(
                        BodyIndex: member.TargetSlot,
                        SourceAuthority: sourceInstance.Server.AuthorityIdentity,
                        Mobility: member.Mobility.Advance()
                    );

                    onward = new WorldRemoteForwardedAuthority(
                        authority: forwardedAuthority,
                        credential: credential
                    );
                    onwardSlot = credential.BodyIndex;
                } else if (targetAuthority.Local is { } localTarget) {
                    onward = new WorldLocalForwardedAuthority(
                        server: localTarget.Server,
                        endpoint: (localTarget.Server.Definition.Host.Authority ?? EndpointFor(instance: localTarget).Identity),
                        sourceAuthority: sourceInstance.Server.AuthorityIdentity,
                        mobility: member.Mobility.Advance()
                    );
                }

                if (onward is not null) {
                    var key = (sourceInstance.Server, member.Mobility.Incarnation);

                    if (m_forwardedBodies.TryGetValue(
                        key: key,
                        value: out var superseded
                    )) {
                        (superseded.Authority as IDisposable)?.Dispose();
                    }

                    m_forwardedBodies[key] = new ForwardedBody(
                        Authority: new WorldDeferredForwardedAuthority(
                            destination: onward.DescribeForCheckpoint(),
                            initial: onward
                        ),
                        BodyIndex: onwardSlot
                    );
                }
            }

            if (targetAuthority.Local is { } target) {
                SeedArrivalOccupancy(
                    instance: target,
                    seat: member.TargetSlot
                );
            }
        }

        // COMMIT: the whole cohort's join is certain, so the CLIENT-side state that mirrors and ROUTES a seat catches
        // up here — and only here, after every member's outcome is known, so an aborted member is never seen to have
        // left at all (see LandedMember's own remarks). The transfer's authoritative body work is already complete;
        // this route decides where subsequent presentation and input submissions follow it.
        for (var landedOrdinal = 0; (landedOrdinal < landed.Count); landedOrdinal++) {
            var member = landed[landedOrdinal];
            // Any local roster slot whose authority claim currently names
            // (transfer.SourceInstance, member.SourceSlot) moves WITH this member — unconditional across
            // boot<->anywhere and anywhere<->anywhere, the ONE new write this stage adds. At most one roster slot
            // ever matches (a followed seat's own location is exactly its own presenting body), but the walk costs
            // O(4) regardless of which instance is source or destination.
            // A previous attempt may already have published one or more routes. Such a participant still follows
            // this member even though its endpoint no longer names the source; never vacate its roster on retry.
            var followed = (member.FollowedSeatMask != 0);

            for (var followedSlot = 0; (followedSlot < m_seats.SeatCount); followedSlot++) {
                // The seat ceiling is the host's, not the world's: a world declaring fewer local seats never
                // publishes a route for the ones it did not declare, and asking such a slot for its entity is a
                // refusal rather than an absence. The endpoint answers null for an unrouted slot, so it is what
                // decides whether this slot has anything to follow.
                var locationEndpoint = m_seats.RoutedEndpoint(slot: followedSlot);

                if (
                    ((member.FollowedSeatMask & (1 << followedSlot)) == 0) ||
                    (locationEndpoint is null) ||
                    !string.Equals(
                    a: locationEndpoint.Identity,
                    b: transfer.SourceInstance,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    continue;
                }

                var locationEntity = m_seats.RoutedEntity(slot: followedSlot);

                if (locationEntity.Index != member.SourceSlot) {
                    continue;
                }

                WorldAuthorityEndpoint endpoint;
                WorldAuthorityRouteDescription? initialRoute = null;
                WorldRemoteAuthority? routedAuthority = null;

                if (targetAuthority.Remote is { } remoteTarget) {
                    var routeCredential = new WorldRemoteRouteCredential(
                        BodyIndex: member.TargetSlot,
                        SourceAuthority: sourceInstance!.Server.AuthorityIdentity,
                        Mobility: member.Mobility.Advance()
                    );

                    try {
                        if (remoteTarget.TryDescribeRoute(
                            credential: in routeCredential,
                            reason: out var routeReason,
                            route: out var describedRoute
                        )) {
                            initialRoute = describedRoute;
                        } else {
                            if (m_narration.HasNarrationSink) {
                                m_narration.Narrate(
                                    channel: "world.continuum",
                                    text: $"[world.continuum: committed transfer={transferId} route seed unavailable for body:{member.TargetSlot} ({routeReason})]"
                                );
                            }
                        }
                    } catch (Exception exception) when ((exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException)) {
                        if (m_narration.HasNarrationSink) {
                            m_narration.Narrate(
                                channel: "world.continuum",
                                text: $"[world.continuum: committed transfer={transferId} route seed transport failed for body:{member.TargetSlot} ({exception.GetType().Name}: {exception.Message})]"
                            );
                        }
                    }

                    var trackedSlot = followedSlot;
                    // One route wrapper per local traveler, not per crossing. Its stable mobility credential follows
                    // the committed onward route recursively, so replacing the wrapper here would tear down a live
                    // intent stream between ownership epochs and manufacture an unavailable/release window. Keying
                    // this cache by transfer id leaked one route, pump and observer for every A↔B seam crossing.
                    var routeName = $"$traveler/{m_machineId:N}/{trackedSlot}";

                    if (!m_remoteAuthorities.TryGetValue(
                        key: routeName,
                        value: out var routeAuthority
                    )) {
                        WorldAuthorityEndpoint? publishedEndpoint = null;

                        routeAuthority = new WorldRemoteAuthority(
                            endpoint: remoteTarget.Endpoint,
                            placeholder: remoteTarget.Definition,
                            security: sourceInstance!.Federation.Authenticator,
                            observerAuthority: sourceInstance.Federation.Subject,
                            submissionAuthority: remoteTarget,
                            submissionCredential: routeCredential,
                            initialRoute: initialRoute,
                            applicationStopping: m_applicationStopping,
                            narrationHub: m_narration,
                            routeChanged: route => {
                                if (
                                    (publishedEndpoint is not null) &&
                                    (m_seats.RoutedEndpoint(slot: trackedSlot) is { } expectedEndpoint) &&
                                    ReferenceEquals(
                                    objA: expectedEndpoint,
                                    objB: publishedEndpoint
                                )
                                ) {
                                    publishedEndpoint.SeedRoute(route: in route);

                                    // The traveler may have gone on through turned doors of its own: the seat's view
                                    // turns by the turn the route accumulated since this host last turned it.
                                    if (!m_seats.TryUpdateRoutedEntity(
                                        slot: trackedSlot,
                                        expectedEndpoint: expectedEndpoint,
                                        replacement: route.Entity
                                    )) {
                                        return;
                                    }

                                    var turn = m_routedTurns.Follow(
                                        slot: trackedSlot,
                                        travelTurn: route.TravelTurn
                                    );

                                    if (turn != FixedQ4816.Zero) {
                                        m_seats.CrossView(
                                            slot: trackedSlot,
                                            yawDelta: turn,
                                            yawReference: route.Definition.Views.SeatControl.YawReference
                                        );
                                    }
                                }
                            }
                        );
                        m_remoteAuthorities[routeName] = routeAuthority;
                        publishedEndpoint = EndpointFor(
                            authority: routeAuthority,
                            identity: routeName,
                            seed: initialRoute
                        );
                        routedAuthority = routeAuthority;
                    }
                    endpoint = EndpointFor(
                        identity: routeName,
                        authority: routeAuthority
                    );
                } else if (targetAuthority.Local is { } localEndpointTarget) {
                    endpoint = EndpointFor(instance: localEndpointTarget);
                    // Parity with the federated arm above: the endpoint carries the arrival pose before the route
                    // naming it is published, so no frame observes the route without an anchor.
                    var localRoute = localEndpointTarget.Server.ExecuteAuthorityOperation(operation: () =>
                        WorldLocalForwardedAuthority.DescribeRoute(
                        server: localEndpointTarget.Server,
                        endpoint: endpoint.Identity,
                        bodyIndex: member.TargetSlot
                    ));

                    endpoint.SeedRoute(route: in localRoute);
                    initialRoute = localRoute;
                } else {
                    throw new InvalidOperationException(message: "committed transfer has no target authority");
                }
                var routedEntity = (initialRoute?.Entity ?? new WorldEntityAddress(
                    Authority: (targetAuthority.Local?.Server.AuthorityIdentity ?? endpoint.Authority),
                    Index: member.TargetSlot,
                    Generation: (targetAuthority.Local?.Server.Population.Generation(index: member.TargetSlot) ?? 0)
                ));

                // Held before the route is published, so a route the destination describes at once turns nothing.
                m_routedTurns.Hold(
                    slot: followedSlot,
                    travelTurn: commits[landedOrdinal].TravelTurn
                );
                m_seats.PublishRoute(
                    endpoint: endpoint,
                    entity: routedEntity,
                    slot: followedSlot
                );

                // A mapped arrival turned the body by the turn between the landed member's departure yaw and the
                // commit member's arrival yaw, at the same ordinal. The seat's view turns with it, so it looks along
                // what the door's window showed.
                if (commits[landedOrdinal].HasMappedArrival) {
                    m_seats.CrossView(
                        slot: followedSlot,
                        yawDelta: WorldFrameIsometry.TurnBetween(
                            after: commits[landedOrdinal].YawRadians,
                            before: member.Yaw
                        ),
                        yawReference: (targetAuthority.Local?.Server.Definition ?? targetAuthority.Remote!.Definition).Views.SeatControl.YawReference
                    );
                }
                // A delayed commit acknowledgement can outlive onward crossings. Consume the latest route even if
                // its one-time observation notification arrived before PublishRoute made this seat follow it.
                routedAuthority?.RepublishObservedRoute();
            }

            // Scoped to the BOOT instance on each side independently, because that is the only instance a
            // local client mirrors — a transfer between two non-boot instances touches neither, and an
            // unscoped write would clear or fill a boot seat belonging to somebody who never moved. A
            // followed seat's local participant does not vacate when it departs boot — it relocates (the
            // router publish above already records exactly where), so the roster's own occupied/device-bound
            // state stays as it was through the whole trip, and WorldClient.SubmitAuthorityIntents keeps
            // reading a live seat rather than a vacated one. A followed seat returning to boot symmetrically
            // skips OccupySeat below: the slot was never vacated, so it is already occupied under the same
            // participant that left.
            if (
                !followed &&
                (member.SourceSlot < sourceInstance!.Server.Population.LocalSeatCount) &&
                string.Equals(
                a: transfer.SourceInstance,
                b: BootInstanceName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                // The roster's own seat-vacated fact — the SAME one player.leave emits, from a second producer.
                _ = m_seats.VacateSeat(slot: member.SourceSlot);
            }

            // The mirror fact, for a traveler landing in the instance the client mirrors.
            if (
                !followed &&
                (member.TargetSlot < targetAuthority.Definition.Population.LocalSeats) &&
                string.Equals(
                a: targetName,
                b: BootInstanceName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                _ = m_seats.OccupySeat(
                    slot: member.TargetSlot,
                    profile: member.Profile
                );
            }

            // The accepted transfer echoes its full decision on STDOUT — departed source seat, arrived target seat,
            // the transfer id, and the arrival pose read from the target's OWN snapshot (PlayerWhere.Index is the
            // 0-based body index, identical to TargetSlot) — so a caller reads the outcome here rather than
            // inferring it from a later world.instance.seats.
            var arrival = ((targetAuthority.Local is { } localTarget)
                ? localTarget.Server.Answer(query: new WorldQuery.PlayerWhere(Index: member.TargetSlot))
                : new QueryAnswer(Text: $"remote authority {targetAuthority.Remote!.Endpoint} body:{member.TargetSlot}")
            );

            var arrivedTransferId = transfer.TransferId;
            var arrivedSource = transfer.SourceInstance;

            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    stream: WorldNarrationStream.Output,
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={arrivedTransferId} '{arrivedSource}' {TravellerName(index: member.SourceSlot, localSeatCount: sourceInstance!.Server.Population.LocalSeatCount)} departed -> '{targetName}' {TravellerName(index: member.TargetSlot, localSeatCount: targetAuthority.Definition.Population.LocalSeats)} arrived{((member.Profile is not null)
                    ? $" as {member.Profile.Id}"
                    : " (anonymous)")} — {arrival.Text}]"
                );
            }
        }

    }
}
