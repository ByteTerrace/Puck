using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One diegetic screen's merged engagement pad this tick — the OR-merged controller image every player
/// currently engaged on <see cref="ScreenIndex"/> translates to (the multiplayer-cabinet shape:
/// <see cref="WorldEngagement.FoldTick"/> is the writer). Sparse: a screen with nobody engaged carries no entry at
/// all rather than an explicit neutral one, so the common (nobody-engaged) tick costs nothing to encode.
/// Server-internal: <see cref="WorldMachineHost.Advance"/> reads <see cref="WorldEngagement.BuildPadSnapshot"/>
/// directly, in-process, so this never crosses the loopback.</summary>
/// <param name="ScreenIndex">The engine screen-surface index.</param>
/// <param name="Pad">The merged controller image for this tick's machine step.</param>
public readonly record struct ScreenPadSnapshot(int ScreenIndex, MachinePadState Pad);
/// <summary>
/// The route fold — where an entity's resolved intent goes, distinct from the <see cref="IntentSource"/> axis (what
/// fills it). It is a view over the server's one capability table (<see cref="WorldGrants"/>, reached through
/// <see cref="IWorldGrantsView"/>): an entity's route is an exclusive-per-body <see cref="WorldCapability.Control"/>
/// grant over a target subject — a screen (today's machine-engagement UX) or another body (possession) — no parallel
/// route table. Screen-machine engagement is a special case of the general primitive: a channel-masked
/// tap on the one intent wire, carrying a capture policy (does the source body idle, or keep integrating?) and a
/// channel mask (which ordinals the route reaches at all). While a route captures its body, that body's per-tick
/// <see cref="WorldBody.EngagedIntent"/> is translated/passed through to the target once per tick
/// (<see cref="FoldTick"/>) instead of driving the avatar, which stands idle; while mirrored (capture:false) the body
/// keeps integrating normally and its resolved intent still reaches the target. A screen target's fold publishes on
/// <see cref="BuildPadSnapshot"/> for <see cref="WorldMachineHost.Advance"/> to read, in-process, inside
/// <see cref="WorldServer.Step"/>; a body target's fold is a co-drive contribution queued for the target's next tick,
/// through the exact same Drive-gated contribution path an addon or a co-driving seat already uses
/// (<see cref="WorldServer.StageContribution"/>) — no second write path.
/// </summary>
/// <remarks>
/// The write half (<see cref="Engage"/>/<see cref="Disengage"/>) is
/// server-side authority reached only through <see cref="WorldServer.ApplyCommand"/>'s <see cref="WorldCommand.Engage"/>/
/// <see cref="WorldCommand.Disengage"/> arms — the same ordered domain, tape, and eventual socket door every other
/// authority command travels through, so a future remote client inherits routing for free with no special-case
/// loopback shortcut. The route owner identity (<c>TargetPrincipal</c> on both command kinds) is resolved by the
/// caller (a local seat's own claimed identity via <c>Puck.World.Client.PlayerRoster.PrincipalOf</c>, or a population
/// entry's Peer identity) rather than by this class, because that resolution needs the local roster's claim state —
/// client-only bookkeeping this class has no business holding. The read half's entity→principal resolution needs no
/// such indirection: a body/entity index is a <see cref="WorldPrincipal.Index"/> directly for both
/// <see cref="PrincipalKind.Seat"/> (0..3, <see cref="WorldPopulation.LocalSeatCount"/>-bounded) and
/// <see cref="PrincipalKind.Peer"/> (4..127) principals, so <see cref="PlayersOn"/>/<see cref="FoldTick"/>/
/// <see cref="DisengageScreen"/> resolve bodies with plain index arithmetic and never consult a roster.
/// <para><b>Replay visibility.</b> A body-target route's per-tick channel passthrough is
/// synthesized directly into <see cref="WorldServer"/>'s own intent queue (never through <c>LoopbackTransport</c>'s
/// <c>IntentTap</c>), so it is structurally excluded from the replay tape's recorded intent list — exactly like a
/// mounted addon's driving. It is re-derived at replay time by re-running this same fold against the source seat's
/// own recorded submission (which is captured, ordinarily) and the recorded route state (the <see cref="Engage"/>
/// command itself, and any Grant/Revoke touching the route's Control subject, all travel as ordinary tape entries) —
/// the same property the addon precedent already established, not a new exception. The pose hash needs no format
/// change either: it already hashes every active body's position/orientation each tick, so a possessed body's motion
/// and a captured source's stillness are covered automatically, the same as any other cause of a body moving.</para>
/// <para>Single-threaded, like the grant table it views: <see cref="WorldServer.ApplyCommand"/> mutates it on the
/// command-apply window and <see cref="FoldTick"/> reads it during <see cref="WorldServer.Step"/>, both on the
/// launcher's window-pump thread, so no lock guards this state.</para>
/// </remarks>
public sealed class WorldEngagement {
    private readonly IWorldGrantsView m_grants;
    private readonly WorldPopulation m_population;

