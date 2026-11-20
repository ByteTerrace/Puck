using Puck.Abstractions.Waiting;
using Puck.Hosting.Jobs;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Set only while the single-threaded mutation apply call is running. It lets the deferred typed completion carry
    // the exact refusal detail already emitted by the legacy echo path without changing the composition/staging code.
    private string? m_lastMutationFailureDetail;

    private string m_lastMutationFailureCode = "world.mutation.refused";

    internal string LastMutationFailureCode => m_lastMutationFailureCode;

    // The presented action-state register file of every entity index, the borrowed scratch each snapshot entry's
    // State slices; overwritten by the next snapshot, like the entry array itself.
    private long[] m_snapshotState = [];

    internal bool ApplyDesignationCore(WorldDesignation designation, Principal principal, bool knownSubject, int connectionId, long correlationId) {
        var sourceIndex = designation.EntityIndex;

        if (
            (((uint)sourceIndex) >= ((uint)Host.Population.Capacity)) ||
            (Host.Body(index: sourceIndex) is not { } source)
        ) {
            return Refuse($"body:{sourceIndex} is not active");
        }
        if (!Host.Population.TryResolveTargetRegister(
            name: designation.Register,
            index: out var registerIndex
        )) {
            return Refuse($"register '{designation.Register}' is not declared");
        }

        var sourceSubject = GrantSubject.Body(index: sourceIndex);

        if (!knownSubject) {
            var drive = Host.GrantTable.Allows(
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
                !Host.Grants.IsOperator(principal: principal) &&
                (!Host.GrantTable.TryGetChannelReach(
                channels: source.Channels,
                mask: out var reach,
                principal: principal,
                subject: sourceSubject
            )
                    || !reach.ContainsRegister(index: registerIndex))
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

                if (!Host.Population.DesignationWithinEnvelope(
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

            Host.Population.SetDesignation(
                bodyIndex: sourceIndex,
                registerIndex: registerIndex,
                target: WorldTargetDesignation.AtPoint(point: point)
            );
            var pointMessage = string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"body:{sourceIndex} {designation.Register}=at:{((double)point.X):0.###},{((double)point.Y):0.###},{((double)point.Z):0.###}"
            );

            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.designation", text: $"[world.designation: {pointMessage}]");
            }
            Host.EchoTap?.Invoke(obj: new WorldEditEcho(
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
            (((uint)targetIndex) >= ((uint)Host.Population.Capacity)) ||
            (Host.Body(index: targetIndex) is null)
        ) {
            return Refuse(((targetIndex == sourceIndex)
                ? "a body cannot designate itself"
                : $"body:{targetIndex} is not active"));
        }

        var targetSubject = GrantSubject.Body(index: targetIndex);

        if (!knownSubject) {
            var observe = Host.GrantTable.Allows(
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

            if (!Host.Population.DesignationWithinEnvelope(
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

        Host.Population.SetDesignation(
            bodyIndex: sourceIndex,
            registerIndex: registerIndex,
            target: WorldTargetDesignation.Body(index: targetIndex)
        );
        var message = $"body:{sourceIndex} {designation.Register}={targetSubject.Describe()}";

        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.designation", text: $"[world.designation: {message}]");
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: false,
            Kind: WorldEditEchoKind.Designation,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
        return true;

        bool Refuse(string reason, bool denied = false) {
            Host.Population.NoteDesignationRefusal(
                bodyIndex: sourceIndex,
                reason: reason
            );
            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.designation refused", text: $"[world.designation refused: {reason}]");
            }
            Host.EchoTap?.Invoke(obj: new WorldEditEcho(
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
    internal WorldSubmissionResult? ApplyEnvelope(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) {
        if (TryRefuseStaleSession(envelope: in envelope, refusal: out var stale)) {
            return stale;
        }

        switch (envelope.Payload) {
            case WorldSubmissionPayload.Command command:
                ApplyCommand(
                    command: command.Value,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Grant grant:
                Host.Grant(
                    grant: grant.Value,
                    actor: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Revoke revoke:
                Host.Revoke(
                    grant: revoke.Value,
                    actor: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Session session:
                return new WorldSubmissionResult.Session(Reply: Host.ApplySession(request: session.Value));
            case WorldSubmissionPayload.Rebuild rebuild:
                SubmitRebuild(
                    request: rebuild.Value,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Mutation mutation:
                if (!WorldMutationBindingFactory.TryCreate(binding: out var binding, detail: out var bindingDetail, envelope: in envelope)) {
                    return new WorldSubmissionResult.Refusal(
                        Code: "world.mutation.ingress-refused",
                        Detail: bindingDetail
                    );
                }
                if (RefuseActivationMismatch(binding: binding, envelope: in envelope, expected: mutation.ExpectedActivation) is { } mismatch) {
                    return mismatch;
                }
                // The tape's one mutation ingress: fires here rather than at the loopback so a forwarded traveller's
                // submission and an admitted peer's are captured on the same terms as a local one, each with the
                // actor its own envelope stamped. See WorldServer.MutationTap.
                Host.MutationTap?.Invoke(
                    arg1: mutation.Value,
                    arg2: envelope.Principal
                );
                EnqueueMutation(
                    mutation: mutation.Value,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId,
                    binding: binding,
                    completion: completion,
                    // Threaded through the buffered op itself, never a generic drain-wide firing: only THIS dispatch
                    // point — the one MutationTap above also covers — ever supplies a completion, so a guest's
                    // decoded act and a rule's generate effect (both call EnqueueMutation directly) never produce one.
                    outcomeObserved: ((Host.MutationOutcomeTap is { } outcomeTap)
                    ? (applied => outcomeTap(
                        arg1: mutation.Value,
                        arg2: applied
                    ))
                    : null)
                );

                // The typed mutation result is delivered from DrainPendingOps after actual apply. Returning null
                // transfers the ordered entry's completion ownership to that pending operation.
                return null;
            case WorldSubmissionPayload.Undo undo:
                Host.EnqueueUndo(
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
                Host.ApplySessionLever(
                    lever: lever.Value,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                );

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Query query:
                return new WorldSubmissionResult.Query(Answer: Host.RuleHost.AnswerSubmittedQuery(
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
                    correlationId: envelope.CorrelationId
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
            case WorldSubmissionPayload.Operation operation:
                return new WorldSubmissionResult.MachineOperation(Result: Host.ApplyMachineOperation(
                    operation: operation.Value,
                    principal: envelope.Principal,
                    connectionId: envelope.ConnectionId,
                    correlationId: envelope.CorrelationId
                ));
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
    // candidate (the server's own base for Reset, the console-resolved document for Load, a reload's next source revision
    // merged over the live document — or, on a REPLAY drive, a fresh re-read of the tape's path hint) → compute/check its
    // CAS content hash → validate → capacity-check → install as the change it is → journal RESET → re-mint every
    // admitted peer connection's admission grant when the authority moved (the document swap re-syncs group/ownership
    // grant state but never re-mints the admitted peers' admission grants on its own). A prepared admission replaces the
    // apply-time local validation only for the exact candidate and machine catalog it validated
    // (WorldDefinitionAdmission.AppliesTo); a reload's preparation is taken only while the revision and the live document
    // it was merged against are still the authority's, and is merged again here otherwise.
    internal bool ApplyRebuild(WorldRebuildRequest request, Principal principal, int connectionId, long correlationId, string? expectedContentHash = null, string? preparationFailure = null, WorldDefinitionAdmission? admission = null, WorldReloadPreparation? reload = null,
        double reconstructMilliseconds = 0, double admitMilliseconds = 0) {
        var reads = Host.DrawKeys.Reads;
        var accepted = false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try {
            accepted = ApplyRebuildCore(request, principal, connectionId, correlationId, expectedContentHash, preparationFailure, admission, reload);
            // Where an installed rebuild's time went off the tick (the delta's reconstruction, the candidate's
            // admission) and on it (the install), beside the console's own line for the source's compile.
            if (accepted && Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.rebuild", text: string.Create(provider: System.Globalization.CultureInfo.InvariantCulture,
                    handler: $"[world.rebuild] {RebuildVerb(kind: request.Kind)} reconstruct={reconstructMilliseconds:0} ms admit={admitMilliseconds:0} ms install={System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started).TotalMilliseconds:0} ms"));
            }
            return accepted;
        } finally {
            if (!accepted && reads != Host.DrawKeys.Reads) { Host.DrawKeys.AdvanceGeneration(); }
        }
    }

    private bool ApplyRebuildCore(WorldRebuildRequest request, Principal principal, int connectionId, long correlationId, string? expectedContentHash, string? preparationFailure, WorldDefinitionAdmission? admission, WorldReloadPreparation? reload) {
        var verb = RebuildVerb(kind: request.Kind);

        if (!Host.Grants.IsOperator(principal: principal)) {
            DenyGrantTable(connectionId: connectionId, correlationId: correlationId,
                denial: $"{verb} requires the operator", echoKind: WorldEditEchoKind.Rebuild);
            return false;
        }
        var priorDefinition = m_definition;
        var preRebuildPeerRows = new Dictionary<int, IReadOnlyList<WorldGrant>>();

        for (var peerIndex = Host.Population.LocalSeatCount; (peerIndex < Host.Population.Capacity); peerIndex++) {
            if (
                !Host.Population.IsAdmittedPeer(bodyIndex: peerIndex) ||
                Host.Population.IsParked(index: peerIndex)
            ) {
                continue;
            }

            preRebuildPeerRows[peerIndex] = Host.GrantTable.Rows(principal: Host.Population.PeerPrincipal(index: peerIndex));
        }



        if ((request.Delta is not null) && (preparationFailure is { } deltaFailure)) {
            RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: deltaFailure, verb: verb);
            return false;
        }

        // THE CAS RESOLUTION, FIRST — before any refusal gate, so a rebuild the door goes on to refuse below is
        // still taped and reproduces as the identical refusal on replay (RebuildTap's own remarks).
        //   Reset: the candidate is THIS DRIVE'S OWN current base (m_base) — live or replay, always freshly read
        //   here, never the request's (Reset carries none). Its hash is the base's canonical bytes AT THIS MOMENT,
        //   which is why it must be computed here rather than at submission (m_base can move between submission and
        //   drain — see EnqueueRebuild's remarks).
        //   Load: request.Definition is non-null on the LIVE path (the console already read + validated the file and
        //   computed request.ContentHash from those exact bytes) and null on a REPLAY drive (the tape never embeds the
        //   document — WorldReplaySnapshot.Drive passes only Kind/Origin/Force/ContentHash, so a re-drive proves the file
        //   on disk still matches what was recorded rather than trusting a stored copy).
        //   Reload: the next source revision, reconstructed from the delta over the revision this authority holds (or the
        //   whole document a holder supplied, or a replay drive's re-read), merged over the live document below.
        WorldDefinition candidate;
        WorldDefinition? next = null;
        string contentHash;
        IReadOnlyList<string> conflicts = [];
        byte[]? journalPatch = null;
        var prepared = ((reload is not null) && ReferenceEquals(objA: reload.Revision, objB: m_revision.Definition) && ReferenceEquals(objA: reload.Live, objB: priorDefinition))
            ? reload
            : null;

        if (request.Kind == WorldRebuildKind.Reset) {
            candidate = m_base;
            contentHash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate));
        } else if (prepared is not null) {
            candidate = prepared.Candidate;
            next = prepared.Next;
            conflicts = prepared.Conflicts;
            journalPatch = prepared.JournalPatch;
            contentHash = (request.ContentHash ?? throw new InvalidOperationException(message: $"{verb}: a reload request carrying a delta must also carry its content hash."));
        } else if ((request.Kind == WorldRebuildKind.Reload) && (request.Delta is { } delta)) {
            if (!delta.TryReconstruct(m_revision.Definition, out next, out var reconstructReason)) {
                RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: reconstructReason, verb: verb);
                return false;
            }
            candidate = next;
            contentHash = (request.ContentHash ?? throw new InvalidOperationException(message: $"{verb}: a reload request carrying a delta must also carry its content hash."));
        } else if (request.Definition is { } supplied) {
            candidate = supplied;
            contentHash = (request.ContentHash ?? throw new InvalidOperationException(message: $"{verb}: a Load/Reload request carrying a document must also carry its content hash."));
        } else if (request.Origin is not WorldRebuildOrigin.File { Path: var path }) {
            throw ReplayRefusal.RebuildSourceUnavailable.Raise(message: ((request.Origin is WorldRebuildOrigin.Store store)
                ? $"{verb}: a hosted world's rebuild is replayed from its store ('{store}'), which this drive cannot read."
                : $"{verb}: a Load/Reload request with no embedded document must carry a file origin to re-read for replay."));
        } else {
            candidate = RereadForReplay(
                contentHash: out contentHash,
                edit: (request.Kind == WorldRebuildKind.Reload),
                path: path,
                verb: verb
            );
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
                : $"'{request.Origin}'"
            );

            throw ReplayRefusal.RebuildContentMismatch.Raise(message: $"{verb}: content hash mismatch on {pinned} — found {contentHash}, expected {expected} (recorded). The pinned content has changed since this recording was made; re-record it.");
        }

        // A reload installs the change it is: the next source revision's change from the revision this authority holds,
        // merged over the live document, which keeps every edit the session made that the source did not also change
        // and holds the live document's own instance of every member and row the change leaves equal. A merge prepared
        // off the tick is taken while its revision and live document still stand; any other holder of the next revision
        // (a replay drive's re-read, a supplied document, a delta whose preparation an install overtook) merges here by
        // the same computation, so a live run and its replay install the same candidate.
        if ((request.Kind == WorldRebuildKind.Reload) && (prepared is null)) {
            next = candidate;
            try {
                var change = (request.Delta ?? WorldReloadDelta.Between(previous: m_revision.Definition, next: next));

                if (!change.TryMergeOver(m_revision.Definition, next, priorDefinition, out var merged, out conflicts, out journalPatch, out var mergeReason)) {
                    RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: mergeReason, verb: verb);
                    return false;
                }
                candidate = merged;
            } catch (InvalidDataException exception) {
                RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: exception.Message, verb: verb);
                return false;
            }
        }
        // A loaded document is the source revision it installs from.
        next ??= candidate;

        var keepAuthority = ((request.Kind == WorldRebuildKind.Reload) && priorDefinition.Grants.SequenceEqual(second: candidate.Grants) &&
            (ReferenceEquals(objA: priorDefinition.Admission, objB: candidate.Admission) || System.Text.Json.JsonSerializer.Serialize(priorDefinition.Admission,
                WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(IReadOnlyList<WorldAdmissionEntry>))) ==
            System.Text.Json.JsonSerializer.Serialize(candidate.Admission,
                WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(IReadOnlyList<WorldAdmissionEntry>)))));

        Host.RebuildTap?.Invoke(
            arg1: request,
            arg2: principal,
            arg3: contentHash
        );

        // A rebuild can touch any section: the principal must hold Mutate over EVERY section — the same door
        // world.load/world.undo have always used.
        if (!Host.GrantTable.AllowsAllSections(
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
        // than silently discarding unsaved work. Reset discards that work explicitly, and reload adds to it.
        if (
            (request.Kind == WorldRebuildKind.Load) &&
            !request.Force &&
            (m_journal.Count > 0)
        ) {
            var denial = $"{m_journal.Count} unsaved mutation(s) would be discarded — world.save first, world.reset to discard them without loading a new document, or world.load {request.Origin} force to discard them and load anyway";

            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.load rejected", text: $"[world.load rejected: {denial}]");
            }
            Host.EchoTap?.Invoke(obj: new WorldEditEcho(
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

        // A reload that changes nothing the simulation reads, or only state values, installs as the edit it is and keeps
        // every compiled product; anything else takes the full install below.
        var changedMembers = ((request.Kind == WorldRebuildKind.Reload) ? DescribeChanged(changed: WorldDocumentInputs.Changed(after: candidate, before: priorDefinition)) : string.Empty);

        if ((request.Kind == WorldRebuildKind.Reload) && keepAuthority) {
            var kind =((prepared is not null) ? prepared.Kind : ReloadInstallKindOf(candidate: candidate, live: priorDefinition, rows: out _));

            if (kind != WorldInstallKind.Full) {
                return ApplyReloadEdit(
                    candidate: candidate,
                    changed: changedMembers,
                    conflicts: conflicts,
                    connectionId: connectionId,
                    contentHash: contentHash,
                    correlationId: correlationId,
                    kind: kind,
                    journalPatch: journalPatch,
                    next: next!,
                    prepared: prepared,
                    principal: principal,
                    request: request,
                    verb: verb
                );
            }
        }

        // Cross-document claims were proved before enqueue. Apply-time validation runs from Step, so it repeats only
        // document-local checks and never reaches transport from the tick path, and a prepared admission of this exact
        // candidate already holds them.
        if (candidate.AuthoredState.Any(r => r.Draw?.PrivateStream == true) || priorDefinition.StateRaw?.DrawInstallCount > 0) {
            var previous = Host.MaterializeArena();
            var fileLoad = request.Origin is WorldRebuildOrigin.File && (request.Kind == WorldRebuildKind.Reload || WorldDefinitionLoader.IsFileAdmission(candidate));
            if (fileLoad) {
                Host.DrawKeys.AdvanceGeneration();
                previous = null;
            } else if (request.Kind == WorldRebuildKind.Reset) {
                Host.DrawKeys.AdvanceGeneration();
                previous = WorldPrivateDrawKeys.WithLast(new WorldDefinition(), previous.StateRaw?.DrawInstallCount ?? 0);
            }
            if (!WorldDrawBootResolver.TryResolve(candidate, Host.InstanceIdentity, out candidate, out var drawReason, Host.DrawKeys, previous) ||
                !WorldStateDocumentValues.TryResolve(candidate, out candidate, out drawReason)) {
                RejectRebuild(verb, drawReason, connectionId, correlationId);
                return false;
            }
        }
        var admitted = ((admission is not null) && admission.AppliesTo(definition: candidate, machines: Host.Machines.ValidationCatalog))
            ? admission
            : null;
        var compilation = admitted?.Compilation;
        var validationReason = string.Empty;

        if ((compilation is null) && !WorldDefinitionValidator.TryValidateLocally(
            compilation: out compilation,
            definition: candidate,
            reason: out validationReason
        )) {
            RejectRebuild(
                connectionId: connectionId,
                correlationId: correlationId,
                reason: validationReason,
                verb: verb
            );

            return false;
        }

        // A loaded document's overrides bind against the CANDIDATE's own directory, not Host.PipelineSources (still
        // the currently installed document's), since the candidate is not installed until this whole gate passes.
        // A reset reinstalls the base already bound to its own directory.
        WorldPipelineSources? rebuildPipelineSources = null;

        if (request.Kind != WorldRebuildKind.Reset) {
            rebuildPipelineSources = new WorldPipelineSources(documentDirectory: candidate.DocumentDirectory);

            if (!TryBindPipelineRows(
                candidate: candidate,
                reason: out var pipelineReason,
                sourcesOverride: rebuildPipelineSources
            )) {
                RejectRebuild(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    reason: pipelineReason,
                    verb: verb
                );

                return false;
            }
        }

        if (candidate.Population.Capacity != Host.Population.Capacity) {
            RejectRebuild(
                verb: verb,
                reason: $"population capacity {candidate.Population.Capacity} differs from the boot-allocated capacity {Host.Population.Capacity}; restart the host to load it",
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

        // A reload rebuilds the products whose inputs its candidate changed, by the rule a mutation's install follows;
        // a load or a reset replaces the document whole, which changes every part.
        var products = PlanProducts(candidate: candidate, previous: priorDefinition, whole: (request.Kind != WorldRebuildKind.Reload));
        WorldDocumentInputs? envelopeInputs = null;
        WorldDocumentInputs? addonInputs = null;
        WorldDocumentInputs? machineInputs = null;

        if (!products.Envelope.IsEmpty) {
            if (!TryFitEnvelope(candidate: candidate, inputs: out var fitted, reason: out var capacityReason)) {
                RejectRebuild(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    reason: capacityReason,
                    verb: verb
                );

                return false;
            }
            envelopeInputs = fitted;
        }

        if (!Host.Population.CanInstallFields(
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

        if (!TryStageSolids(
            candidate: candidate,
            products: products,
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

        // Reconcile the addon runtime against the candidate when a part its last preparation read changed: a reused
        // row costs a structural compare, never a recompile. A server with no addon host attached (a test double;
        // production hosts attach WorldAddonRuntime) refuses a candidate holding an enabled row by name, the shape a
        // mutation's install uses, rather than installing a candidate that claims a mounted addon no host can run.
        IWorldAddonPreparedPlan? rebuildAddonPlan = null;
        int[]? newRebuildTickWrittenEntity = null;
        Principal[]? newRebuildTickWrittenPrincipal = null;
        bool[]? newRebuildTickCollided = null;
        var rebuildAddonPlanCommitted = false;

        IWorldMachinePreparedPlan? rebuildMachinePlan = null;
        var rebuildMachinePlanCommitted = false;
        var stateKept = string.Empty;

        // The whole sequence from here through Commit runs under ONE try/finally — see TryApplyMutation's identical
        // shape for why: rebuildAddonPlan starts null, so a return before TryPrepare ever succeeds leaves the
        // finally a no-op, and a downstream throw from contention-array staging, Install, or Commit alike still
        // disposes an uncommitted plan. Replay restores the complete guest image through the checkpoint boundary.

        try {
            if (products.Addons.IsEmpty) {
                // The addon host's last preparation read no part this candidate changed.
            } else if (Host.Addons is { } addonsForRebuild) {
                if (!TryPrepareAddons(
                    addons: addonsForRebuild,
                    candidate: candidate,
                    current: m_definition,
                    inputs: out var preparedAddons,
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
                addonInputs = preparedAddons;

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

            if (!products.Machines.IsEmpty) {
                if (!TryPrepareMachines(
                    admission: admitted,
                    candidate: candidate,
                    current: m_definition,
                    inputs: out var preparedMachines,
                    plan: out rebuildMachinePlan,
                    reason: out var rebuildMachineReason
                )) {
                    RejectRebuild(
                        connectionId: connectionId,
                        correlationId: correlationId,
                        reason: $"screen machine {rebuildMachineReason}",
                        verb: verb
                    );

                    return false;
                }
                machineInputs = preparedMachines;
            }

            // Candidate refusals precede arena preparation, which carries the boot-sized session lanes. Nothing
            // else crosses from the old layout but its key ledger: the candidate's declared rows settle at this tick.
            StateArena? rebuildArena = null;
            var installed = candidate;
            // A reload re-seeds the state rows its source changed and keeps every other row's live values, as a
            // mutation's install does: the live arena itself when no row changed, otherwise a replacement born with
            // the changed rows that loads the rest. A load or a reset, and a reload that changes the section beside
            // its rows, installs the document's state whole.
            var reloadEdit = ((request.Kind == WorldRebuildKind.Reload) ? NameReloadEdit(candidate: candidate) : null);

            if (reloadEdit is { } stateEdit) {
                candidate = stateEdit.Candidate;
                installed = candidate;
                if (Host.KeepsArena(candidate: candidate)) {
                    rebuildArena = Host.Arena;
                    stateKept = ", state kept";
                } else if (!Host.TryPrepareArenaReplacement(
                    definition: candidate,
                    edit: stateEdit,
                    prepared: out var editArena,
                    reason: out var editReason,
                    settled: out var settledEdit
                )) {
                    RejectRebuild(
                        connectionId: connectionId,
                        correlationId: correlationId,
                        reason: $"the state section does not load into an arena: {editReason}",
                        verb: verb
                    );

                    return false;
                } else {
                    rebuildArena = editArena;
                    installed = settledEdit;
                    stateKept = $", state rows re-seeded {stateEdit.Named!.Count} kept {stateEdit.Carried!.Count}";
                }
            } else if (!Host.ReloadsArenaInPlace(definition: candidate)) {
                if (!Host.TryPrepareArenaReplacement(
                    definition: candidate,
                    prepared: out var preparedArena,
                    reason: out var arenaReason,
                    settled: out var settledCandidate
                )) {
                    RejectRebuild(
                        connectionId: connectionId,
                        correlationId: correlationId,
                        reason: $"the state section does not load into an arena: {arenaReason}",
                        verb: verb
                    );

                    return false;
                }

                rebuildArena = preparedArena;
                installed = settledCandidate;
            }

            if (!Host.Input.TryValidateDefinition(installed, rebuildArena, out var inputModeReason, fresh: (request.Kind != WorldRebuildKind.Reload))) {
                RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: inputModeReason, verb: verb);
                return false;
            }

            var authorityAdmitted = (keepAuthority
                ? Host.GrantTable.TryPrepareDocument(actor: principal, candidate: installed, current: priorDefinition, grantsWrite: false, prepared: out var authority, reason: out var authorityReason)
                : Host.GrantTable.TryPrepareReset(actor: principal, candidate: installed, current: priorDefinition, prepared: out authority, reason: out authorityReason));

            if (!authorityAdmitted) {
                RejectRebuild(connectionId: connectionId, correlationId: correlationId, reason: authorityReason, verb: verb);
                return false;
            }
            if (request.Kind != WorldRebuildKind.Reload) { Host.Input.Restart(); }
            SwapSolids(solids: rebuildSolids);
            // A whole-document rebuild restarts scalar state and lattice stores together. Preserving one while
            // reseeding the other can duplicate finite stores; live mutations and checkpoints retain their own paths.

            Install(
                authority: authority!,
                arena: rebuildArena,
                compilation: compilation,
                continuity: ((reloadEdit is null) ? WorldArenaContinuity.Load : WorldArenaContinuity.Edit),
                definition: installed,
                products: products,
                rebuildKind: request.Kind
            );
            AdoptProductInputs(addons: addonInputs, envelope: envelopeInputs, machines: machineInputs);
            NarrateProducts(plan: products, population: products.RebuildsPopulation);
            // A lattice keeps its cells through an install whose state it kept; only a row whose draw pass the source
            // changed repaints. An install of the document's state whole restarts every lattice with it.
            if (reloadEdit is null) {
                Host.ReseedLatticeFields(definition: installed);
            } else {
                Host.RepaintChangedLatticeDraws(current: installed, previous: priorDefinition);
            }

            if (rebuildAddonPlan is not null) {
                Host.Addons!.Commit(plan: rebuildAddonPlan);
                rebuildAddonPlanCommitted = true;

                if (newRebuildTickWrittenEntity is not null) {
                    Host.AdoptContentionArrays(
                        collided: newRebuildTickCollided!,
                        entity: newRebuildTickWrittenEntity,
                        principal: newRebuildTickWrittenPrincipal!
                    );
                }
            }

            if (rebuildMachinePlan is not null) {
                Host.Machines.Commit(plan: rebuildMachinePlan);
                rebuildMachinePlanCommitted = true;
            }
        } finally {
            if (!rebuildAddonPlanCommitted) {
                rebuildAddonPlan?.Dispose();
            }

            if (!rebuildMachinePlanCommitted) {
                rebuildMachinePlan?.Dispose();
            }
        }

        // A new base starts a fresh deal lifecycle, even when its source rows equal the previous session's.
        // Keeping a completed memo would skip materialization after reset or a live replay drive. A reload merges over
        // the live document, whose dealt placements it keeps, so its deals stand.
        if (request.Kind != WorldRebuildKind.Reload) {
            Host.ResetPlacementDeals();
            m_journal.Clear();
        }

        // Snapshot, for every CURRENTLY CONNECTED (admitted, not parked) peer, exactly
        // which of its ORIGINAL admission-minted rows it still actually holds — BEFORE the reset below discards the
        // whole table. A row present at connection time but absent here is a live world.revoke the operator issued
        // against this exact peer since; RemintPeerAdmissionGrants must not resurrect it. A PARKED peer (disconnected,
        // inside its reconnect grace — WorldPopulation.IsAdmittedPeer stays true through that window) is excluded
        // outright: it has no live session to act through, so it is re-authorized (and, if still trusted, reminted)
        // only on an actual reconnect, never on a rebuild that happens to land during its grace window.
        if (!keepAuthority) {
            // Admitted PEER CONNECTIONS are the one exception: "admitted peers survive"
            // means their CONNECTION stays (WorldPopulation never dropped them — Install/Rebuild's own Install call
            // above left every admitted peer body active), but the reset above just wiped their admission grant along
            // with everything else, because a peer is not a boot-time seat and not a document row either. RE-AUTHORIZE
            // (never blindly re-mint) each one against the CANDIDATE's own current admission policy.
            Host.GrantTable.RemintPeerAdmissionGrants(
                candidate: candidate,
                preRebuildPeerRows: preRebuildPeerRows
            );
            // A live SESSION ends: the reset wiped its rows, and a screen observing through it re-admits under the candidate.
            Host.GrantTable.EndSessionsForRebuild();
        }

        // Finish runs here, AFTER the candidate's own grants have installed, never earlier: its capability
        // disclosure narration is computed lazily against the LIVE grant table at the moment each line actually
        // prints (see WorldAddonRuntime.Finish), so a rebuild that also moves Grants reports what the addon is
        // ACTUALLY granted under the candidate, never a mount report pinned to the table this rebuild just replaced.
        if (rebuildAddonPlanCommitted) {
            Host.Addons!.Finish(plan: rebuildAddonPlan!);
        }

        if (rebuildMachinePlanCommitted) {
            Host.Machines.Finish(plan: rebuildMachinePlan!);
        }

        // Load moves the undo base. Reload moves the independent source revision and appends to the undo history.
        string history;

        if (request.Kind == WorldRebuildKind.Reset) {
            history = $"base is {m_baseOrigin}, journal cleared";
        } else if (request.Kind == WorldRebuildKind.Load) {
            m_base = candidate;
            m_revision = new WorldSourceRevision(ContentHash: contentHash, Definition: next);
            m_baseOrigin = new WorldBaseOrigin(Kind: WorldBaseOriginKind.Load, Source: request.Origin);
            history = $"base is {m_baseOrigin}, journal cleared";
            // Installed as of this point: a later live commit resolves against the candidate's own directory too.
            Host.PipelineSources = rebuildPipelineSources;
        } else {
            history = AcceptReload(request, next, contentHash, journalPatch, WorldPrivateDrawKeys.Positions(m_definition));
            Host.PipelineSources = rebuildPipelineSources;
        }

        var message = $"{verb} applied — {history}{stateKept}{changedMembers}{DescribeConflicts(conflicts: conflicts)}";

        if ((request.Delta is { } appliedDelta) &&
            WorldSubmissionCodec.TryEncodeRebuild(request with { Definition = null }, out var payload, out _)) {
            message += $", delta={appliedDelta.Patch.Length} bytes payload={payload.Length} bytes";
        }

        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.definition", text: $"[world.definition: {message}]");
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: false,
            Kind: WorldEditEchoKind.Rebuild,
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            RebuildOrigin: ((request.Kind == WorldRebuildKind.Reset)
            ? null
            : request.Origin)
        ));

        return true;
    }
    // The non-consuming primer path for AttachSink: PEEKS every body's continuity hint instead of consuming it, so a
    // newly attached sink's boot-state primer can never steal the flag an already-attached sink is still due to
    // observe via the next ordinary EmitSnapshot broadcast (the bug this repairs — see docs/architecture/worlds.md's
    // "Observation and display" section). Stamped with the server's actual current tick/step width
    // (Host.CompletedTick/Host.LastStepTicks), which are still their default 0/0 before the very first Step has ever
    // run — the one case where 0/0 is the honest answer, preserved exactly.
    internal WorldSnapshot BuildPrimerSnapshot() {
        lock (Host.AuthorityGate) {
            Host.ThrowIfTickPending();
            return BuildSnapshotCore(Host.CompletedTick, Host.LastStepTicks, consumeContinuity: false);
        }
    }
    // Every live body's authoritative sim pose, color, archetype, and this tick's continuity hint, written into the
    // reused Host.SnapshotEntries array — the SAME borrowed-scratch shape as before the output hub: a typed subscriber
    // must fully consume (or copy) the returned WorldSnapshot before returning from DeliverSnapshot, because the next
    // tick's BuildSnapshot call overwrites this same backing array. Consumes (TakeContinuity) every body's one-shot
    // continuity hint — the ORDINARY per-tick broadcast path (EmitSnapshot). A late AttachSink must NOT call this
    // overload: see BuildPrimerSnapshot.
    internal WorldSnapshot BuildSnapshot(ulong tick, ulong stepTicks) => BuildSnapshotCore(
        consumeContinuity: true,
        stepTicks: stepTicks,
        tick: tick
    );

    private WorldSnapshot BuildSnapshotCore(ulong tick, ulong stepTicks, bool consumeContinuity) {
        var capacity = Host.Population.Capacity;
        EnsureSnapshotStateCapacity(capacity);
        return FinishSnapshot(tick, stepTicks, consumeContinuity, WriteSnapshotRows(Host.Population, consumeContinuity, end: capacity, first: 0, offset: 0));
    }

    internal struct SnapshotOperation {
        internal JobPool.ForkOperation Fork;
        internal int Partitions;
        internal int Count;
    }

    internal WaitOutcome TryBuildTickSnapshot(ulong tick, ulong stepTicks, ref SnapshotOperation operation, WaitDeadline deadline, out WorldSnapshot snapshot) {
        var capacity = Host.Population.Capacity;
        if (operation.Partitions == 0) {
            EnsureSnapshotStateCapacity(capacity);
            var partitions = capacity.CeilingDivide(divisor: SnapshotPartitionRows);
            if ((partitions > 1) && (Host.Jobs is { Serial: false }) &&
                ((operation.Count = CountSnapshotPartitions(capacity, partitions)) >= SnapshotForkRows)) {
                m_snapshotRows ??= new SnapshotRows(document: this);
                m_snapshotRows.Population = Host.Population;
                operation.Partitions = partitions;
            } else {
                operation.Count = WriteSnapshotRows(Host.Population, consumeContinuity: true, end: capacity, first: 0, offset: 0);
            }
        }
        if ((operation.Partitions > 0) &&
            (Host.Jobs!.Fork(operation.Partitions, m_snapshotRows!, ref operation.Fork, deadline) == WaitOutcome.DeadlinePassed)) {
            snapshot = default;
            return WaitOutcome.DeadlinePassed;
        }
        if (operation.Partitions > 0) { m_snapshotRows!.Population = null!; }
        snapshot = FinishSnapshot(tick, stepTicks, consumeContinuity: true, operation.Count);
        return WaitOutcome.Completed;
    }

    private void EnsureSnapshotStateCapacity(int capacity) {
        var stateWidth = (capacity * Host.Population.ActionStateLane.Definitions.Count);
        if (m_snapshotState.Length < stateWidth) { m_snapshotState = new long[stateWidth]; }
    }

    private WorldSnapshot FinishSnapshot(ulong tick, ulong stepTicks, bool consumeContinuity, int count) {
        var fieldsFull = !consumeContinuity;
        var latticeDeltas = m_latticeSnapshotDeltas;

        latticeDeltas.Clear();
        var regions = Host.Population.FieldRegions;

        for (var regionIndex = 0; (regionIndex < regions.Count); regionIndex++) {
            var region = regions[regionIndex];
            var deltas = region.Lattice.TakeDeltas(full: !consumeContinuity, isFull: out var regionFull);

            fieldsFull |= regionFull;
            foreach (var delta in deltas) {
                latticeDeltas.Add(item: new FieldCellDelta(Cell: delta.Cell, Field: delta.Field, Raw: delta.Raw,
                    Region: region.Name, FieldName: region.Lattice.Input.Fields[delta.Field].Name));
            }
        }
        // FieldsFull replaces the entire physical image, so a region requesting a primer also republishes
        // the other regions even when their cells did not move this tick.
        if (fieldsFull && consumeContinuity && (Host.Population.FieldRegions.Count > 1)) {
            latticeDeltas.Clear();
            for (var regionIndex = 0; (regionIndex < regions.Count); regionIndex++) {
                var region = regions[regionIndex];

                foreach (var delta in region.Lattice.TakeDeltas(full: true, isFull: out _)) {
                    latticeDeltas.Add(item: new FieldCellDelta(Cell: delta.Cell, Field: delta.Field, Raw: delta.Raw,
                        Region: region.Name, FieldName: region.Lattice.Input.Fields[delta.Field].Name));
                }
            }
        }
        // The kernel's own delta shape carries no Puck.World.Protocol reference; the wire shape is minted here,
        // the one caller that needs both.
        var stateCells = Host.PublishedCellCount(full: !consumeContinuity);
        var cellCount = (latticeDeltas.Count + stateCells);

        if (m_snapshotCells.Length < cellCount) { Array.Resize(array: ref m_snapshotCells, newSize: ((int)System.Numerics.BitOperations.RoundUpToPowerOf2(value: ((uint)cellCount)))); }
        var fieldCells = m_snapshotCells.AsMemory(length: cellCount, start: 0);

        for (var index = 0; (index < latticeDeltas.Count); index++) {
            fieldCells.Span[index] = latticeDeltas[index];
        }

        Host.CopyPublishedCells(fieldCells.Span[latticeDeltas.Count..], full: !consumeContinuity);

        return new WorldSnapshot(
            Tick: tick,
            Revision: Host.Population.Revision,
            StepTicks: stepTicks,
            Entries: Host.SnapshotEntries.AsMemory(
                length: count,
                start: 0
            ),
            Authority: Host.AuthorityIdentity,
            FieldCells: fieldCells,
            FieldsFull: fieldsFull,
            EngineTick: Host.CompletedEngineTicks,
            Inputs: Host.Input.Publish(),
            InputAnswers: Host.Input.Answers(),
            SolidScales: Host.Population.SolidScales()
        );
    }

    // The entity rows one snapshot partition holds. The row index space is the population's, so the partition count
    // follows the document and never the worker count.
    private const int SnapshotPartitionRows = 256;
    // The delivered rows below which the snapshot writes serially even with partitions and workers to spare: under
    // two full partitions, waking a worker and joining it cost as much as the rows the fork would take off this
    // thread.
    private const int SnapshotForkRows = (2 * SnapshotPartitionRows);

    // Counts each partition's delivered rows and assigns each partition the first position after every lower
    // partition's (an exclusive prefix sum), so partitions write their rows in index order at their own offsets and
    // the delivered sequence is the serial one at any worker count. Returns the delivered row count.
    private int CountSnapshotPartitions(int capacity, int partitions) {
        if (m_snapshotOffsets.Length < partitions) {
            m_snapshotOffsets = new int[partitions];
        }

        var count = 0;
        var population = Host.Population;

        for (var partition = 0; (partition < partitions); partition++) {
            m_snapshotOffsets[partition] = count;
            var end = Math.Min(val1: capacity, val2: ((partition + 1) * SnapshotPartitionRows));

            for (var index = (partition * SnapshotPartitionRows); (index < end); index++) {
                if (SnapshotBody(population, index) is not null) {
                    count++;
                }
            }
        }
        return count;
    }
    // The body a snapshot delivers a row for at an entity index, or null for an inactive or bodiless slot.
    private static WorldBody? SnapshotBody(WorldPopulation population, int index) => (population.IsActive(index: index)
        ? population.EntryBody(index: index)
        : null);
    // Writes the delivered rows of entity indices [first, end) into Host.SnapshotEntries from position offset, in
    // index order, and returns the position after the last. Partitions call it concurrently over disjoint ranges: it
    // writes only those rows' entries, their bodies' continuity and their register-file ranges of m_snapshotState,
    // and reads the arena only through participant lane slots (StateArena.TryReadSlot), which build no lazy read
    // cache. A read added here that can build one (a linked row's positions) needs the arena prepared before the fork.
    private int WriteSnapshotRows(WorldPopulation population, bool consumeContinuity, int first, int end, int offset) {
        var count = offset;
        var stateLane = population.ActionStateLane;
        var stateSlots = stateLane.Definitions;

        for (var index = first; (index < end); index++) {
            if (SnapshotBody(population, index) is not { } body) {
                continue;
            }

            var stateStart = (index * stateSlots.Count);

            for (var slot = 0; (slot < stateSlots.Count); slot++) {
                m_snapshotState[(stateStart + slot)] = WorldBodyStateReads.Presented(
                    kind: stateSlots[slot].Kind,
                    raw: stateLane.Read(
                    ordinal: index,
                    slot: slot
                )
                );
            }

            Host.SnapshotEntries[count++] = new EntitySnapshot(
                Index: index,
                Position: body.Position,
                Orientation: body.Orientation,
                BodyColor: population.BodyColor(index: index),
                Active: true,
                Kit: population.KitIndex(index: index),
                Look: population.LookIndex(index: index),
                CatalogRig: population.CatalogRig(index: index),
                Continuity: (consumeContinuity
                ? body.TakeContinuity()
                : body.PeekContinuity()),
                Generation: population.Generation(index: index),
                PlacementId: population.InhabitantPlacementId(index: index),
                Heading: body.Yaw,
                Facts: body.Facts,
                State: m_snapshotState.AsMemory(
                    length: stateSlots.Count,
                    start: stateStart
                )
            );
        }

        return count;
    }

    private FieldCellDelta[] m_snapshotCells = [];
    private readonly List<FieldCellDelta> m_latticeSnapshotDeltas = [];
    private int[] m_snapshotOffsets = [];

    private SnapshotRows? m_snapshotRows;

    // The snapshot's partitioned phase: partition p writes entity indices [256p, 256(p+1)) at the offset the serial
    // count assigned it. Retained across ticks, so a fork allocates nothing.
    private sealed class SnapshotRows(WorldDocument document) : Puck.Hosting.Jobs.IJobPartitions {
        public WorldPopulation Population { get; set; } = null!;

        public void Run(int partition) => _ = document.WriteSnapshotRows(
            population: Population,
            consumeContinuity: true,
            end: Math.Min(val1: Population.Capacity, val2: ((partition + 1) * SnapshotPartitionRows)),
            first: (partition * SnapshotPartitionRows),
            offset: document.m_snapshotOffsets[partition]
        );
    }

    // The ONE grant-table DENIAL emission — the loud stderr line plus the submitter-routed denied echo (Rejected,
    // Denied). Grant's administration and co-drive-consent refusals, Revoke's administration refusal, a lever write
    // lacking its section's Mutate hold, world.undo lacking every section's Mutate hold, and a rebuild lacking every
    // section's Mutate hold all differ only in what they say and which verb's echo kind names the denied operation —
    // never in how the denial is reported. echoKind defaults to GrantTable (a direct grant/revoke denial); a caller
    // reporting a DIFFERENT verb's authority denial (Mutation, Rebuild) names that verb's own kind instead, so the
    // echo still routes to the operation the caller actually attempted.
    /// <summary>Narrates and echoes one grant-table denial without changing the table.</summary>
    /// <param name="denial">The refusal text.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="echoKind">The edit-boundary class the echo is tagged with.</param>
    internal void DenyGrantTable(string denial, int connectionId, long correlationId, WorldEditEchoKind echoKind = WorldEditEchoKind.GrantTable) {
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.grant denied", text: $"[world.grant denied: {denial}]");
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: denial,
            Rejected: true,
            Kind: echoKind,
            Denied: true,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
    }
    // Swap the live definition and rebuild the derived state that compiled from it. Sim-affecting sections (kits,
    // assignment, motion, producer, seat kit, spawns) recompile the population's fixed tables and live bodies; the
    // scene/screens rebuild on the client through the delivered definition, and cameras/render/population defaults are
    // document-only.
    internal void Install(WorldDefinition definition, WorldProductPlan products, WorldGrants.PreparedAuthorityTransition authority, WorldRuleCompilation? compilation = null, StateArena? arena = null,
        WorldRebuildKind? rebuildKind = null, WorldArenaContinuity? continuity = null) {
        var rebuildPopulation = products.RebuildsPopulation;

        Host.Input.PrepareCameraReconfigure(definition);
        authority.Install();
        var previousActionState = (rebuildKind.HasValue ? Host.Population.ActionStateLane.Definitions : null);

        WorldBootWork.Count(kind: WorldBootWork.FullInstalls);
        MarkDefinitionDeliveryPending();
        AdoptDefinition(definition: definition);
        Host.InputHold.Reconfigure(settings: definition.CompiledInputHold);
        definition = Host.RecompileRules(arena: arena, compilation: compilation, definition: definition,
            continuity: (continuity ?? (rebuildKind.HasValue ? WorldArenaContinuity.Load : WorldArenaContinuity.Edit)));
        // Unconditional, like RecompileRules above: a group/member count is capacity-bounded, so a full resync costs
        // nothing on the ticks that never touch the groups section, and unconditional is what keeps membership
        // expansion CHECK-TIME correct without a bespoke "did this mutation touch Groups" classification to maintain.
        // Unconditional here (no touchedRow) — the mutation kinds that reach Install (a whole-document rebuild)
        // are too varied to cheaply name the one row they touched, unlike the single-cell write InstallRuntimeStateValue
        // below narrows. This is what makes a live world.state.cell.set that flips a gate settle before the SAME
        // tick's later intent drain reads it (Install always runs before the intents loop within one Step).
        Host.GrantTable.SyncState(definition: definition, arena: Host.Arena);

        // Field reactions are a compiled runtime product of their own, installed on every full install before the
        // population's build reads the lattices. A compatible replacement swaps the typed plan in place and retains
        // every lattice cell.
        Host.Population.InstallFields(definition: definition);

        // Reconcile the machine host to the (possibly changed) screens section on EVERY install — cheap (a
        // dictionary diff over a handful of declared screens), and the one choke point every screen-affecting
        // mutation AND every whole-document rebuild both pass through. The host reports which indices it removed;
        // this project (not the host — see WorldMachineHost's own remarks on why) owns the engagement-side admin
        // cleanup for them: Host.Engagement.DissolveScreen runs before the removed slot's machine is disposed.
        foreach (var removed in Host.Machines.ReconcileScreens(screens: definition.Screens)) {
            Host.Engagement.DissolveScreen(screenIndex: removed);
        }

        // Cable groups reconcile AFTER screens (a group resolves against the live slot set) — the SAME choke
        // point, so a live UpsertScreen carrying a cable port AND a whole-document rebuild (world.reset/.load/
        // .reload) both establish/tear down live links, not merely the boot constructor.
        Host.Machines.ReconcileLinks(links: definition.MachineCableGroups());

        if (rebuildPopulation) {
            RebuildPopulation(definition: definition);
        }

        if (previousActionState is not null) {
            Host.Population.RebirthActionState(previousActionState, keepsParticipant: (rebuildKind == WorldRebuildKind.Reload));
        }

        // AFTER the population branch above: a rebuild replaces every WorldBody, so resyncing scale before it would
        // read the registers into instances Rebuild is about to discard, leaving the fresh ones at their constructed
        // default until the next Install happens to touch state again.
        Host.Population.SyncBodyCells(definition: definition);
        if (products.ReconfiguresInput) { Host.Input.Reconfigure(definition: definition); }
    }

    // Rebuilds the population's compiled tables and live bodies against a definition, then reconciles the inhabited
    // placements and their cell-driven counts the rebuild re-seeded.
    private void RebuildPopulation(WorldDefinition definition) {
        Host.Population.Rebuild(
            definition: definition,
            solids: m_solids
        );
        Host.InputHold.Reset();
        // Reconcile inhabited placements AFTER the census rebuild (a placement/creation/kit edit can add, retire, or
        // re-kit a driven body). Idempotent — a no-op when the inhabited set is unchanged.
        var admitted = new List<WorldPeerEventEntry>();
        var disconnected = new List<WorldPeerEventEntry>();

        Host.Population.ReconcileInstall(
            admitted: admitted,
            arena: Host.Arena,
            definition: definition,
            disconnected: disconnected,
            tick: Host.NextInputTick
        );
        Host.GrantTable.ApplyLifecycleEvents(
            admitted: admitted,
            disconnected: disconnected,
            ordered: true,
            input: false
        );
    }

    // A state mutation changes runtime values, not declaration shape. Keep the authoritative document as the
    // journal/save source while retaining the compiled rule/catalog/group/machine products that depend only on
    // declarations; only state-sensitive grants and field reactions observe the new values immediately. The
    // drive-gate rescan runs only when a touched row gates drive.
    // Reused across every InstallRuntimeStateValue call so a state mutation unrelated to any cell-driven inhabit
    // facet — the overwhelming majority — costs zero list allocation: Clear() keeps the backing array.
    private readonly List<WorldPeerEventEntry> m_inhabitCountAdmitted = [];
    private readonly List<WorldPeerEventEntry> m_inhabitCountDisconnected = [];

    private void InstallRuntimeStateValue(WorldDefinition definition, WorldMutation mutation, StateArena? arena = null) =>
        InstallRuntimeStateValue(arena: arena, definition: definition, driveGate: TouchesDriveGate(definition: definition, mutation: mutation));
    // A value install of named rows (a reload whose source moved only state values).
    private void InstallRuntimeStateValue(WorldDefinition definition, IReadOnlyCollection<string> rows) =>
        InstallRuntimeStateValue(arena: null, definition: definition, driveGate: GatesDrive(definition: definition, rows: rows));
    private void InstallRuntimeStateValue(WorldDefinition definition, StateArena? arena, bool driveGate) {
        var previous = m_definition;

        WorldBootWork.Count(kind: WorldBootWork.ValueInstalls);
        AdoptDefinition(definition: definition);
        // Ordinary rows the write replaced enter through the import door, while a pool candidate arrives as the
        // already-validated typed replacement prepared before commit.
        if (arena is null) {
            Host.SyncArenaValues(definition: definition, previous: previous);
        } else {
            Host.AdoptPreparedArena(arena: arena, continuity: WorldArenaContinuity.Edit, definition: definition);
        }
        Host.ContinueValueDisclosure(previous, m_definition);
        m_pendingStateDelivery = true;

        ReconcileStateConsumers(
            definition: definition,
            driveGate: driveGate,
            fields: true
        );
    }

    // Everything outside the arena that reads a state value and keeps its own copy: the drive gates the grant table
    // resolved, the field lattice's input and the cell-driven inhabit counts. A state value
    // reaches the installed document through two doors, a value mutation and the arena's end-of-tick export of what
    // the rules wrote, and both end here, so a gate a rule closed is as closed as one a command closed. Nothing here
    // writes the arena or opens a scope. Each flag says whether a row that consumer reads moved.
    internal void ReconcileStateConsumers(WorldDefinition definition, bool driveGate, bool fields) {
        if (driveGate) {
            Host.GrantTable.SyncState(definition: definition, arena: Host.Arena);
        }
        if (fields) {
            Host.Population.InstallFields(definition: definition);
        }
        // ReconcileInhabitCounts compares against its own cache and is a no-op walk when no tracked inhabit facet's
        // row moved.
        Host.Population.ReconcileInhabitCounts(arena: Host.Arena,
            admitted: m_inhabitCountAdmitted,
            definition: definition,
            disconnected: m_inhabitCountDisconnected,
            tick: Host.NextInputTick
        );

        if ((m_inhabitCountAdmitted.Count > 0) || (m_inhabitCountDisconnected.Count > 0)) {
            Host.GrantTable.ApplyLifecycleEvents(
                admitted: m_inhabitCountAdmitted,
                disconnected: m_inhabitCountDisconnected,
                ordered: true,
                input: false
            );
        }

        m_inhabitCountAdmitted.Clear();
        m_inhabitCountDisconnected.Clear();
    }

    private bool TouchesDriveGate(WorldDefinition definition, WorldMutation mutation) {
        m_touchedRows.Clear();

        return (!TryCollectStateMutationRowNames(definition: definition, mutation: mutation, names: m_touchedRows, reason: out _) ||
            GatesDrive(definition: definition, rows: m_touchedRows));
    }
    // Whether any of the named rows gates drive.
    private bool GatesDrive(WorldDefinition definition, IReadOnlyCollection<string> rows) {
        var catalog = definition.StateCatalog;

        foreach (var name in rows) {
            if (
                catalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: name) &&
                StateReader.TryResolveRowHandle(arena: Host.Arena, catalog: catalog, handle: handle, rowOrdinal: out var ordinal, row: out _) &&
                definition.State[ordinal].GatesDrive
            ) {
                return true;
            }
        }

        return false;
    }
    // The mutation kinds that change state values and nothing else: a cell write or removal, a transform, a draw
    // site fire, or a batch made only of those.
    private static bool IsStateMutation(WorldMutation mutation) {
        switch (mutation) {
            case WorldMutation.UpsertStateCell or WorldMutation.RemoveStateCell or WorldMutation.TransformState or WorldMutation.Generate:
                return true;
            case WorldMutation.Batch batch:
                foreach (var member in batch.Mutations) {
                    if (!IsStateMutation(mutation: member)) {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }
    // A whole-row write that keeps the state catalog identity (the same rows, shapes and kinds) and redeclares the row
    // exactly as it stood but for its cells' values moves values alone, as a cell write does, so it takes the same
    // validation and install. A write that also changes a value-over-time trait (a cell's dynamics, clock or advance, the
    // row's cycle) keeps the catalog but must settle through the full install.
    private static bool IsValueMutation(WorldMutation mutation, WorldDefinition candidate, WorldDefinition previous) => (
        IsStateMutation(mutation: mutation) ||
        ((mutation is WorldMutation.UpsertStateRow upsert) &&
        ReferenceEquals(objA: candidate.StateCatalog, objB: previous.StateCatalog) &&
        (WorldDefinitionRows.FindStateRow(rows: previous.State, name: upsert.Row.Name.Value) is { } before) &&
        DiffersInValuesAlone(before: before, row: upsert.Row))
    );
    private static bool DiffersInValuesAlone(WorldStateRow row, WorldStateRow before) {
        var cells = (row.Cells ?? []);
        var previous = (before.Cells ?? []);

        if (!SameComposeDeclaration(previous: before, row: row) || (row.DrawCursor != before.DrawCursor) ||
            (row.HistoryCursor != before.HistoryCursor) || !ReferenceEquals(objA: row.DrawnMasks, objB: before.DrawnMasks) ||
            (cells.Count != previous.Count)) {
            return false;
        }

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];
            var old = previous[index];

            if ((cell.Key != old.Key) || (cell.Advance != old.Advance) ||
                (cell.Dynamics != old.Dynamics) || (cell.Cycle != old.Cycle) || (cell.Behavior != old.Behavior) ||
                (cell.Clock != old.Clock) || (cell.Visibility != old.Visibility) || (cell.Observation != old.Observation)) {
                return false;
            }
        }

        return true;
    }

    internal bool TryValidateMutationCandidate(WorldDefinition candidate, WorldDefinition current, WorldMutation mutation, out string reason, out WorldRuleCompilation? compilation, bool retainCompilation = true) {
        compilation = null;

        if (IsValueMutation(candidate: candidate, mutation: mutation, previous: current)) {
            return TryValidateStateMutation(candidate: candidate, mutation: mutation, reason: out reason);
        }

        // Only the units the change reaches are judged (WorldDefinitionValidator.Partitions), so a unit the mutation
        // does not name, hidden or not, cannot refuse it.
        return WorldDefinitionValidator.TryValidateChange(after: candidate, before: current, compilation: out compilation, reason: out reason, retainCompilation: retainCompilation);
    }

    // A state mutation can only have changed the rows it names, so validation covers those rows and the rows keyed
    // over them, with nothing compiled.
    private bool TryValidateStateMutation(WorldDefinition candidate, WorldMutation mutation, out string reason) {
        m_touchedRows.Clear();

        return (TryCollectStateMutationRowNames(definition: candidate, mutation: mutation, names: m_touchedRows, reason: out reason)
            ? WorldDefinitionValidator.TryValidateTouchedStateRows(definition: candidate, reason: out reason, rowNames: m_touchedRows)
            : WorldDefinitionValidator.TryValidateLocally(definition: candidate, reason: out reason));
    }

    // Scratch for the rows one mutation touches; the step is single-threaded, so one set serves every door.
    private readonly HashSet<string> m_touchedRows = new(comparer: StringComparer.Ordinal);

    // Collects the row names a state mutation touches; false (with an empty reason) when the mutation — or, for a
    // Batch, any one of its members — is not one of the state kinds TryValidateStateMutation covers, which the
    // caller reads as "fall back to whole-document validation" rather than a refusal.
    private static bool TryCollectStateMutationRowNames(WorldDefinition definition, WorldMutation mutation, ISet<string> names, out string reason) {
        reason = string.Empty;

        switch (mutation) {
            case WorldMutation.UpsertStateCell upsertCell:
                names.Add(item: upsertCell.Row);

                return true;
            case WorldMutation.UpsertStateRow upsertRow:
                names.Add(item: upsertRow.Row.Name.Value);

                return true;
            case WorldMutation.RemoveStateCell removeCell:
                names.Add(item: removeCell.Row);

                return true;
            case WorldMutation.Generate generate:
                names.Add(item: generate.Row);

                return true;
            case WorldMutation.TransformState transform:
                return WorldDefinitionValidator.TryCollectTransformRowNames(definition: definition, transform: transform.Transform, names: names, reason: out reason);
            case WorldMutation.Batch batch:
                foreach (var member in batch.Mutations) {
                    if (!TryCollectStateMutationRowNames(definition: definition, mutation: member, names: names, reason: out reason)) {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }
    private void Reject(WorldMutation mutation, string reason, int connectionId, long correlationId) {
        m_lastMutationFailureDetail = reason;
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.mutation rejected", text: $"[world.mutation rejected: {WorldServer.Describe(mutation: mutation)} — {reason}]");
        }
        if (!HasSubmitter(mutation: mutation)) {
            return;
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: ((m_lastMutationFailureCode == WorldRecipientMutations.NotDisclosedCode) ? reason : $"{WorldServer.Describe(mutation: mutation)} rejected: {reason}"),
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
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.definition rejected", text: $"[world.definition rejected: {verb} — {reason}]");
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: $"{verb} rejected: {reason}",
            Rejected: true,
            Kind: WorldEditEchoKind.Rebuild,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
    }
    // ApplyRebuild's own null-host gate: with no addon host attached, a candidate whose addon rows are all absent or
    // disabled is vacuous (nothing to mount either way), but an ENABLED row names a guest that would install into
    // the document with no runtime ever able to run it — the same shape TryApplyMutation's own no-host gate refuses.
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
            case WorldScreenOp.Select select: return new[] { select.Index };
            case WorldScreenOp.Link link: return link.Members;
            case WorldScreenOp.Unlink unlink:
                return (Host.Machines.TryReadLinkMembers(
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
    internal void SwapSolids(WorldSolidField? solids) {
        if (!ReferenceEquals(
            objA: solids,
            objB: m_solids
        )) {
            SetSolids(solids: solids);
            m_solidRevision++;
        }
    }

    // Applies one screen op SYNCHRONOUSLY (see WorldScreenOp's own remarks for why: never buffered, so a following
    // Command.Engage in the same batch observes the effect). Authority FIRST — Control over the targeted screen(s),
    // the SAME grant subject ScreenCommandModule's pre-inversion client-side precheck used, now checked
    // AUTHORITATIVELY server-side — then the mechanical apply through Host.Machines. ScreenOpTap fires exactly once,
    // after the outcome (success or refusal) is known, so a refused op still reproduces on replay.
    private bool TryApplyScreenOp(WorldScreenOp op, Principal principal, int connectionId, long correlationId) {
        var verb = op switch {
            WorldScreenOp.Select => "screen.select",
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

            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.grant denied", text: $"[world.grant denied: {denial}]");
            }
            Host.EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: denial,
                Rejected: true,
                Kind: WorldEditEchoKind.ScreenOp,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));
            Host.ScreenOpTap?.Invoke(
                arg1: op,
                arg2: principal
            );

            return false;
        }

        var (ok, message) = op switch {
            WorldScreenOp.Select select => Host.Machines.TrySelect(
            index: select.Index,
            entry: select.Entry
        ),
            WorldScreenOp.Link link => Host.Machines.TryLink(
            name: link.Name,
            members: link.Members
        ),
            WorldScreenOp.Unlink unlink => Host.Machines.TryUnlink(name: unlink.Name),
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(op),
            actualValue: op,
            message: $"no {nameof(TryApplyScreenOp)} apply arm for screen op kind '{op.GetType().Name}'."
        ),
        };

        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "{verb}", text: $"[{verb}: {message}]");
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: !ok,
            Kind: WorldEditEchoKind.ScreenOp,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
        // Taped whether or not the op succeeded, so a refused op refuses the identical way on replay.
        Host.ScreenOpTap?.Invoke(
            arg1: op,
            arg2: principal
        );

        // Latch AnyScreenOpEverApplied for EVERY op that reaches dispatch — not just `ok` ones. A host-level
        // refusal is not uniformly mutation-free: TrySelect moves slot.SelectedEntry BEFORE booting and retains
        // the new selector when the boot fails, so a pre-record failed select still diverges the live host from
        // the definition a recording would capture. Grant denials return before this point and mutate nothing;
        // past the authority gate, over-blocking a recording is safe and under-blocking is a silent divergence.
        Host.NoteScreenOpApplied();

        return ok;
    }

    // Build the SDF contact field for a candidate — null when the requirements permit analytic contact (the set is
    // derived inside the population's compile, not here), the built field under the FIELD provider, or a
    // named failure when a solid names an op the warp-free evaluator cannot interpret.
    internal static bool TryBuildSolids(WorldDefinition definition, out WorldSolidField? solids, out string reason) {
        reason = string.Empty;

        if (!RequiresSolidField(definition: definition)) {
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
    private bool TryCheckScreenOpControl(WorldScreenOp op, Principal principal, out int denial) {
        denial = -1;

        var indices = ScreenOpTargets(op: op);

        foreach (var index in indices) {
            if (Host.GrantTable.Allows(
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
    internal void ApplyCommand(WorldCommand command, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        ArgumentNullException.ThrowIfNull(argument: command);

        // The control-application commands are Control-over-TARGET commands, never Drive-over-BODY ones — the
        // generic gate below does not apply to them at all, so they branch out first. Both apply through
        // Server.WorldEngagement, which runs its own check-then-mutate (see its own remarks); nothing here
        // duplicates that check.
        switch (command) {
            case WorldCommand.CalibrateInput calibration:
                var calibrationReason = "input.calibration: owner route is not admitted";
                if (!Host.Input.TryGetBody(calibration.Principal, out var calibrationBody) || (calibrationBody != calibration.EntityIndex) ||
                    ((calibration.Principal.Kind == PrincipalKind.Session) && !Host.GrantTable.IsLiveSession(principal: calibration.Principal)) ||
                    !Host.Input.TryCalibrate(calibration.Principal, calibration.Offset, out calibrationReason)) {
                    Host.Output.Narrate(channel: "input.refused", text: calibrationReason);
                }
                return;
            case WorldCommand.SelectInputState selection:
                var ownsSelection = ((selection.Principal == selection.Owner) || (selection.Principal == Principal.Console));
                var hasSelectionOwner = Host.Input.TryGetBody(selection.Owner, out var selectionBody);
                if (!ownsSelection && hasSelectionOwner && (selectionBody >= 0)) {
                    ownsSelection = Host.GrantTable.Allows(selection.Principal, WorldCapability.Drive,
                        GrantSubject.Body(index: selectionBody)).IsAllowed;
                }
                ownsSelection &= (hasSelectionOwner && (selection.EntityIndex == selectionBody));
                if ((selection.Owner.Kind == PrincipalKind.Session) && !Host.GrantTable.IsLiveSession(principal: selection.Owner)) { ownsSelection = false; }
                var selectionReason = "input.mode: actor cannot select this owner's state";
                if (!ownsSelection || !Host.Input.TrySetSeatState(selection.Owner, selection.Family, selection.State, selection.Activation, out selectionReason, selection.Principal, selection.Toggle)) {
                    Host.Output.Narrate(channel: "input.refused", text: selectionReason);
                }
                return;
            case WorldCommand.AdmitInput admission when (admission.EntityIndex == -1):
                if (Host.GrantTable.IsLiveSession(principal: admission.Principal)) {
                    if (!Host.Input.TryAdmitProfile(admission.Principal, -1, admission.Sources, out var inputReason, admission.FreshAttachment)) {
                        Host.Output.Narrate(channel: "input.refused", text: inputReason);
                    }
                } else { Host.Output.Narrate(channel: "input.refused", text: "input.owner: bodyless input requires a live admitted session"); }
                return;
            case WorldCommand.ComposeControl compose:
                if (!Host.CheckEngagePolicy(
                    entityIndex: compose.EntityIndex,
                    target: compose.Target,
                    reason: out var reason
                )) {
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "world.engage denied", text: $"[world.engage denied: {reason}]");
                    }

                    return;
                }

                _ = Host.Engagement.Compose(
                    entityIndex: compose.EntityIndex,
                    target: compose.Target,
                    exclusive: compose.Exclusive,
                    actingPrincipal: compose.Principal,
                    targetPrincipal: compose.TargetPrincipal
                );

                return;
            case WorldCommand.DissolveControl dissolve:
                _ = Host.Engagement.Dissolve(
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
        var gated = Host.TryDriveGateVerdict(
            bodyIndex: command.EntityIndex,
            verdict: out var gatedVerdict
        );
        var verdict = (gated
            ? gatedVerdict
            : Host.GrantTable.Allows(
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

            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.grant denied", text: $"[world.grant denied: {denial}]");
            }
            Host.EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: denial,
                Rejected: true,
                Kind: WorldEditEchoKind.GrantTable,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));
            Host.Tick.NoteDriveRefusalIfTracked(
                command: command,
                reason: denial
            );

            return;
        }

        if (Host.Body(index: command.EntityIndex) is not { } body) {
            Host.Tick.NoteDriveRefusalIfTracked(
                command: command,
                reason: $"body:{command.EntityIndex} is inactive"
            );

            return;
        }

        foreach (var scope in Host.ScopedReplaySpan) { scope.Observe(command: command); }
        switch (command) {
            case WorldCommand.AdmitInput admission:
                if (!Host.Input.TryAdmitProfile(command.Principal, command.EntityIndex, admission.Sources, out var profileReason, admission.FreshAttachment)) {
                    Host.Output.Narrate(channel: "input.refused", text: profileReason);
                }
                break;
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
            case WorldCommand.RigidImpulse impulse:
                if (!body.IsRigid) {
                    var notRigidDenial = $"body:{command.EntityIndex} carries no rigid kit facet";

                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.impulse denied", text: $"[body.impulse denied: {notRigidDenial}]");
                    }
                    Host.Tick.NoteDriveRefusalIfTracked(
                        command: command,
                        reason: notRigidDenial
                    );

                    break;
                }

                if (
                    !float.IsFinite(f: impulse.Impulse.X) ||
                    !float.IsFinite(f: impulse.Impulse.Y) ||
                    !float.IsFinite(f: impulse.Impulse.Z) ||
                    !body.TryApplyRigidImpulse(
                        impulse: FixedVector3.FromVector3(value: impulse.Impulse),
                        velocityCeiling: Host.Population.RigidVelocityCeiling
                    )
                ) {
                    var overflowDenial = $"body:{command.EntityIndex} impulse is not representable or would exceed the world's declared speed ceiling ({((double)Host.Population.RigidVelocityCeiling):0.###})";

                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.impulse denied", text: $"[body.impulse denied: {overflowDenial}]");
                    }
                    Host.Tick.NoteDriveRefusalIfTracked(
                        command: command,
                        reason: overflowDenial
                    );
                }

                break;
            case WorldCommand.CarryBody carry:
                if (!Host.Population.TryBeginCarry(
                    carrierIndex: command.EntityIndex,
                    targetIndex: carry.TargetIndex,
                    reason: out var carryDenial
                )) {
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.carry denied", text: $"[body.carry denied: body:{command.EntityIndex} {carryDenial}]");
                    }
                    Host.Tick.NoteDriveRefusalIfTracked(
                        command: command,
                        reason: carryDenial
                    );
                }

                break;
            case WorldCommand.ReleaseCarry:
                if (!Host.Population.TryEndCarry(
                    carrierIndex: command.EntityIndex,
                    reason: out var releaseDenial
                )) {
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.release denied", text: $"[body.release denied: body:{command.EntityIndex} {releaseDenial}]");
                    }
                    Host.Tick.NoteDriveRefusalIfTracked(
                        command: command,
                        reason: releaseDenial
                    );
                }

                break;
            case WorldCommand.EnqueueSegment segment:
                body.EnqueueRun(
                    actor: segment.Principal,
                    intent: segment.Intent,
                    seconds: segment.Seconds
                );

                break;
            case WorldCommand.PressChannel press:
                if (!body.Channels.TryGetOrdinal(name: press.Channel, ordinal: out var pressOrdinal)) {
                    var reason = $"input.channel: '{press.Channel}' is not active for body:{press.EntityIndex}";

                    Host.Population.NotePressRefusal(bodyIndex: press.EntityIndex, reason: reason);
                    Host.Output.Narrate(channel: "body.press denied", text: reason);
                    break;
                }
                if (press.HoldSeconds is { } holdSeconds) {
                    var holdCeiling = FixedQ4816.FromRawBits(value: Host.GrantTable.HoldCeiling(
                        grantee: press.Principal,
                        subject: GrantSubject.Body(index: press.EntityIndex)
                    ));
                    var outcome = body.PressChannel(
                        actor: press.Principal,
                        ordinal: pressOrdinal,
                        value: press.Value,
                        holdSeconds: holdSeconds,
                        authoredMaximum: holdCeiling
                    );

                    // The submit drains synchronously, so body.press's handler can read this back immediately —
                    // the same MotionRefusal/StopOutcome read-back shape — and name a silent grant-budget truncation
                    // instead of echoing the requested duration as if it were honored. Clears any prior refusal note
                    // this body's press slot carried, so a stale denial can never bleed into a fresh success.
                    Host.Population.NotePressOutcome(
                        bodyIndex: press.EntityIndex,
                        outcome: outcome
                    );
                } else {
                    body.PressChannel(
                        actor: press.Principal,
                        ordinal: pressOrdinal,
                        value: press.Value
                    );
                    Host.Population.NotePressSuccess(bodyIndex: press.EntityIndex);
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
                    !Host.Population.TryGetBodyMotionProgram(
                    name: motion.BodyMotionProgram,
                    out var targetMotionProgram
                ) ||
                    (targetMotionProgram is not { } resolvedMotionProgram)
                ) {
                    var reason = $"body motion program '{motion.BodyMotionProgram}' is not declared";

                    Host.Population.NoteMotionRefusal(
                        bodyIndex: motion.EntityIndex,
                        reason: reason
                    );
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.motion refused", text: $"[body.motion refused: {reason}]");
                    }
                    Host.EchoTap?.Invoke(obj: new WorldEditEcho(
                        Message: reason,
                        Rejected: true,
                        Kind: WorldEditEchoKind.BodyMotion,
                        ConnectionId: connectionId,
                        CorrelationId: correlationId
                    ));
                } else if (!WorldDefinitionValidator.TryValidateProgramCoherence(
                    motion: Host.Population.KitMotion(index: motion.EntityIndex),
                    program: resolvedMotionProgram,
                    reason: out var coherenceReason
                )) {
                    Host.Population.NoteMotionRefusal(
                        bodyIndex: motion.EntityIndex,
                        reason: coherenceReason
                    );
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.motion refused", text: $"[body.motion refused: {coherenceReason}]");
                    }
                    Host.EchoTap?.Invoke(obj: new WorldEditEcho(
                        Message: coherenceReason,
                        Rejected: true,
                        Kind: WorldEditEchoKind.BodyMotion,
                        ConnectionId: connectionId,
                        CorrelationId: correlationId
                    ));
                } else {
                    body.SetBodyMotionProgram(programName: motion.BodyMotionProgram);
                    Host.Population.NoteMotionRefusal(
                        bodyIndex: motion.EntityIndex,
                        reason: string.Empty
                    );
                    Host.EchoTap?.Invoke(obj: new WorldEditEcho(
                        Message: $"body:{motion.EntityIndex} motion={motion.BodyMotionProgram}",
                        Rejected: false,
                        Kind: WorldEditEchoKind.BodyMotion,
                        ConnectionId: connectionId,
                        CorrelationId: correlationId
                    ));
                }

                break;
            case WorldCommand.SetControl control:
                if (Host.Population.SupportsSource(
                    index: control.EntityIndex,
                    source: control.Source,
                    refusal: out var sourceRefusal
                )) {
                    body.SetIntentSource(source: control.Source);
                } else {
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.control refused", text: $"[body.control refused: {sourceRefusal}]");
                    }
                }

                break;
            case WorldCommand.Reconcile reconcile:
                var continuity = body.Reconcile(
                    x: reconcile.X,
                    z: reconcile.Z,
                    yawRadians: reconcile.YawRadians,
                    seconds: reconcile.Seconds
                );
                if (Host.Output.HasNarrationSink) {
                    Host.Output.Narrate(channel: "body.reconcile", text: $"[body.reconcile: body:{reconcile.EntityIndex} continuity={continuity.ToString().ToLowerInvariant()} maxSmoothError={m_definition.Motion.MaxSmoothError:0.###}]");
                }

                break;
            case WorldCommand.Stop:
                // The submit drains synchronously (WorldServer.Submit), so body.stop's handler can read this back
                // through WorldPopulation.LastStopOutcome the instant control returns to it — the same pattern
                // body.motion's MotionRefusal read-back uses.
                Host.Population.NoteStopOutcome(
                    bodyIndex: command.EntityIndex,
                    outcome: body.Stop()
                );

                break;
            case WorldCommand.LoadDurableState load:
                if (load.Tick != Host.NextInputTick) {
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.state-load refused", text: $"[body.state-load refused: tick {load.Tick} is not next tick {Host.NextInputTick}]");
                    }
                } else if (!body.TryStageDurableState(
                    tick: load.Tick,
                    values: load.Values,
                    requirePlayerWritable: true,
                    writer: load.Principal.Describe(),
                    reason: out var stateReason
                )) {
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(channel: "body.state-load refused", text: $"[body.state-load refused: {stateReason}]");
                    }
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
    internal void ApplyComposition(WorldComposition composition, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        ArgumentNullException.ThrowIfNull(argument: composition);

        if (Host.GrantTable.Allows(
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

            if (Host.Output.HasNarrationSink) {
                Host.Output.Narrate(channel: "world.grant denied", text: $"[world.grant denied: {denial}]");
            }
            Host.EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: denial,
                Rejected: true,
                Kind: WorldEditEchoKind.GrantTable,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return;
        }

        Host.Output.DeliverComposition(composition: composition);
    }
    /// <summary>Validates and applies one subject-bearing target-register write.</summary>
    internal bool ApplyDesignation(WorldDesignation designation, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
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
    /// through (mirroring <see cref="ApplyCommand"/>/<see cref="WorldGrants.ApplyGrant"/>/<see cref="WorldGrants.ApplyRevoke"/>'s own re-drive shape:
    /// a live screen op never buffers, so a replayed one does not either).</summary>
    /// <param name="op">The screen op.</param>
    /// <param name="principal">The acting identity the op is checked against.</param>
    internal void ApplyScreenOp(WorldScreenOp op, Principal principal) =>
        TryApplyScreenOp(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            op: op,
            principal: principal
        );
    /// <summary>Attaches the mounted addon host this server pumps at the three pinned points of <see cref="WorldServer.Step"/>,
    /// mirroring <c>LoopbackTransport.Bind</c>'s one-shot wiring. Called once the host's guests have mounted, so the
    /// server never observes a half-built host. Also re-sizes the per-tick contention tracking to cover the addon
    /// writers that now exist beside the seat lanes.</summary>
    /// <param name="runtime">The mounted host.</param>
    /// <param name="preparedFrom">What the preparation that mounted the host read of the live document
    /// (<see cref="WorldDocumentReads"/>), which the next install judges the host by; <see langword="null"/> when the
    /// caller did not record it. A host that takes the place of one already attached (a decorator over the mounted
    /// host, or the same host attached again after its factory attached it) is the same mount, so the inputs recorded
    /// for the attached one stand; a first attach with none recorded prepares the host at the next install. A pending
    /// checkpoint the attach restores the host from records its own.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A tick-pumped host does not implement recomputed contributions.</exception>
    internal void AttachAddons(IWorldAddonHost runtime, WorldDocumentInputs? preparedFrom = null) {
        ArgumentNullException.ThrowIfNull(argument: runtime);
        if (runtime.ReplayPolicy != WorldExtensionReplayPolicy.Recomputed) {
            throw new InvalidOperationException(message: "Tick-pumped addon hosts must implement Recomputed contributions; use WorldRecordedExtension for recorded providers.");
        }

        var restoresCheckpoint = Host.Persistence.HasPendingAddonCheckpoint;
        var restoring = WorldDocumentReads.Begin();
        WorldDocumentInputs restored;

        try {
            Host.Persistence.RestoreAttachedAddons(host: runtime);
        } finally {
            restored = restoring.End();
        }
        m_addonInputs = (restoresCheckpoint ? restored : (preparedFrom ?? ((Host.Addons is not null) ? m_addonInputs : null)));
        Host.AdoptAddonHost(runtime: runtime);

        StageAddonContentionArrays(
            mountedCount: runtime.MountedCount,
            entity: out var entity,
            principal: out var principal,
            collided: out var collided
        );

        if (entity is not null) {
            Host.AdoptContentionArrays(
                collided: collided!,
                entity: entity,
                principal: principal!
            );
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
    internal void StageAddonContentionArrays(int mountedCount, out int[]? entity, out Principal[]? principal, out bool[]? collided) {
        var capacity = (Host.Population.LocalSeatCount + (mountedCount * 2));

        if (capacity == Host.TickWrittenEntity.Length) {
            entity = null;
            principal = null;
            collided = null;

            return;
        }

        entity = new int[capacity];
        principal = new Principal[capacity];
        collided = new bool[capacity];
    }
    /// <summary>Buffers one live world mutation for the next <see cref="WorldServer.Step"/> (drained before intents). Retains the
    /// submitting envelope's connection/correlation identity so the eventual accept/reject <see cref="WorldEditEcho"/>
    /// routes back to the submitter (see <see cref="WorldEditEcho.ConnectionId"/>) — a deferred op's echo fires later
    /// than its submission, so that identity must travel with the buffered entry rather than being read live.</summary>
    /// <param name="mutation">The mutation to apply.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="sourceAddonInstanceId">The mounted addon instance token this mutation was decoded from, or
    /// <c>-1</c> for a console/client submission (the addon mutation seam's completion field — see
    /// <see cref="WorldPendingOp.Mutate"/>).</param>
    /// <param name="actOrdinal">The addon's own output-batch ordinal this mutation answers, when
    /// <paramref name="sourceAddonInstanceId"/> is not <c>-1</c>.</param>
    /// <param name="outcomeObserved">Invoked once, with whether this exact mutation applied, the moment
    /// <see cref="WorldServer.Step"/> drains and applies it — <see langword="null"/> for every caller but
    /// <see cref="ApplyEnvelope"/>'s own dispatch (see <see cref="WorldServer.MutationOutcomeTap"/>).</param>
    /// <param name="binding">The canonical operation binding for a wire mutation, or <see langword="null"/> for
    /// internal mutation producers.</param>
    /// <param name="completion">The deferred typed completion for a wire mutation, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> is <see langword="null"/>.</exception>
    internal void EnqueueMutation(WorldMutation mutation, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, long sourceAddonInstanceId = -1L, ushort actOrdinal = 0, Action<bool>? outcomeObserved = null, WorldMutationBinding? binding = null, Action<WorldSubmissionResult>? completion = null) {
        ArgumentNullException.ThrowIfNull(argument: mutation);

        m_pending.Enqueue(item: new WorldPendingOp.Mutate(
            ActOrdinal: actOrdinal,
            Binding: binding,
            Completion: completion,
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            Mutation: mutation,
            OutcomeObserved: outcomeObserved,
            SourceAddonInstanceId: sourceAddonInstanceId
        ));
    }
}
