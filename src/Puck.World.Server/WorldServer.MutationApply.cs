using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool ApplyDesignationCore(WorldDesignation designation, WorldPrincipal principal, bool knownSubject, int connectionId, long correlationId) {
        var sourceIndex = designation.EntityIndex;

        if (
            (((uint)sourceIndex) >= ((uint)m_population.Capacity)) ||
            (Body(index: sourceIndex) is not { } source)
        ) {
            return Refuse($"body:{sourceIndex} is not active");
        }
        if (!m_population.TryResolveTargetRegister(
            name: designation.Register,
            index: out var registerIndex
        )) {
            return Refuse($"register '{designation.Register}' is not declared");
        }

        var sourceSubject = GrantSubject.Body(index: sourceIndex);

        if (!knownSubject) {
            var drive = m_grants.Allows(
                capability: WorldCapability.Drive,
                principal: principal,
                subject: sourceSubject
            );

            if (!drive.IsAllowed) {
                return Refuse(
                    drive.DescribeRefusal(
                        actor: principal,
                        subject: sourceSubject.Describe(),
                        verb: "drive"
                    ),
                    denied: true
                );
            }

            var ownsBody = ((principal.Kind is PrincipalKind.Seat or PrincipalKind.Peer) && (principal.Index == sourceIndex));

            if (
                !ownsBody &&
                (principal.Kind != PrincipalKind.Console) &&
                (!m_grants.TryGetChannelReach(
                mask: out var reach,
                principal: principal,
                subject: sourceSubject
            )
                    || !reach.Contains(ordinal: m_population.TargetRegisters.ReachOrdinal(index: registerIndex)))
            ) {
                return Refuse(
                    $"{principal.Describe()} Drive reach does not include target register '{designation.Register}'",
                    denied: true
                );
            }
        }
        if (designation.Point is { } point) {
            if (!knownSubject) {
                var pointRegister = m_definition.TargetRegisters[registerIndex];
                var pointRange = WorldPopulation.EffectiveTargetValue(
                    body: source,
                    stateName: pointRegister.RangeState,
                    authoredMaximum: pointRegister.MaximumRange
                );
                var pointHalfAngle = WorldPopulation.EffectiveTargetValue(
                    body: source,
                    stateName: pointRegister.HalfAngleState,
                    authoredMaximum: pointRegister.MaximumHalfAngleDegrees
                );

                if (!m_population.DesignationWithinEnvelope(
                    halfAngleDegrees: pointHalfAngle,
                    point: in point,
                    rangeValue: pointRange,
                    reason: out var pointReason,
                    register: pointRegister,
                    sourceIndex: sourceIndex
                )) {
                    return Refuse(pointReason);
                }
            }

            m_population.SetDesignation(
                bodyIndex: sourceIndex,
                registerIndex: registerIndex,
                target: WorldTargetDesignation.AtPoint(point: point)
            );
            var pointMessage = string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"body:{sourceIndex} {designation.Register}=at:{((double)point.X):0.###},{((double)point.Y):0.###},{((double)point.Z):0.###}"
            );

            Console.Error.WriteLine(value: $"[world.designation: {pointMessage}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: pointMessage,
                Rejected: false,
                Kind: WorldEditEchoKind.Designation,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));
            return true;
        }
        if (designation.Subject.Kind != GrantSubjectKind.Body) {
            return Refuse($"subject '{designation.Subject.Describe()}' is not a body");
        }

        var targetIndex = designation.Subject.Value;

        if (
            (targetIndex == sourceIndex) ||
            (((uint)targetIndex) >= ((uint)m_population.Capacity)) ||
            (Body(index: targetIndex) is null)
        ) {
            return Refuse(((targetIndex == sourceIndex)
                ? "a body cannot designate itself"
                : $"body:{targetIndex} is not active"));
        }

        var targetSubject = GrantSubject.Body(index: targetIndex);

        if (!knownSubject) {
            var observe = m_grants.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: targetSubject
            );

            if (!observe.IsAllowed) {
                return Refuse(
                    observe.DescribeRefusal(
                        actor: principal,
                        subject: targetSubject.Describe(),
                        verb: "observe"
                    ),
                    denied: true
                );
            }
        }

        if (!knownSubject) {
            var register = m_definition.TargetRegisters[registerIndex];
            var range = WorldPopulation.EffectiveTargetValue(
                body: source,
                stateName: register.RangeState,
                authoredMaximum: register.MaximumRange
            );
            var halfAngle = WorldPopulation.EffectiveTargetValue(
                body: source,
                stateName: register.HalfAngleState,
                authoredMaximum: register.MaximumHalfAngleDegrees
            );

            if (!m_population.DesignationWithinEnvelope(
                halfAngleDegrees: halfAngle,
                rangeValue: range,
                reason: out var reason,
                register: register,
                sourceIndex: sourceIndex,
                targetIndex: targetIndex
            )) {
                return Refuse(reason);
            }
        }

        m_population.SetDesignation(
            bodyIndex: sourceIndex,
            registerIndex: registerIndex,
            target: WorldTargetDesignation.Body(index: targetIndex)
        );
        var message = $"body:{sourceIndex} {designation.Register}={targetSubject.Describe()}";

        Console.Error.WriteLine(value: $"[world.designation: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: false,
            Kind: WorldEditEchoKind.Designation,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
        return true;

        bool Refuse(string reason, bool denied = false) {
            m_population.NoteDesignationRefusal(
                bodyIndex: sourceIndex,
                reason: reason
            );
            Console.Error.WriteLine(value: $"[world.designation refused: {reason}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: reason,
                Rejected: true,
                Kind: WorldEditEchoKind.Designation,
                Denied: denied,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));
            return false;
        }
    }
    // Dispatches one envelope to the apply method its payload kind names, stamping the envelope's connection/
    // correlation identity onto the WorldEditEcho those methods emit. Grant/Revoke's actor and Session/Mutation/
    // Definition/Undo/Composition/Lever's acting principal are ALWAYS the envelope's own Principal — the one field
    // every submission kind funnels its acting identity through now, never a second copy.
    private WorldSubmissionResult ApplyEnvelope(SubmissionEnvelope envelope) {
        switch (envelope.Payload) {
            case WorldSubmissionPayload.Command command:
                ApplyCommand(
                    command: command.Value,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Grant grant:
                Grant(
                    grant: grant.Value,
                    actor: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Revoke revoke:
                Revoke(
                    grant: revoke.Value,
                    actor: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Session session:
                return new WorldSubmissionResult.Session(Reply: ApplySession(request: session.Value));
            case WorldSubmissionPayload.Rebuild rebuild:
                EnqueueRebuild(
                    request: rebuild.Value,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Mutation mutation:
                // The tape's one mutation ingress: fires here rather than at the loopback so a forwarded traveller's
                // submission and an admitted peer's are captured on the same terms as a local one, each with the
                // actor its own envelope stamped. See WorldServer.MutationTap.
                MutationTap?.Invoke(
                    arg1: mutation.Value,
                    arg2: envelope.Principal
                );
                EnqueueMutation(
                    mutation: mutation.Value,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId,
                    // Threaded through the buffered op itself, never a generic drain-wide firing: only THIS dispatch
                    // point — the one MutationTap above also covers — ever supplies a completion, so a guest's
                    // decoded act and a rule's generate effect (both call EnqueueMutation directly) never produce one.
                    outcomeObserved: ((MutationOutcomeTap is { } outcomeTap)
                    ? (applied => outcomeTap(
                        arg1: mutation.Value,
                        arg2: applied
                    ))
                    : null)
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Undo undo:
                EnqueueUndo(
                    count: undo.Count,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Composition composition:
                ApplyComposition(
                    composition: composition.Value,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Lever lever:
                ApplySessionLever(
                    lever: lever.Value,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Query query:
                return new WorldSubmissionResult.Query(Answer: AnswerSubmittedQuery(
                    query: query.Value,
                    principal: envelope.Principal
                ));
            case WorldSubmissionPayload.ScreenOp screenOp:
                // Synchronous, like Command/Grant/Revoke — never buffered to the tick boundary — so a following
                // WorldCommand.ComposeControl submitted in the same batch (body.engage's auto-insert precheck) observes
                // this op's effect immediately. See WorldScreenOp's own remarks for why.
                TryApplyScreenOp(
                    op: screenOp.Value,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId,
                    expectedContentHash: null
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Designation designation:
                ApplyDesignation(
                    designation: designation.Value,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            default:
                // No silent fallback: a new payload kind added without its own arm here would otherwise vanish
                // silently — a build-time authoring gap, surfaced loudly rather than dropped.
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(envelope),
                    actualValue: envelope.Payload,
                    message: $"no ApplyEnvelope arm for submission payload kind '{envelope.Payload.GetType().Name}' — every kind must map to its apply method."
                );
        }
    }
    // The whole-document rebuild-and-swap (SubmitRebuild / world.reset, world.load, world.reload): resolve the
    // candidate (the server's own base for Reset, the console-resolved document for Load/Reload — or, on a REPLAY
    // drive, a fresh re-read of the tape's path hint) → compute/check its CAS content hash → validate → capacity-check
    // → solids rebuild → swap → journal RESET → re-mint every admitted peer connection's admission grant
    // (the document swap re-syncs group/ownership grant state but never re-mints the admitted peers' admission grants
    // on its own, and a rebuild is exactly the kind of whole-state swap a future authority change might reasonably
    // reset around — this closes that loudly, by construction, rather than by omission). The console handler already
    // validated a Load/Reload file (WorldDefinitionLoader.TryLoadFile); this
    // re-check is the defensive apply-time gate every install passes through, same as the prior world.load-only path.
    private bool ApplyRebuild(WorldRebuildRequest request, WorldPrincipal principal, int connectionId, long correlationId, string? expectedContentHash = null, string? preparationFailure = null) {
        var verb = request.Kind switch {
            WorldRebuildKind.Reset => "world.reset",
            WorldRebuildKind.Load => "world.load",
            WorldRebuildKind.Reload => "world.reload",
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(request),
            actualValue: request.Kind,
            message: $"no {nameof(ApplyRebuild)} verb for rebuild kind '{request.Kind}'."
        ),
        };

        // THE CAS RESOLUTION, FIRST — before any refusal gate, so a rebuild the door goes on to refuse below is
        // still taped and reproduces as the identical refusal on replay (RebuildTap's own remarks).
        //   Reset: the candidate is THIS DRIVE'S OWN current base (m_base) — live or replay, always freshly read
        //   here, never the request's (Reset carries none). Its hash is the base's canonical bytes AT THIS MOMENT,
        //   which is why it must be computed here rather than at submission (m_base can move between submission and
        //   drain — see EnqueueRebuild's remarks).
        //   Load/Reload: request.Definition is non-null on the LIVE path (the console already read + validated the
        //   file and computed request.ContentHash from those exact bytes) and null on a REPLAY drive (the tape never
        //   embeds the document — WorldReplaySnapshot.Drive passes only Kind/PathHint/Force/ContentHash, so a
        //   re-drive proves the file on disk still matches what was recorded rather than trusting a stored copy).
        WorldDefinition candidate;
        string contentHash;

        if (request.Kind == WorldRebuildKind.Reset) {
            candidate = m_base;
            contentHash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate));
        } else if (request.Definition is { } supplied) {
            candidate = supplied;
            contentHash = (request.ContentHash ?? throw new InvalidOperationException(message: $"{verb}: a Load/Reload request carrying a document must also carry its content hash."));
        } else if (request.PathHint is not { } path) {
            throw ReplayRefusal.RebuildSourceUnavailable.Raise(message: $"{verb}: a Load/Reload request with no embedded document must carry a path hint to re-read for replay.");
        } else if (!WorldDefinitionFileSource.TryLoadLocally(
            contentHash: out var rereadHash,
            definition: out var reread,
            path: path,
            reason: out var rereadReason
        )) {
            throw ReplayRefusal.RebuildSourceUnavailable.Raise(message: $"{verb}: cannot re-read '{path}' for replay — {rereadReason}");
        } else {
            candidate = reread!;
            contentHash = rereadHash;
        }

        if (
            (expectedContentHash is { } expected) &&
            !string.Equals(
            a: contentHash,
            b: expected,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            var pinned = ((request.Kind == WorldRebuildKind.Reset)
                ? "the re-driven run's own base"
                : $"'{request.PathHint}'"
            );

            throw ReplayRefusal.RebuildContentMismatch.Raise(message: $"{verb}: content hash mismatch on {pinned} — found {contentHash}, expected {expected} (recorded). The pinned content has changed since this recording was made; re-record it.");
        }

        RebuildTap?.Invoke(
            arg1: request,
            arg2: principal,
            arg3: contentHash
        );

        // A rebuild can touch any section: the principal must hold Mutate over EVERY section — the same door
        // world.load/world.undo have always used.
        if (!m_grants.AllowsAllSections(
            capability: WorldCapability.Mutate,
            denial: out var deniedVerdict,
            deniedSection: out var deniedSection,
            principal: principal
        )) {
            DenyGrantTable(
                denial: $"{principal.Describe()} cannot mutate every section (section:{deniedSection.ToString().ToLowerInvariant()} — {deniedVerdict.DescribeDenial()}) — {verb} dropped",
                connectionId: connectionId,
                correlationId: correlationId,
                echoKind: WorldEditEchoKind.Rebuild
            );

            return false;
        }

        // The live-authoring guard: world.load without `force` refuses outright while the journal is dirty, rather
        // than silently discarding unsaved work. Orthogonal to world.reset (reset IS the discard, by name, every
        // time) and to world.reload (the artist external-edit loop is expected to discard the in-session journal on
        // every reload — its whole point is re-reading the file the artist just edited).
        if (
            (request.Kind == WorldRebuildKind.Load) &&
            !request.Force &&
            (m_journal.Count > 0)
        ) {
            var denial = $"{m_journal.Count} unsaved mutation(s) would be discarded — world.save first, world.reset to discard them without loading a new document, or world.load {request.PathHint} force to discard them and load anyway";

            Console.Error.WriteLine(value: $"[world.load rejected: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: $"{verb} rejected: {denial}",
                Rejected: true,
                Kind: WorldEditEchoKind.Rebuild,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return false;
        }

        if (preparationFailure is not null) {
            RejectRebuild(
                connectionId: connectionId,
                correlationId: correlationId,
                reason: preparationFailure,
                verb: verb
            );

            return false;
        }

        // The load command already proved cross-document claims before enqueue. Apply-time validation runs from
        // Step, so it repeats only document-local checks and never reaches transport from the tick path.
        if (!WorldDefinitionValidator.TryValidateLocally(
            definition: candidate,
            reason: out var validationReason
        )) {
            RejectRebuild(
                connectionId: connectionId,
                correlationId: correlationId,
                reason: validationReason,
                verb: verb
            );

            return false;
        }

        if (candidate.Population.Capacity != m_population.Capacity) {
            RejectRebuild(
                verb: verb,
                reason: $"population capacity {candidate.Population.Capacity} differs from the boot-allocated capacity {m_population.Capacity}; restart the host to load it",
                connectionId: connectionId,
                correlationId: correlationId
            );

            return false;
        }

        if (ExceedsBootDerivedFaceReservation(
            candidate: candidate,
            reason: out var reservationReason
        )) {
            RejectRebuild(
                connectionId: connectionId,
                correlationId: correlationId,
                reason: reservationReason,
                verb: verb
            );

            return false;
        }

        if (!m_envelope.TryFit(
            candidate: candidate,
            reason: out var capacityReason
        )) {
            RejectRebuild(
                connectionId: connectionId,
                correlationId: correlationId,
                reason: capacityReason,
                verb: verb
            );

            return false;
        }

        if (!m_population.CanInstallFields(
            definition: candidate,
            reason: out var fieldReason
        )) {
            RejectRebuild(
                connectionId: connectionId,
                correlationId: correlationId,
                reason: fieldReason!,
                verb: verb
            );

            return false;
        }

        // A rebuild rebuilds the field wholesale (loud rejection on an unsupported solid, definition unchanged) —
        // same as a whole-document swap always has.
        if (!TryBuildSolids(
            definition: candidate,
            reason: out var rebuildSolidReason,
            solids: out var rebuildSolids
        )) {
            RejectRebuild(
                connectionId: connectionId,
                correlationId: correlationId,
                reason: rebuildSolidReason,
                verb: verb
            );

            return false;
        }

        // Reconcile the addon runtime against the CANDIDATE document — unconditional, never gated on a per-mutation
        // classification the way TryApplyMutation's own AffectsAddons predicate is: a whole-document swap can move
        // ANY section, including the channel table TryPrepare's own dependency check watches for. Unconditional is
        // cheap here too — a reused row costs a structural compare, never a recompile. A server with no addon host
        // attached (a test double; the silo attaches its own refusing WorldNoAddonHost instead of leaving this null)
        // is vacuous only for a candidate whose addon rows are all absent or disabled — an ENABLED row is refused by
        // name below, the identical shape TryApplyMutation's own no-host gate and WorldNoAddonHost.TryPrepare use,
        // rather than installing a candidate that claims a mounted addon no host can ever run.
        IWorldAddonPreparedPlan? rebuildAddonPlan = null;
        int[]? newRebuildTickWrittenEntity = null;
        WorldPrincipal[]? newRebuildTickWrittenPrincipal = null;
        bool[]? newRebuildTickCollided = null;
        var rebuildAddonPlanCommitted = false;

        // The whole sequence from here through Commit runs under ONE try/finally — see TryApplyMutation's identical
        // shape for why: rebuildAddonPlan starts null, so a return before TryPrepare ever succeeds leaves the
        // finally a no-op, and a downstream throw from contention-array staging, Install, or Commit alike still
        // disposes an uncommitted plan.
        try {
            if (m_addons is { } addonsForRebuild) {
                if (!addonsForRebuild.TryPrepare(
                    candidate: candidate,
                    current: m_definition,
                    plan: out rebuildAddonPlan,
                    reason: out var rebuildAddonReason
                )) {
                    RejectRebuild(
                        connectionId: connectionId,
                        correlationId: correlationId,
                        reason: $"addon {rebuildAddonReason}",
                        verb: verb
                    );

                    return false;
                }

                if (rebuildAddonPlan is not null) {
                    StageAddonContentionArrays(
                        mountedCount: rebuildAddonPlan.MountedCount,
                        entity: out newRebuildTickWrittenEntity,
                        principal: out newRebuildTickWrittenPrincipal,
                        collided: out newRebuildTickCollided
                    );
                }
            } else if (TryFindEnabledAddonRow(
                candidate: candidate,
                name: out var enabledRowName
            )) {
                RejectRebuild(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    reason: $"addon '{enabledRowName}' cannot mount — no addon host is attached to this server",
                    verb: verb
                );

                return false;
            }

            SwapSolids(solids: rebuildSolids);
            if (request.Kind != WorldRebuildKind.Reset) {
                m_machines.SetDocumentPath(documentPath: request.PathHint);
            }
            Install(
                definition: candidate,
                rebuildPopulation: true
            );
            PaintLatticeDraws(definition: candidate);

            if (rebuildAddonPlan is not null) {
                m_addons!.Commit(plan: rebuildAddonPlan);
                rebuildAddonPlanCommitted = true;

                if (newRebuildTickWrittenEntity is not null) {
                    m_tickWrittenEntity = newRebuildTickWrittenEntity;
                    m_tickWrittenPrincipal = newRebuildTickWrittenPrincipal!;
                    m_tickCollided = newRebuildTickCollided!;
                }
            }
        } finally {
            if (!rebuildAddonPlanCommitted) {
                rebuildAddonPlan?.Dispose();
            }
        }

        m_journal.Clear();

        // Snapshot, for every CURRENTLY CONNECTED (admitted, not parked) peer, exactly
        // which of its ORIGINAL admission-minted rows it still actually holds — BEFORE the reset below discards the
        // whole table. A row present at connection time but absent here is a live world.revoke the operator issued
        // against this exact peer since; RemintPeerAdmissionGrants must not resurrect it. A PARKED peer (disconnected,
        // inside its reconnect grace — WorldPopulation.IsAdmittedPeer stays true through that window) is excluded
        // outright: it has no live session to act through, so it is re-authorized (and, if still trusted, reminted)
        // only on an actual reconnect, never on a rebuild that happens to land during its grace window.
        var preRebuildPeerRows = new Dictionary<int, IReadOnlyList<WorldGrant>>();

        for (var peerIndex = Population.LocalSeatCount; (peerIndex < m_population.Capacity); peerIndex++) {
            if (
                !m_population.IsAdmittedPeer(bodyIndex: peerIndex) ||
                m_population.IsParked(index: peerIndex)
            ) {
                continue;
            }

            preRebuildPeerRows[peerIndex] = m_grants.Rows(principal: m_population.PeerPrincipal(index: peerIndex));
        }

        // THE GRANT-TABLE HALF: runtime grants drop; document grants re-apply as at boot.
        // A world.grant/world.revoke acquisition is RUNTIME state — orthogonal to the document
        // and never touched by Install/Rebuild on its own — so a rebuild that left it standing would silently keep
        // whatever authority the PRE-rebuild session had accumulated, including grants a fresh boot of this exact
        // document would never have seeded. Reset silently to the SAME permissive local-play defaults the
        // constructor seeds (WorldGrants.Reset — never the loud Grant door, exactly like the constructor's own
        // seed), THEN replay the NEW candidate's own document-authored Grants section under Console through the
        // IDENTICAL loud accept/reject path the constructor and world.grant both use — same consent-withholding
        // (WithoutAuthoredConsent), same narration.
        m_grants.Reset(seatCount: Population.LocalSeatCount);

        foreach (var grant in candidate.Grants) {
            if (IsDocumentChannelRow(grant: grant)) {
                continue;
            }

            Grant(
                grant: WithoutAuthoredConsent(grant: grant),
                actor: WorldPrincipal.Console,
                connectionId: connectionId,
                correlationId: correlationId
            );
        }

        // Admitted PEER CONNECTIONS are the one exception: "admitted peers survive"
        // means their CONNECTION stays (WorldPopulation never dropped them — Install/Rebuild's own Install call
        // above left every admitted peer body active), but the reset above just wiped their admission grant along
        // with everything else, because a peer is not a boot-time seat and not a document row either. RE-AUTHORIZE
        // (never blindly re-mint) each one against the CANDIDATE's own current admission policy.
        RemintPeerAdmissionGrants(
            candidate: candidate,
            preRebuildPeerRows: preRebuildPeerRows
        );

        // Finish runs here, AFTER the candidate's own grants have installed, never earlier: its capability
        // disclosure narration is computed lazily against the LIVE grant table at the moment each line actually
        // prints (see WorldAddonRuntime.Finish), so a rebuild that also moves Grants reports what the addon is
        // ACTUALLY granted under the candidate, never a mount report pinned to the table this rebuild just replaced.
        if (rebuildAddonPlanCommitted) {
            m_addons!.Finish(plan: rebuildAddonPlan!);
        }

        // Reset targets the base WITHOUT moving it (the whole point: repeated resets always land on the same base
        // until the next save/load). Load/Reload REPLACE the base — the newly installed document becomes what the
        // NEXT reset targets, exactly like a swap always has.
        string origin;

        if (request.Kind == WorldRebuildKind.Reset) {
            origin = m_baseOrigin;
        } else {
            m_base = candidate;
            origin = $"'{request.PathHint}' ({verb})";
            m_baseOrigin = origin;
        }

        var message = $"{verb} applied — base is {origin}, journal cleared";

        Console.Error.WriteLine(value: $"[world.definition: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: false,
            Kind: WorldEditEchoKind.Rebuild,
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            RebuildOrigin: ((request.Kind == WorldRebuildKind.Reset)
            ? null
            : request.PathHint)
        ));

        return true;
    }
    // The non-consuming primer path for AttachSink: PEEKS every body's continuity hint instead of consuming it, so a
    // newly attached sink's boot-state primer can never steal the flag an already-attached sink is still due to
    // observe via the next ordinary EmitSnapshot broadcast (the bug this repairs — see docs/vision.md's
    // "Observation and display" section). Stamped with the server's actual current tick/step width
    // (m_lastCompletedTick/m_lastStepTicks), which are still their default 0/0 before the very first Step has ever
    // run — the one case where 0/0 is the honest answer, preserved exactly.
    private WorldSnapshot BuildPrimerSnapshot() => BuildSnapshotCore(
        consumeContinuity: false,
        stepTicks: m_lastStepTicks,
        tick: m_lastCompletedTick
    );
    // Every live body's authoritative sim pose, color, archetype, and this tick's continuity hint, written into the
    // reused m_snapshotEntries array — the SAME borrowed-scratch shape as before the output hub: a typed subscriber
    // must fully consume (or copy) the returned WorldSnapshot before returning from DeliverSnapshot, because the next
    // tick's BuildSnapshot call overwrites this same backing array. Consumes (TakeContinuity) every body's one-shot
    // continuity hint — the ORDINARY per-tick broadcast path (EmitSnapshot). A late AttachSink must NOT call this
    // overload: see BuildPrimerSnapshot.
    private WorldSnapshot BuildSnapshot(ulong tick, ulong stepTicks) => BuildSnapshotCore(
        consumeContinuity: true,
        stepTicks: stepTicks,
        tick: tick
    );
    private WorldSnapshot BuildSnapshotCore(ulong tick, ulong stepTicks, bool consumeContinuity) {
        var count = 0;

        for (var index = 0; (index < m_population.Capacity); index++) {
            if (
                !m_population.IsActive(index: index) ||
                (m_population.EntryBody(index: index) is not { } body)
            ) {
                continue;
            }

            m_snapshotEntries[count++] = new EntitySnapshot(
                Index: index,
                Position: body.Position,
                Orientation: body.Orientation,
                BodyColor: m_population.BodyColor(index: index),
                Active: true,
                Kit: m_population.KitIndex(index: index),
                Look: m_population.LookIndex(index: index),
                CatalogRig: m_population.CatalogRig(index: index),
                Continuity: (consumeContinuity
                ? body.TakeContinuity()
                : body.PeekContinuity()),
                Generation: m_population.Generation(index: index),
                PlacementId: m_population.InhabitantPlacementId(index: index),
                Heading: body.Yaw,
                Facts: body.Facts
            );
        }

        var fieldsFull = false;
        var fieldCells = (m_population.Fields?.TakeDeltas(
            full: !consumeContinuity,
            isFull: out fieldsFull
        ) ?? []);

        return new WorldSnapshot(
            Tick: tick,
            Revision: m_population.Revision,
            StepTicks: stepTicks,
            Entries: m_snapshotEntries.AsMemory(
                length: count,
                start: 0
            ),
            Authority: AuthorityIdentity,
            FieldCells: fieldCells,
            FieldsFull: fieldsFull
        );
    }
    // The ONE grant-table DENIAL emission — the loud stderr line plus the submitter-routed denied echo (Rejected,
    // Denied). Grant's administration and co-drive-consent refusals, Revoke's administration refusal, a lever write
    // lacking its section's Mutate hold, world.undo lacking every section's Mutate hold, and a rebuild lacking every
    // section's Mutate hold all differ only in what they say and which verb's echo kind names the denied operation —
    // never in how the denial is reported. echoKind defaults to GrantTable (a direct grant/revoke denial); a caller
    // reporting a DIFFERENT verb's authority denial (Mutation, Rebuild) names that verb's own kind instead, so the
    // echo still routes to the operation the caller actually attempted.
    private void DenyGrantTable(string denial, int connectionId, long correlationId, WorldEditEchoKind echoKind = WorldEditEchoKind.GrantTable) {
        Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: denial,
            Rejected: true,
            Kind: echoKind,
            Denied: true,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
    }
    // Swap the live definition and rebuild the derived state that compiled from it. Sim-affecting sections (kits,
    // assignment, motion, wander, seat kit, spawns) recompile the population's fixed tables and live bodies; the
    // scene/screens rebuild on the client through the delivered definition, and cameras/render/population defaults are
    // document-only.
    private void Install(WorldDefinition definition, bool rebuildPopulation) {
        m_definition = definition;
        m_inputHold.Reconfigure(settings: definition.CompiledInputHold);
        RecompileRules(definition: definition);
        // Unconditional, like RecompileRules above: a group/member count is capacity-bounded, so a full resync costs
        // nothing on the ticks that never touch the groups section, and unconditional is what keeps membership
        // expansion CHECK-TIME correct without a bespoke "did this mutation touch Groups" classification to maintain.
        m_grants.SyncGroups(
            groups: (definition.Groups ?? WorldGroupsSection.Empty).Groups,
            kinds: (definition.Groups ?? WorldGroupsSection.Empty).Kinds,
            ownership: (definition.Groups ?? WorldGroupsSection.Empty).Ownership
        );
        // Unconditional for the identical reason — a drive-gate row lives in `state`, an ordinary section like any
        // other, so there is no cheaper "did this mutation touch a gate row" classification worth maintaining
        // either; this is what makes a live world.state.cell.set that flips a gate settle before the SAME tick's
        // later intent drain reads it (Install always runs before the intents loop within one Step).
        m_grants.SyncState(definition: definition);

        // Field reactions are a compiled runtime product even when the mutation does not require a population
        // rebuild. A compatible replacement swaps the typed plan in place and retains every lattice cell.
        if (!rebuildPopulation) {
            m_population.InstallFields(definition: definition);
        }

        // Reconcile the machine host to the (possibly changed) screens section on EVERY install — cheap (a
        // dictionary diff over a handful of declared screens), and the one choke point every screen-affecting
        // mutation AND every whole-document rebuild both pass through. The host reports which indices it removed;
        // this project (not the host — see WorldMachineHost's own remarks on why) owns the engagement-side admin
        // cleanup for them: m_engagement.DissolveScreen runs before the removed slot's machine is disposed.
        foreach (var removed in m_machines.ReconcileScreens(screens: definition.Screens)) {
            m_engagement.DissolveScreen(screenIndex: removed);
        }

        // Cable groups reconcile AFTER screens (a group resolves against the live slot set) — the SAME choke
        // point, so a live UpsertScreen carrying a cable port AND a whole-document rebuild (world.reset/.load/
        // .reload) both establish/tear down live links, not merely the boot constructor.
        m_machines.ReconcileLinks(links: definition.MachineCableGroups());

        if (rebuildPopulation) {
            m_population.Rebuild(
                definition: definition,
                solids: m_solids
            );
            m_inputHold.Reset();
            // Reconcile inhabited placements AFTER the census rebuild (a placement/creation/kit edit can add, retire, or
            // re-kit a driven body). Idempotent — a no-op when the inhabited set is unchanged.
            var admitted = new List<WorldPeerEventEntry>();
            var disconnected = new List<WorldPeerEventEntry>();

            m_population.ReconcileInhabitants(
                admitted: admitted,
                definition: definition,
                disconnected: disconnected
            );
            ApplyLifecycleEvents(
                admitted: admitted,
                disconnected: disconnected,
                ordered: true
            );
        }
    }
    // A scalar state write changes runtime values, not declaration shape. Keep the authoritative document as the
    // journal/save source while retaining the compiled rule/catalog/group/machine products that depend only on
    // declarations; only state-sensitive grants and field reactions observe the new value immediately.
    private void InstallRuntimeStateValue(WorldDefinition definition) {
        m_definition = definition;
        m_grants.SyncState(definition: definition);
        m_population.InstallFields(definition: definition);
    }

    private static bool TryValidateMutationCandidate(WorldDefinition candidate, WorldMutation mutation, out string reason) => mutation switch {
        WorldMutation.UpsertStateCell state => WorldDefinitionValidator.TryValidateRuntimeStateCell(
            definition: candidate,
            rowName: state.Row,
            key: state.Key,
            reason: out reason
        ),
        _ => WorldDefinitionValidator.TryValidateLocally(definition: candidate, reason: out reason),
    };
    private void Reject(WorldMutation mutation, string reason, int connectionId, long correlationId) {
        Console.Error.WriteLine(value: $"[world.mutation rejected: {Describe(mutation: mutation)} — {reason}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: $"{Describe(mutation: mutation)} rejected: {reason}",
            Rejected: true,
            Kind: WorldEditEchoKind.Mutation,
            Mutation: mutation,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
    }
    // A refused whole-document rebuild: loud, and echoed so the same tap that counts a refused mutation counts this
    // too.
    private void RejectRebuild(string verb, string reason, int connectionId, long correlationId) {
        Console.Error.WriteLine(value: $"[world.definition rejected: {verb} — {reason}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: $"{verb} rejected: {reason}",
            Rejected: true,
            Kind: WorldEditEchoKind.Rebuild,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
    }
    // ApplyRebuild's own null-host gate: with no addon host attached, a candidate whose addon rows are all absent or
    // disabled is vacuous (nothing to mount either way), but an ENABLED row names a guest that would install into
    // the document with no runtime ever able to run it — the same shape TryApplyMutation's own no-host gate and
    // WorldNoAddonHost.TryPrepare both refuse.
    private static bool TryFindEnabledAddonRow(WorldDefinition candidate, out string name) {
        foreach (var row in candidate.Addons) {
            if (row.Enabled) {
                name = row.Name;

                return true;
            }
        }

        name = string.Empty;

        return false;
    }
    // The screen index(es) an op's Control check runs over — see TryCheckScreenOpControl's own remarks for Link/
    // Unlink's multi-member shape.
    private IReadOnlyList<int> ScreenOpTargets(WorldScreenOp op) {
        switch (op) {
            case WorldScreenOp.Insert insert: return new[] { insert.Index };
            case WorldScreenOp.Eject eject: return new[] { eject.Index };
            case WorldScreenOp.Select select: return new[] { select.Index };
            case WorldScreenOp.SetOptions options: return new[] { options.Index };
            case WorldScreenOp.Link link: return link.Members;
            case WorldScreenOp.Unlink unlink:
                return (m_machines.TryReadLinkMembers(
                    name: unlink.Name,
                    members: out var members
                )
                    ? members
                    : []
                );
            default:
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(op),
                    actualValue: op,
                    message: $"no {nameof(ScreenOpTargets)} arm for screen op kind '{op.GetType().Name}'."
                );
        }
    }
    // Adopt a wholesale-rebuilt field (a swap/undo), bumping the revision when the field actually moved so the status
    // read-back tracks it. A swap into an analytic world clears the field.
    private void SwapSolids(WorldSolidField? solids) {
        if (!ReferenceEquals(
            objA: solids,
            objB: m_solids
        )) {
            m_solids = solids;
            m_solidRevision++;
        }
    }
    // Apply one mutation at the tick boundary: authority through the ONE admission predicate → compose a candidate
    // (with-expression) → revalidate the WHOLE document → capacity-check scene/screen edits against the probed render
    // envelope → on any failure reject loudly (definition unchanged) → on success swap the live definition, rebuild the
    // changed section's derived state, and journal it.
    private bool TryApplyMutation(WorldMutation mutation, ulong tick, int connectionId, long correlationId, bool preMetered) {
        // THE ONE ADMISSION PREDICATE decides the whole authority question — section hold, the Mutate/section kind
        // mask, the row-scoped Edit hold and ITS mask, and the untrusted per-tick dispatch budget. Every ordered-domain
        // ingress converges here (loopback, console, and the TCP peer door alike), so this call is what gives the peer
        // door exactly the masks and metering the addon seam has, from the same code rather than from a second reading
        // of the same rules. `preMetered` says only whether THIS ingress already charged the dispatch (the addon seam
        // meters at its own pre-flight, before decode, deliberately); it never changes which rules run.
        if (!TryAdmitMutation(
            principal: mutation.Principal,
            section: SectionOf(mutation: mutation),
            kindOrdinal: WorldMutationKindCatalog.OrdinalOf(mutation: mutation),
            rowScopedEditSubject: RowScopedEditSubjectOf(mutation: mutation),
            rowScopedMutateSubject: RowScopedMutateSubjectOf(mutation: mutation),
            meter: !preMetered,
            admission: out var admission
        )) {
            var denial = admission.Describe();

            Console.Error.WriteLine(value: $"[world.grant denied: {mutation.Principal.Describe()} {denial} — {Describe(mutation: mutation)} dropped]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: $"{Describe(mutation: mutation)} denied: {denial}",
                Rejected: true,
                Kind: WorldEditEchoKind.Mutation,
                Mutation: mutation,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return false;
        }

        if (!TryCompose(
            current: m_definition,
            mutation: mutation,
            tick: tick,
            instanceIdentity: InstanceIdentity,
            candidate: out var candidate,
            reason: out var composeReason,
            evictedKey: out var evictedKey
        )) {
            Reject(
                connectionId: connectionId,
                correlationId: correlationId,
                mutation: mutation,
                reason: composeReason
            );

            return false;
        }

        candidate = RebaseCellTraits(
            candidate: candidate,
            mutation: mutation,
            original: m_definition,
            tick: tick
        );

        if (
            (mutation is WorldMutation.UpsertKit upsertKit) &&
            !m_population.CanReplaceKit(
            replacement: upsertKit.Kit,
            refusal: out var sourceReason
        )
        ) {
            Reject(
                connectionId: connectionId,
                correlationId: correlationId,
                mutation: mutation,
                reason: sourceReason
            );

            return false;
        }

        // Cross-document adjacency claims are proved at load, never from this tick path. An edit that can change a
        // standing claim or one of its floor inputs must go through a document reload; unrelated edits revalidate
        // only the facts owned by this document.
        if (
            (candidate.Adjacencies is { Count: > 0 }) &&
            AdjacencyProofInputsChanged(
            candidate: candidate,
            current: m_definition,
            mutation: mutation
        )
        ) {
            Reject(
                connectionId: connectionId,
                correlationId: correlationId,
                mutation: mutation,
                reason: "the mutation changes an adjacency overlap input; apply it through world.load/world.reload so the neighbour can be re-proved outside the tick path"
            );

            return false;
        }

        if (!TryValidateMutationCandidate(candidate: candidate, mutation: mutation, reason: out var validationReason)) {
            Reject(
                connectionId: connectionId,
                correlationId: correlationId,
                mutation: mutation,
                reason: validationReason
            );

            return false;
        }

        if (ExceedsBootDerivedFaceReservation(
            candidate: candidate,
            reason: out var reservationReason
        )) {
            Reject(
                connectionId: connectionId,
                correlationId: correlationId,
                mutation: mutation,
                reason: reservationReason
            );

            return false;
        }

        if (
            AffectsRenderEnvelope(mutation: mutation) &&
            !m_envelope.TryFit(
            candidate: candidate,
            reason: out var capacityReason
        )
        ) {
            Reject(
                connectionId: connectionId,
                correlationId: correlationId,
                mutation: mutation,
                reason: capacityReason
            );

            return false;
        }

        if (!m_population.CanInstallFields(
            definition: candidate,
            reason: out var fieldReason
        )) {
            Reject(
                connectionId: connectionId,
                correlationId: correlationId,
                mutation: mutation,
                reason: fieldReason!
            );

            return false;
        }

        // Step 4b — the SDF contact field, built once here (before install) so the warp-free evaluator's excluded-op
        // ceiling is a LOUD apply-time rejection (the definition and the field both stay byte-identical on failure)
        // rather than a constructor throw at install. Only a solid-affecting mutation rebuilds it; otherwise the live
        // field carries forward untouched.
        var solids = m_solids;
        var solidAffecting = AffectsSolidField(mutation: mutation);

        if (solidAffecting) {
            // A SetCollision edit touches only the collision tuning row — the compiled SDF program (screens and
            // placements) is byte-identical — so when the live field already exists and the requirements still need it,
            // candidate still is, re-wrap the existing evaluator with the new scalars instead of recompiling the program
            // (a slope/skin drag never rebuilds hundreds of instructions). Every other solid-affecting edit, and a
            // a requirement-selection flip rebuilds from scratch.
            if (
                (mutation is WorldMutation.SetCollision) &&
                (m_solids is { } live) &&
                WorldContactSelection.RequiresField(collision: candidate.Collision)
            ) {
                solids = live.WithTuning(tuning: FixedWorldCollision.Compile(collision: candidate.Collision));
            } else if (!TryBuildSolids(
                definition: candidate,
                reason: out var solidReason,
                solids: out solids
            )) {
                Reject(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    mutation: mutation,
                    reason: solidReason
                );

                return false;
            }
        }

        // Assign the field BEFORE the rebuild so a recompiled body's first step already solves against it. A field change
        // forces a population rebuild (bodies must receive the new field reference) even when the mutation kind is not
        // otherwise population-affecting; the analytic path is untouched (solidAffecting is inert without the field provider).
        if (
            solidAffecting &&
            !ReferenceEquals(
            objA: solids,
            objB: m_solids
        )
        ) {
            m_solids = solids;
            m_solidRevision++;
        }

        // THE LAST FALLIBLE GATE — expensive compilation after every cheap refusal above. Only an Addons-affecting
        // mutation ever reaches here (AffectsAddons); every other mutation leaves the addon runtime untouched, both
        // this call and TryPrepare's own diff. A server with no addon host attached refuses an addon-affecting
        // mutation BY NAME rather than silently accepting configuration with no effect. Prepare-refusal rejects the
        // WHOLE mutation with the candidate discarded and m_definition byte-identical — the tick still survives.
        // The whole sequence from here through Commit runs under ONE try/finally: addonPlan starts null and TryPrepare
        // only ever sets it on success, so the finally is a no-op for every path that never obtains a plan, and a
        // downstream throw — from contention-array staging, Install, or Commit alike — still disposes an uncommitted
        // plan rather than leaking it.
        IWorldAddonPreparedPlan? addonPlan = null;
        int[]? newTickWrittenEntity = null;
        WorldPrincipal[]? newTickWrittenPrincipal = null;
        bool[]? newTickCollided = null;
        var addonPlanCommitted = false;

        try {
            if (AffectsAddons(mutation: mutation)) {
                if (m_addons is not { } addonsForPrepare) {
                    Reject(
                        connectionId: connectionId,
                        correlationId: correlationId,
                        mutation: mutation,
                        reason: "no addon host is attached to this server — addon-affecting mutations are refused"
                    );

                    return false;
                }

                if (!addonsForPrepare.TryPrepare(
                    candidate: candidate,
                    current: m_definition,
                    plan: out addonPlan,
                    reason: out var addonReason
                )) {
                    Reject(
                        connectionId: connectionId,
                        correlationId: correlationId,
                        mutation: mutation,
                        reason: (addonReason ?? "addon preparation refused")
                    );

                    return false;
                }

                if (addonPlan is not null) {
                    StageAddonContentionArrays(
                        mountedCount: addonPlan.MountedCount,
                        entity: out newTickWrittenEntity,
                        principal: out newTickWrittenPrincipal,
                        collided: out newTickCollided
                    );
                }
            }

            // Commit runs only after Install below succeeds, so nothing observable (registration, disclosure
            // narration, disposal of a superseded guest) moves until then. Narration and superseded-guest disposal
            // (Finish) run only AFTER the journal write below, so neither can still be unwound by what Finish
            // itself does.
            if (mutation is WorldMutation.UpsertStateCell) {
                InstallRuntimeStateValue(definition: candidate);
            } else {
                Install(
                    definition: candidate,
                    rebuildPopulation: (AffectsPopulation(mutation: mutation) || RefreshesLookAssignment(
                    candidate: candidate,
                    mutation: mutation
                ) || (solidAffecting && WorldContactSelection.RequiresField(collision: candidate.Collision)))
                );
            }

            if (mutation is WorldMutation.Generate generated) {
                RepaintLatticeDrawAfterGenerate(
                    definition: candidate,
                    rowName: generated.Row
                );
            }

            if (addonPlan is not null) {
                m_addons!.Commit(plan: addonPlan);
                addonPlanCommitted = true;

                if (newTickWrittenEntity is not null) {
                    m_tickWrittenEntity = newTickWrittenEntity;
                    m_tickWrittenPrincipal = newTickWrittenPrincipal!;
                    m_tickCollided = newTickCollided!;
                }
            }
        } finally {
            if (!addonPlanCommitted) {
                addonPlan?.Dispose();
            }
        }

        m_journal.Add(item: new JournalEntry(
            Mutation: mutation,
            Tick: tick
        ));
        MutationJournalTap?.Invoke(tick, mutation);

        if (addonPlanCommitted) {
            m_addons!.Finish(plan: addonPlan!);
        }

        // A defaults-class mutation edits what the NEXT boot wakes on while the live
        // session levers keep their values (world.save folds them); every other mutation applies live on delivery.
        // SetAuthoringDefaults is the honest exception to the binary split: ONE whole-row mutation carries BOTH
        // classes at once (WorldPlacementPolicyDefaults' own remarks name which field is which) — the headroom/repeat-cap
        // fields are boot-consumed by the frozen render-envelope probe, while candidate/layout/preview fields are
        // re-read live at every use site. The narration spells out the split rather than forcing the mutation into
        // either WorldEditEchoKind bucket; Kind stays Mutation because the live-consumed majority applies NOW.
        var documentOnly = IsDocumentDefaults(mutation: mutation);
        var message = mutation switch {
            WorldMutation.SetAuthoringDefaults => $"{Describe(mutation: mutation)} applied — candidate/layout/preview levers live now; headroom + max-repeat-per-segment apply at next boot",
            // SetPopulationDefaults is a THIRD timing class: the census figures are document defaults (next boot), but
            // the distribution is LIVE for future activations while INERT for bodies already standing — spell out the split.
            WorldMutation.SetPopulationDefaults => $"{Describe(mutation: mutation)} applied — census figures next boot; spawn policy live for future activations, standing bodies unmoved",
            _ => $"{Describe(mutation: mutation)} applied{(documentOnly
            ? " — document default (next boot; live levers unchanged)"
            : string.Empty)}",
        };

        // An Evicts row's overflow policy dropped a cell to make room — named on the SAME echo line rather than a
        // separate one, so an eviction can never scroll past unnoticed the way a second stderr line could.
        if (evictedKey is { } evicted) {
            message = $"{message} (evicted '{evicted}')";
        }

        Console.Error.WriteLine(value: $"[world.mutation: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: false,
            Kind: (documentOnly
            ? WorldEditEchoKind.DocumentDefaults
            : WorldEditEchoKind.Mutation),
            Mutation: mutation,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));

        return true;
    }
    // Applies one screen op SYNCHRONOUSLY (see WorldScreenOp's own remarks for why: never buffered, so a following
    // Command.Engage in the same batch observes the effect). Authority FIRST — Control over the targeted screen(s),
    // the SAME grant subject ScreenCommandModule's pre-inversion client-side precheck used, now checked
    // AUTHORITATIVELY server-side — then the mechanical apply through m_machines. ScreenOpTap fires exactly once,
    // after the outcome (success or refusal) is known, so a refused op still reproduces on replay.
    private bool TryApplyScreenOp(WorldScreenOp op, WorldPrincipal principal, int connectionId, long correlationId, string? expectedContentHash) {
        var verb = op switch {
            WorldScreenOp.Insert => "screen.insert",
            WorldScreenOp.Eject => "screen.eject",
            WorldScreenOp.Select => "screen.select",
            WorldScreenOp.SetOptions => "screen.options",
            WorldScreenOp.Link => "screen.link",
            WorldScreenOp.Unlink => "screen.unlink",
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(op),
            actualValue: op,
            message: $"no {nameof(TryApplyScreenOp)} verb name for screen op kind '{op.GetType().Name}'."
        ),
        };

        if (!TryCheckScreenOpControl(
            denial: out var deniedIndex,
            op: op,
            principal: principal
        )) {
            var denial = $"{principal.Describe()} lacks Control over screen {deniedIndex} — {verb} dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: denial,
                Rejected: true,
                Kind: WorldEditEchoKind.ScreenOp,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));
            ScreenOpTap?.Invoke(
                arg1: op,
                arg2: null,
                arg3: principal
            );

            return false;
        }

        var (ok, message, contentHash) = op switch {
            WorldScreenOp.Insert insert => m_machines.TryInsert(
            index: insert.Index,
            contentPath: insert.ContentPath,
            engineId: insert.EngineId,
            options: insert.Options,
            expectedContentHash: expectedContentHash
        ),
            WorldScreenOp.Eject eject => ((m_machines.TryEject(index: eject.Index) is (var ejectOk, var ejectMessage))
            ? (ejectOk, ejectMessage, ((string?)null))
            : default),
            // Select threads the SAME CAS pin Insert does when the entry it resolves to is a Machine row — a
            // magazine entry's document-declared path is not immune to on-disk drift either. See
            // WorldMachineHost.TrySelect's own remarks.
            WorldScreenOp.Select select => m_machines.TrySelect(
            index: select.Index,
            entry: select.Entry,
            expectedContentHash: expectedContentHash
        ),
            WorldScreenOp.SetOptions options => ((m_machines.TryReconfigure(
            index: options.Index,
            options: options.Options
        ) is (var optionsOk, var optionsMessage))
            ? (optionsOk, optionsMessage, ((string?)null))
            : default),
            WorldScreenOp.Link link => ((m_machines.TryLink(
            name: link.Name,
            members: link.Members
        ) is (var linkOk, var linkMessage))
            ? (linkOk, linkMessage, ((string?)null))
            : default),
            WorldScreenOp.Unlink unlink => ((m_machines.TryUnlink(name: unlink.Name) is (var unlinkOk, var unlinkMessage))
            ? (unlinkOk, unlinkMessage, ((string?)null))
            : default),
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(op),
            actualValue: op,
            message: $"no {nameof(TryApplyScreenOp)} apply arm for screen op kind '{op.GetType().Name}'."
        ),
        };

        Console.Error.WriteLine(value: $"[{verb}: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: !ok,
            Kind: WorldEditEchoKind.ScreenOp,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
        // The content signature (a real hash, or WorldMachineHost's "content absent" sentinel) rides the tape
        // REGARDLESS of whether the op succeeded — a FAILED insert/select must refuse the identical way on
        // replay, or refuse BY NAME the moment the file's on-disk state has since changed (present when it was
        // absent, or vice versa). Gating this on `ok` would tape a failed insert with a null hash, which replays
        // as an UNPINNED live insert — silently diverging if the file later became readable.
        ScreenOpTap?.Invoke(
            arg1: op,
            arg2: contentHash,
            arg3: principal
        );

        // Latch AnyScreenOpEverApplied for EVERY op that reaches dispatch — not just `ok` ones. A host-level
        // refusal is not uniformly mutation-free: TrySelect moves slot.SelectedEntry BEFORE booting and retains
        // the new selector when the boot fails, so a pre-record failed select still diverges the live host from
        // the definition a recording would capture. Grant denials return before this point and mutate nothing;
        // past the authority gate, over-blocking a recording is safe and under-blocking is a silent divergence.
        AnyScreenOpEverApplied = true;

        return ok;
    }
    // Build the SDF contact field for a candidate — null when the requirements permit analytic contact (the set is
    // derived inside the population's compile, not here), the built field under the FIELD provider, or a
    // named failure when a solid names an op the warp-free evaluator cannot interpret.
    private static bool TryBuildSolids(WorldDefinition definition, out WorldSolidField? solids, out string reason) {
        reason = string.Empty;

        if (
            !WorldContactSelection.RequiresField(collision: definition.Collision) &&
            !WorldTargetSelection.RequiresLineOfSight(definition: definition)
        ) {
            solids = null;

            return true;
        }

        return WorldSolidField.TryBuild(
            built: out solids,
            definition: definition,
            reason: out reason
        );
    }
    // The Control check over a screen op's targeted screen(s): every op names exactly one index except Link (every
    // named member) and Unlink (every member of the ALREADY-LIVE link by that name, when one exists — mirroring the
    // pre-inversion console module's own "control over every member is required to sever" rule; a missing link
    // passes this check trivially and falls through to TryUnlink's own honest "no link" refusal).
    private bool TryCheckScreenOpControl(WorldScreenOp op, WorldPrincipal principal, out int denial) {
        denial = -1;

        var indices = ScreenOpTargets(op: op);

        foreach (var index in indices) {
            if (m_grants.Allows(
                principal: principal,
                capability: WorldCapability.Control,
                subject: GrantSubject.Screen(index: index)
            ) is { IsAllowed: false }) {
                denial = index;

                return false;
            }
        }

        return true;
    }

    /// <summary>Applies an authority command to its target body. Synchronous at submit (see the class summary), so a
    /// policy read following the command in the same batch observes its effect. A command whose entity is not live
    /// no-ops (validation happened at submit; the miss is benign).</summary>
    /// <param name="command">The command to apply.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller (replay, the addon runtime) with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public void ApplyCommand(WorldCommand command, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        ArgumentNullException.ThrowIfNull(argument: command);

        // The control-application commands are Control-over-TARGET commands, never Drive-over-BODY ones — the
        // generic gate below does not apply to them at all, so they branch out first. Both apply through
        // Server.WorldEngagement, which runs its own check-then-mutate (see its own remarks); nothing here
        // duplicates that check.
        switch (command) {
            case WorldCommand.ComposeControl compose:
                if (!CheckEngagePolicy(
                    entityIndex: compose.EntityIndex,
                    target: compose.Target,
                    reason: out var reason
                )) {
                    Console.Error.WriteLine(value: $"[world.engage denied: {reason}]");

                    return;
                }

                _ = m_engagement.Compose(
                    entityIndex: compose.EntityIndex,
                    target: compose.Target,
                    exclusive: compose.Exclusive,
                    actingPrincipal: compose.Principal,
                    targetPrincipal: compose.TargetPrincipal
                );

                return;
            case WorldCommand.DissolveControl dissolve:
                _ = m_engagement.Dissolve(
                    entityIndex: dissolve.EntityIndex,
                    actingPrincipal: dissolve.Principal,
                    targetPrincipal: dissolve.TargetPrincipal
                );

                return;
        }

        // CC/death gating (Seam A) — see TryDriveGateVerdict's own remarks: the SAME rule ApplyIntentSubmission
        // consults, checked here BEFORE the ordinary grant-table lookup so a scripted tape segment
        // (body.fly/EnqueueSegment) or any other authority command is refused by the identical state fact a raw
        // per-tick submission is, never a lesser door a script could walk around.
        var gated = TryDriveGateVerdict(
            bodyIndex: command.EntityIndex,
            verdict: out var gatedVerdict
        );
        var verdict = (gated
            ? gatedVerdict
            : m_grants.Allows(
                principal: command.Principal,
                capability: WorldCapability.Drive,
                subject: GrantSubject.Body(index: command.EntityIndex)
            )
        );

        if (!verdict.IsAllowed) {
            var denial = verdict.DescribeRefusal(
                actor: command.Principal,
                dropped: $"{command.GetType().Name} dropped",
                subject: $"body:{command.EntityIndex}",
                verb: "drive"
            );

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: denial,
                Rejected: true,
                Kind: WorldEditEchoKind.GrantTable,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));
            NoteDriveRefusalIfTracked(
                command: command,
                reason: denial
            );

            return;
        }

        if (Body(index: command.EntityIndex) is not { } body) {
            NoteDriveRefusalIfTracked(
                command: command,
                reason: $"body:{command.EntityIndex} is inactive"
            );

            return;
        }

        switch (command) {
            case WorldCommand.SnapPose snap:
                switch (snap.Mode) {
                    case SnapPoseMode.Pose:
                        body.Pose(
                            x: snap.Position.X,
                            y: snap.Position.Y,
                            z: snap.Position.Z,
                            yawRadians: snap.YawRadians,
                            pitchRadians: snap.PitchRadians,
                            rollRadians: snap.RollRadians
                        );
                        break;
                    default:
                        throw new InvalidOperationException(message: $"SnapPose mode value {((int)snap.Mode)} reached the server without codec validation.");
                }

                break;
            case WorldCommand.EnqueueSegment segment:
                body.EnqueueRun(
                    intent: segment.Intent,
                    seconds: segment.Seconds
                );

                break;
            case WorldCommand.PressChannel press:
                if (press.HoldSeconds is { } holdSeconds) {
                    var holdCeiling = FixedQ4816.FromRawBits(value: m_grants.HoldCeiling(
                        principal: press.Principal,
                        subject: GrantSubject.Body(index: press.EntityIndex)
                    ));
                    var outcome = body.PressChannel(
                        ordinal: press.ChannelOrdinal,
                        value: press.Value,
                        holdSeconds: holdSeconds,
                        authoredMaximum: holdCeiling
                    );

                    // The submit drains synchronously, so body.press's handler can read this back immediately —
                    // the same MotionRefusal/StopOutcome read-back shape — and name a silent grant-budget truncation
                    // instead of echoing the requested duration as if it were honored. Clears any prior refusal note
                    // this body's press slot carried, so a stale denial can never bleed into a fresh success.
                    m_population.NotePressOutcome(
                        bodyIndex: press.EntityIndex,
                        outcome: outcome
                    );
                } else {
                    body.PressChannel(
                        ordinal: press.ChannelOrdinal,
                        value: press.Value
                    );
                    m_population.NotePressSuccess(bodyIndex: press.EntityIndex);
                }

                break;
            case WorldCommand.SetBodyMotion motion:
                // The runtime door: a program that exists is not automatically one this body's kit can run. Coherence
                // is the SAME check WorldDefinitionValidator runs at boot (WorldDefinitionValidator.TryValidateProgramCoherence)
                // — reusing it here is what keeps a document-legal kit from runtime-switching into an incoherent program.
                // Refusal narrates through the SAME echo path world.designation/world.grant use (stderr line + EchoTap),
                // and records on the population so the SYNCHRONOUS submitter (body.motion's handler) can read back the
                // true outcome instead of assuming success.
                if (
                    !m_population.TryGetBodyMotionProgram(
                    name: motion.BodyMotionProgram,
                    out var targetMotionProgram
                ) ||
                    (targetMotionProgram is not { } resolvedMotionProgram)
                ) {
                    var reason = $"body motion program '{motion.BodyMotionProgram}' is not declared";

                    m_population.NoteMotionRefusal(
                        bodyIndex: motion.EntityIndex,
                        reason: reason
                    );
                    Console.Error.WriteLine(value: $"[body.motion refused: {reason}]");
                    EchoTap?.Invoke(obj: new WorldEditEcho(
                        Message: reason,
                        Rejected: true,
                        Kind: WorldEditEchoKind.BodyMotion,
                        ConnectionId: connectionId,
                        CorrelationId: correlationId
                    ));
                } else if (!WorldDefinitionValidator.TryValidateProgramCoherence(
                    model: m_population.KitMotion(index: motion.EntityIndex),
                    program: resolvedMotionProgram,
                    reason: out var coherenceReason
                )) {
                    m_population.NoteMotionRefusal(
                        bodyIndex: motion.EntityIndex,
                        reason: coherenceReason
                    );
                    Console.Error.WriteLine(value: $"[body.motion refused: {coherenceReason}]");
                    EchoTap?.Invoke(obj: new WorldEditEcho(
                        Message: coherenceReason,
                        Rejected: true,
                        Kind: WorldEditEchoKind.BodyMotion,
                        ConnectionId: connectionId,
                        CorrelationId: correlationId
                    ));
                } else {
                    body.SetBodyMotionProgram(programName: motion.BodyMotionProgram);
                    m_population.NoteMotionRefusal(
                        bodyIndex: motion.EntityIndex,
                        reason: string.Empty
                    );
                    EchoTap?.Invoke(obj: new WorldEditEcho(
                        Message: $"body:{motion.EntityIndex} motion={motion.BodyMotionProgram}",
                        Rejected: false,
                        Kind: WorldEditEchoKind.BodyMotion,
                        ConnectionId: connectionId,
                        CorrelationId: correlationId
                    ));
                }

                break;
            case WorldCommand.SetControl control:
                if (m_population.SupportsSource(
                    index: control.EntityIndex,
                    source: control.Source,
                    refusal: out var sourceRefusal
                )) {
                    body.SetIntentSource(source: control.Source);
                } else {
                    Console.Error.WriteLine(value: $"[body.control refused: {sourceRefusal}]");
                }

                break;
            case WorldCommand.Reconcile reconcile:
                var continuity = body.Reconcile(
                    x: reconcile.X,
                    z: reconcile.Z,
                    yawRadians: reconcile.YawRadians,
                    seconds: reconcile.Seconds
                );
                Console.Error.WriteLine(value: $"[body.reconcile: body:{reconcile.EntityIndex} continuity={continuity.ToString().ToLowerInvariant()} maxSmoothError={m_definition.Motion.MaxSmoothError:0.###}]");

                break;
            case WorldCommand.Stop:
                // The submit drains synchronously (WorldServer.Submit), so body.stop's handler can read this back
                // through WorldPopulation.LastStopOutcome the instant control returns to it — the same pattern
                // body.motion's MotionRefusal read-back uses.
                m_population.NoteStopOutcome(
                    bodyIndex: command.EntityIndex,
                    outcome: body.Stop()
                );

                break;
            case WorldCommand.LoadDurableState load:
                if (load.Tick != NextInputTick) {
                    Console.Error.WriteLine(value: $"[body.state-load refused: tick {load.Tick} is not next tick {NextInputTick}]");
                } else if (!body.TryStageDurableState(
                    tick: load.Tick,
                    values: load.Values,
                    requirePlayerWritable: true,
                    writer: load.Principal.Describe(),
                    reason: out var stateReason
                )) {
                    Console.Error.WriteLine(value: $"[body.state-load refused: {stateReason}]");
                }

                break;
        }
    }
    /// <summary>Applies a live window-composition override synchronously (the <c>view.override layout</c>/<c>view.override camera</c>
    /// path). Checks <see cref="WorldCapability.Control"/> over
    /// <see cref="GrantSubject.Composition"/>; on accept pushes it to the client composer, on denial prints a loud line
    /// and changes nothing. Never durable — no document, no journal.</summary>
    /// <param name="composition">The composition override.</param>
    /// <param name="principal">The acting identity the override is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="composition"/> is <see langword="null"/>.</exception>
    public void ApplyComposition(WorldComposition composition, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        ArgumentNullException.ThrowIfNull(argument: composition);

        if (m_grants.Allows(
            principal: principal,
            capability: WorldCapability.Control,
            subject: GrantSubject.Composition
        ) is { IsAllowed: false } verdict) {
            var denial = verdict.DescribeRefusal(
                actor: principal,
                dropped: $"{composition.GetType().Name} dropped",
                subject: "composition",
                verb: "control"
            );

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: denial,
                Rejected: true,
                Kind: WorldEditEchoKind.GrantTable,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return;
        }

        m_output.DeliverComposition(composition: composition);
    }
    /// <summary>Validates and applies one subject-bearing target-register write.</summary>
    public bool ApplyDesignation(WorldDesignation designation, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        return ApplyDesignationCore(
            connectionId: connectionId,
            correlationId: correlationId,
            designation: designation,
            knownSubject: false,
            principal: principal
        );
    }
    /// <summary>Applies one screen op synchronously, under the ordinary Control-authority gate — the public entry
    /// point <see cref="WorldReplaySnapshot.Drive"/> re-applies a recorded <see cref="WorldReplayEntry.ScreenOp"/>
    /// through (mirroring <see cref="ApplyCommand"/>/<see cref="Grant"/>/<see cref="Revoke"/>'s own re-drive shape:
    /// a live screen op never buffers, so a replayed one does not either).</summary>
    /// <param name="op">The screen op.</param>
    /// <param name="principal">The acting identity the op is checked against.</param>
    /// <param name="expectedContentHash">Replay only: the CAS pin a recorded <see cref="WorldScreenOp.Insert"/> or
    /// machine-booting <see cref="WorldScreenOp.Select"/> entry carries (a real <c>sha256-64</c> hash, or
    /// <see cref="WorldMachineHost"/>'s "content absent" sentinel when the recording itself never read the file) —
    /// see <see cref="WorldMachineHost.TryInsert"/>'s own remarks. <see langword="null"/> for every other op kind
    /// and for the live path.</param>
    public void ApplyScreenOp(WorldScreenOp op, WorldPrincipal principal, string? expectedContentHash = null) =>
        TryApplyScreenOp(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            expectedContentHash: expectedContentHash,
            op: op,
            principal: principal
        );
    /// <summary>Attaches the mounted addon host this server pumps at the three pinned points of <see cref="Step"/>,
    /// mirroring <c>LoopbackTransport.Bind</c>'s one-shot wiring. Called once the host's guests have mounted, so the
    /// server never observes a half-built host. Also re-sizes the per-tick contention tracking to cover the addon
    /// writers that now exist beside the seat lanes.</summary>
    /// <param name="runtime">The mounted host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is <see langword="null"/>.</exception>
    public void AttachAddons(IWorldAddonHost runtime) {
        ArgumentNullException.ThrowIfNull(argument: runtime);

        m_addons = runtime;

        StageAddonContentionArrays(
            mountedCount: runtime.MountedCount,
            entity: out var entity,
            principal: out var principal,
            collided: out var collided
        );

        if (entity is not null) {
            m_tickWrittenEntity = entity;
            m_tickWrittenPrincipal = principal!;
            m_tickCollided = collided!;
        }
    }

    // Pre-sizes the per-tick addon contention tracking against a plan's own MountedCount (or, at boot/AttachAddons,
    // the runtime's already-committed count) — called BEFORE the addon plan's own Commit, so the caller can adopt
    // the new arrays by reference in the same breath as the plan itself publishes, with no allocation at that
    // instant. Two lanes per mounted guest beside the seats: an addon holds Drive over as many bodies as it was
    // granted, so this is a sized BOUND on how many distinct entities one tick's contention tracking follows, never
    // a limit on how many a guest may drive. Past it, ReportContention's defensive length check stops recording new
    // entities and contention reporting saturates. Every array stays null when the capacity did not move, so an
    // addon-affecting mutation that leaves MountedCount unchanged allocates nothing here either.
    private void StageAddonContentionArrays(int mountedCount, out int[]? entity, out WorldPrincipal[]? principal, out bool[]? collided) {
        var capacity = (Population.LocalSeatCount + (mountedCount * 2));

        if (capacity == m_tickWrittenEntity.Length) {
            entity = null;
            principal = null;
            collided = null;

            return;
        }

        entity = new int[capacity];
        principal = new WorldPrincipal[capacity];
        collided = new bool[capacity];
    }

    /// <summary>Attaches a client sink the per-tick snapshot is delivered to, immediately delivering the live
    /// definition followed by a primer snapshot of the current table, so the client renders the current state before
    /// its first ordinary tick delivery. A subscribe, not an overwrite: <see cref="WorldOutputHub"/> supports more
    /// than one attached sink (play-and-host — a local sink plus N future connections plus the tape all
    /// subscribing), so a second call adds a second subscriber rather than displacing the first.</summary>
    /// <param name="sink">The sink to deliver snapshots to.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed — see
    /// <see cref="WorldOutputHub.Subscribe(IClientSink)"/> for the threading/idempotency contract. Disposal takes the sink out of
    /// every future delivery; it never retracts what the primer or an earlier tick already delivered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public IDisposable AttachSink(IClientSink sink) =>
        AttachSink(
            sink: sink,
            disclosure: WorldSinkDisclosure.Full
        );
    /// <summary>Attaches a sink whose snapshot deliveries are filtered by <paramref name="disclosure"/> — see
    /// <see cref="AttachSink(IClientSink)"/> for the lifetime contract, which is identical. The attach primer is
    /// filtered the same way an ordinary tick's delivery is, so a redacted sink never sees an unredacted first
    /// frame.</summary>
    /// <param name="sink">The sink to deliver snapshots to.</param>
    /// <param name="disclosure">What this sink's observer is delivered.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public IDisposable AttachSink(IClientSink sink, in WorldSinkDisclosure disclosure) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        var lease = m_output.Subscribe(
            disclosure: in disclosure,
            sink: sink
        );

        // Both the definition and the primer go to the NEWLY attached sink only (not a hub-wide broadcast) — an
        // already-attached sink must not replay a stale definition/snapshot every time a later sink joins. Isolated
        // the SAME way WorldOutputHub isolates an ordinary tick delivery fault (its own remarks): a sink that throws
        // during its own attach primer must not take down whoever called AttachSink, and is detached before it ever
        // reaches an ordinary tick delivery.
        try {
            sink.DeliverDefinition(definition: m_definition);

            var primer = BuildPrimerSnapshot();

            if (disclosure.IsFull) {
                sink.DeliverSnapshot(snapshot: in primer);
            } else {
                var scratch = Array.Empty<EntitySnapshot>();
                var redacted = WorldOutputHub.Redact(
                    disclosure: in disclosure,
                    scratch: ref scratch,
                    snapshot: in primer
                );

                sink.DeliverSnapshot(snapshot: in redacted);
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[world.output: {sink.GetType().Name} threw during its own attach primer — detached] {exception}");
            lease.Dispose();
        }

        return lease;
    }
    /// <summary>Buffers one live world mutation for the next <see cref="Step"/> (drained before intents). Retains the
    /// submitting envelope's connection/correlation identity so the eventual accept/reject <see cref="WorldEditEcho"/>
    /// routes back to the submitter (see <see cref="WorldEditEcho.ConnectionId"/>) — a deferred op's echo fires later
    /// than its submission, so that identity must travel with the buffered entry rather than being read live.</summary>
    /// <param name="mutation">The mutation to apply.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="sourceAddonInstanceId">The mounted addon instance token this mutation was decoded from, or
    /// <c>-1</c> for a console/client submission (the addon mutation seam's completion field — see
    /// <see cref="PendingOp.Mutate"/>).</param>
    /// <param name="actOrdinal">The addon's own output-batch ordinal this mutation answers, when
    /// <paramref name="sourceAddonInstanceId"/> is not <c>-1</c>.</param>
    /// <param name="outcomeObserved">Invoked once, with whether this exact mutation applied, the moment
    /// <see cref="Step"/> drains and applies it — <see langword="null"/> for every caller but
    /// <see cref="ApplyEnvelope"/>'s own dispatch (see <see cref="MutationOutcomeTap"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> is <see langword="null"/>.</exception>
    public void EnqueueMutation(WorldMutation mutation, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, long sourceAddonInstanceId = -1L, ushort actOrdinal = 0, Action<bool>? outcomeObserved = null) {
        ArgumentNullException.ThrowIfNull(argument: mutation);

        m_pending.Enqueue(item: new PendingOp.Mutate(
            ActOrdinal: actOrdinal,
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            Mutation: mutation,
            OutcomeObserved: outcomeObserved,
            SourceAddonInstanceId: sourceAddonInstanceId
        ));
    }
    /// <summary>Buffers a whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>)
    /// for the next <see cref="Step"/> (drained before intents). Retains the submitting envelope's
    /// connection/correlation identity — see <see cref="EnqueueMutation"/>'s own remarks.</summary>
    /// <param name="request">The rebuild request.</param>
    /// <param name="principal">The acting identity the rebuild is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="expectedContentHash">Replay only: the CAS content hash a recorded tape entry pins. When set,
    /// <see cref="ApplyRebuild"/> compares it against the hash it computes for this drive's own resolved candidate
    /// (its own base for Reset, a fresh re-read of <see cref="WorldRebuildRequest.PathHint"/> for Load/Reload) and
    /// refuses by name on a mismatch, before any other guard runs. <see langword="null"/> (the default) is the live
    /// path — nothing to compare against, since the live drive is what establishes the hash a later recording pins.
    /// <see cref="WorldReplaySnapshot.Drive"/> is the one caller that ever passes a non-null value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public void EnqueueRebuild(WorldRebuildRequest request, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, string? expectedContentHash = null) {
        ArgumentNullException.ThrowIfNull(argument: request);

        string? preparationFailure = null;

        // A carried document is available at submission time, outside Step. Prove its neighbour claims here and carry
        // any refusal into the ordered tick-boundary decision; ApplyRebuild repeats only document-local checks.
        var rebuildNeighbours = ((request.PathHint is { } candidatePath)
            ? ResolveRebuildNeighbours(path: candidatePath)
            : Neighbours
        );

        if (
            (request.Definition is { } supplied) &&
            !WorldDefinitionValidator.TryValidate(
            definition: supplied,
            neighbours: rebuildNeighbours,
            reason: out var proofReason
        )
        ) {
            preparationFailure = $"cross-document load proof failed before enqueue — {proofReason}";
        }

        m_pending.Enqueue(item: new PendingOp.Rebuild(
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            ExpectedContentHash: expectedContentHash,
            PreparationFailure: preparationFailure,
            Principal: principal,
            Request: request
        ));
    }
    /// <summary>Resolves the proof transport appropriate to one replacement document path.</summary>
    public IWorldNeighbourResolver? ResolveRebuildNeighbours(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        return (RebuildNeighbours?.Invoke(arg: path) ?? Neighbours);
    }
}
