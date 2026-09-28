using Puck.Commands;
using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One diegetic screen's merged engagement pad this tick — the OR-merged controller image every application
/// currently standing on <see cref="ScreenIndex"/> translates to (the multiplayer-cabinet shape:
/// <see cref="WorldEngagement.FoldTick"/> is the writer). Sparse: a screen nobody applies to carries no entry at
/// all rather than an explicit neutral one, so the common (nobody-engaged) tick costs nothing to encode.
/// Server-internal: <see cref="IWorldMachineHost.Advance"/> reads <see cref="WorldEngagement.BuildPadSnapshot"/>
/// directly, in-process, so this never crosses the loopback.</summary>
/// <param name="ScreenIndex">The engine screen-surface index.</param>
/// <param name="Pad">The merged controller image for this tick's machine step.</param>
public readonly record struct ScreenPadSnapshot(int ScreenIndex, MachinePadState Pad);
/// <summary>
/// The control-application fold — where a participant's resolved intent goes, distinct from the
/// <see cref="IntentSource"/> axis (what fills it). A principal holds a SET of
/// <see cref="ControlApplication"/>s (<see cref="IWorldGrantsView.Applications"/>), and that set is the whole of
/// engagement: the own-body member is the avatar driving itself, a screen member is a channel-masked tap onto a
/// booted machine's pad through a kit's <see cref="WorldKit.Pad"/> map, and a body member is possession. Capturing
/// is the own-body member's ABSENCE; mirroring is both members present. Screen members publish on
/// <see cref="BuildPadSnapshot"/> for <see cref="IWorldMachineHost.Advance"/> to read, in-process, inside
/// <see cref="WorldServer.Step"/>; body members become co-drive contributions queued for the target's next tick,
/// through the exact same Drive-gated contribution path an addon or a co-driving seat already uses
/// (<see cref="WorldTick.StageContribution"/>) — no second write path.
/// </summary>
/// <remarks>
/// The write half (<see cref="Compose"/>/<see cref="Dissolve"/>) is server-side authority reached only through
/// <see cref="WorldServer.ApplyCommand"/>'s <see cref="WorldCommand.ComposeControl"/>/
/// <see cref="WorldCommand.DissolveControl"/> arms — the same ordered domain, tape, and eventual socket door every
/// other authority command travels through, so a future remote client inherits routing for free with no
/// special-case loopback shortcut. The set owner identity (<c>TargetPrincipal</c> on both command kinds) is
/// resolved by the caller (a local seat's own claimed identity via <c>Puck.World.Client.PlayerRoster.PrincipalOf</c>,
/// or a population entry's Peer identity) rather than by this class, because that resolution needs the local
/// roster's claim state — client-only bookkeeping this class has no business holding. The read half's
/// entity→principal resolution needs no such indirection: a body/entity index is a
/// <see cref="Principal.Index"/> directly for both <see cref="PrincipalKind.Seat"/> (0..3,
/// <see cref="WorldPopulation.LocalSeatCount"/>-bounded) and <see cref="PrincipalKind.Peer"/> (indices 4 through
/// the population capacity minus one) principals,
/// so <see cref="PlayersOn"/>/<see cref="FoldTick"/>/<see cref="DissolveScreen"/> resolve bodies with plain index
/// arithmetic and never consult a roster.
/// <para><b>The capture latch is derived, never stored beside the set.</b> <see cref="WorldBody.SetEngaged"/> is a
/// projection of one predicate — "the set omits the own-body application" — written by <see cref="SyncLatch"/>
/// alone, from every write path and re-asserted for every visited body at the top of <see cref="FoldTick"/>. There
/// is one storage and one derivation, so a latch and an application set cannot disagree and no repair exists to
/// perform.</para>
/// <para><b>Replay visibility.</b> A body member's per-tick channel passthrough is synthesized directly into
/// <see cref="WorldServer"/>'s own intent queue (never through <c>LoopbackTransport</c>'s <c>IntentTap</c>), so it
/// is structurally excluded from the replay tape's recorded intent list — exactly like a mounted addon's driving.
/// It is re-derived at replay time by re-running this same fold against the source seat's own recorded submission
/// (which is captured, ordinarily) and the recorded set state (the <see cref="WorldCommand.ComposeControl"/> command
/// itself, and any Grant/Revoke touching an applied target, all travel as ordinary tape entries). Replay's
/// state-system verdict includes the population pose digest, so a possessed body's motion and a captured source's
/// stillness are covered automatically; the separate pose trace keeps that divergence inspectable.</para>
/// <para>Single-threaded, like the grant table it views: <see cref="WorldServer.ApplyCommand"/> mutates it on the
/// command-apply window and <see cref="FoldTick"/> reads it during <see cref="WorldServer.Step"/>, both on the
/// launcher's window-pump thread, so no lock guards this state.</para>
/// </remarks>
public sealed partial class WorldEngagement {
    private readonly WorldChannelTable m_channels;
    private readonly WorldPadElement?[] m_defaultPad;
    private readonly Func<WorldDefinition> m_definition;
    private readonly IWorldGrantsView m_grants;
    private readonly WorldPopulation m_population;