    // Per-screen compiled translation (channel ordinal -> pad element, or null when unmapped) and channel mask,
    // resolved ONCE at construction from each screen's authored WorldScreenRoute (Translation/Channels) — the
    // context-routes widening's "authored data replaces the hard-wired map" growth point. A screen absent from either
    // dictionary declares no route (Route.Passive) and is never looked up (FoldTick only visits routed bodies, whose
    // target screen index always names a declared screen).
    private readonly Dictionary<int, WorldPadElement?[]> m_screenTranslation = new();
    private readonly Dictionary<int, ChannelReachMask> m_screenChannelMask = new();
    // Reused scratch for the per-frame PlayersOn/DisengageScreen collect+prune, so the hot path allocates nothing
    // after warmup.
    private readonly List<WorldPrincipal> m_holderScratch = new();
    private readonly List<WorldPrincipal> m_staleScratch = new();
    // This tick's per-screen merged pad accumulator (FoldTick's write, BuildPadSnapshot's read) — a reused Dictionary
    // cleared and refilled every FoldTick call, since screen indices are document-authored and not a compact 0..N
    // space a fixed-size array could index directly.
    private readonly Dictionary<int, MachinePadState> m_screenPads = new();
    // The reused, capacity-only-grows backing array BuildPadSnapshot slices into — the same "grow, never shrink"
    // idiom WorldServer.BuildSnapshot's m_snapshotEntries uses, sized against however many screens are EVER
    // simultaneously engaged rather than a document-declared screen count (there is no such cap).
    private ScreenPadSnapshot[] m_padEntries = Array.Empty<ScreenPadSnapshot>();
    // This tick's body-target route contributions — cleared and refilled every FoldTick call, drained by
    // WorldServer.Step right after FoldTick returns (queued for the NEXT tick's intent drain through the ordinary
    // co-drive/StageContribution path; see the class remarks on replay visibility for why this is never taped here).
    private readonly List<BodyRouteContribution> m_bodyContributions = new();

    /// <summary>Initializes the routing fold over the population (bodies 0..127) and the one grant table the routes
    /// live in.</summary>
    /// <param name="population">The entity table.</param>
    /// <param name="grants">The capability table's view (route reads/writes plus the Control-over-target check).</param>
    /// <param name="definition">The world definition, read once for its boot-fixed channel table and each screen's
    /// authored route policy (channel mask, translation).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldEngagement(WorldPopulation population, IWorldGrantsView grants, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: grants);
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_population = population;
        m_grants = grants;

        var channels = WorldChannelTable.Compile(channels: definition.Channels);

