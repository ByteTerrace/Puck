using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    internal bool CheckEngagePolicy(int entityIndex, GrantSubject target, out string reason) {
        reason = string.Empty;

        if (target.Kind == GrantSubjectKind.Placement) {
            return CheckTabletopEngagePolicy(
                distanceSquared: out _,
                entityIndex: entityIndex,
                placementId: (target.Id ?? string.Empty),
                reason: out reason
            );
        }

        if (target.Kind != GrantSubjectKind.Screen) {
            return true;
        }

        foreach (var screen in Host.Document.Definition.Screens) {
            if (screen.Index == target.Value) {
                return CheckScreenEngagePolicy(
                    entityIndex: entityIndex,
                    reason: out reason,
                    screen: screen
                );
            }
        }

        // A portal face engages while the engaging body is live: its input then routes through the glass to the
        // session the face observes.
        if (Host.Engagement.TryPortalFace(
            face: out _,
            screenIndex: target.Value
        )) {
            if (Host.Body(index: entityIndex) is null) {
                reason = $"body {entityIndex} is not live";

                return false;
            }

            return true;
        }

        reason = $"screen {target.Value} does not exist";
        return false;
    }

    // The reason names a refusal only when describe is set: the engage probe asks every screen every tick and reads none.
    private bool CheckScreenEngagePolicy(int entityIndex, WorldScreen screen, out string reason, bool describe = true) {
        reason = string.Empty;

        if (!screen.Route.Engageable) {
            if (describe) { reason = $"screen {screen.Index} is not engageable"; }
            return false;
        }

        if (!ShowsEngageTarget(hasMachine: Host.Machines.HasMachine(index: screen.Index), screen: screen)) {
            if (describe) { reason = $"screen {screen.Index} has no machine to control and shows no producer"; }
            return false;
        }

        if (Host.Body(index: entityIndex) is not { } body) {
            if (describe) { reason = $"body {entityIndex} is not live"; }
            return false;
        }

        var radius = body.EngageReach(radius: screen.Route.EngageRadius);

        if (PlanarDistanceSquared(body: body, screen: screen) > (radius * radius)) {
            if (describe) { reason = $"body {entityIndex} is outside screen {screen.Index}'s engage radius ({((double)radius).ToString(format: "0.###", provider: System.Globalization.CultureInfo.InvariantCulture)} at its size)"; }
            return false;
        }

        reason = string.Empty;
        return true;
    }
    // A tabletop engages a live body standing within its engage radius of the board's centre; a placement carrying no
    // board facet with an engage block is no tabletop. The body's squared ground-plane distance from the board's centre
    // is answered beside it, for the probe's nearest-target choice.
    private bool CheckTabletopEngagePolicy(int entityIndex, string placementId, out string reason, out FixedQ4816 distanceSquared, bool describe = true) {
        distanceSquared = FixedQ4816.Zero;

        if (!WorldTabletopFrame.TryFind(
            definition: Host.Document.Definition,
            engage: out var engage,
            placementId: placementId,
            topology: out var topology
        )) {
            reason = (describe ? $"placement '{placementId}' carries no engageable tabletop" : string.Empty);

            return false;
        }

        if (Host.Body(index: entityIndex) is not { } body) {
            reason = (describe ? $"body {entityIndex} is not live" : string.Empty);

            return false;
        }

        var radius = body.EngageReach(radius: engage.Radius);

        distanceSquared = PlanarDistanceSquared(body: body, centre: WorldTabletopFrame.Centre(topology: topology));

        if (distanceSquared > (radius * radius)) {
            reason = (describe ? $"body {entityIndex} is outside tabletop '{placementId}''s engage radius ({((double)radius).ToString(format: "0.###", provider: System.Globalization.CultureInfo.InvariantCulture)} at its size)" : string.Empty);

            return false;
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Returns whether an engageable screen has something a seat engages: a live booted machine, whose pad the
    /// seat plays, or a producer source (a window capture, a camera, a test pattern), which the seat's camera frames on
    /// the screen and no pad reaches. Read from the document and the machine host alone, so a replay decides it the
    /// same.</summary>
    /// <param name="screen">The screen row.</param>
    /// <param name="hasMachine">Whether the screen carries a live booted machine
    /// (<see cref="IWorldMachineHost.HasMachine"/>).</param>
    /// <returns><see langword="true"/> when the screen carries a machine or shows a producer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static bool ShowsEngageTarget(WorldScreen screen, bool hasMachine) {
        ArgumentNullException.ThrowIfNull(argument: screen);

        return (hasMachine || (screen.Source is WorldScreenSource.Producer));
    }

    // The squared distance on the ground plane from a body to a screen's origin, which the engage radius bounds.
    private static FixedQ4816 PlanarDistanceSquared(WorldBody body, WorldScreen screen) => PlanarDistanceSquared(
        body: body,
        centre: new FixedVector3(
            X: FixedQ4816.FromDouble(value: screen.Origin.X),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: screen.Origin.Z)
        )
    );
    // The squared distance on the ground plane from a body to a point.
    private static FixedQ4816 PlanarDistanceSquared(WorldBody body, in FixedVector3 centre) {
        var position = body.FixedPosition;

        return new FixedVector2(
            X: (position.X - centre.X),
            Y: (position.Z - centre.Z)
        ).LengthSquared;
    }

    // body.press and body.stop are read back SYNCHRONOUSLY by their console handlers immediately after a submit
    // (WorldPopulation.PressRefusal/StopRefusal, mirroring MotionRefusal) — so a refusal that reaches EITHER of
    // ApplyCommand's early returns above (the grant-table denial, the missing/inactive body) must leave a note
    // behind too, or the handler reads whatever an EARLIER, unrelated attempt on the SAME body left there and
    // echoes a fabricated affirmative quoting stale numbers. Every other command kind is untracked and a no-op here
    // — their handlers narrate a refusal off the existing stderr/EchoTap path instead.
    internal void NoteDriveRefusalIfTracked(WorldCommand command, string reason) {
        switch (command) {
            case WorldCommand.PressChannel press:
                Host.Population.NotePressRefusal(
                    bodyIndex: press.EntityIndex,
                    reason: reason
                );

                break;
            case WorldCommand.Stop stop:
                Host.Population.NoteStopRefusal(
                    bodyIndex: stop.EntityIndex,
                    reason: reason
                );

                break;
        }
    }

    // Whether the principal's application set names anything other than its own body — the "already engaged" test
    // the context-button probe skips on. Reads the one storage; there is no separate latch to consult.
    private bool HasComposedApplication(Principal principal) {
        var own = GrantSubject.Body(index: principal.Index);

        var applications = Host.GrantTable.Applications(principal: principal);

        for (var index = 0; (index < applications.Count); index++) {
            var application = applications[index];

            if (application.Target != own) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Queues one principal's effective Control-route change onto the ordered domain.</summary>
    /// <param name="principal">The principal whose route moved.</param>
    /// <param name="previous">The route it held, or <see langword="null"/>.</param>
    /// <param name="current">The route it holds now, or <see langword="null"/>.</param>
    internal void QueueRouteTransition(Principal principal, GrantSubject? previous, GrantSubject? current) {
        var sourceBody = principal.Kind switch {
            PrincipalKind.Seat => principal.Index,
            PrincipalKind.Peer => principal.Index,
            _ => -1,
        };

        if (sourceBody < 0) {
            return;
        }

        if (previous is { } disengaged) {
            Host.Events.QueueRouteDisengaged(
                sourceBody: sourceBody,
                target: disengaged
            );
        }

        if (current is { } engaged) {
            Host.Events.QueueRouteEngaged(
                sourceBody: sourceBody,
                target: engaged
            );
        }
    }

    // Records this tick's (entity, principal) write; when a DIFFERENT principal already wrote the SAME entity earlier
    // in this same tick, prints one loud, attributed line — but ONLY the first tick this body transitions into a
    // contended state (see m_contended's own remarks: the check reads LAST tick's settled outcome, never this tick's
    // in-progress one, so a body contended for many consecutive ticks logs once, not once per tick). Two distinct
    // ALLOWED Drive grants naming one body is a genuine conflict (see WorldClient.SubmitSeatIntents' own remarks) —
    // the later writer still wins (unchanged from before this method existed); only the SILENCE of that outcome is what
    // this closes. Allocation-free: past m_tickWrittenEntity's sized capacity (see its own remarks) the tracked set
    // simply stops growing, so a body written for the first time after saturation goes unreported this tick. That is a
    // DIAGNOSTIC degrading, never a write changing — the deliberate trade against resizing on the hot path.
    private void ReportContention(int entityIndex, Principal principal) {
        for (var index = 0; (index < m_tickWrittenCount); index++) {
            if (m_tickWrittenEntity[index] != entityIndex) {
                continue;
            }

            if (m_tickWrittenPrincipal[index] != principal) {
                if (!m_contended[entityIndex]) {
                    if (Host.Output.HasNarrationSink) {
                        Host.Output.Narrate(
                            channel: "world.grant",
                            text: $"[world.grant: body:{entityIndex} driven by both {m_tickWrittenPrincipal[index].Describe()} and {principal.Describe()} this tick — {principal.Describe()}'s intent applies]"
                        );
                    }
                }

                m_tickCollided[index] = true;
                m_tickWrittenPrincipal[index] = principal;
            }

            return;
        }

        if (m_tickWrittenCount < m_tickWrittenEntity.Length) {
            m_tickWrittenEntity[m_tickWrittenCount] = entityIndex;
            m_tickWrittenPrincipal[m_tickWrittenCount] = principal;
            m_tickCollided[m_tickWrittenCount] = false;
            m_tickWrittenCount++;
        }
    }
    /// <summary>Returns the context-sensitive-button interception's eligibility pass (the RPG A-button, <c>AGENTS.md</c>'s
    /// overworld intent) — for each active, un-routed local seat, the nearest target on the ground plane (the earlier in
    /// document order on a tie, screens before tabletops): a tabletop whose board facet's engage block names a channel
    /// this world resolves, within its radius of the board's centre, that would pass
    /// <see cref="WorldEngagement.CheckEngage"/> (several seats may sit at one table), or a screen that is
    /// engageable and backed by a live booted machine or showing a producer (the real gate is
    /// <see cref="CheckScreenEngagePolicy"/>'s <see cref="ShowsEngageTarget"/> check over
    /// <see cref="IWorldMachineHost.HasMachine"/> — the authoritative server-side boot signal; the host boots and
    /// steps the machine in-process, so this project sees the real boot directly rather than a document-declared
    /// proxy), names an <see cref="WorldScreenRoute.EngageChannel"/> this world's channel table resolves, carries no live occupant
    /// (<see cref="WorldEngagement.PlayersOn"/> empty), sits within <see cref="WorldScreenRoute.EngageRadius"/> of the
    /// seat's pre-move position (this tick's population has not advanced yet — <c>Step</c> calls this before
    /// <see cref="WorldPopulation.AdvanceSeats"/>), and would actually pass <see cref="WorldEngagement.CheckEngage"/>.
    /// <para>
    /// <see cref="WorldEngagement.Compose"/>'s own remarks leave engageable/proximity/machine policy to the caller
    /// (ordinarily the client, ahead of a manual <c>body.engage</c>'s submission) — this is that same policy,
    /// resolved here instead, from document and grant state alone. Pure sim state in, pure sim state out: a shadow
    /// replay re-derives the identical decision at the identical tick from the identical taped inputs, with nothing
    /// new to tape — the same "re-derived, not recorded" shape <see cref="WorldEngagement"/>'s own body-route
    /// contributions already establish (see its class remarks).
    /// </para></summary>
    /// <param name="ordinals">Per-seat-slot output: the channel ordinal to probe this tick, or <c>-1</c> for none —
    /// filled entirely (every slot without an eligible screen reads <c>-1</c>, the zero-cost default every world
    /// without an <c>engageChannel</c>-bearing screen takes, which is every shipped world today).</param>
    /// <param name="targets">Per-seat-slot output: the eligible screen or tabletop placement paired with
    /// <paramref name="ordinals"/>'s entry, meaningful only beside an ordinal that is not <c>-1</c>.</param>
    /// <param name="leaving">Per-seat-slot output: whether the probe LEAVES its target rather than engaging it. The
    /// same button is the way out: a seat captured on a screen whose route names an engage channel, or at a tabletop,
    /// probes that channel, and its rising edge dissolves the seat's applications (see
    /// <see cref="ResolveLeaveProbe"/>).</param>
    private void ResolveEngageProbes(Span<int> ordinals, Span<GrantSubject> targets, Span<bool> leaving) {
        ordinals.Fill(value: -1);
        targets.Clear();
        leaving.Fill(value: false);

        var screenRows = Host.Document.Definition.Screens;
        var placementRows = Host.Document.Definition.Placements;

        for (var slot = 0; (slot < Host.Population.LocalSeatCount); slot++) {
            if (Host.Body(index: slot) is not { } body) {
                continue;
            }

            var principal = Principal.Seat(slot: slot);

            if (body.Engaged) {
                if (ResolveLeaveProbe(
                    ordinal: out var leaveOrdinal,
                    principal: principal,
                    target: out var leaveTarget
                )) {
                    ordinals[slot] = leaveOrdinal;
                    targets[slot] = leaveTarget;
                    leaving[slot] = true;
                }

                continue;
            }

            // A seat that has composed anything beyond its own body keeps that set — composing off an unrelated
            // button press over an active possession/mirror is not this feature's job.
            if (HasComposedApplication(principal: principal)) {
                continue;
            }

            var nearest = FixedQ4816.Zero;

            for (var index = 0; (index < screenRows.Count); index++) {
                var screen = screenRows[index];

                if (
                    (screen.Route.EngageChannel is not { Length: > 0 } channelName) ||
                    !CheckScreenEngagePolicy(
                    describe: false,
                    entityIndex: slot,
                    reason: out _,
                    screen: screen
                )
                ) {
                    continue;
                }

                if (!body.Channels.TryGetOrdinal(
                    name: channelName,
                    ordinal: out var ordinal
                )) {
                    continue;
                }

                if (Host.Engagement.HasPlayersOn(screenIndex: screen.Index)) {
                    continue;
                }

                if (!Host.Engagement.CheckEngage(
                    target: GrantSubject.Screen(index: screen.Index),
                    actingPrincipal: principal
                ).IsAllowed) {
                    continue;
                }

                var distance = PlanarDistanceSquared(body: body, screen: screen);

                if ((ordinals[slot] < 0) || (distance < nearest)) {
                    ordinals[slot] = ordinal;
                    targets[slot] = GrantSubject.Screen(index: screen.Index);
                    nearest = distance;
                }
            }

            for (var index = 0; (index < placementRows.Count); index++) {
                var placement = placementRows[index];

                if (
                    (placement.Board?.Engage is not { } engage) ||
                    !body.Channels.TryGetOrdinal(
                    name: engage.Channel,
                    ordinal: out var ordinal
                ) ||
                    !CheckTabletopEngagePolicy(
                    describe: false,
                    distanceSquared: out var distance,
                    entityIndex: slot,
                    placementId: placement.Id,
                    reason: out _
                )
                ) {
                    continue;
                }

                var table = GrantSubject.Placement(id: placement.Id);

                if (!Host.Engagement.CheckEngage(
                    actingPrincipal: principal,
                    target: table
                ).IsAllowed) {
                    continue;
                }

                if ((ordinals[slot] < 0) || (distance < nearest)) {
                    ordinals[slot] = ordinal;
                    targets[slot] = table;
                    nearest = distance;
                }
            }
        }
    }
    // The leave half of the context button: a seat captured on a screen or at a tabletop (its set holds that
    // application and not its own body) whose engage channel this world resolves probes that channel, and its rising
    // edge dissolves the set back to the own-body default. The validator refuses a screen engage channel the route's
    // kit plays as a pad button, so the press that leaves is never also a machine button.
    private bool ResolveLeaveProbe(Principal principal, out int ordinal, out GrantSubject target) {
        target = default;
        ordinal = -1;
        if (Host.Body(index: principal.Index) is not { } body) { return false; }

        foreach (var application in Host.GrantTable.Applications(principal: principal)) {
            if (
                (application.Target.Kind == GrantSubjectKind.Placement) &&
                WorldTabletopFrame.TryFind(
                definition: Host.Document.Definition,
                engage: out var engage,
                placementId: (application.Target.Id ?? string.Empty),
                topology: out _
            ) &&
                body.Channels.TryGetOrdinal(
                name: engage.Channel,
                ordinal: out ordinal
            )
            ) {
                target = application.Target;

                return true;
            }

            if (application.Target.Kind != GrantSubjectKind.Screen) {
                continue;
            }

            foreach (var screen in Host.Document.Definition.Screens) {
                if (
                    (screen.Index == application.Target.Value) &&
                    (screen.Route.EngageChannel is { Length: > 0 } channelName) &&
                    body.Channels.TryGetOrdinal(
                        name: channelName,
                        ordinal: out ordinal
                    )
                ) {
                    target = application.Target;

                    return true;
                }
            }
        }

        ordinal = -1;

        return false;
    }

    /// <summary>Determines whether <paramref name="bodyIndex"/> carries a nonzero cell on a state row declaring
    /// <see cref="WorldStateRow.GatesDrive"/> — Composition-core's CC/death gating (Seam A), the one rule both
    /// Drive-admission doors consult (<see cref="ApplyIntentSubmission"/>'s per-tick channel submission,
    /// <see cref="WorldDocument.ApplyCommand"/>'s generic Drive gate over an authority command such as
    /// <c>EnqueueSegment</c>/<c>SnapPose</c>). The door reads state — this never touches the grant table, and neither
    /// caller consults it before this check: a status effect refuses regardless of what <see cref="WorldGrants.Allows"/>
    /// would otherwise answer, including for a principal that genuinely holds Drive (an exclusive reserver
    /// included).</summary>
    /// <param name="bodyIndex">The 0-based entity index the command/submission targets.</param>
    /// <param name="verdict">The <see cref="GrantRule.DriveGated"/> verdict, when gated.</param>
    /// <returns><see langword="true"/> when the body is gated — the caller must refuse without consulting the grant
    /// table at all.</returns>
    /// <remarks>The complete ingress inventory obliged to call this before admitting a drive (a new drive ingress is
    /// obliged to call it too — that is what keeps a two-call-site pattern honest over time): <see cref="ApplyIntentSubmission"/>
    /// — seat-channel submissions, addon FoldActs, the unoccupied-body bot at full authority, and co-drive folds all
    /// land there — and <see cref="WorldDocument.ApplyCommand"/>, the command-shaped drive path. Two call-sites, one rule.</remarks>
    internal bool TryDriveGateVerdict(int bodyIndex, out GrantVerdict verdict) {
        if (Host.GrantTable.TryGetDriveGate(
            bodyIndex: bodyIndex,
            gateRow: out var gateRow
        )) {
            verdict = new GrantVerdict(
                Rule: GrantRule.DriveGated,
                GateRow: gateRow
            );

            return true;
        }

        verdict = default;

        return false;
    }
    // Every LIVE inhabitant of placementId, drive-possessed by a concrete grant — the guard's own read, walked over
    // WorldPopulation.CollectInhabitants' (small, per-placement) result. Stops at the first possessed inhabitant: one
    // is enough to refuse the whole despawn, and a multi-count Inhabit facet is rare enough that finding every
    // possessed slot before refusing would not change the operator's remedy.
    internal bool TryFindPossessedInhabitant(string placementId, out int bodyIndex, out Grantee holder) {
        Host.Population.CollectInhabitants(
            into: m_possessedInhabitantScratch,
            placementId: placementId
        );

        foreach (var index in m_possessedInhabitantScratch) {
            if (Host.GrantTable.IsBodyPossessed(
                body: index,
                holder: out holder
            )) {
                bodyIndex = index;

                return true;
            }
        }

        bodyIndex = -1;
        holder = default;

        return false;
    }
}
