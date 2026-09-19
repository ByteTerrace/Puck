using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    /// <summary>Observes a music segment transition the instant it commits (the same tick <c>MusicDirector</c>
    /// records it, from the music-step call site in <see cref="StepCore"/>) — mirroring
    /// <see cref="WorldServer.SaveEffectTap"/>/<see cref="IWorldMachineHost.MachineLifecycleTap"/>'s "the server calls out, the
    /// composition root supplies the capability" shape: this project references no audio director, so it cannot fire
    /// the <c>music.transition</c> cue itself. Carries nothing but the tick — the committed segment ids are already
    /// re-derivable from <c>MusicDirector.LastTransitionFromSegmentId</c>/<c>LastTransitionToSegmentId</c>, so no
    /// second value need round-trip through the tap. A <see langword="null"/> tap is a silent no-op, the same
    /// convention every other tap here follows; every live boot shape wires one (<c>WorldPostBuildWiring.Install</c>).
    /// Never taped — see <c>MusicReplayReDerivabilityLawTests</c>: the director's own state is purely
    /// re-derivable from the document plus tick, so a fresh replay boot re-fires the identical sequence of
    /// invocations without a recorded entry.</summary>
    internal Action<ulong>? MusicTransitionTap { get; set; }
    /// <summary>Observes the active conditional-layer set the instant it CHANGES tick over tick (the same music-step
    /// call site <see cref="MusicTransitionTap"/> fires from) — level-triggered, so unlike a transition this can
    /// fire on any tick, not only a commit. Carries the whole new set (never a delta): a layer is level-triggered,
    /// so the composition root's own consumer re-derives from the current set every time regardless. A
    /// <see langword="null"/> tap is a silent no-op; every live boot shape wires one.</summary>
    internal Action<IReadOnlyList<string>>? MusicLayerTap { get; set; }
    /// <summary>Observes a director embellishment the instant it fires (the same music-step call site
    /// <see cref="MusicTransitionTap"/> fires from) — carries the patch id, since (unlike a transition) an
    /// embellishment's PATCH is authored per-embellishment and not re-derivable from any fixed cue-table row. A
    /// <see langword="null"/> tap is a silent no-op; every live boot shape wires one.</summary>
    internal Action<string>? MusicEmbellishmentTap { get; set; }

    // The active-layer set observed as of the end of the PREVIOUS Step call — MusicLayerTap fires only when this
    // tick's set differs, so a level-triggered layer that stays active for many ticks in a row costs one comparison
    // per tick, not one tap invocation per tick.
    private readonly List<Puck.Audio.Simulation.MusicSenseEdge> m_senseEdgeScratch = [];

    private IReadOnlyList<Puck.Audio.Simulation.MusicSenseEdge> ProjectSenseEdges() {
        MusicDirectorFactory.ProjectSenseEdges(
            edges: Host.Events.Edges,
            projected: m_senseEdgeScratch
        );

        return m_senseEdgeScratch;
    }

    private IReadOnlyList<string> m_lastTappedActiveLayerTuneIds = [];

    // MusicDirector.ActiveLayerTuneIds is recomputed in stable declared order every Step, so an ordinal sequence
    // compare is exact — never a set compare, which would treat a reorder as a no-op.
    private static bool ActiveLayerSetsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b) {
        if (a.Count != b.Count) {
            return false;
        }

        for (var index = 0; (index < a.Count); index++) {
            if (!string.Equals(
                a: a[index],
                b: b[index],
                comparisonType: StringComparison.Ordinal
            )) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Dispatches one server-authored event, either inline or through the ordered domain.</summary>
    /// <param name="serverEvent">The event.</param>
    /// <param name="ordered">Whether to enqueue it on the ordered domain rather than apply it inline.</param>
    internal void DispatchServerEvent(WorldServerEvent serverEvent, bool ordered) {
        lock (Host.AuthorityGate) {
            if (Host.AuthorityRetiring) {
                return;
            }
            if (ordered) {
                EnqueueOrdered(entry: new WorldOrderedEntry.ServerEvent(Value: serverEvent));
            } else {
                ApplyServerEvent(serverEvent: serverEvent);
            }
        }
    }
    // Drains the ordered domain FIFO until empty, applying each envelope through the same per-kind apply methods the
    // per-kind IServerLink surface called directly, and invoking that entry's completion with the typed result.
    // Callers hold the authority gate. The reentrancy guard therefore only ever sees this thread's own drain: a
    // re-entrant Submit-from-inside-an-apply re-enqueues and returns to the outer drain's loop instead of recursing.
    internal void DrainOrdered() {
        if (m_drainingOrdered) {
            return;
        }

        m_drainingOrdered = true;

        try {
            while (m_ordered.TryDequeue(result: out var entry)) {
                switch (entry) {
                    case WorldOrderedEntry.Submission submission:
                        var result = Host.Document.ApplyEnvelope(
                            envelope: submission.Envelope,
                            completion: submission.Completion
                        );

                        if (result is not null) {
                            submission.Completion?.Invoke(obj: result);
                        }
                        break;
                    case WorldOrderedEntry.ServerEvent serverEvent:
                        ApplyServerEvent(serverEvent: serverEvent.Value);
                        break;
                }
            }
        } finally {
            m_drainingOrdered = false;
        }
    }
    // Drain every buffered live edit in FIFO order, applying it at this tick boundary. Delivers the new definition to
    // the client sink ONCE if at least one edit applied (once per step with >=1 applied edit, not once per edit).
    internal bool DrainPendingOps(ulong tick) {
        var applied = false;

        while (Host.Document.Pending.TryDequeue(result: out var op)) {
            var ok = op switch {
                // An addon-sourced op was already metered at the seam's pre-flight (before decode, deliberately), so
                // re-entering the budget gate here would charge one guest dispatch twice against the same tick's
                // allowance. Every other source — console, loopback, a peer's submission — is metered right here.
                WorldPendingOp.Mutate mutate => Host.TryApplyMutation(
                mutation: mutate.Mutation,
                tick: tick,
                engineTick: CompletedEngineTicks,
                connectionId: mutate.ConnectionId,
                correlationId: mutate.CorrelationId,
                preMetered: (mutate.SourceAddonInstanceId >= 0L)
            ),
                WorldPendingOp.Rebuild rebuild => Host.Document.ApplyRebuild(
                request: rebuild.Request,
                principal: rebuild.Principal,
                connectionId: rebuild.ConnectionId,
                correlationId: rebuild.CorrelationId,
                expectedContentHash: rebuild.ExpectedContentHash,
                preparationFailure: rebuild.PreparationFailure
            ),
                WorldPendingOp.Undo undo => Host.Persistence.ApplyUndo(
                count: undo.Count,
                principal: undo.Principal,
                connectionId: undo.ConnectionId,
                correlationId: undo.CorrelationId
            ),
                _ => false,
            };

            // The tape's own completion field: fires exactly once, for exactly the ops ApplyEnvelope's own dispatch
            // threaded one onto — see EnqueueMutation's own remarks.
            if (op is WorldPendingOp.Mutate { OutcomeObserved: { } outcomeObserved }) {
                outcomeObserved(obj: ok);
            }

            if (op is WorldPendingOp.Mutate { Binding: { } binding, Completion: { } completion }) {
                var outcome = (ok
                    ? WorldMutationOutcome.AppliedOutcome(
                        binding,
                        "world.mutation.applied"
                    )
                    : WorldMutationOutcome.RefusedOutcome(
                        binding,
                        "world.mutation.refused",
                        (Host.Document.LastMutationFailureDetail ?? "mutation was refused")
                    )
                );

                completion(new WorldSubmissionResult.Mutation(Outcome: outcome));
            }

            // The addon mutation seam's I2: an addon-sourced Mutate op's OUTCOME — never its application, which
            // just ran above through the identical machinery a console mutation runs through — routes back to the
            // originating guest's RESERVED answer cell here, at drain time (same Step, before intents). The cell
            // itself is not delivered until ResolveReads(T) stages it into the guest's batch T+1; this only records
            // which verdict that staging will use. A well-formed mutation the document-apply pipeline itself
            // refused (a validation/capacity/cross-row failure — TryApplyMutation already printed the loud reason)
            // answers Rejected, distinct from every dispatch-door refusal the seam's earlier stages produce.
            if ((op is WorldPendingOp.Mutate { SourceAddonInstanceId: >= 0L } addonMutate)) {
                Host.Addons?.CompleteMutation(
                    addonInstanceId: addonMutate.SourceAddonInstanceId,
                    actOrdinal: addonMutate.ActOrdinal,
                    applied: ok
                );
            }

            applied |= ok;
        }

        if (applied) {
            Host.Document.DeliverPending();
        }

        return applied;
    }
    // Build and deliver the tick's snapshot to every typed-lane subscriber. Skipped with no subscriber attached.
    private void EmitSnapshot(ulong tick, ulong stepTicks) {
        if (!Host.Output.HasTypedSubscribers) {
            return;
        }

        Host.Output.DeliverSnapshot(snapshot: Host.Document.BuildSnapshot(
            stepTicks: stepTicks,
            tick: tick
        ));
    }
    // The one door into the ordered domain. The authority gate is held across both the enqueue and the drain, which
    // is what makes m_ordered and m_drainingOrdered single-threaded state rather than shared state: a tick-thread
    // Submit and a socket worker's gated authority operation both reach this queue, and a drain skipped because a
    // different thread held the guard would leave an already-applied population change (an admitted arrival)
    // standing without the grant rows its own event carries. lock is reentrant, so an authority operation that
    // dispatches from inside the gate re-enters here without deadlocking.
    private void EnqueueOrdered(WorldOrderedEntry entry) {
        lock (Host.AuthorityGate) {
            if (
                (entry is WorldOrderedEntry.Submission retiringSubmission) &&
                Host.AuthorityRetiring
            ) {
                retiringSubmission.Completion?.Invoke(new WorldSubmissionResult.Refusal(
                    Code: "world.authority.retiring",
                    Detail: "authority is retiring and no longer admits submissions"
                ));
                return;
            }
            m_ordered.Enqueue(item: entry);
            DrainOrdered();
        }
    }

    // The live half of link liveness: each DIRECT projection in the tick's frozen graph whose delivered snapshot tick
    // advanced is one refresh. An authored row the source could not resolve contributes no projection at all, which
    // is exactly "nothing was delivered" — the staleness count rises and the grace comparison decides. Replay drives
    // this from taped LinkDelivery entries instead; a shadow server holds no adjacency source, so the two never
    // double-count.
    private void ObserveAdjacencyDeliveries() {
        if (Host.Population.Adjacencies is not { } adjacencies) {
            return;
        }

        var projections = adjacencies.Visuals();

        for (var index = 0; (index < projections.Count); index++) {
            var projection = projections[index];

            if (
                !projection.Direct ||
                !Host.Events.ObserveLinkDelivery(
                adjacencyName: projection.Name,
                deliveredTick: projection.Neighbour.SnapshotTick
            )
            ) {
                continue;
            }

            Host.LinkDeliveryTap?.Invoke(obj: projection.Name);
        }
    }
    private void StepCore(in FixedStepContext context) {
        // Settled here, before anything below can compose or rebase a mutation: context.ElapsedTicks is this whole
        // step's own engine-time coordinate (the exact engine tick the step completes at), so every write and read
        // this tick performs — an administrative mutation drained below, a rule's own writes, a response sweep —
        // rebase an Advance epoch, or reads one, against the SAME value. Reassigned identically at the step's own
        // end (m_lastCompletedEngineTicks = context.ElapsedTicks); setting it again there is a no-op.
        m_lastCompletedEngineTicks = context.ElapsedTicks;
        // The per-tick mutation-dispatch allowance opens HERE, before either half of the tick that spends it: the
        // addon seam's pre-flight (TickAddons, immediately below) and the drain that applies what it — and every peer
        // submission buffered since the last step — enqueued.
        Host.MutationBudget.BeginTick();
        Host.Extensions.Drain();
        Host.Addons?.TickAddons(tick: (context.Tick + 1UL));
        _ = DrainPendingOps(tick: context.Tick);
        Host.TransferForwarder?.ResolveContinuations(source: Host);
        Host.InputHold.PrepareParticipants(population: Host.Population);

        m_tickWrittenCount = 0;

        while (m_intents.TryDequeue(result: out var submission)) {
            if (Host.Body(index: submission.EntityIndex) is not { } body) {
                continue;
            }

            _ = ApplyIntentSubmission(
                body: body,
                submission: in submission
            );
        }

        ApplyFederatedIntents();

        Host.Addons?.ApplyContributions(tick: (context.Tick + 1UL));
        FoldChannelContributions();
        Host.InputHold.Apply(population: Host.Population);

        // Settle m_contended for real now that the WHOLE tick's writers have run — the seat drain AND the addon
        // contributions: a queue's dequeue order says nothing about whether a body was genuinely contended for the tick
        // as a whole, only ReportContention's own observation of the FULL set could (see its remarks) — this is that
        // observation, applied once per tracked entity rather than mid-drain.
        for (var index = 0; (index < m_tickWrittenCount); index++) {
            m_contended[m_tickWrittenEntity[index]] = m_tickCollided[index];
        }

        // The context-sensitive-button interception's eligibility pass (the RPG A-button) — resolved against the
        // PRE-MOVE positions (this tick's population has not advanced yet), so a rising edge computed inside
        // AdvanceSeats below diverts into an Engage instead of ever reaching the avatar's action track.
        Span<int> engageProbeOrdinals = stackalloc int[Host.Population.LocalSeatCount];
        Span<int> engageProbeScreens = stackalloc int[Host.Population.LocalSeatCount];
        Span<bool> engageEdges = stackalloc bool[Host.Population.LocalSeatCount];

        ResolveEngageProbes(
            ordinals: engageProbeOrdinals,
            screens: engageProbeScreens
        );

        var tick = (context.Tick + 1UL);

        Host.Population.Adjacencies?.BeginTick(tick: tick);
        // Immediately after the projection graph freezes, so "did this seam refresh" is read off the SAME pinned
        // image contact and rendering will read for this tick, never a delivery that lands mid-step.
        ObserveAdjacencyDeliveries();

        var stepStartEngineTick = (context.ElapsedTicks - context.StepTicks);

        // Release every carry relationship this tick's drain invalidated (a partner gone inactive, a kit retune away
        // from the facet either side needs) BEFORE the advance passes, so an orphaned target re-enters rigid
        // integration and contact in the same tick its carrier disappeared rather than skipping one.
        Host.Population.PrepareCarriedBodies();
        // Sample every active body's medium surface BEFORE either half of the tick advances it, so a medium
        // hold's phase-4 law (inside AdvanceSimulated/AdvanceSeats' own body.Advance calls) reads this tick's
        // surface, never last tick's.
        Host.Population.SampleMediumSurfaces();
        Host.Population.AdvanceSimulated(
            tick: tick,
            stepTicks: context.StepTicks,
            stepStartEngineTick: stepStartEngineTick
        );
        Host.Population.AdvanceSeats(
            tick: tick,
            stepTicks: context.StepTicks,
            stepStartEngineTick: stepStartEngineTick,
            engageProbeOrdinals: engageProbeOrdinals,
            engageEdges: engageEdges
        );
        Host.Population.ResolveDynamicContacts();
        Host.Population.ResolveTethers();
        Host.Population.UpdateCarriedBodies();
        Host.Population.CompleteStep(tick: tick);
        foreach (var designation in Host.Population.DesignationOutputs) {
            _ = Host.Document.ApplyDesignationCore(
                designation: designation,
                principal: WorldPrincipal.Console,
                knownSubject: true,
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0
            );
        }
        Host.Population.ClearDesignationOutputs();

        // Kit-fired `generate` effects, staged during THIS tick's advance and enqueued through the ORDINARY mutation
        // pipeline for the NEXT tick's drain — the same door a console world.generate and a world rule both use, so
        // one mechanism covers all three rather than three. The one-tick latency is real and reported: this is the
        // first ActionEffect to write the DOCUMENT rather than per-body state, so it is the first to pay the
        // pipeline's own round trip. The acting principal is WorldPrincipal.World whichever body fired it — the
        // effect is the world's authored program acting, not the seat (see that principal's remarks).
        foreach (var invocation in Host.Population.GeneratorInvocationOutputs) {
            Host.EnqueueMutation(mutation: new WorldMutation.Generate(
                Principal: WorldPrincipal.World,
                Row: invocation.Row
            ));
        }

        Host.Population.ClearGeneratorInvocationOutputs();

        if (Host.Population.DurableStateOutputs.Count > 0) {
            Host.DurableStateOutputTap?.Invoke(obj: Host.Population.DurableStateOutputs);
            foreach (var output in Host.Population.DurableStateOutputs) {
                var submission = new WorldDocumentSubmission(
                    SourceDocumentId: (Host.Document.Definition.DocumentId ?? string.Empty),
                    OwnerDocumentId: output.PlayerId,
                    Tick: output.Tick,
                    Slot: output.Value.Name,
                    Kind: output.Kind,
                    StorageKind: output.StorageKind,
                    Value: ((output.StorageKind == ActionStateKind.Counter)
                    ? output.Value.Value.Value
                    : checked((long)output.Value.TimerTicks))
                );

                Host.Document.LastDocumentReceipt = Host.Profiles.Submit(submission: submission);
                Host.DocumentSubmissionTap?.Invoke(obj: Host.Document.LastDocumentReceipt.Value);
            }
        }

        // Route every fired probe into an ordinary Engage, through the SAME authority path a manual body.engage
        // takes — see ResolveEngageProbes for why this is expected to succeed (its own eligibility pass already
        // re-checks CheckEngage), so a denial here can only mean the grant table changed between the two passes on
        // this single-threaded step (an admin revoke applied in between — not a concurrent race, the step runs one
        // thread) — rare enough to accept as a swallowed press rather than a second suppression path.
        for (var slot = 0; (slot < Host.Population.LocalSeatCount); slot++) {
            if (!engageEdges[slot]) {
                continue;
            }

            var principal = WorldPrincipal.Seat(slot: slot);
            var target = GrantSubject.Screen(index: engageProbeScreens[slot]);

            if (Host.Engagement.Compose(
                actingPrincipal: principal,
                entityIndex: slot,
                exclusive: true,
                target: target,
                targetPrincipal: principal
            )) {
                if (Host.Output.HasNarrationSink) {
                    Host.Output.Narrate(
                        channel: "world.engage",
                        text: $"[world.engage: {principal.Describe()} auto-engaged {target.Describe()} — context button]"
                    );
                }
            }
        }

        // Collect this tick's world-scoped events AFTER the population settles (so positions/occupancy are this
        // tick's) and BEFORE the addon read pump, so ResolveReads can stage them into the SAME batch as this tick's
        // disclosures/answers.
        Host.Events.Collect(
            definition: Host.Document.Definition,
            population: Host.Population
        );

        // The music clock/director step HERE — immediately after Collect() so this tick's own edges (never a stale
        // tick's) drive this tick's transition arming, and before anything else reads Host.Events.Edges (one call site,
        // one reader, no second-consumer ordering to pin).
        if (
            (m_musicClock is { } musicClock) &&
            (m_musicDirector is { } musicDirector)
        ) {
            var previousElapsedTicks = musicClock.ElapsedTicks;
            var boundary = musicClock.Advance(stepTicks: context.StepTicks);

            // Diegetic-instrument clock fold — see InstrumentClockBoundary's own remarks for why holding the screen
            // application is the whole gate (never a WorldSessionLever) and why only Beat, never Bar, is contributed.
            boundary |= Host.InstrumentClockBoundary(
                previousElapsedTicks: previousElapsedTicks,
                currentElapsedTicks: musicClock.ElapsedTicks
            );

            musicDirector.Step(
                boundary: boundary,
                edges: ProjectSenseEdges(),
                tick: tick
            );

            // Fire the music.transition cue lane on the SAME tick the transition committed — never a later tick's
            // read-back of LastTransitionTick, which would fire once per subsequent Step call instead of once.
            if (musicDirector.LastTransitionTick == tick) {
                MusicTransitionTap?.Invoke(obj: tick);
            }

            // The active-layer set is level-triggered (never queued), so the tap fires on ANY tick the set differs
            // from what was last tapped — not only a transition-commit tick, and not gated to only fire once.
            if (!ActiveLayerSetsEqual(
                a: musicDirector.ActiveLayerTuneIds,
                b: m_lastTappedActiveLayerTuneIds
            )) {
                m_lastTappedActiveLayerTuneIds = [.. musicDirector.ActiveLayerTuneIds];
                MusicLayerTap?.Invoke(obj: m_lastTappedActiveLayerTuneIds);
            }

            // Fire the music.embellishment cue lane on the SAME tick it fired — the same reasoning as the transition
            // tap immediately above.
            if (musicDirector.LastEmbellishmentTick == tick) {
                MusicEmbellishmentTap?.Invoke(obj: musicDirector.LastEmbellishmentPatchId!);
            }
        }

        // World rules evaluate HERE — after the event feed (so a $region gate reads this tick's settled occupancy)
        // and before the addon read pump and the snapshot (so a rule's write is visible to the same tick's guest
        // reads and delivery).
        Host.RuleHost.EvaluateWorldRules(
            tick: tick,
            stepTicks: context.StepTicks
        );
        StepBoardEnforcement(tick: tick);
        Host.StepSearch(tick: tick);
        StepFields(tick: tick);
        // Every tick-driven deadline recovery evaluates on the same terms, right beside rules — see SweepDeadlines'
        // own remarks.
        SweepDeadlines(tick: tick);
        // Placement response sweep — AFTER StepFields, so a response condition reads this tick's own lattice writes;
        // a state-driven prototype swap for a placement carrying a Respond trait (see WorldPlacementResponse).
        SweepPlacementResponses(tick: tick);
        // Placement deal sweep — after the rule frame has folded, so a cell a rule wrote this tick deals on this
        // tick; each dealt template's children follow its row (see WorldPlacementDeal).
        SweepPlacementDeals(tick: tick);
        Host.Addons?.ResolveReads(tick: (context.Tick + 1UL));
        // Fold this tick's routed intents into their targets BEFORE the snapshot is built.
        Host.Engagement.FoldTick();

        // screens[].memory bindings poke a moved cell into its machine and mirror a machine's moved byte into its
        // cell — see WorldServer.MachineMemory.cs. Runs right before the machine steps so a Write binding's poke
        // reaches it before this tick's advance.
        Host.SyncMachineMemory(tick: tick);

        // Step every booted machine off THIS tick's freshly-folded pads: reads WorldEngagement.BuildPadSnapshot()
        // directly, in-process, no client/wire round-trip. Runs in EVERY boot shape via WorldServerStepShell.Step
        // (headless and windowed alike both call WorldServer.Step) — ROM state IS sim state, not presentation-fed.
        // context.StepTicks is forwarded exactly, preserving the exact-rational T-cycle bridge.
        Host.Machines.Advance(
            stepTicks: context.StepTicks,
            pads: Host.Engagement.BuildPadSnapshot()
        );

        // A body-target route's contribution lands on the TARGET's NEXT tick — FoldTick runs after this tick's
        // population has already advanced, so there is no earlier point this tick where the target could still fold
        // it in. Queued through the ordinary intent path (never LoopbackTransport's IntentTap), so it is re-derived at
        // replay time rather than taped directly — see WorldEngagement's class remarks on replay visibility.
        foreach (var contribution in Host.Engagement.BodyContributions) {
            EnqueueIntent(submission: new IntentSubmission(
                Tick: (context.Tick + 2UL),
                EntityIndex: contribution.TargetBody,
                Intent: contribution.Intent,
                Principal: contribution.Principal
            ));
        }

        EmitSnapshot(
            tick: (context.Tick + 1UL),
            stepTicks: context.StepTicks
        );
        m_lastCompletedTick = (context.Tick + 1UL);
        m_lastStepTicks = context.StepTicks;
        m_lastCompletedEngineTicks = context.ElapsedTicks;
    }

    /// <summary>Applies one submission to a live body under the per-tick Drive check, and returns the verdict that
    /// decided it. The one write path every intent producer shares — the seat drain and every mounted addon's staged
    /// contributions — so authority, the fold routing below, and the denial latch can never diverge between them.
    /// <para>A submission whose principal does not hold <see cref="WorldCapability.Drive"/> over the target body
    /// applies nothing and is reported once per denial episode (a revoked driver keeps submitting; the first
    /// refused tick logs, then the body idles until re-granted). Allocation-free, O(1). The line prints the
    /// verdict's reason, so distinct denial causes such as "exclusively reserved by seat1" and "no grant names it"
    /// surface as distinct messages. The <c>m_driveDenied</c> reporting latch stays deliberately outside the
    /// verdict.</para>
    /// <para>An allowed submission then routes one of two ways, because one body has exactly one base: the
    /// participant that owns it. The body's owning seat or peer (its principal index equals the entity index) — or
    /// any principal when the body is not human-occupied (<see cref="WorldPopulation.IsHumanOccupied"/>: an
    /// unoccupied body is a bot at full authority by construction) — writes through
    /// <see cref="WorldBody.SubmitIntent"/>, which overwrites, and this tick's write is tracked for contention
    /// reporting. Everything else — an addon's contribution, or a different seat co-driving a body it does not own
    /// — is staged into the per-tick contribution set instead (<see cref="StageContribution"/>, which carries both
    /// the submission's intent and its held-channel composition image) and folded later by
    /// <see cref="FoldChannelContributions"/>; it is never tracked as contention, because a consented (or
    /// default-denied) contribution is a deliberate composition path, not a race.</para></summary>
    /// <param name="body">The live body the submission targets — the caller resolves it, because a submission
    /// naming an entity that holds no body is not an authority outcome and must not be answered as one.</param>
    /// <param name="submission">The tick, entity index, principal, intent, and held-lane image.</param>
    /// <returns>The verdict that decided the check; nothing was applied unless it allows.</returns>
    /// <remarks>A body carrying a nonzero cell on a <see cref="WorldStateRow.GatesDrive"/> row
    /// (<see cref="TryDriveGateVerdict"/>, resynced from live document state — see
    /// <see cref="WorldGrants.SyncState"/>) has its intent refused before the grant table is checked, regardless of
    /// any Drive hold, including an exclusive reservation: a status effect is a fact about the body, not about who
    /// is allowed to drive it, so it outranks a principal that genuinely holds Drive. No rule or effect touches the
    /// grant table to express this; the check reads the state fact directly, the same "deciding fact beyond the
    /// static grant table" shape <see cref="GrantRule.OwnershipHold"/> reads a different fact through. The gate is
    /// released, never latched: once the gate row's cell reads zero, this check passes straight through to the
    /// ordinary <see cref="WorldGrants.Allows"/> call below. <see cref="WorldDocument.ApplyCommand"/>'s generic Drive gate checks
    /// the same <see cref="TryDriveGateVerdict"/> before its own <see cref="WorldGrants.Allows"/> call, so a
    /// scripted tape segment (<c>body.fly</c>/<c>EnqueueSegment</c>) is refused by the same fact a raw per-tick
    /// channel submission is.</remarks>
    internal GrantVerdict ApplyIntentSubmission(WorldBody body, in IntentSubmission submission) {
        var gated = TryDriveGateVerdict(
            bodyIndex: submission.EntityIndex,
            verdict: out var gatedVerdict
        );
        var verdict = (gated
            ? gatedVerdict
            : Host.GrantTable.Allows(
                principal: submission.Principal,
                capability: WorldCapability.Drive,
                subject: GrantSubject.Body(index: submission.EntityIndex)
            )
        );

        if (!verdict.IsAllowed) {
            if (!m_driveDenied[submission.EntityIndex]) {
                var actor = submission.Principal;
                var entityIndex = submission.EntityIndex;

                if (Host.Output.HasNarrationSink) {
                    Host.Output.Narrate(
                        channel: "world.grant denied",
                        text: $"[world.grant denied: {verdict.DescribeRefusal(
                            actor: actor,
                            dropped: "intent dropped, body idle",
                            subject: $"body:{entityIndex}",
                            verb: "drive"
                        )}]"
                    );
                }
                m_driveDenied[submission.EntityIndex] = true;
            }

            return verdict;
        }

        m_driveDenied[submission.EntityIndex] = false;
        Host.InputHold.ObserveMeasurement(submission: in submission);

        var bodyIndex = submission.EntityIndex;
        var isOwningParticipant = (((submission.Principal.Kind == PrincipalKind.Seat) || (submission.Principal.Kind == PrincipalKind.Peer)) && (submission.Principal.Index == bodyIndex));
        var isOwningSeat = ((submission.Principal.Kind == PrincipalKind.Seat) && (submission.Principal.Index == bodyIndex));
        var occupied = Host.Population.IsHumanOccupied(bodyIndex: bodyIndex);

        if (
            isOwningParticipant ||
            !occupied
        ) {
            ReportContention(
                entityIndex: bodyIndex,
                principal: submission.Principal
            );
            body.SubmitIntent(intent: submission.Intent);
            body.SetHeldChannels(channels: submission.HeldChannels);

            if (isOwningSeat) {
                // This tick's `h` and its held-device image — never the ladder's winner (a tape still outranks the
                // former; see WorldBody.NextIntent). Recorded even when nothing ends up contributing this tick, so
                // FoldChannelContributions's common-case check (m_hasContribution) stays the only extra cost an
                // uncontended body pays. The held image is recorded because the fold may have to REPLACE the direct
                // write above with the max of it and a contributor's own composition act.
                m_ownerBase[bodyIndex] = submission.Intent;
                m_ownerHeld[bodyIndex] = submission.HeldChannels;
                m_hasOwnerBase[bodyIndex] = true;
                // The read-back's baseline for THIS write: no pool, no contributors — true unless
                // FoldChannelContributions (below, later this same tick) overwrites it with a real fold, because a
                // contribution actually landed. Reset rather than left stale, so a body contended two ticks ago and
                // quiet since does not keep reporting a pool that no longer exists.
                RecordDirectChannelRead(
                    seat: bodyIndex,
                    intent: submission.Intent
                );
            }
        } else {
            StageContribution(
                bodyIndex: bodyIndex,
                principal: submission.Principal,
                submission: in submission
            );
        }

        return verdict;
    }
    /// <summary>Re-applies a server-authored event through the population and grant doors. Replay calls this same
    /// method; there is no state-install bypass.</summary>
    /// <param name="serverEvent">The ordered event.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serverEvent"/> is <see langword="null"/>.</exception>
    internal void ApplyServerEvent(WorldServerEvent serverEvent) {
        ArgumentNullException.ThrowIfNull(argument: serverEvent);

        switch (serverEvent) {
            case WorldServerEvent.PeerAdmitted admitted:
                foreach (var peer in admitted.Entries) {
                    Host.Population.ApplyPeerAdmitted(
                        grantTemplates: [],
                        peer: in peer
                    );

                    foreach (var stale in Host.GrantTable.StalePeerGenerations(
                        index: peer.BodyIndex,
                        currentGeneration: peer.Generation
                    )) {
                        foreach (var row in Host.GrantTable.Rows(principal: stale)) {
                            Host.Revoke(
                                grant: row,
                                actor: WorldPrincipal.Console
                            );
                        }
                    }
                }

                var installedGrants = new List<WorldGrant>();

                foreach (var grant in admitted.MintedGrants) {
                    if (Host.GrantTable.TryApplyGrant(
                        grant: grant,
                        actor: WorldPrincipal.Console
                    )) {
                        installedGrants.Add(item: grant);
                    }
                }

                foreach (var peer in admitted.Entries) {
                    var installedTemplates = WorldGrants.AdmissionTemplatesFor(
                        mintedGrants: installedGrants,
                        peer: peer
                    );

                    Host.Population.SetPeerAdmissionInstalledGrantTemplates(
                        bodyIndex: peer.BodyIndex,
                        grantTemplates: installedTemplates
                    );
                }

                break;
            case WorldServerEvent.PeerDisconnected disconnected:
                // The body parks with grace; the authority does not. Park serves body continuity (pose, durable
                // state, collidability, targetability) and ApplyPeerDisconnected still defers that half to
                // ReclaimExpiredParks. Authority follows the CONNECTION: while disconnected, nothing can exercise
                // the generation's rows, yet an Exclusive subject it reserved would refuse every live acquirer —
                // for the whole grace window, and forever at rate 0, where the compiled grace is Never and no sweep
                // ever runs. Release is therefore unconditional here, the same path a non-parking disconnect (an
                // authored-zero grace, or no live match) takes; a verified-identity reconnect that resumes the
                // parked BODY re-mints its admission templates through the ordinary PeerAdmitted event
                // (WorldServer.TryAdmitVerifiedParticipant's resume arm), so only live acquisitions beyond the
                // templates fail to survive the gap. It rides this event, so replay re-drives the identical
                // revocations through the identical door at the identical tick, with no separate tape entry.
                foreach (var peer in disconnected.Entries) {
                    Host.Population.ApplyPeerDisconnected(
                        peer: in peer,
                        tick: Host.NextInputTick
                    );
                }

                foreach (var grant in disconnected.RevokedGrants) {
                    Host.Revoke(
                        grant: grant,
                        actor: WorldPrincipal.Console
                    );
                }

                break;
            default:
                if (Host.Output.HasNarrationSink) {
                    Host.Output.Narrate(
                        channel: "world.server-event refused",
                        text: $"[world.server-event refused: {serverEvent.GetType().Name} is not declared]"
                    );
                }
                return;
        }

        Host.ServerEventTap?.Invoke(obj: serverEvent);
    }
    /// <summary>The administrative drain — applies every buffered document-level operation (mutations, rebuilds,
    /// undo, addon lifecycle changes) without advancing simulation time: no addon tick, no intent drain, no body
    /// integration, no rules, no event collection, and no snapshot delivery. <see cref="DrainPendingOps"/> is
    /// normally reached only from inside <see cref="Step"/>, so an instance that never steps (an authored
    /// <c>simulation.rateHz</c> of 0, or a live <c>world.rate pause</c>) could otherwise never apply the very
    /// mutation that would change that — a permanent self-lock. Called on the host's own master timeline in place
    /// of <see cref="Step"/> for a tick a stopped/paused instance does not take (see <c>Puck.World.WorldInstanceHost</c>'s
    /// per-instance scheduling remarks for the host-side half of this contract — that type lives a layer above this
    /// assembly, hence prose rather than a cref here).</summary>
    /// <remarks>Opens a fresh per-tick mutation-dispatch allowance exactly as <see cref="Step"/>'s own top does
    /// (<see cref="WorldMutationBudgetMeter.BeginTick"/> is a plain clear — safe to call once per administrative
    /// drain, same as once per real tick), so an untrusted principal keeps a steady dispatch rate while stopped
    /// rather than being starved by a budget that never resets. Every applied entry journals against
    /// <see cref="m_lastCompletedTick"/> — the tick that does not move while stopped — so <c>world.undo</c> stays
    /// coherent: an administrative entry undoes exactly like an ordinary one, it is simply attributed to a tick
    /// number that repeats until the instance actually steps again. Document mutations are outside the replay
    /// tape's own recorded scope already (<see cref="Puck.World.WorldReplayTape"/>'s honest-scope remarks — the
    /// tape records the human/authority command stream, never a raw <see cref="Protocol.WorldMutation"/>), so this
    /// method introduces no new tape interaction.</remarks>
    /// <returns><see langword="true"/> when anything applied (a definition delivery occurred).</returns>
    internal bool DrainAdministrative() {
        lock (Host.AuthorityGate) {
            if (Host.AuthorityRetiring) { return false; }
            Host.MutationBudget.BeginTick();
            Host.Extensions.Drain();
            return DrainPendingOps(tick: m_lastCompletedTick);
        }
    }
    /// <summary>Buffers one entity's submitted intent for the next <see cref="Step"/>.</summary>
    /// <param name="submission">The tick, entity index, and merged intent.</param>
    internal void EnqueueIntent(in IntentSubmission submission) {
        lock (Host.AuthorityGate) {
            if (!Host.AuthorityRetiring) { m_intents.Enqueue(item: submission); }
        }
    }
    /// <summary>Advances the authoritative world by one exact host tick: run every mounted addon's guest code first (see
    /// <see cref="IWorldAddonHost.TickAddons"/>, which applies nothing) → drain the buffered live edits (mutations,
    /// swaps, undo), applying each at the tick boundary and delivering the new definition once if any applied → drain
    /// the tick's submitted intents → apply the addons' staged contributions
    /// (<see cref="IWorldAddonHost.ApplyContributions"/>) → fold every human-occupied body's tick (see
    /// <see cref="FoldChannelContributions"/>) → settle per-body contention over the tick as a whole → advance every
    /// body (peers, then seats) → resolve the addons' reads against the stepped state
    /// (<see cref="IWorldAddonHost.ResolveReads"/>) → deliver the tick's <see cref="WorldSnapshot"/>.</summary>
    /// <remarks>The three addon points are pinned, and each is pinned for a reason: guests run before anything is
    /// applied so a guest's own effect never depends on where in the tick it happened to be pumped; reads resolve
    /// after the step of the tick they were written in, so a verdict, a minted handle, and a pose all describe the
    /// same settled instant. <b>An addon's contribution to a human-occupied body is never a plain overwrite of the
    /// seat's own submission (<see cref="FixedContributionFold"/>).</b> <see cref="ApplyIntentSubmission"/> routes a
    /// non-owning contributor into a per-tick contribution set instead of calling <see cref="WorldBody.SubmitIntent"/>
    /// directly, and <see cref="FoldChannelContributions"/> — the fourth point, run once contributions have finished
    /// landing and before the population advances — folds each occupied body's owning-seat base with its tick's
    /// pooled/unpooled contributions into the single value <see cref="WorldBody.SubmitIntent"/> receives. An
    /// unoccupied body (no seat, or an inactive one) is untouched by any of this and keeps plain overwrite
    /// semantics, because occupancy is what makes a pool exist at all (a bot at full authority is not an oversight
    /// there). <see cref="WorldBody.NextIntent"/>'s tape-outranks-submitted ladder is itself untouched; only how the
    /// submitted tier is produced differs by occupancy.</remarks>
    /// <param name="context">Explicit simulation coordinates for this tick. Hosts and ordinary replay drivers
    /// use <see cref="Advance"/> to derive these from the authority's own checkpointed clock.</param>
    internal void Step(in FixedStepContext context) {
        lock (Host.AuthorityGate) {
            if (Host.AuthorityRetiring) { return; }
            StepCore(context: in context);
        }
    }
    /// <summary>Advances one step from this authority's own checkpointed clock. Hosts and replay drivers use this
    /// entry point so restoring a timeline cannot inherit the host pacing counter's old tick or elapsed time.</summary>
    /// <param name="stepTicks">The exact duration of this step in engine ticks.</param>
    /// <exception cref="OverflowException">The completed tick or engine-time coordinate would overflow.</exception>
    internal void Advance(ulong stepTicks) {
        lock (Host.AuthorityGate) {
            if (Host.AuthorityRetiring) { return; }
            _ = checked((m_lastCompletedTick + 1UL));
            var context = new FixedStepContext(
                ElapsedTicks: checked((m_lastCompletedEngineTicks + stepTicks)),
                StepTicks: stepTicks,
                Tick: m_lastCompletedTick
            );

            StepCore(context: in context);
        }
    }
    /// <summary>Submits one envelope into the ordered domain — the single front door every non-intent submission kind
    /// drains through (see <see cref="IWorldServerHost.Submit"/>'s own remarks). Enqueues, then immediately drains
    /// the whole queue inline, so a submission applies synchronously before this call returns — exactly matching the
    /// per-kind synchronous methods it replaces. The in-process <c>LoopbackTransport</c> submits on connection 0;
    /// <c>WorldPeerHost</c> submits each admitted socket peer under its own per-connection id.</summary>
    /// <param name="envelope">The envelope to submit.</param>
    /// <param name="completion">Invoked once with the envelope's typed result, or <see langword="null"/>.</param>
    internal void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) =>
        EnqueueOrdered(entry: new WorldOrderedEntry.Submission(
            Completion: completion,
            Envelope: envelope
        ));
    /// <summary>Runs every tick-driven deadline recovery through its own sorted <see cref="WorldDeadlineTable{T}"/>:
    /// ownership escrow reclaim, transfer-lease expiry, contribution-tenure retraction and reconnect-park
    /// teardown.</summary>
    /// <param name="tick">The simulation tick being swept.</param>
    /// <remarks>All four evaluate on the same replay-deterministic terms, so one call keeps that shared shape visible
    /// at the call site rather than four lines that could drift apart. A tick with nothing due pays four front
    /// comparisons and reads nothing else.</remarks>
    private void SweepDeadlines(ulong tick) {
        Host.GrantTable.ReclaimExpiredEscrows(tick: tick);
        Host.TransferEscrow.ReclaimExpired(tick: tick);
        SweepContributionTenure(tick: tick);
        // The body half of a park only: a peer generation's grant rows go at its PeerDisconnected event, and a
        // restored parked generation's go at RestoreCheckpoint, so an expiring park holds nothing to release here.
        Host.Population.ReclaimExpiredParks(tick: tick);
    }
}
