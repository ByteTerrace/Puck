using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool CheckEngagePolicy(int entityIndex, GrantSubject target, out string reason) {
        reason = string.Empty;

        if (target.Kind != GrantSubjectKind.Screen) {
            return true;
        }

        foreach (var screen in m_definition.Screens) {
            if (screen.Index == target.Value) {
                return CheckScreenEngagePolicy(
                    entityIndex: entityIndex,
                    reason: out reason,
                    screen: screen
                );
            }
        }

        reason = $"screen {target.Value} does not exist";
        return false;
    }
    private bool CheckScreenEngagePolicy(int entityIndex, WorldScreen screen, out string reason) {
        if (!screen.Route.Engageable) {
            reason = $"screen {screen.Index} is not engageable";
            return false;
        }

        if (!m_machines.HasMachine(index: screen.Index)) {
            reason = $"screen {screen.Index} has no machine to control";
            return false;
        }

        if (Body(index: entityIndex) is not { } body) {
            reason = $"body {entityIndex} is not live";
            return false;
        }

        var position = body.FixedPosition;
        var delta = new FixedVector2(
            X: (position.X - FixedQ4816.FromDouble(value: screen.Origin.X)),
            Y: (position.Z - FixedQ4816.FromDouble(value: screen.Origin.Z))
        );
        var radius = FixedQ4816.FromDouble(value: screen.Route.EngageRadius);

        if (delta.LengthSquared > (radius * radius)) {
            reason = $"body {entityIndex} is outside screen {screen.Index}'s engage radius ({screen.Route.EngageRadius.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)})";
            return false;
        }

        reason = string.Empty;
        return true;
    }
    // body.press and body.stop are read back SYNCHRONOUSLY by their console handlers immediately after a submit
    // (WorldPopulation.PressRefusal/StopRefusal, mirroring MotionRefusal) — so a refusal that reaches EITHER of
    // ApplyCommand's early returns above (the grant-table denial, the missing/inactive body) must leave a note
    // behind too, or the handler reads whatever an EARLIER, unrelated attempt on the SAME body left there and
    // echoes a fabricated affirmative quoting stale numbers. Every other command kind is untracked and a no-op here
    // — their handlers narrate a refusal off the existing stderr/EchoTap path instead.
    private void NoteDriveRefusalIfTracked(WorldCommand command, string reason) {
        switch (command) {
            case WorldCommand.PressChannel press:
                m_population.NotePressRefusal(
                    bodyIndex: press.EntityIndex,
                    reason: reason
                );

                break;
            case WorldCommand.Stop stop:
                m_population.NoteStopRefusal(
                    bodyIndex: stop.EntityIndex,
                    reason: reason
                );

                break;
        }
    }
    // Whether the principal's application set names anything other than its own body — the "already engaged" test
    // the context-button probe skips on. Reads the one storage; there is no separate latch to consult.
    private bool HasComposedApplication(WorldPrincipal principal) {
        var own = GrantSubject.Body(index: principal.Index);

        foreach (var application in m_grants.Applications(principal: principal)) {
            if (application.Target != own) {
                return true;
            }
        }

        return false;
    }
    private void QueueRouteTransition(WorldPrincipal principal, GrantSubject? previous, GrantSubject? current) {
        var sourceBody = principal.Kind switch {
            PrincipalKind.Seat => principal.Index,
            PrincipalKind.Peer => principal.Index,
            _ => -1,
        };

        if (sourceBody < 0) {
            return;
        }

        if (previous is { } disengaged) {
            m_events.QueueRouteDisengaged(
                sourceBody: sourceBody,
                target: disengaged
            );
        }

        if (current is { } engaged) {
            m_events.QueueRouteEngaged(
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
    private void ReportContention(int entityIndex, WorldPrincipal principal) {
        for (var index = 0; (index < m_tickWrittenCount); index++) {
            if (m_tickWrittenEntity[index] != entityIndex) {
                continue;
            }

            if (m_tickWrittenPrincipal[index] != principal) {
                if (!m_contended[entityIndex]) {
                    Console.Error.WriteLine(value: $"[world.grant: body:{entityIndex} driven by both {m_tickWrittenPrincipal[index].Describe()} and {principal.Describe()} this tick — {principal.Describe()}'s intent applies]");
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
    /// <summary>Returns the context-sensitive-button interception's eligibility pass (the RPG A-button, <c>CLAUDE.md</c>'s
    /// overworld intent) — for each active, un-routed local seat, the first (document order) screen that is
    /// engageable and backed by a live booted machine (the real gate is <see cref="CheckScreenEngagePolicy"/>'s
    /// <see cref="WorldMachineHost.HasMachine"/> check — the authoritative server-side boot signal; the host boots and
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
    /// <param name="screens">Per-seat-slot output: the eligible screen's engine index paired with <paramref name="ordinals"/>'s
    /// entry, or <c>-1</c> alongside a <c>-1</c> ordinal.</param>
    private void ResolveEngageProbes(Span<int> ordinals, Span<int> screens) {
        ordinals.Fill(value: -1);
        screens.Fill(value: -1);

        var screenRows = m_definition.Screens;

        for (var slot = 0; (slot < Population.LocalSeatCount); slot++) {
            if (
                (Body(index: slot) is not { } body) ||
                body.Engaged
            ) {
                continue;
            }

            var principal = WorldPrincipal.Seat(slot: slot);

            // A seat that has composed anything beyond its own body keeps that set — composing off an unrelated
            // button press over an active possession/mirror is not this feature's job.
            if (HasComposedApplication(principal: principal)) {
                continue;
            }

            for (var index = 0; (index < screenRows.Count); index++) {
                var screen = screenRows[index];

                if (
                    (screen.Route.EngageChannel is not { Length: > 0 } channelName) ||
                    !CheckScreenEngagePolicy(
                    entityIndex: slot,
                    reason: out _,
                    screen: screen
                )
                ) {
                    continue;
                }

                if (!m_population.Channels.TryGetOrdinal(
                    name: channelName,
                    ordinal: out var ordinal
                )) {
                    continue;
                }

                if (m_engagement.PlayersOn(screenIndex: screen.Index).Count > 0) {
                    continue;
                }

                if (!m_engagement.CheckEngage(
                    target: GrantSubject.Screen(index: screen.Index),
                    actingPrincipal: principal
                ).IsAllowed) {
                    continue;
                }

                ordinals[slot] = ordinal;
                screens[slot] = screen.Index;

                break;
            }
        }
    }
    /// <summary>Determines whether <paramref name="bodyIndex"/> carries a nonzero cell on a state row declaring
    /// <see cref="WorldStateRow.GatesDrive"/> — Composition-core's CC/death gating (Seam A), the one rule both
    /// Drive-admission doors consult (<see cref="ApplyIntentSubmission"/>'s per-tick channel submission,
    /// <see cref="ApplyCommand"/>'s generic Drive gate over an authority command such as
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
    /// land there — and <see cref="ApplyCommand"/>, the command-shaped drive path. Two call-sites, one rule.</remarks>
    private bool TryDriveGateVerdict(int bodyIndex, out GrantVerdict verdict) {
        if (m_grants.TryGetDriveGate(
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
    private bool TryFindPossessedInhabitant(string placementId, out int bodyIndex, out WorldPrincipal holder) {
        m_population.CollectInhabitants(
            into: m_ruleInhabitantScratch,
            placementId: placementId
        );

        foreach (var index in m_ruleInhabitantScratch) {
            if (m_grants.IsBodyPossessed(
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