        foreach (var screen in definition.Screens) {
            m_screenTranslation[screen.Index] = CompileTranslation(
                route: screen.Route,
                channels: channels
            );
            m_screenChannelMask[screen.Index] = CompileChannelMask(
                route: screen.Route,
                channels: channels
            );
        }
    }

    // Zeroes every ordinal the mask does not reach — the route's channel-mask application, applied once before a
    // captured/mirrored intent reaches ITS TARGET (never applied to the source body's own integration, which always
    // sees the full unmasked intent).
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
    // The route's channel mask, compiled from authored channel NAMES to a ChannelReachMask — every ordinal when the
    // screen authors none (route.Channels is null).
    private static ChannelReachMask CompileChannelMask(WorldScreenRoute route, WorldChannelTable channels) {
        if (route.Channels is not { Count: > 0 } names) {
            return ChannelReachMask.All;
        }

        var bits = 0UL;

        foreach (var name in names) {
            if (channels.TryGetOrdinal(
                name: name,
                ordinal: out var maskOrdinal
            )) {
                bits |= (1UL << maskOrdinal);
            }
        }

        return new ChannelReachMask(Bits: bits);
    }
    // The baked-default translation (the two movement roles to the left stick — structural ChannelRole ordinals, no
    // channel name involved) when the screen authors none; otherwise the authored rows alone, resolved by declared
    // channel name to ordinal (an unresolvable name — validator-refused already — is simply skipped rather than
    // throwing here, since this constructor has no error channel). The baked default names no gameplay channel: a
    // screen whose machine needs a face button (or any other element) must author that row itself.
    private static WorldPadElement?[] CompileTranslation(WorldScreenRoute route, WorldChannelTable channels) {
        var table = new WorldPadElement?[ChannelLimits.MaxChannels];

        if (route.Translation is { Count: > 0 } rows) {
            foreach (var row in rows) {
                if (channels.TryGetOrdinal(
                    name: row.Channel,
                    ordinal: out var rowOrdinal
                )) {
                    table[rowOrdinal] = row.Element;
                }
            }

            return table;
        }

        table[((int)ChannelRole.MoveStrafe)] = WorldPadElement.LeftStickX;
        table[((int)ChannelRole.MoveForward)] = WorldPadElement.LeftStickY;

        return table;
    }
    // Clear the Control route of every principal collected as stale this pass (its body no longer live). Runs over
    // the scratch copy, so it never mutates a live enumeration.
    private void PruneStale() {
        foreach (var principal in m_staleScratch) {
            _ = m_grants.ClearControlRoute(principal: principal);
        }

        m_staleScratch.Clear();
    }
    /// <summary><b>Decides latch/route consistency.</b> <see cref="WorldBody.Engaged"/> (the capture latch) and
    /// the grant table's Control route row are two independent pieces of state <see cref="Engage"/> sets together and
    /// an ordinary disengage clears together — but nothing enforces that they move together in between. An admin
    /// <c>world.revoke &lt;principal&gt; control screen:&lt;n&gt;</c> (or <c>control body:&lt;n&gt;</c>) can pull the
    /// route out from under a genuinely captured body (the route dies, the latch does not — a stuck latch with no way
    /// to release it, since a route-less body reads <see cref="DisengageOutcome.NotEngaged"/> forever otherwise); a
    /// plain <c>world.grant &lt;principal&gt; control screen:&lt;n&gt;</c> can mint a route with no latch behind it at
    /// all (equally true of capture:false's mirrored route, which never sets the latch in the first place — disengage
    /// clears it just the same, unconditionally, since <c>WorldBody.SetEngaged(false)</c> is a no-op when already
    /// clear).
    /// <para>The two directions are not symmetric. The stuck-latch direction (latch set, no route) touches only
    /// <see cref="WorldBody.Engaged"/> — pure body state, nothing in the grant table — so it self-heals
    /// unconditionally: whatever cleared the route already exercised authority over the target, and there is no
    /// grant-table mutation left here for an actor check to gate. The route-without-latch direction is different: a
    /// route can exist through a perfectly legitimate <c>world.grant</c> with no matching <see cref="Engage"/> call
    /// yet — a real, authorized row, not always debris — and clearing it mutates the same per-principal Control
    /// subject set an ordinary <c>world.revoke</c> touches. This direction requires <paramref name="actingPrincipal"/>
    /// to hold Control over that target (the identical pair an ordinary disengage/revoke already checks) before
    /// clearing it; lacking it answers <see cref="DisengageOutcome.Denied"/>.</para></summary>
    private DisengageOutcome ResolveDisengage(int entityIndex, WorldPrincipal actingPrincipal, WorldPrincipal targetPrincipal, bool apply) {
        if (Body(index: entityIndex) is not { } body) {
            return DisengageOutcome.NotEngaged;
        }

        var route = m_grants.ControlRoute(principal: targetPrincipal);

        if (
            body.Engaged &&
            (route is null)
        ) {
            if (apply) {
                body.SetEngaged(engaged: false);
            }

            return DisengageOutcome.RepairedLatch;
        }

        if (
            !body.Engaged &&
            (route is { } orphaned)
        ) {
            if (!CheckEngage(
                actingPrincipal: actingPrincipal,
                target: orphaned
            ).IsAllowed) {
                return DisengageOutcome.Denied;
            }

            // A route with no latch is ordinarily debris — unless it was deliberately established this way: an
            // Engage(capture:false) mirror route never sets the latch by design, so its routine disengage must not
            // read as an inconsistency repair. m_grants.RouteCapture reports the established route's own recorded
            // policy (true for a bare world.grant with no Engage behind it, the genuine repair case). Read before
            // clearing so the decision does not depend on ClearRoutes' own implementation.
            var wasMirrored = !m_grants.RouteCapture(principal: targetPrincipal);

            if (apply) {
                _ = m_grants.ClearControlRoute(principal: targetPrincipal);
            }

            return (wasMirrored
                ? DisengageOutcome.Disengaged
                : DisengageOutcome.RepairedRoute
            );
        }

        if (route is not { } target) {
            return DisengageOutcome.NotEngaged;
        }

        if (!CheckEngage(
            actingPrincipal: actingPrincipal,
            target: target
        ).IsAllowed) {
            return DisengageOutcome.Denied;
        }

        if (apply) {
            body.SetEngaged(engaged: false);
            _ = m_grants.ClearControlRoute(principal: targetPrincipal);
        }

        return DisengageOutcome.Disengaged;
    }
    // Canonicalize in the deterministic fixed-point domain before narrowing to the presentation-facing pad float.
    // A channel's authored shape does not constrain which pad element a route may target, and raw programmatic
    // intents can exceed either normalized domain, so the destination element owns the final bound.
    private static float StickValue(FixedQ4816 raw) => ((float)((double)FixedQ4816.Clamp(
        value: raw,
        minimum: -FixedQ4816.One,
        maximum: FixedQ4816.One
    )));
    private static float TriggerValue(FixedQ4816 raw) => ((float)((double)FixedQ4816.Clamp(
        value: raw,
        minimum: FixedQ4816.Zero,
        maximum: FixedQ4816.One
    )));

    /// <summary>Returns this tick's per-screen merged pad lane, sliced from a reused backing array — read directly by
    /// <see cref="WorldMachineHost.Advance"/> from inside <see cref="WorldServer.Step"/>, in-process. Must be read
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
    /// client's auto-insert-boot precheck ahead of <see cref="WorldCommand.Engage"/>'s submission); <see cref="Engage"/>
    /// re-runs the identical check server-side itself, so this is never the only gate a mutation passes through.</summary>
    /// <param name="target">The route target subject (a screen or a body) to check.</param>
    /// <param name="actingPrincipal">The principal asking to route/unroute.</param>
    public GrantVerdict CheckEngage(GrantSubject target, WorldPrincipal actingPrincipal) =>
        m_grants.Allows(
            capability: WorldCapability.Control,
            principal: actingPrincipal,
            subject: target
        );
    /// <summary>Disengages an entity: reads its capture latch and <paramref name="targetPrincipal"/>'s Control route
    /// together and decides among four outcomes (see the class remarks and the outcome shape's own docs) — the
    /// mutating half. Applies the same check-then-mutate decision <see cref="PeekDisengage"/> reports without
    /// mutating.</summary>
    /// <param name="entityIndex">The 0-based entity index to disengage.</param>
    /// <param name="actingPrincipal">The principal asking to disengage — checked wherever the decision needs it.</param>
    /// <param name="targetPrincipal">The identity the route is recorded under.</param>
    /// <returns>The outcome (see <see cref="DisengageOutcome"/>).</returns>
    public DisengageOutcome Disengage(int entityIndex, WorldPrincipal actingPrincipal, WorldPrincipal targetPrincipal) =>
        ResolveDisengage(
            actingPrincipal: actingPrincipal,
            apply: true,
            entityIndex: entityIndex,
            targetPrincipal: targetPrincipal
        );
    /// <summary>Disengages every entity routed to <paramref name="screenIndex"/> — the administrative teardown a
    /// screen removal runs before its slot is disposed. No actor check: whatever authorized the removal (a
    /// Mutate-over-Screens grant) already exercised the authority this cleanup is a consequence of. Each routed
    /// body's latch is cleared (its avatar resumes normal intent) and the Control route grant is cleared. Iterates a
    /// scratch copy of the holders, so it never mutates a live grant enumeration.</summary>
    /// <param name="screenIndex">The engine screen-surface index being removed.</param>
    public void DisengageScreen(int screenIndex) {
        m_grants.CollectRouteHolders(
            target: GrantSubject.Screen(index: screenIndex),
            into: m_holderScratch
        );

        foreach (var principal in m_holderScratch) {
            if (Body(index: principal.Index) is { } body) {
                body.SetEngaged(engaged: false);
            }

            _ = m_grants.ClearControlRoute(principal: principal);
        }

        m_holderScratch.Clear();
    }
    /// <summary>Routes an entity (by 0-based body index) to <paramref name="target"/>: checks
    /// <paramref name="actingPrincipal"/> holds <see cref="WorldCapability.Control"/> over <paramref name="target"/>
    /// before any mutation, then latches the body's <see cref="WorldBody.SetEngaged"/> capture policy so its intent
    /// diverts (capture:true) or also reaches the target while still driving the avatar (capture:false, the mirrored
    /// policy), and records the route under <paramref name="targetPrincipal"/> (re-pointing an already-routed entity
    /// just replaces the route) — the identity <see cref="PlayersOn"/>/<see cref="FoldTick"/> later resolve the route
    /// back to a live body through. The route's channel mask is resolved here, from document data (a screen's
    /// authored <see cref="WorldScreenRoute.Channels"/>, or every ordinal for a body target — bodies carry no route
    /// document row to author one from). Screen policy (engageable, proximity, and machine presence) remains the
    /// caller's concern; <see cref="WorldServer.ApplyCommand"/> and its context-button probe share the authoritative
    /// server-side policy check. This type checks authority and records the route.
    /// Denied when the actor lacks Control, or when the entity index holds no live body — either way nothing is
    /// mutated.</summary>
    /// <param name="entityIndex">The 0-based entity index to route.</param>
    /// <param name="target">The route target subject — a screen or a body.</param>
    /// <param name="capture">Whether the route captures the source body (idles it) or mirrors it (leaves it driving).</param>
    /// <param name="actingPrincipal">The principal asking to route — checked instead of the target's own principal so
    /// one seat cannot force another onto (or off) a target merely because the target happens to still hold the
    /// seeded permissive default.</param>
    /// <param name="targetPrincipal">The identity the route is recorded under — the entity's own resolved identity.</param>
    /// <returns>Whether the route was permitted and recorded.</returns>
    public bool Engage(int entityIndex, GrantSubject target, bool capture, WorldPrincipal actingPrincipal, WorldPrincipal targetPrincipal) {
        if (!CheckEngage(
            actingPrincipal: actingPrincipal,
            target: target
        ).IsAllowed) {
            return false;
        }

        if (Body(index: entityIndex) is not { } body) {
            return false;
        }

        var channelMask = ((target.Kind == GrantSubjectKind.Screen)
            ? (m_screenChannelMask.TryGetValue(
                key: target.Value,
                out var mask
            )
                ? mask
                : ChannelReachMask.All)
            : ChannelReachMask.All
        );

        body.SetEngaged(engaged: capture);
        m_grants.SetControlRoute(
            capture: capture,
            channelMask: channelMask,
            principal: targetPrincipal,
            target: target
        );

        return true;
    }
    /// <summary>Folds every routed body's channel-masked intent onto its target for this tick — a screen target
    /// merges into <see cref="m_screenPads"/> (the multiplayer-cabinet OR-merge, <see cref="MachinePadState.Merge"/>);
    /// a body target queues a co-drive contribution into <see cref="BodyContributions"/> for
    /// <see cref="WorldServer.Step"/> to enqueue onto the target's next tick, through the ordinary Drive-gated
    /// contribution path. Visits every routed body, captured or mirrored alike — capture only decides whether the
    /// avatar also idled; the route itself always folds. Run once per <see cref="WorldServer.Step"/>, before the
    /// tick's <see cref="Protocol.WorldSnapshot"/> is built, so <see cref="BuildPadSnapshot"/> reflects this tick's
    /// routed intents. Scans the whole population once (allocation-free after warmup, like every other per-tick
    /// population scan in <see cref="WorldServer"/>) rather than per-target, because a body/entity index resolves its
    /// own principal directly (see the class remarks) with no target-side indirection needed.</summary>
    public void FoldTick() {
        m_screenPads.Clear();
        m_bodyContributions.Clear();

        for (var index = 0; (index < m_population.Capacity); index++) {
            if (m_population.EntryBody(index: index) is not { } body) {
                continue;
            }

            var principal = ((index < WorldPopulation.LocalSeatCount)
                ? WorldPrincipal.Seat(slot: index)
                : m_population.PeerPrincipal(index: index)
            );

            // A stuck latch (see ResolveDisengage's remarks) still folds nothing onto a target nobody actually
            // granted it — self-heals the next player.disengage.
            if (m_grants.ControlRoute(principal: principal) is not { } route) {
                continue;
            }

            var masked = ApplyMask(
                intent: body.EngagedIntent,
                mask: m_grants.RouteChannelMask(principal: principal)
            );

            if (route.Kind == GrantSubjectKind.Screen) {
                var pad = Translate(
                    intent: masked,
                    screenIndex: route.Value
                );

                m_screenPads[route.Value] = (m_screenPads.TryGetValue(
                    key: route.Value,
                    value: out var existing
                )
                    ? MachinePadState.Merge(
                        first: in existing,
                        second: in pad
                    )
                    : pad
                );
            } else if (route.Kind == GrantSubjectKind.Body) {
                m_bodyContributions.Add(item: new BodyRouteContribution(
                    TargetBody: route.Value,
                    Principal: principal,
                    Intent: masked
                ));
            }
        }
    }
    /// <summary>Returns the read-only twin of <see cref="Disengage"/>: computes the identical outcome without mutating
    /// anything. The client submits <see cref="WorldCommand.Disengage"/> for the actual (server-authoritative)
    /// mutation regardless of what this reports — the command's own apply re-derives the same decision from the same
    /// state, since nothing can intervene between a local peek and its immediately-following submit over loopback —
    /// but the client needs this ahead of time to format its console echo and decide whether to drop its own
    /// client-side held device state (only <see cref="DisengageOutcome.RepairedLatch"/>/<see cref="DisengageOutcome.Disengaged"/>
    /// warrant that; <see cref="DisengageOutcome.RepairedRoute"/> must not, since the entity was never truly captured).</summary>
    /// <param name="entityIndex">The 0-based entity index.</param>
    /// <param name="actingPrincipal">The principal asking to disengage.</param>
    /// <param name="targetPrincipal">The identity the route is recorded under.</param>
    /// <returns>The outcome disengaging would produce.</returns>
    public DisengageOutcome PeekDisengage(int entityIndex, WorldPrincipal actingPrincipal, WorldPrincipal targetPrincipal) =>
        ResolveDisengage(
            actingPrincipal: actingPrincipal,
            apply: false,
            entityIndex: entityIndex,
            targetPrincipal: targetPrincipal
        );
    /// <summary>Returns every entity currently routed to <paramref name="screenIndex"/>, reported as 1-based display numbers
    /// (1..128, matching the <c>player.*</c> verb convention — a Seat/Peer principal's <see cref="WorldPrincipal.Index"/>
    /// plus one) alongside whether the route captures it (its avatar idle, the classic engage) or mirrors it (its
    /// avatar still driving, capture:false), in ascending display order. Prunes any route that no longer resolves to a
    /// live body.</summary>
    /// <param name="screenIndex">The engine screen index.</param>
    /// <returns>The routed entities' display indices and capture policy.</returns>
    public IReadOnlyList<(int Display, bool Capture)> PlayersOn(int screenIndex) {
        m_grants.CollectRouteHolders(
            target: GrantSubject.Screen(index: screenIndex),
            into: m_holderScratch
        );

        var players = new List<(int Display, bool Capture)>(capacity: m_holderScratch.Count);

        m_staleScratch.Clear();

        foreach (var principal in m_holderScratch) {
            if (Body(index: principal.Index) is { } body) {
                players.Add(item: ((principal.Index + 1), body.Engaged));
            } else {
                m_staleScratch.Add(item: principal);
            }
        }

        PruneStale();
        players.Sort(comparison: static (a, b) => a.Display.CompareTo(value: b.Display));

        return players;
    }
    /// <summary>Translates a resolved <see cref="PlayerIntent"/> to a neutral standard-controller image through
    /// <paramref name="screenIndex"/>'s compiled translation table (<see cref="CompileTranslation"/>) — authored data
    /// (<see cref="WorldScreenRoute.Translation"/>) when the screen declares one, otherwise the engine's baked default
    /// (the two movement roles to the left stick only; a route whose machine needs a face button or any other
    /// element must author that row itself). Analog channels are canonicalized for their destination element:
    /// stick axes to -1..1 and triggers to 0..1. Digital elements compare against
    /// <see cref="WorldChannelTable.DefaultBinaryThreshold"/>.</summary>
    /// <param name="intent">The resolved (and route-mask-applied) intent to translate.</param>
    /// <param name="screenIndex">The target screen's engine index — selects which compiled table to read.</param>
    /// <returns>The controller image the intent presses.</returns>
    public MachinePadState Translate(PlayerIntent intent, int screenIndex) {
        if (!m_screenTranslation.TryGetValue(
            key: screenIndex,
            value: out var table
        )) {
            return MachinePadState.Neutral;
        }

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
                    // A digital element compares the RAW fixed-point value against the threshold, never a float
                    // round-trip — the same discipline the old hard-wired South check applied.
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

    /// <summary>Gets this tick's body-target route contributions — <see cref="WorldServer.Step"/> drains this right after
    /// <see cref="FoldTick"/> and enqueues each as an ordinary <see cref="IntentSubmission"/> for the next tick's
    /// drain, landing in <see cref="WorldServer.StageContribution"/> under the same Drive-over-body check every
    /// co-driving seat or addon contribution passes through — possession is Drive-over-body plus this route, never a
    /// second authority path.</summary>
    public IReadOnlyList<BodyRouteContribution> BodyContributions => m_bodyContributions;
}
/// <summary>One body-target route's per-tick contribution — <see cref="WorldEngagement.FoldTick"/>'s output for a
/// possession/co-drive route, drained by <see cref="WorldServer.Step"/> right after <see cref="WorldEngagement.FoldTick"/>
/// and enqueued as an ordinary <see cref="IntentSubmission"/> for the target's next tick.</summary>
/// <param name="TargetBody">The 0-based entity index the contribution targets.</param>
/// <param name="Principal">The routed principal — the contribution's acting identity (still checked for Drive over
/// <paramref name="TargetBody"/> at the ordinary intent-submission door; a route alone never grants Drive).</param>
/// <param name="Intent">The channel-masked intent to contribute.</param>
public readonly record struct BodyRouteContribution(int TargetBody, WorldPrincipal Principal, PlayerIntent Intent);