    // The policy cache, the one place a screen's or a kit's engagement policy is compiled: each entry is compiled from
    // the live definition's row and kept only while that definition still holds the very same row object, so a live
    // edit (world.row.set, a reload) replaces the row and the next read compiles the new one, once. A screen's entry is
    // its authored reach mask and pad-kit name (what Compose stamps and FoldTick reads) and its light gun's mapping
    // (what FoldTick aims through); a kit's is its compiled pad map (channel ordinal -> pad element, or null when
    // unmapped). A kit that declares no pad, like a screen naming no kit, reads the engine's baked default table; a null
    // Kit on a body target never reaches a pad at all (pure passthrough). Channels are boot-fixed, so the channel table
    // is compiled once.
    private readonly Dictionary<int, (WorldScreen Row, ScreenPolicy Policy)> m_screenPolicies = new();
    private readonly Dictionary<string, (WorldKit Row, WorldPadElement?[] Pad)> m_kitPads = new(comparer: StringComparer.Ordinal);
    // The live definition's screen and kit rows by index and name (IndexRows), for the section objects they were read from.
    private readonly Dictionary<int, WorldScreen> m_screenRows = new();
    private readonly Dictionary<string, WorldKit> m_kitRows = new(comparer: StringComparer.Ordinal);

    private WorldKitsSection? m_indexedKits;
    private IReadOnlyList<WorldScreen>? m_indexedScreens;

    // Reused scratch for the per-frame PlayersOn/DissolveScreen collect+prune, so the hot path allocates nothing
    // after warmup.
    private readonly List<Principal> m_holderScratch = new();
    private readonly List<Principal> m_staleScratch = new();
    // Reused scratch for a Compose/Dissolve set rebuild — the write path is human-cadence, but reusing it keeps the
    // allocation profile of this class uniform.
    private readonly List<ControlApplication> m_composeScratch = new();
    // This tick's per-screen merged pad accumulator (FoldTick's write, BuildPadSnapshot's read) — a reused Dictionary
    // cleared and refilled every FoldTick call, since screen indices are document-authored and not a compact 0..N
    // space a fixed-size array could index directly.
    private readonly Dictionary<int, MachinePadState> m_screenPads = new();
    // The reused, capacity-only-grows backing array BuildPadSnapshot slices into — the same "grow, never shrink"
    // idiom WorldServer.BuildSnapshot's m_snapshotEntries uses, sized against however many screens are EVER
    // simultaneously applied to rather than a document-declared screen count (there is no such cap).
    private ScreenPadSnapshot[] m_padEntries = Array.Empty<ScreenPadSnapshot>();
    // This tick's body-target contributions — cleared and refilled every FoldTick call, drained by
    // WorldServer.Step right after FoldTick returns (queued for the NEXT tick's intent drain through the ordinary
    // co-drive/StageContribution path; see the class remarks on replay visibility for why this is never taped here).
    private readonly List<BodyRouteContribution> m_bodyContributions = new();

    // Zeroes every ordinal the mask does not reach — the application's reach, applied once before an intent reaches
    // ITS TARGET (never applied to the source body's own integration, which always sees the full unmasked intent).
    private static PlayerIntent ApplyMask(PlayerIntent intent, ChannelReachMask mask) {
        var masked = intent;

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (!mask.Contains(ordinal: ordinal)) {
                masked = masked.WithChannel(
                    ordinal: ordinal,
                    value: default
                );
            }
        }

        return masked;
    }
    // Resolve a 0-based entity index to its live body, or null when the index holds none.
    private WorldBody? Body(int index) => ((((uint)index) < ((uint)m_population.Capacity))
        ? m_population.EntryBody(index: index)
        : null
    );
    // The closed WorldPadElement-to-MachineButtons mapping for every non-axis element — a compile-time-exhaustive
    // switch (a new WorldPadElement button member needs an arm here before it can ever be pressed).
    private static MachineButtons ButtonBit(WorldPadElement element) => element switch {
        WorldPadElement.South => MachineButtons.South,
        WorldPadElement.East => MachineButtons.East,
        WorldPadElement.West => MachineButtons.West,
        WorldPadElement.North => MachineButtons.North,
        WorldPadElement.DpadUp => MachineButtons.DpadUp,
        WorldPadElement.DpadDown => MachineButtons.DpadDown,
        WorldPadElement.DpadLeft => MachineButtons.DpadLeft,
        WorldPadElement.DpadRight => MachineButtons.DpadRight,
        WorldPadElement.LeftShoulder => MachineButtons.LeftShoulder,
        WorldPadElement.RightShoulder => MachineButtons.RightShoulder,
        WorldPadElement.Start => MachineButtons.Start,
        WorldPadElement.Back => MachineButtons.Back,
        _ => MachineButtons.None,
    };
    // The engine's baked pad map for a screen naming no kit: the two movement ROLES to the left stick (structural
    // ChannelRole ordinals, no channel name involved). It names no gameplay channel — a screen whose machine needs
    // a face button must name a kit whose pad map binds it.
    private static WorldPadElement?[] CompileDefaultPad() {
        var table = new WorldPadElement?[ChannelLimits.MaxChannels];

        table[((int)ChannelRole.MoveStrafe)] = WorldPadElement.LeftStickX;
        table[((int)ChannelRole.MoveAdvance)] = WorldPadElement.LeftStickY;

        return table;
    }
    // A kit's authored channel-name-keyed pad map resolved to an ordinal-indexed table. An unresolvable name —
    // validator-refused already — is simply skipped rather than throwing here, since this constructor has no error
    // channel.
    private static WorldPadElement?[] CompilePad(IReadOnlyDictionary<string, WorldPadElement> pad, WorldChannelTable channels) {
        var table = new WorldPadElement?[ChannelLimits.MaxChannels];

        foreach (var (name, element) in pad) {
            if (channels.TryGetOrdinal(
                name: name,
                ordinal: out var ordinal
            )) {
                table[ordinal] = element;
            }
        }

        return table;
    }
    // An authored channel-name list compiled to a ChannelReachMask — every ordinal when the screen authors none.
    private static ChannelReachMask CompileReach(IReadOnlyList<string>? names, WorldChannelTable channels) {
        if (names is not { Count: > 0 }) {
            return ChannelReachMask.All;
        }

        var bits = 0UL;

        foreach (var name in names) {
            if (channels.TryGetOrdinal(
                name: name,
                ordinal: out var ordinal
            )) {
                bits |= (1UL << ordinal);
            }
        }

        return new ChannelReachMask(Bits: bits);
    }
    // Whether the set still contains the participant's own-body application — the one predicate the capture latch
    // is derived from.
    private static bool HoldsOwnBody(IReadOnlyList<ControlApplication> applications, int bodyIndex) {
        var own = GrantSubject.Body(index: bodyIndex);

        for (var index = 0; (index < applications.Count); index++) {
            if (applications[index].Target == own) {
                return true;
            }
        }

        return false;
    }
    // Whether a principal's Index is its own body — only a seat's and a peer's is. An application set is written for a
    // body's participant, so any other identity (a session's ordinal, the console, an addon) owns no set here.
    private static bool OwnsABody(Principal principal) => (principal.Kind is PrincipalKind.Seat or PrincipalKind.Peer);
    // The principal a 0-based entity index resolves to — a seat slot below the local seat count, a population peer
    // identity above it (see the class remarks: no roster indirection is needed on the read half).
    private Principal PrincipalOf(int index) => ((index < m_population.LocalSeatCount)
        ? Principal.Seat(slot: index)
        : m_population.PeerPrincipal(index: index)
    );
    // Clear the composed application set of every principal collected as stale this pass (its body no longer live).
    // Runs over the scratch copy, so it never mutates a live enumeration.
    private void PruneStale() {
        foreach (var principal in m_staleScratch) {
            _ = m_grants.ClearApplications(principal: principal);
        }

        m_staleScratch.Clear();
    }
    // The shared check-then-mutate decision Dissolve applies and PeekDissolve reports. Every dissolved target is
    // Control-checked against the actor — the identical pair composing it required — before anything is written.
    private ControlOutcome ResolveDissolve(int entityIndex, Principal actingPrincipal, Principal targetPrincipal, bool apply) {
        if (
            !OwnsABody(principal: targetPrincipal) ||
            (Body(index: entityIndex) is null)
        ) {
            return ControlOutcome.NotApplied;
        }

        var applications = m_grants.Applications(principal: targetPrincipal);
        var own = GrantSubject.Body(index: targetPrincipal.Index);
        var composed = false;

        for (var index = 0; (index < applications.Count); index++) {
            if (applications[index].Target == own) {
                continue;
            }

            composed = true;

            if (!CheckEngage(
                actingPrincipal: actingPrincipal,
                target: applications[index].Target
            ).IsAllowed) {
                return ControlOutcome.Denied;
            }
        }

        if (!composed) {
            return ControlOutcome.NotApplied;
        }

        if (apply) {
            _ = m_grants.ClearApplications(principal: targetPrincipal);
            SyncLatch(
                applications: m_grants.Applications(principal: targetPrincipal),
                principal: targetPrincipal
            );
        }

        return ControlOutcome.Dissolved;
    }
    // Canonicalize in the deterministic fixed-point domain before narrowing to the presentation-facing pad float.
    // A channel's authored shape does not constrain which pad element an application may target, and raw
    // programmatic intents can exceed either normalized domain, so the destination element owns the final bound.
    private static float StickValue(FixedQ4816 raw) => ((float)((double)FixedQ4816.Clamp(
        value: raw,
        minimum: -FixedQ4816.One,
        maximum: FixedQ4816.One
    )));
    // The single latch derivation — the ONLY writer of WorldBody.SetEngaged. Idempotent (SetEngaged short-circuits
    // an unchanged value), so calling it from every write path and again per tick costs nothing and leaves no
    // window in which the latch and the set disagree.
    private void SyncLatch(Principal principal, IReadOnlyList<ControlApplication> applications) {
        if (Body(index: principal.Index) is not { } body) {
            return;
        }

        body.SetEngaged(engaged: !HoldsOwnBody(
            applications: applications,
            bodyIndex: principal.Index
        ));
    }
    private static float TriggerValue(FixedQ4816 raw) => ((float)((double)FixedQ4816.Clamp(
        value: raw,
        minimum: FixedQ4816.Zero,
        maximum: FixedQ4816.One
    )));

    /// <summary>Asserts this fold carries no state a checkpoint would need to capture — every application set lives
    /// in the grant table (already captured there), the per-kit pad tables and per-screen policy are boot-compiled
    /// (re-derived identically from the definition), and the pad snapshot/body-contribution buffers are per-tick
    /// scratch fully overwritten by the next <see cref="FoldTick"/> before anything reads them — so this asserts the
    /// one buffer (<see cref="m_bodyContributions"/>) that is supposed to be empty at a master boundary rather than
    /// silently assuming it.</summary>
    /// <exception cref="InvalidOperationException"><see cref="m_bodyContributions"/> is non-empty.</exception>
    public void AssertCheckpointQuiescent() {
        if (m_bodyContributions.Count != 0) {
            throw new InvalidOperationException(message: "a checkpoint requires the engagement fold's body-contribution buffer to be empty — capture only between a completed Step and the next StepInstances.");
        }
    }
    /// <summary>Returns this tick's per-screen merged pad lane, sliced from a reused backing array — read directly by
    /// <see cref="IWorldMachineHost.Advance"/> from inside <see cref="WorldServer.Step"/>, in-process. Must be read
    /// (or copied) before the next <see cref="FoldTick"/> call, exactly like <c>WorldServer.BuildSnapshot</c>'s own
    /// reused entity array.</summary>
    /// <returns>This tick's engaged-pad entries.</returns>
    public ReadOnlyMemory<ScreenPadSnapshot> BuildPadSnapshot() {
        if (m_padEntries.Length < m_screenPads.Count) {
            m_padEntries = new ScreenPadSnapshot[Math.Max(
                val1: m_screenPads.Count,
                val2: Math.Max(
                    val1: (m_padEntries.Length * 2),
                    val2: 4
                )
            )];
        }

        var count = 0;

        foreach (var pair in m_screenPads) {
            m_padEntries[count++] = new ScreenPadSnapshot(
                ScreenIndex: pair.Key,
                Pad: pair.Value
            );
        }

        return m_padEntries.AsMemory(
            length: count,
            start: 0
        );
    }
    /// <summary>Determines whether <paramref name="actingPrincipal"/> — the submitter, never the target entity's own principal —
    /// holds <see cref="WorldCapability.Control"/> over <paramref name="target"/> (the default permissive Control/all
    /// satisfies it for a seat/console actor). A read-only check a caller may run before side effects (e.g. the
    /// client's auto-insert-boot precheck ahead of <see cref="WorldCommand.ComposeControl"/>'s submission);
    /// <see cref="Compose"/> re-runs the identical check server-side itself, so this is never the only gate a
    /// mutation passes through.</summary>
    /// <param name="target">The application target subject (a screen or a body) to check.</param>
    /// <param name="actingPrincipal">The principal asking to compose or dissolve.</param>
    public GrantVerdict CheckEngage(GrantSubject target, Principal actingPrincipal) =>
        m_grants.Allows(
            capability: WorldCapability.Control,
            principal: actingPrincipal,
            subject: target
        );
    /// <summary>Composes a <see cref="ControlApplication"/> onto <paramref name="targetPrincipal"/>'s set: checks
    /// <paramref name="actingPrincipal"/> holds <see cref="WorldCapability.Control"/> over <paramref name="target"/>
    /// before any mutation, then writes the new set — the target application plus the participant's own-body
    /// application when <paramref name="exclusive"/> is <see langword="false"/> (mirroring), or the target
    /// application alone when it is <see langword="true"/> (capturing, which idles the avatar). Re-composing onto a
    /// target already applied replaces that member rather than stacking a duplicate. The application's kit and reach
    /// are resolved here, from document data (a screen's authored <see cref="WorldScreenRoute.Kit"/>/
    /// <see cref="WorldScreenRoute.Channels"/>, or passthrough over every ordinal for a body target — a body carries
    /// no route row to author them from). Screen policy (engageable, proximity, and machine presence) remains the
    /// caller's concern; <see cref="WorldServer.ApplyCommand"/> and its context-button probe share the authoritative
    /// server-side policy check. Denied when the actor lacks Control, or when the entity index holds no live body —
    /// either way nothing is mutated, and so is a target identity that owns no body (a session, the console, an
    /// addon), whose index names none.</summary>
    /// <param name="entityIndex">The 0-based entity index whose intent the composed application carries.</param>
    /// <param name="target">The application target subject — a screen or a body.</param>
    /// <param name="exclusive">Whether composing drops the own-body application (capture) or retains it (mirror).</param>
    /// <param name="actingPrincipal">The principal asking to compose — checked instead of the target's own principal
    /// so one seat cannot force another onto (or off) a target merely because the target happens to still hold the
    /// seeded permissive default.</param>
    /// <param name="targetPrincipal">The identity whose set is composed — the entity's own resolved identity.</param>
    /// <returns>Whether the application was permitted and recorded.</returns>
    public bool Compose(int entityIndex, GrantSubject target, bool exclusive, Principal actingPrincipal, Principal targetPrincipal) {
        if (!CheckEngage(
            actingPrincipal: actingPrincipal,
            target: target
        ).IsAllowed) {
            return false;
        }

        if (
            !OwnsABody(principal: targetPrincipal) ||
            (Body(index: entityIndex) is null)
        ) {
            return false;
        }

        var own = GrantSubject.Body(index: targetPrincipal.Index);

        m_composeScratch.Clear();

        // Everything already applied except this target (replaced below) and, under capture, the own body.
        foreach (var existing in m_grants.Applications(principal: targetPrincipal)) {
            if (
                (existing.Target == target) ||
                (exclusive && (existing.Target == own))
            ) {
                continue;
            }

            m_composeScratch.Add(item: existing);
        }

        if (
            !exclusive &&
            (target != own) &&
            !HoldsOwnBody(
            applications: m_composeScratch,
            bodyIndex: targetPrincipal.Index
        )
        ) {
            m_composeScratch.Add(item: ControlApplication.OwnBody(bodyIndex: targetPrincipal.Index));
        }

        // A screen application records the screen's policy as it stands at compose, for the echoes and the set's
        // identity; FoldTick reads the live policy again, so a later edit of the screen still reaches the fold.
        var policy = ((target.Kind == GrantSubjectKind.Screen)
            ? PolicyOf(screenIndex: target.Value)
            : null);

        m_composeScratch.Add(item: new ControlApplication(
            Kit: policy?.Kit,
            Reach: (policy?.Reach ?? ChannelReachMask.All),
            Target: target
        ));

        m_grants.SetApplications(
            applications: m_composeScratch,
            principal: targetPrincipal
        );
        SyncLatch(
            applications: m_grants.Applications(principal: targetPrincipal),
            principal: targetPrincipal
        );

        return true;
    }
    /// <summary>Dissolves every non-own-body application in <paramref name="targetPrincipal"/>'s set, restoring the
    /// own-body default — the mutating half of <see cref="PeekDissolve"/>'s decision. Requires
    /// <paramref name="actingPrincipal"/> to hold <see cref="WorldCapability.Control"/> over each dissolved target;
    /// a set already at its default is a friendly no-op.</summary>
    /// <param name="entityIndex">The 0-based entity index to dissolve.</param>
    /// <param name="actingPrincipal">The principal asking to dissolve.</param>
    /// <param name="targetPrincipal">The identity whose set is dissolved.</param>
    /// <returns>The outcome (see <see cref="ControlOutcome"/>).</returns>
    public ControlOutcome Dissolve(int entityIndex, Principal actingPrincipal, Principal targetPrincipal) =>
        ResolveDissolve(
            actingPrincipal: actingPrincipal,
            apply: true,
            entityIndex: entityIndex,
            targetPrincipal: targetPrincipal
        );
    /// <summary>Dissolves every application standing on <paramref name="screenIndex"/> — the administrative teardown
    /// a screen removal runs before its slot is disposed. No actor check: whatever authorized the removal (a
    /// Mutate-over-Screens grant) already exercised the authority this cleanup is a consequence of. Each holder's
    /// own-body application is restored when nothing else survives, so the avatar resumes driving itself. Iterates a
    /// scratch copy of the holders, so it never mutates a live enumeration.</summary>
    /// <param name="screenIndex">The engine screen-surface index being removed.</param>
    public void DissolveScreen(int screenIndex) {
        var target = GrantSubject.Screen(index: screenIndex);

        m_grants.CollectApplicationHolders(
            into: m_holderScratch,
            target: target
        );

        foreach (var principal in m_holderScratch) {
            m_composeScratch.Clear();

            foreach (var existing in m_grants.Applications(principal: principal)) {
                if (existing.Target != target) {
                    m_composeScratch.Add(item: existing);
                }
            }

            if (m_composeScratch.Count == 0) {
                m_composeScratch.Add(item: ControlApplication.OwnBody(bodyIndex: principal.Index));
            }

            m_grants.SetApplications(
                applications: m_composeScratch,
                principal: principal
            );
            SyncLatch(
                applications: m_grants.Applications(principal: principal),
                principal: principal
            );
        }

        m_holderScratch.Clear();
    }
    /// <summary>Folds every applied body's channel-masked intent onto its targets for this tick — a screen member
    /// merges into <see cref="m_screenPads"/> (the multiplayer-cabinet OR-merge,
    /// <see cref="MachinePadState.Merge"/>), its light gun aimed where the body's pointer ray maps on a
    /// <see cref="SourceDestination.Simulation"/> screen (<see cref="Aim"/>); a body member queues a co-drive contribution into
    /// <see cref="BodyContributions"/> for <see cref="WorldServer.Step"/> to enqueue onto the target's next tick,
    /// through the ordinary Drive-gated contribution path. ONE loop over the whole population covers both, and
    /// re-asserts each visited body's capture latch from the same set it folds (see the class remarks) — the
    /// own-body member is visited like any other and simply has nowhere to route, because the avatar's own
    /// integration IS its delivery. Run once per <see cref="WorldServer.Step"/>, before the tick's
    /// <see cref="Protocol.WorldSnapshot"/> is built, so <see cref="BuildPadSnapshot"/> reflects this tick's
    /// applied intents. A tick that replays recorded input stages no portal forward: the destinations beyond a portal
    /// are not replaying with this world.</summary>
    /// <param name="replaysInput">Whether this tick's input was fed from a replay tape (<see cref="WorldServer.ReplaysInput"/>).</param>
    public void FoldTick(bool replaysInput) {
        m_screenPads.Clear();
        m_bodyContributions.Clear();
        m_portalForwards.Clear();

        for (var index = 0; (index < m_population.Capacity); index++) {
            if (m_population.EntryBody(index: index) is not { } body) {
                continue;
            }

            var principal = PrincipalOf(index: index);
            var applications = m_grants.Applications(principal: principal);

            body.SetEngaged(engaged: !HoldsOwnBody(
                applications: applications,
                bodyIndex: index
            ));

            for (var slot = 0; (slot < applications.Count); slot++) {
                var application = applications[slot];

                // The own-body member routes nowhere: the avatar's own integration in WorldBody.Advance is what
                // delivers it, which is exactly what the latch above just enabled.
                if (
                    (application.Target.Kind == GrantSubjectKind.Body) &&
                    (application.Target.Value == index)
                ) {
                    continue;
                }

                if (application.Target.Kind == GrantSubjectKind.Screen) {
                    // The screen's live policy, never the one stamped at compose, so an edit of the screen or its kit
                    // reaches the next fold. A screen the document no longer declares routes nowhere.
                    // A screen with no row of its own may be a portal face, which routes the body's input through its
                    // glass to the session the face observes (StagePortalForward).
                    if (PolicyOf(screenIndex: application.Target.Value) is not { } policy) {
                        if (!replaysInput) {
                            StagePortalForward(
                                intent: body.EngagedIntent,
                                principal: principal,
                                screenIndex: application.Target.Value
                            );
                        }

                        continue;
                    }

                    var masked = ApplyMask(
                        intent: body.EngagedIntent,
                        mask: policy.Reach
                    );
                    var pad = (Translate(
                        intent: masked,
                        kit: policy.Kit
                    ) with {
                        Pointer = AimThrough(
                            mapping: policy.Aim,
                            ray: masked.SourceRay
                        ),
                    });

                    m_screenPads[application.Target.Value] = (m_screenPads.TryGetValue(
                        key: application.Target.Value,
                        value: out var existing
                    )
                        ? MachinePadState.Merge(
                            first: in existing,
                            second: in pad
                        )
                        : pad
                    );
                } else if (application.Target.Kind == GrantSubjectKind.Body) {
                    m_bodyContributions.Add(item: new BodyRouteContribution(
                        TargetBody: application.Target.Value,
                        Principal: principal,
                        Intent: ApplyMask(
                            intent: body.EngagedIntent,
                            mask: application.Reach
                        )
                    ));
                }
            }
        }
    }
    /// <summary>Returns where a pointer ray aims a screen's light gun, mapped in fixed point from the live document's
    /// screen row alone through <see cref="WorldScreenMappings.Normalized"/>, the mapping the <c>$pointer:</c> rule read
    /// runs: the hit's source-normalized fractions while the ray lands on the source, and <see cref="MachinePointer.Off"/>
    /// for no ray, a miss, the bezel, a screen the document does not declare, or a screen whose route input is not
    /// <see cref="SourceDestination.Simulation"/>.</summary>
    /// <param name="screenIndex">The engine screen index.</param>
    /// <param name="ray">The applied body's pointer ray this tick, or <see langword="null"/> for none.</param>
    /// <returns>The light gun's aim.</returns>
    public MachinePointer Aim(int screenIndex, SourceRay? ray) => AimThrough(
        mapping: PolicyOf(screenIndex: screenIndex)?.Aim,
        ray: ray
    );

    // Maps a pointer ray through a screen's light-gun mapping (ScreenPolicy.Aim); off for no mapping, no ray, or a hit
    // off the source.
    private static MachinePointer AimThrough(SourceMapping? mapping, SourceRay? ray) {
        if (
            (mapping is not { } normalized) ||
            (ray is not { } pointer)
        ) {
            return MachinePointer.Off;
        }

        var hit = normalized.MapRay(ray: pointer);

        return (hit.IsOnSource
            ? new MachinePointer(
                x: checked((ushort)hit.Coordinate.X.Value),
                y: checked((ushort)hit.Coordinate.Y.Value)
            )
            : MachinePointer.Off
        );
    }

    /// <summary>Returns the read-only twin of <see cref="Dissolve"/>: computes the identical outcome without
    /// mutating anything. The client submits <see cref="WorldCommand.DissolveControl"/> for the actual
    /// (server-authoritative) mutation regardless of what this reports — the command's own apply re-derives the same
    /// decision from the same state, since nothing can intervene between a local peek and its
    /// immediately-following submit over loopback — but the client needs this ahead of time to format its console
    /// echo and decide whether to drop its own client-side held device state.</summary>
    /// <param name="entityIndex">The 0-based entity index.</param>
    /// <param name="actingPrincipal">The principal asking to dissolve.</param>
    /// <param name="targetPrincipal">The identity whose set would be dissolved.</param>
    /// <returns>The outcome dissolving would produce.</returns>
    public ControlOutcome PeekDissolve(int entityIndex, Principal actingPrincipal, Principal targetPrincipal) =>
        ResolveDissolve(
            actingPrincipal: actingPrincipal,
            apply: false,
            entityIndex: entityIndex,
            targetPrincipal: targetPrincipal
        );
    /// <summary>Returns where this tick's merged pad aims <paramref name="screenIndex"/>'s light gun — the
    /// <c>screen.state</c> read-back of <see cref="FoldTick"/>'s aim.</summary>
    /// <param name="screenIndex">The engine screen index.</param>
    /// <returns>The aim, or <see cref="MachinePointer.Off"/> when no application reaches the screen this tick.</returns>
    public MachinePointer PointerOn(int screenIndex) => (m_screenPads.TryGetValue(
        key: screenIndex,
        value: out var pad
    )
        ? pad.Pointer
        : MachinePointer.Off
    );
    /// <summary>Returns every entity currently applied to <paramref name="screenIndex"/>, reported as 1-based display
    /// numbers (1..128, matching the <c>player.*</c> verb convention — a Seat/Peer principal's
    /// <see cref="Principal.Index"/> plus one) alongside whether the application captures it (its avatar idle,
    /// the classic engage) or mirrors it (its avatar still driving), in ascending display order. Prunes any set that
    /// no longer resolves to a live body.</summary>
    /// <param name="screenIndex">The engine screen index.</param>
    /// <returns>The applied entities' display indices and capture policy.</returns>
    public IReadOnlyList<(int Display, bool Capture)> PlayersOn(int screenIndex) {
        m_grants.CollectApplicationHolders(
            into: m_holderScratch,
            target: GrantSubject.Screen(index: screenIndex)
        );

        var players = new List<(int Display, bool Capture)>(capacity: m_holderScratch.Count);

        m_staleScratch.Clear();

        foreach (var principal in m_holderScratch) {
            if (Body(index: principal.Index) is not null) {
                players.Add(item: ((principal.Index + 1), !HoldsOwnBody(
                    applications: m_grants.Applications(principal: principal),
                    bodyIndex: principal.Index
                )));
            } else {
                m_staleScratch.Add(item: principal);
            }
        }

        PruneStale();
        players.Sort(comparison: static (a, b) => a.Display.CompareTo(value: b.Display));

        return players;
    }
    /// <summary>Translates a resolved <see cref="PlayerIntent"/> to a neutral standard-controller image through
    /// <paramref name="kit"/>'s compiled pad map — an authored <see cref="WorldKit.Pad"/> when the application names
    /// a kit, otherwise the engine's baked default (the two movement roles to the left stick only; an application
    /// whose machine needs a face button or any other element must name a kit binding it). Analog channels are
    /// canonicalized for their destination element: stick axes to -1..1 and triggers to 0..1. Digital elements
    /// compare the RAW fixed-point value against <see cref="WorldChannelTable.DefaultBinaryThreshold"/>, never a
    /// float round-trip.</summary>
    /// <param name="intent">The resolved (and reach-masked) intent to translate.</param>
    /// <param name="kit">The application's kit name, or <see langword="null"/> for the engine default.</param>
    /// <returns>The controller image the intent presses.</returns>
    public MachinePadState Translate(PlayerIntent intent, string? kit) {
        var table = PadOf(kit: kit);
        var buttons = MachineButtons.None;
        var leftStick = Vector2.Zero;
        var rightStick = Vector2.Zero;
        var leftTrigger = 0f;
        var rightTrigger = 0f;

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (table[ordinal] is not { } element) {
                continue;
            }

            var raw = intent[ordinal];

            switch (element) {
                case WorldPadElement.LeftStickX: leftStick.X = StickValue(raw: raw); break;
                case WorldPadElement.LeftStickY: leftStick.Y = StickValue(raw: raw); break;
                case WorldPadElement.RightStickX: rightStick.X = StickValue(raw: raw); break;
                case WorldPadElement.RightStickY: rightStick.Y = StickValue(raw: raw); break;
                case WorldPadElement.LeftTrigger: leftTrigger = TriggerValue(raw: raw); break;
                case WorldPadElement.RightTrigger: rightTrigger = TriggerValue(raw: raw); break;
                default:
                    if (raw >= WorldChannelTable.DefaultBinaryThreshold) {
                        buttons |= ButtonBit(element: element);
                    }

                    break;
            }
        }

        return new MachinePadState(
            Buttons: buttons,
            LeftStick: leftStick,
            RightStick: rightStick,
            LeftTrigger: leftTrigger,
            RightTrigger: rightTrigger
        );
    }

    /// <summary>Initializes the application fold over the population (up to bodies 0..4095) and the one grant table the
    /// sets live in.</summary>
    /// <param name="population">The entity table.</param>
    /// <param name="grants">The capability table's view (application set reads/writes plus the Control-over-target check).</param>
    /// <param name="definition">Reads the live world definition: its boot-fixed channel table once, here, and each
    /// screen's application policy (channel reach, pad kit) and each kit's pad map whenever a fold or a compose needs
    /// them, compiled again only after a live edit replaces the row.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldEngagement(WorldPopulation population, IWorldGrantsView grants, Func<WorldDefinition> definition) {
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: grants);
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_population = population;
        m_grants = grants;
        m_definition = definition;
        m_channels = WorldChannelTable.Compile(channels: definition().Channels);
        m_defaultPad = CompileDefaultPad();
    }

    /// <summary>Returns an application as the fold delivers it now: a screen member carries its screen's live policy
    /// (the reach and pad kit the document's row authors today, which <see cref="FoldTick"/> reads), not the policy it
    /// recorded at compose; any other member, or a screen the document no longer declares, is returned as held.</summary>
    /// <param name="application">A member of an application set.</param>
    /// <returns>The member with the policy the fold applies.</returns>
    public ControlApplication Live(ControlApplication application) =>
        (((application.Target.Kind == GrantSubjectKind.Screen) && (PolicyOf(screenIndex: application.Target.Value) is { } policy))
            ? (application with {
                Kit = policy.Kit,
                Reach = policy.Reach,
            })
            : application);

    // Indexes the live definition's screen and kit rows by index and name, each again only when its own section object
    // has changed: only a screen or kit mutation (or a reload) replaces those, while every other install, a state
    // publication included, carries them over, so a steady tick indexes nothing. The first row of an index or name wins,
    // as a scan would find it. A screen or kit the new section no longer declares leaves the policy cache, so live
    // edits that rename or remove rows never grow it (removing while enumerating a Dictionary's keys is allowed).
    private void IndexRows() {
        var definition = m_definition();

        if (!ReferenceEquals(
            objA: definition.ScreensRaw,
            objB: m_indexedScreens
        )) {
            m_indexedScreens = definition.ScreensRaw;
            m_screenRows.Clear();

            var screens = definition.Screens;

            for (var index = 0; (index < screens.Count); index++) {
                _ = m_screenRows.TryAdd(
                    key: screens[index].Index,
                    value: screens[index]
                );
            }
            foreach (var screenIndex in m_screenPolicies.Keys) {
                if (!m_screenRows.ContainsKey(key: screenIndex)) {
                    _ = m_screenPolicies.Remove(key: screenIndex);
                }
            }
        }
        if (!ReferenceEquals(
            objA: definition.KitsRaw,
            objB: m_indexedKits
        )) {
            m_indexedKits = definition.KitsRaw;
            m_kitRows.Clear();

            var kits = definition.Kits;

            for (var index = 0; (index < kits.Count); index++) {
                _ = m_kitRows.TryAdd(
                    key: kits[index].Name,
                    value: kits[index]
                );
            }
            foreach (var kitName in m_kitPads.Keys) {
                if (!m_kitRows.ContainsKey(key: kitName)) {
                    _ = m_kitPads.Remove(key: kitName);
                }
            }
        }
    }
    // A screen's application policy from the live definition's row for that index, compiled once per row object; null
    // when the definition declares no screen at that index. Allocates nothing once the row's entry is compiled.
    private ScreenPolicy? PolicyOf(int screenIndex) {
        IndexRows();

        if (!m_screenRows.TryGetValue(
            key: screenIndex,
            value: out var screen
        )) {
            return null;
        }
        if (
            m_screenPolicies.TryGetValue(
                key: screenIndex,
                value: out var cached
            ) &&
            ReferenceEquals(
                objA: cached.Row,
                objB: screen
            )
        ) {
            return cached.Policy;
        }

        var mapping = ((screen.Route.Input == SourceDestination.Simulation)
            ? WorldScreenMappings.Normalized(screen: screen)
            : null
        );
        var policy = new ScreenPolicy(
            Aim: ((mapping?.TryValidate(refusal: out _) ?? false)
                ? mapping
                : null
            ),
            Kit: screen.Route.Kit,
            Reach: CompileReach(
                channels: m_channels,
                names: screen.Route.Channels
            )
        );

        m_screenPolicies[screenIndex] = (screen, policy);

        return policy;
    }
    // A kit's compiled pad map from the live definition's row of that name, compiled once per row object; the engine's
    // baked default table for no kit, a kit the definition does not declare, or a kit that declares no pad.
    private WorldPadElement?[] PadOf(string? kit) {
        if (kit is not { Length: > 0 } name) {
            return m_defaultPad;
        }

        IndexRows();

        if (
            !m_kitRows.TryGetValue(
                key: name,
                value: out var row
            ) ||
            (row.PadRaw is not { Count: > 0 })
        ) {
            return m_defaultPad;
        }
        if (
            m_kitPads.TryGetValue(
                key: name,
                value: out var cached
            ) &&
            ReferenceEquals(
                objA: cached.Row,
                objB: row
            )
        ) {
            return cached.Pad;
        }

        var pad = CompilePad(
            channels: m_channels,
            pad: row.Pad
        );

        m_kitPads[name] = (row, pad);

        return pad;
    }

    /// <summary>Gets this tick's body-target contributions — <see cref="WorldServer.Step"/> drains this right after
    /// <see cref="FoldTick"/> and enqueues each as an ordinary <see cref="IntentSubmission"/> for the next tick's
    /// drain, landing in <see cref="WorldTick.StageContribution"/> under the same Drive-over-body check every
    /// co-driving seat or addon contribution passes through — possession is Drive-over-body plus this application,
    /// never a second authority path.</summary>
    public IReadOnlyList<BodyRouteContribution> BodyContributions => m_bodyContributions;
}
/// <summary>One body-target application's per-tick contribution — <see cref="WorldEngagement.FoldTick"/>'s output for
/// a possession/co-drive member, drained by <see cref="WorldServer.Step"/> right after
/// <see cref="WorldEngagement.FoldTick"/> and enqueued as an ordinary <see cref="IntentSubmission"/> for the
/// target's next tick.</summary>
/// <param name="TargetBody">The 0-based entity index the contribution targets.</param>
/// <param name="Principal">The applying principal — the contribution's acting identity (still checked for Drive over
/// <paramref name="TargetBody"/> at the ordinary intent-submission door; an application alone never grants Drive).</param>
/// <param name="Intent">The reach-masked intent to contribute.</param>
public readonly record struct BodyRouteContribution(int TargetBody, Principal Principal, PlayerIntent Intent);

/// <summary>A screen's engagement policy as its row authors it: the pad kit its applications translate through, the
/// channels they reach, and the mapping its light gun aims through.</summary>
/// <param name="Aim">The row's source-normalized mapping (<see cref="WorldScreenMappings.Normalized"/>), the one the
/// <c>$pointer:</c> rule read maps through, or <see langword="null"/> for a screen whose route input is not
/// <see cref="SourceDestination.Simulation"/> or that maps no ray.</param>
/// <param name="Kit">The route's <see cref="WorldScreenRoute.Kit"/>, or <see langword="null"/> for the engine default.</param>
/// <param name="Reach">The compiled mask of the route's <see cref="WorldScreenRoute.Channels"/>.</param>
internal readonly record struct ScreenPolicy(SourceMapping? Aim, string? Kit, ChannelReachMask Reach);
