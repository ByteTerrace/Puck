using System.Collections.Concurrent;
using Puck.Hosting;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// A host's running world instances, keyed by console-chosen name — the <i>host</i> of docs/vision.md's
/// "The words": the machine or process running instances. A boot-shaped host's own boot row (<see cref="Boot"/>) sits
/// beside every instance admitted later; a boot-free host (a silo) admits every row the identical way. Owns starting,
/// stepping, reading back and retiring them, and the transfer engine — adjacency/portal sweeps, transfer
/// minting/draining, escrow, forwarding, remote lanes — that lets several authorities share one process.
/// </summary>
/// <remarks><para><b>Boot-free.</b> Construction admits no row. <see cref="AdmitBoot"/> admits the one row every
/// boot-only member below (<see cref="ShouldStepBoot"/>, <see cref="PrepareBootSeatIntents"/>,
/// <see cref="ScanBootBoundaryTriggers"/>, <see cref="SubmitExternallyClockedSeatIntents"/>,
/// <see cref="FinishSeatIntents"/>) reads through <see cref="Boot"/> — a no-op on every one of them while
/// <see cref="Boot"/> is <see langword="null"/>. <see cref="Admit"/> is the general door every other row (a desktop's
/// own <see cref="TryStart"/> spawn arm, a hosted silo's activation mailbox) enters through — <see cref="AdmitBoot"/>
/// calls it too, then does the boot-only extra wiring (local-seat route seeding, the roster leave door).</para>
/// <para><b>Seats and embodiment.</b> Every row has its own local-seat table — the <c>player.*</c> verbs'
/// <c>instance:&lt;name&gt;</c> token enters, drives (warp/face/run/stop), and leaves a seat inside a named instance,
/// applying through that instance's own <see cref="Server.WorldServer.ApplySession"/>/<see cref="Server.WorldServer.ApplyCommand"/>
/// doors — the identical path the boot row's <c>player.*</c> verbs use, never a bypass. Seating carries the seated
/// identity's declared durable state in through the same cross-document durable channel
/// (<see cref="Server.WorldOwnedWorlds.TryReadDurableState"/>) the boot row's own session-join already stages with —
/// a snapshot taken once at entry; the instance then advances its own copy. <see cref="ReapIfEmpty"/> is the
/// lifetime rule over that occupancy: a caller that just vacated an instance's last active entry reaps it through
/// the same door <see cref="TryStop"/> already exposes by name. A live TCP peer entering a spawned instance
/// (composing the existing peer-admission door with this same seating seam) remains an unbuilt stretch — see
/// <c>WorldInstanceCommandModule</c>'s own remarks.</para>
/// <para>The 24 lines this engine used to call directly into a desktop's client/roster/seat-router/input-router now
/// go through <see cref="IWorldEmbodiedSeats"/> — the desktop implements it over those types
/// (<c>Puck.World.Client.WorldClientSeats</c>); a host with no local seats passes
/// <see cref="WorldEmbodiedSeats.None"/>, and every seat-facing member below is inert against it.</para>
/// <para><b>Per-instance scheduling (docs/vision.md).</b> Each instance advances on its own
/// authored <c>simulation.rateHz</c>, never a shared build-wide rate: <see cref="StepInstances"/> holds a
/// per-instance accumulator (<see cref="WorldInstance.ScheduleAccumulatorTicks"/>) of engine ticks banked against
/// the host's master timeline — a desktop boot row's own rate-derived cadence the fixed-step pump already drives
/// (<c>Puck.Launcher.LauncherHostLoop</c>), never a second clock this type invents. An instance steps once each
/// time its own accumulator crosses its own step width (<c>50400 / rateHz</c> engine ticks); a rate faster than the
/// master cadence steps more than once per master tick, a rate slower steps less than once. A live pause
/// (<see cref="WorldInstance.IsPaused"/>, driven by the <c>world.rate pause</c>/<c>resume</c> console verb) holds the
/// accumulator exactly where it is — nothing is banked toward a step that will not happen — so resuming continues
/// on the identical schedule with no skew. An authored rate of 0 is the durable stop (never divided by; the instance
/// stays resident and readable, simply never steps) and is entirely independent of the live pause lever. Neither a
/// stopped nor a paused instance is left inert, though: <see cref="Server.WorldServer.DrainAdministrative"/> still
/// applies its buffered document mutations/rebuilds/undo ops every master tick — otherwise a
/// document mutation that would rate a stopped world back up could never itself apply, a permanent self-lock.
/// A desktop's boot row is governed by the identical rule, special-cased only where <see cref="WorldServerStepShell"/>'s
/// own tape/wait-gate/socket bookkeeping requires (see <see cref="ShouldStepBoot"/>): the master pump's own cadence
/// is already derived from boot's own rate, so boot's own crossing is trivial — a pause/rate-0 gate, never a second
/// accumulator.</para>
/// <para><b>The name is a path segment.</b> An instance's owned worlds live in a directory named by its console name,
/// so admitting a name is admitting a filesystem location: <see cref="TryStart"/> refuses any name that is not one
/// safe segment, and independently refuses any name whose resolved store does not sit under the instances root. The
/// second rule is not redundant with the first — it is what makes the placement true whatever the platform's path
/// grammar turns out to do with a name.</para>
/// <para>Stepping folds into the same <c>IFixedStepSimulation.Step</c> call both boot shapes already drive — never a
/// second pump, a second host loop, or a second <c>IFixedStepSimulation</c> registration. Single-threaded throughout:
/// the fixed-step thread is the only caller, and the verbs that mutate the registry route <c>Simulation</c> so they
/// apply on it at a tick boundary.</para></remarks>
public sealed partial class WorldInstanceHost : IDisposable, IWorldTransferForwarder {
    // How many times one crossing may be re-queued after a retryable (capacity) refusal before the refusal is
    // treated as terminal. A work ceiling, not an authored feel parameter: an unbounded retry is a per-tick
    // reservation attempt against a destination that has already said no.
    private const int TransferRetryCeiling = 8;

    /// <summary>The reserved name of a desktop host's boot row (<c>--world</c>, or the shipped default). A start
    /// request naming it is refused rather than shadowing it.</summary>
    public const string BootInstanceName = WorldDefinitionLoader.BootInstanceName;

    // Path containment is decided the way the platform decides it: case-insensitively where file names are.
    private static readonly StringComparison PathComparison = (OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal
    );

    // Whether TryStart may mint a brand-new, file-backed row. True for a desktop (its console/resolver spawn arms are
    // the only mint doors); false for a hosted silo, where a row exists only through the grain activation door — a
    // `destinations` row can never mint a doorless, keyless row Orleans never placed.
    private readonly bool m_admitsSpawn;
    private readonly CancellationToken m_applicationStopping;
    // Every instance shares the host's own persisted id — it identifies the MACHINE/PROCESS, not a world, so minting
    // a fresh one per instance would both misreport the host and put a Guid.NewGuid() on an admission path.
    private readonly Guid m_machineId;
    // The transport-neutral local resolver ResolveAndEnqueueCoalescedTransfers consumes to turn a
    // destinations row plus a traveling cohort into a scoped generation/instance name — see
    // WorldSessionResolver. TryStop notifies it so a reaped/stopped instance's cache entry does not
    // outlive the instance.
    private readonly WorldSessionResolver m_resolver;
    // The local seats this host embodies — the ONE seam into a desktop's client/roster/seat-router/input-router.
    // WorldEmbodiedSeats.None for a host with no local seats.
    private readonly IWorldEmbodiedSeats m_seats;
    private readonly string m_stateRoot;

    private readonly Dictionary<string, WorldInstance> m_instances = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, WorldRemoteAuthority> m_remoteAuthorities = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, WorldAuthorityEndpoint> m_authorityEndpoints = new(comparer: StringComparer.Ordinal);
    // Socket ingress reads onward routes while the tick thread publishes a just-committed handoff. The table itself
    // must therefore be concurrent even though every mutation still comes from the host's ordinary commit path.
    private readonly ConcurrentDictionary<(WorldServer Server, WorldEntityAddress Incarnation), ForwardedBody> m_forwardedBodies = [];
    private readonly Queue<PendingTransfer> m_pendingTransfers = new();
    private readonly List<InDoubtTransfer> m_inDoubtTransfers = [];
    // The transfer id a seat's crossing hold has already been announced under, so the held-crossing line prints
    // once per traversal rather than once per suppressed scan.
    private readonly Dictionary<(string Instance, int Seat), ulong> m_announcedCrossingHolds = [];
    // The escrow border a seat's arrival occupancy was already seeded for, so one arrival seeds once.
    private readonly Dictionary<(string Instance, int Seat), string> m_seededArrivals = [];
    // Every transfer id this host has drained (committed or aborted). A pure function of enqueue/drain
    // order, never wall-clock or RNG — checked first in ApplyTransfer so a retry-shaped duplicate (the same
    // id resubmitted, e.g. world.transfer's transfer:<id> token) refuses by name rather than double-landing.
    // A diegetic portal crossing always mints a fresh id, so only an explicitly supplied id can collide.
    private readonly HashSet<(string SourceInstance, ulong TransferId)> m_appliedTransferIds = new();
    private readonly Dictionary<string, ulong> m_appliedTransferHighWater = new(comparer: StringComparer.Ordinal);
    // Per-site fresh-instance draw counters. The seed ladder's instance rung is the running instance's own
    // name, so a fresh transfer mints a name no earlier draw at that site used. Advances by exactly one per
    // MintFreshInstanceName call, a pure function of call sequence — never wall-clock, RNG, or tick-of-entry.
    // Deterministic within one process run (DrainPendingTransfers processes the pending FIFO in the same
    // order every run), but not covered by replay.verify — the replay tape captures only the boot
    // instance's own command/intent stream.
    private readonly Dictionary<string, int> m_freshCounters = new(comparer: StringComparer.Ordinal);
    // Names a persistent-lifetime transfer has resolved at least once — retained through an occupancy dip to zero
    // (ReapIfEmpty refuses them by name) so a second traveler's transfer still finds the first traveler's instance
    // standing. Marked in TryResolveDestination whether that call started the instance or found it already running;
    // cleared by an explicit TryStop, which always wins over retention.
    private readonly HashSet<string> m_retainedInstances = new(comparer: StringComparer.Ordinal);
    // Test-only substitution point (§2.4's "the ONE seam a test may decorate"): a row named here has its LOCAL peer
    // call wrapped by the given decorator instead of calling straight through to its own server — see
    // SetPeerCallFault. Empty in every production path; nothing here reads from it unless a caller sets an entry.
    private readonly Dictionary<string, IWorldPeerCall> m_peerCallFaults = new(comparer: StringComparer.Ordinal);

    // The onward authority is transport-neutral: a colocated destination forwards through the same interface a
    // socket one does, so a traveler that leaves over an in-process adjacency keeps the control its client already
    // has. BodyIndex is the destination-local slot a forwarded payload is rebound to.
    private readonly record struct ForwardedBody(IWorldForwardedAuthority Authority, int BodyIndex);

    // Mirrors WorldDefinitionLoader.TryResolve's explicit-path handling, plus a shipped-asset fallback so a
    // console verb can name "Assets/worlds/jump.world.json" regardless of the process's current directory
    // (the boot path needs the fallback only for its own default document; a named instance is always
    // explicit, so it needs both). A third probe under the shipped worlds directory itself is what lets a
    // portal facet's destination resolve a `references` row authored as a bare shipped-world filename
    // ("dive.world.json", exactly how nexus.world.json's own references section spells it). A rooted or
    // already-relative-enough path resolves at the first two probes; this one only fires for a bare
    // filename neither of those found.
    //
    // WorldSessionResolver's own cache-key document identity: the resolver stays I/O-free by construction,
    // so the host canonicalizes once, here, and threads the same canonical string into every resolver call
    // (TryResolve, TryGetActive, TryAdopt, DescribeActive) — never the raw WorldReference.Document string a
    // destination row spells, since two documents naming the identical underlying file through different
    // spellings ("dive.world.json" vs "Assets/worlds/dive.world.json") would otherwise mint two separate
    // resolver cache entries even though TryResolveDocumentPath's own probes already prove them identical.
    // A path this probe cannot resolve to an existing file falls back to the raw string unchanged — the
    // resolver still needs some stable identity for its cache key, and an unresolvable document is about to
    // fail this transfer outright at TryStart regardless.
    public static string CanonicalDocumentIdentity(string documentPath) =>
        (TryResolveDocumentPath(
            path: documentPath,
            resolved: out var resolved
        )
            ? resolved
            : documentPath
        );
    // A references row's locator is relative to the document that AUTHORS it, never to the process's output
    // directory. This is the one host-side fold shared by observed previews and traveler entry; both must name the
    // same physical origin or the resolver can assign two generations to one seamless circuit.
    public static string ResolveReferenceDocument(WorldInstance source, string documentPath) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (!Path.IsPathRooted(path: documentPath)) {
            try {
                if (Path.GetDirectoryName(path: source.SourcePath) is { Length: > 0 } sourceDirectory) {
                    var besideSource = Path.GetFullPath(path: Path.Combine(
                        path1: sourceDirectory,
                        path2: documentPath
                    ));

                    if (File.Exists(path: besideSource)) {
                        return besideSource;
                    }
                }
            } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
                // The ordinary resolver below owns the eventual by-name refusal for an unformable locator.
            }
        }

        return CanonicalDocumentIdentity(documentPath: documentPath);
    }

    // The source's active local seats (0..LocalSeatCount-1), in slot order — a non-resolver `party` transfer's
    // member set, read live (a resolver-driven transfer's own member set is its FROZEN cohort instead — see
    // ApplyTransfer's own remarks).
    private static int[] ActiveLocalSeats(WorldServer server) {
        return server.ExecuteAuthorityOperation(operation: () => ActiveLocalSeatsCore(server: server));
    }
    private static int[] ActiveLocalSeatsCore(WorldServer server) {
        var members = new List<int>(capacity: server.Population.LocalSeatCount);

        for (var slot = 0; (slot < server.Population.LocalSeatCount); slot++) {
            if (server.Population.IsActive(index: slot)) {
                members.Add(item: slot);
            }
        }

        return [.. members];
    }
    // The one leave-standing predicate both the party pre-check and the detach itself ask. A World principal is
    // admitted structurally over a body it authors — it holds no grant row by construction — and refused by name
    // over any body a seat or an admitted peer drives.
    private static bool AllowsLeave(WorldServer server, WorldPrincipal principal, int slot, out string denial) {
        if (principal.Kind == PrincipalKind.World) {
            denial = "that body is driven by a seat or an admitted peer, and the world's own program never travels on their behalf";

            return IsWorldAuthoredBody(
                server: server,
                slot: slot
            );
        }

        var verdict = server.Grants.Allows(
            principal: principal,
            capability: WorldCapability.Drive,
            subject: GrantSubject.Body(index: slot)
        );

        denial = (verdict.IsAllowed
            ? string.Empty
            : verdict.DescribeDenial()
        );

        return verdict.IsAllowed;
    }
    // The directory every non-boot instance's store hangs under, separator-terminated so a prefix test is a containment
    // test rather than a sibling-name test ("…/instances-other" must not read as inside "…/instances").
    private string InstancesRoot() => (Path.GetFullPath(path: Path.Combine(
        path1: m_stateRoot,
        path2: "instances"
    )).TrimEnd(trimChar: Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
    // The ONE local-roster departure transaction. PlayerRoster routes both explicit leaves and device-orphan
    // dissolves here after its ordinary slot/occupancy guard. The authoritative body leaves the CURRENT routed
    // instance first; only an accepted reply clears held input, vacates the local participant, and resets the
    // presentation route. Reaping runs last, after the route no longer makes TryStop's traveler guard fire.
    private bool LeaveRosterSeat(int rosterSlot, WorldPrincipal actingPrincipal) {
        // Only ever wired through m_seats.ConfigureLeave, and only AdmitBoot ever calls that — a boot-free host
        // installs no leave callback, so this can only fire once a boot row is admitted.
        var boot = Boot!;
        var locationEndpoint = m_seats.RoutedEndpoint(slot: rosterSlot)!;
        var locationEntity = m_seats.RoutedEntity(slot: rosterSlot);

        var accepted = false;

        locationEndpoint.Submissions.SubmitSession(
            request: new SessionRequest.Leave(
                Principal: actingPrincipal,
                Slot: locationEntity.Index
            ),
            completion: reply => {
                if (!reply.Accepted) {
                    Console.Error.WriteLine(value: $"[player.leave denied: '{locationEndpoint.Identity}' seat {(locationEntity.Index + 1)} — {reply.Reason}]");

                    return;
                }

                accepted = true;
            }
        );

        if (!accepted) {
            return false;
        }

        m_seats.ClearHeld(slot: rosterSlot);
        _ = m_seats.VacateSeat(slot: rosterSlot);
        m_seats.PublishRoute(
            slot: rosterSlot,
            endpoint: EndpointFor(instance: boot),
            entity: new WorldEntityAddress(
                Authority: boot.Server.AuthorityIdentity,
                Index: rosterSlot,
                Generation: boot.Server.Population.Generation(index: rosterSlot)
            )
        );
        if (
            locationEndpoint.Identity.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "$traveler/"
        ) &&
            m_remoteAuthorities.Remove(
            key: locationEndpoint.Identity,
            value: out var travelerRoute
        )
        ) {
            travelerRoute.Dispose();
            if (m_authorityEndpoints.Remove(
                key: locationEndpoint.Identity,
                value: out var travelerEndpoint
            )) {
                travelerEndpoint.Dispose();
            }
        }

        if (!string.Equals(
            a: locationEndpoint.Identity,
            b: BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            _ = ReapIfEmpty(name: locationEndpoint.Identity);
        }

        return true;
    }

    /// <summary>Disposes every instance this host owns. The boot instance's own graph belongs to the container and
    /// is untouched.</summary>
    public void Dispose() {
        foreach (var endpoint in m_authorityEndpoints.Values) {
            endpoint.Dispose();
        }

        foreach (var instance in m_instances.Values) {
            instance.Dispose();
        }

        foreach (var authority in m_remoteAuthorities.Values) {
            authority.Dispose();
        }

        m_instances.Clear();
        m_remoteAuthorities.Clear();
        m_authorityEndpoints.Clear();
    }
    /// <summary>Closes the shared boot-input lifecycle after every host call. Raw analog samples are tick-local;
    /// carried device state is re-dispatched by the input router on the next tick. A no-op while <see cref="Boot"/>
    /// is <see langword="null"/>.</summary>
    public void FinishSeatIntents() {
        if (Boot is null) {
            return;
        }

        m_seats.ClearAnalog();
    }
    /// <summary>The directory an instance's owned worlds live under — derived from its name so two instances never
    /// share a store, and reported by <c>world.instance.status</c> so the placement is read back rather than
    /// inferred. Normalized, so the answer is where files actually land rather than the spelling that got there;
    /// <see cref="TryStart"/> refuses any name whose answer escapes the instances root.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>The absolute owned-worlds directory for that instance.</returns>
    public string OwnedWorldsDirectory(string name) =>
        Path.GetFullPath(path: (string.Equals(
            a: name,
            b: BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )
            ? Path.Combine(
                path1: m_stateRoot,
                path2: "owned-worlds"
            )
            : Path.Combine(
                path1: InstancesRoot(),
                path2: name,
                path3: "owned-worlds"
            )));
    /// <summary>Consumes the seats' tick-local look samples, then submits the seats currently claimed by the boot
    /// authority when that authority will consume a tick. This is the one boot-input lifecycle used by both the
    /// presented and headless executable shapes: camera-relative movement is simulation input, so a renderer may
    /// observe the view state but must not own its advancement. A no-op while <see cref="Boot"/> is
    /// <see langword="null"/>.</summary>
    public void PrepareBootSeatIntents(bool stepsBoot, ulong tick, ulong stepTicks) {
        if (Boot is not { } boot) {
            return;
        }

        m_seats.AdvanceSeatViews(deltaSeconds: ((float)EngineTicks.ToSeconds(ticks: stepTicks)));

        if (!stepsBoot) {
            return;
        }

        m_seats.SubmitAuthorityIntents(
            endpoint: EndpointFor(instance: boot),
            tick: tick
        );
    }
    /// <summary>Reaps a running instance whose seat occupancy just hit zero — a lifetime rule over the occupancy fact
    /// <see cref="Server.WorldPopulation.ActiveCount"/> already reports (see that member's own remarks on why it is
    /// already per-instance scoped, which is what makes reading it here honest), never bespoke teardown: any caller
    /// that just vacated an instance's last occupied slot calls this, and it is the same <see cref="TryStop"/> path
    /// <c>world.instance.stop</c> uses, applied by rule instead of by name. A no-op — never a refusal — for the boot
    /// instance (which <see cref="TryStop"/> refuses outright), a retained instance (see <see cref="m_retainedInstances"/>
    /// — a <c>persistent</c>-lifetime transfer destination stays up through an occupancy dip to zero, by design), an
    /// unknown name, or an instance that still holds an active entry.</summary>
    /// <param name="name">The instance name to reap if now empty.</param>
    /// <returns><see langword="true"/> when the instance was reaped.</returns>
    public bool ReapIfEmpty(string name) {
        if (string.Equals(
            a: name,
            b: BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            return false;
        }

        if (m_retainedInstances.Contains(item: name)) {
            return false;
        }

        if (
            !m_instances.TryGetValue(
            key: name,
            value: out var instance
        ) ||
            (instance.Server.Population.ActiveCount() > 0)
        ) {
            return false;
        }

        return TryStop(
            name: name,
            reason: out _
        );
    }
    /// <inheritdoc/>
    public void ResolveContinuations(WorldServer source) {
        foreach (var instance in m_instances.Values) {
            if (ReferenceEquals(
                objA: instance.Server,
                objB: source
            )) {
                ScanInstanceAdjacencies(
                    instance: instance,
                    resolveOnly: true
                );
                return;
            }
        }
    }
    /// <summary>Resolves the world definition local seat <paramref name="slot"/> currently presents from, per its
    /// live <c>WorldSeatAuthorityRouter</c> route — the one structure source every drag-time/read-back
    /// consumer reads through, so seats never derive "which document currently frames me" two
    /// different ways. <c>WorldSeatViewInput</c> (the live drag clamp) and <c>WorldViewCommandModule</c>'s
    /// <c>world.view.camera</c> echo are today's two callers.</summary>
    /// <param name="slot">The 0-based local roster slot.</param>
    /// <returns>The routed instance's own definition, or the boot row's own definition for a boot-routed seat, an
    /// out-of-range slot, or a route naming an instance that has since stopped — the same defensive fallback
    /// <see cref="LeaveRosterSeat"/> already applies to a stale route.</returns>
    public WorldDefinition ResolveRoutedDefinition(int slot) =>
        (m_seats.RoutedEndpoint(slot: slot)?.Definition ?? Boot!.Server.Definition);
    /// <summary>Scans the boot instance's document for portal faces (a <see cref="WorldPlacementFace"/> carrying a
    /// <see cref="WorldPlacementPortal"/> facet) against its own active local seats, and enqueues a transfer for each
    /// edge — a seat whose body was outside the face's enterable volume last scan and is inside it now (see
    /// <see cref="WorldInstance.PortalOccupancy"/>). Called from <c>WorldSimulation</c>/<c>HeadlessWorldSimulation</c>
    /// immediately after boot's own <c>Server.Step</c> this master call (only when boot actually stepped — a caller
    /// that skipped the step because boot is paused/rate-0 must skip this call too, exactly like every other
    /// non-stepped instance is simply never scanned inside <see cref="StepInstances"/>), so this reads
    /// boot's own just-settled post-step state — a pure function of that settled, replay-covered sim state — no
    /// wall-clock, RNG, or float ever reaches a decision (every comparison below runs in fixed point; the
    /// placement's authored float Position/YawDegrees are quantized to fixed point exactly once per portal per scan,
    /// the same boundary <c>Server.WorldEventFeed.CollectRegions</c> already crosses for region sensing). Every
    /// other instance's own portals are scanned per-step inside <see cref="StepInstances"/> instead — see
    /// that method's own remarks on why a single once-per-master-call scan
    /// under- and over-scans a faster or paused/stopped instance.</summary>
    public void ScanBootBoundaryTriggers() {
        if (m_instances.TryGetValue(
            key: BootInstanceName,
            value: out var boot
        )) {
            ScanInstanceBoundaries(instance: boot);
        }
    }
    /// <summary>Whether the boot instance is due to actually step this master tick — <see langword="false"/> when
    /// its own live <see cref="WorldInstance.IsPaused"/> lever holds it, its authored rate is the durable stop (0),
    /// or <paramref name="stepTicks"/> no longer matches the width its current rate demands;
    /// otherwise always <see langword="true"/>, because the fixed-step pump's own cadence is already derived from
    /// the boot world's own authored rate (falling back to <c>Puck.Launcher.LauncherHostLoop.DefaultUpdateRate</c>
    /// only while this returns <see langword="false"/>, so the window keeps rendering and the console keeps
    /// answering), so boot's own crossing is trivial by the time this is asked — the same pause/rate-0 rule as every
    /// other instance, expressed without a redundant accumulator field. The caller (<c>WorldSimulation</c>/
    /// <c>HeadlessWorldSimulation</c>) is expected to call <see cref="Server.WorldServer.DrainAdministrative"/> on
    /// the boot server when this returns <see langword="false"/>, mirroring every other stopped/paused instance's
    /// own administrative drain.</summary>
    /// <remarks><b>The width-match gate.</b> <c>Puck.Launcher.FixedStepPump.Advance</c>
    /// caches its <c>stepTicks</c> for its whole catch-up batch (computed once from <c>RatePerSecond</c> before the
    /// batch's first call), and that cached value is what every call in the batch hands this instance's shell as
    /// <c>FixedStepContext.StepTicks</c> — never re-read mid-batch. A <c>world.load</c>/<c>.reset</c>/<c>.reload</c>
    /// rebuild applies inside <see cref="Server.WorldServer.Step"/>, ahead of that same call's own population
    /// advance, so the call that carries the rebuild still steps at the batch's stale width — its own last honest
    /// step under the old world, the transition tick, which this method does not try to further correct (the swap
    /// already landed by the time this is next asked). What this gate stops is the next call: without it, a
    /// still-nonzero post-swap rate would read as "due" under the pause/rate-0 rule alone, and a batch call sharing
    /// the same stale <c>stepTicks</c> would advance the newly-swapped-in world at the old width (240→120 integrating
    /// the 120 Hz world once at 240). Refusing whenever the width no longer matches the current rate — exactly like a
    /// paused/stopped instance, administrative-drain only, Tick/ElapsedTicks frozen — means the next call whose
    /// <c>stepTicks</c> the pump recomputed fresh (its own next outer-loop iteration) is the one that actually steps,
    /// with tick numbering staying contiguous for free (the freeze mechanism already used for pause/stop). A swap to
    /// rate 0 needs no separate handling here: the ordinary rate&gt;0 check already refuses every call after the one
    /// that applied the swap, so "the world that just swapped does not step again" already holds — calling
    /// <see cref="EngineTicks.PerRate"/> for a rate of 0 would be the durable-stop's own forbidden division, which is
    /// exactly why the width check is short-circuited to run only once the ordinary rate check already passed.</remarks>
    /// <param name="stepTicks">This call's own pump-supplied step width (<see cref="FixedStepContext.StepTicks"/>) —
    /// compared against what the boot instance's current rate demands.</param>
    /// <returns><see langword="true"/> when boot should step this call.</returns>
    public bool ShouldStepBoot(ulong stepTicks) {
        if (Boot is not { } boot) {
            return false;
        }

        var rateHz = boot.Server.Definition.SimulationRateHz;

        return (
            !boot.IsPaused &&
            (rateHz > 0) &&
            (stepTicks == EngineTicks.PerRate(ratePerSecond: ((uint)rateHz)))
        );
    }
    /// <summary>Advances every row except a desktop's own boot row on its own authored schedule — the boot row (when
    /// one is admitted) is handled by <see cref="WorldServerStepShell"/> separately by the caller (see
    /// <see cref="ShouldStepBoot"/>), which also carries the tape/wait-gate/socket bookkeeping only it needs. Every
    /// other row steps through the identical <see cref="WorldServerStepShell.Step"/> shell over its OWN
    /// <see cref="WorldInstance.Door"/>/<see cref="WorldInstance.Tape"/>/<see cref="WorldInstance.PublishTick"/> —
    /// null/idle/no-op on a desktop's non-boot rows today, today's behavior to the byte, and the same seam a hosted
    /// row's real door/tape/tick-clock plugs into. Each row banks <paramref name="masterDeltaTicks"/> into its own
    /// <see cref="WorldInstance.ScheduleAccumulatorTicks"/> and steps once per whole crossing of its own step width
    /// (<c>50400 / rateHz</c> engine ticks) — a rate faster than the master cadence steps more than once per call, a
    /// rate slower steps less than once. A row whose authored rate is the durable stop (0), or whose live
    /// <see cref="WorldInstance.IsPaused"/> lever holds it, banks nothing (the accumulator holds exactly where it is,
    /// so a later resume continues on the identical schedule with no skew) but still receives an administrative
    /// drain (<see cref="Server.WorldServer.DrainAdministrative"/>) so a buffered document mutation can still apply —
    /// never a permanent self-lock.</summary>
    /// <remarks><b>Portal scan cadence, per step.</b> Every actual step below is followed immediately by
    /// <see cref="ScanInstancePortals"/> for that same row, reading its own just-settled post-step state —
    /// never a single scan of all rows once per master call: scanning per step keeps the trigger's own
    /// slab-depth argument intact (per-scan displacement equals per-step displacement, never per-master-call
    /// displacement) for a row that steps several times per call, and
    /// means a non-stepping row is never scanned — a pre-pause "inside" state stays latched in
    /// <see cref="WorldInstance.PortalOccupancy"/> exactly where the pause caught it, firing only once resume
    /// produces a genuine new edge. A transfer a step enqueues here still drains at this host's one fixed drain
    /// point (<see cref="DrainPendingTransfers"/>, called once per master call, before any row steps) — the
    /// next master call's drain, for a transfer enqueued by a step that happened during this one, which is the
    /// honest cross-world semantics: a transfer is a host act, not a step-local one.</remarks>
    /// <param name="masterDeltaTicks">The host's own master timeline advance for this call — the same quantum the
    /// fixed-step pump already produced (<see cref="FixedStepContext.StepTicks"/> from the call that invoked this),
    /// never a second clock this type samples on its own.</param>
    public void StepInstances(ulong masterDeltaTicks) {
        // Ordinal name order, so the step sequence is a property of the names rather than of insertion history.
        // Rows never observe one another, so the order carries no cross-row meaning — only each one's own
        // trajectory matters, and that is what makes iterating a hash map by sorted key honest here.
        foreach (var name in Names) {
            if (string.Equals(
                a: name,
                b: BootInstanceName,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            var instance = m_instances[name];

            // A restored row held pending its adjacency mirrors banks no ticks and drains nothing administrative —
            // it is not yet part of the stepping engine at all, exactly like a row this host has not admitted.
            // ReleaseHold clears this and starts the door on the boundary the caller proves every mirror primed.
            if (instance.AwaitingMirrors) {
                continue;
            }

            var rateHz = instance.Server.Definition.SimulationRateHz;

            if (
                (rateHz <= 0) ||
                instance.IsPaused
            ) {
                _ = instance.Server.DrainAdministrative();

                continue;
            }

            instance.ScheduleAccumulatorTicks += masterDeltaTicks;

            var stepWidth = EngineTicks.PerRate(ratePerSecond: ((uint)rateHz));

            while (instance.ScheduleAccumulatorTicks >= stepWidth) {
                instance.ScheduleAccumulatorTicks -= stepWidth;

                var tick = instance.CompletedTicks;
                // Accumulated, never re-derived from (tick + 1) * stepWidth — a product re-derivation breaks
                // the instant this instance's own rate changes (see WorldInstance.ElapsedEngineTicks).
                var elapsedTicks = (instance.ElapsedEngineTicks + stepWidth);
                var context = new FixedStepContext(
                    ElapsedTicks: elapsedTicks,
                    StepTicks: stepWidth,
                    Tick: tick
                );

                m_seats.SubmitAuthorityIntents(
                    endpoint: EndpointFor(instance: instance),
                    tick: (tick + 1UL)
                );

                WorldNarrationScope.Current = name;

                try {
                    _ = WorldServerStepShell.Step(
                        context: in context,
                        publishTick: instance.PublishTick,
                        server: instance.Server,
                        tape: instance.Tape,
                        tcpHost: instance.Door
                    );
                } finally {
                    WorldNarrationScope.Current = null;
                }

                instance.ElapsedEngineTicks = elapsedTicks;
                ScanInstanceBoundaries(instance: instance);

                // Server.Step installs any pending definition swap (world.load/.reset/.reload) before
                // advancing, so a mid-batch rate change makes the cached stepWidth stale for further
                // iterations. Stop the batch here instead — the leftover ScheduleAccumulatorTicks carries
                // over untouched to the next StepInstances call, which reads the fresh rate and
                // resumes correctly; tick numbering and ElapsedEngineTicks stay contiguous across the
                // boundary since neither is reset by this break.
                if (instance.Server.Definition.SimulationRateHz != rateHz) {
                    break;
                }
            }
        }
    }
    /// <summary>
    /// Submits each externally clocked authority once at the next tick announced by its observation stream. The
    /// client de-duplicates by route epoch and authority tick, so a stalled network snapshot cannot enqueue a
    /// growing copy of the same held input.
    /// </summary>
    public void SubmitExternallyClockedSeatIntents() {
        if (Boot is null) {
            return;
        }

        var submitted = new HashSet<WorldAuthorityEndpoint>();

        for (var slot = 0; (slot < m_seats.SeatCount); slot++) {
            if (m_seats.RoutedEndpoint(slot: slot) is not { } endpoint) {
                continue;
            }

            if (
                !endpoint.ClockOwnedHere &&
                submitted.Add(item: endpoint)
            ) {
                m_seats.SubmitAuthorityIntents(
                    endpoint: endpoint,
                    tick: endpoint.NextInputTick
                );
            }
        }
    }
    /// <summary>Looks up a running instance by name.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="instance">The instance, when found.</param>
    /// <returns>Whether an instance is running under <paramref name="name"/>.</returns>
    public bool TryGet(string name, out WorldInstance? instance) => m_instances.TryGetValue(
        key: name,
        value: out instance
    );
    /// <summary>Looks up the transport-side view of a REMOTE authority already opened under
    /// <paramref name="name"/> — its dialled endpoint and its wall-clock lane health
    /// (<see cref="WorldRemoteAuthority.LanesAvailable"/>). Read-back garnish only; neither value is ever a
    /// simulation input. A same-process neighbour, or one nothing has dialled yet, has no row here.</summary>
    /// <param name="name">The console-facing authority name the remote row was opened under.</param>
    /// <param name="endpoint">The dialled endpoint.</param>
    /// <param name="laneAvailable">Whether every opened lane is outside its backoff window.</param>
    /// <returns>Whether a remote authority row exists under <paramref name="name"/>.</returns>
    public bool TryDescribeRemoteAuthority(string name, out string endpoint, out bool laneAvailable) {
        if (!m_remoteAuthorities.TryGetValue(
            key: name,
            value: out var authority
        )) {
            // The table's key is whichever name the dial resolved under — a destinations row for a transfer route,
            // an instance name for an observation. A caller holding only the delivered identity still deserves the
            // true answer, so fall back to matching that against each row's own stamped authority/endpoint.
            foreach (var candidate in m_remoteAuthorities.Values) {
                if (string.Equals(
                    a: candidate.Authority,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                ) || string.Equals(
                    a: candidate.Endpoint,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    authority = candidate;

                    break;
                }
            }
        }
        if (authority is null) {
            endpoint = string.Empty;
            laneAvailable = false;

            return false;
        }

        endpoint = authority.Endpoint;
        laneAvailable = authority.LanesAvailable;

        return true;
    }
    /// <summary>Looks up a running authority's submission transport. Every local instance and remote traveler route
    /// carries the same transport capability; consumers do not branch on where that authority is hosted.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="link">The instance's transport, when found.</param>
    /// <returns>Whether an instance is running under <paramref name="name"/>.</returns>
    public bool TryGetLink(string name, out IServerLink? link) {
        if (m_remoteAuthorities.TryGetValue(
            key: name,
            value: out var authority
        )) {
            link = authority.Link;

            return true;
        }

        if (m_instances.TryGetValue(
            key: name,
            value: out var instance
        )) {
            link = instance.Link;
            return true;
        }

        link = null;

        return false;
    }
    /// <summary>Resolves the continuous projection channel for a local or remote authority already named by the
    /// session router. Definition and per-tick records cross the same observer contract.</summary>
    public bool TryGetProjection(string name, out WorldDefinition? definition, out Func<IClientSink, IDisposable>? attach, out WorldRenderEnvelope? envelope, out IWorldAdjacencySource? adjacencies) {
        if (m_remoteAuthorities.TryGetValue(
            key: name,
            value: out var remote
        )) {
            definition = remote.Definition;
            attach = remote.AttachSink;
            envelope = null;
            adjacencies = null;
            return true;
        }

        if (m_instances.TryGetValue(
            key: name,
            value: out var instance
        )) {
            definition = instance.Server.Definition;
            attach = instance.Server.AttachSink;
            envelope = instance.Server.Envelope;
            adjacencies = instance.Server.Adjacencies;
            return true;
        }

        definition = null;
        attach = null;
        envelope = null;
        adjacencies = null;
        return false;
    }
    /// <summary>Arms the live pause lever for a running instance (see <see cref="WorldInstance.IsPaused"/>) —
    /// <c>world.rate pause</c>'s door. Refuses an unknown name and a rate-0 world by name: a rate-0 world already
    /// never steps (the document's own durable stop), so the live lever would only duplicate that under a
    /// misleading name — the caller should read <c>world.rate</c> to see the distinction rather than pause a world
    /// that was already stopped. Pausing an already-paused instance is a no-op that still reports success (the
    /// caller asked for a state that already holds).</summary>
    /// <param name="name">The instance name — the boot instance included.</param>
    /// <param name="reason">The refusal reason, naming which rule fired, on failure.</param>
    /// <returns><see langword="true"/> when the lever is armed (or already was).</returns>
    public bool TryPause(string name, out string reason) {
        if (!m_instances.TryGetValue(
            key: name,
            value: out var instance
        )) {
            reason = $"no instance named '{name}'";

            return false;
        }

        if (instance.Server.Definition.SimulationRateHz <= 0) {
            reason = $"'{name}' authors simulation.rateHz 0 — it never steps by the document's own durable stop, so the live pause lever exists only for a world that would otherwise be stepping (see world.rate)";

            return false;
        }

        instance.IsPaused = true;
        reason = string.Empty;

        return true;
    }
    /// <summary>Releases the live pause lever, resuming a running instance's schedule exactly where its accumulator
    /// held it (see <see cref="WorldInstance.ScheduleAccumulatorTicks"/> — no skew) — <c>world.rate resume</c>'s
    /// door. Refuses only an unknown name; resuming an instance that was never paused (or is already resumed) is a
    /// no-op echo, never a refusal (see <paramref name="wasPaused"/>).</summary>
    /// <param name="name">The instance name — the boot instance included.</param>
    /// <param name="wasPaused">Whether the lever was actually holding this instance before this call — the caller
    /// uses this to distinguish an actual resume from a no-op echo in its own read-back.</param>
    /// <param name="reason">The refusal reason on failure (an unknown name only).</param>
    /// <returns><see langword="true"/> when the instance exists (paused or not).</returns>
    public bool TryResume(string name, out bool wasPaused, out string reason) {
        if (!m_instances.TryGetValue(
            key: name,
            value: out var instance
        )) {
            wasPaused = false;
            reason = $"no instance named '{name}'";

            return false;
        }

        wasPaused = instance.IsPaused;
        instance.IsPaused = false;
        reason = string.Empty;

        return true;
    }
    /// <summary>Starts a new instance from a world document and admits it under <paramref name="name"/>. Constructs a
    /// fresh <see cref="WorldPopulation"/>, <see cref="WorldRenderEnvelope"/>, <see cref="WorldOwnedWorlds"/> (its own
    /// directory, never shared) and an empty <see cref="WorldMachineHost"/> — nothing shared with any other
    /// instance.</summary>
    /// <param name="name">The console-facing name, which is also the directory segment this instance's owned worlds
    /// live in; refused if empty, reserved, not a single safe path segment, already running, or resolving its store
    /// outside the instances root.</param>
    /// <param name="path">The world document path, resolved like <c>--world</c>: tried directly (rooted, or relative
    /// to the current directory), then relative to <see cref="AppContext.BaseDirectory"/>, so a shipped
    /// <c>Assets/worlds/*.json</c> path resolves regardless of the process's launch directory.</param>
    /// <param name="instance">The started instance, when this returns <see langword="true"/>.</param>
    /// <param name="reason">The refusal reason, naming which rule fired — a running count belongs in neither this
    /// sentence nor the verb's description, since one of them always goes stale first.</param>
    /// <returns><see langword="true"/> when the instance started and was admitted.</returns>
    public bool TryStart(string name, string path, out WorldInstance? instance, out string reason) {
        instance = null;

        if (!m_admitsSpawn) {
            reason = "this host admits rows only through activation";

            return false;
        }

        // The name is a directory segment, not just a label — it is the one component of this instance's
        // owned-worlds path. A name carrying a separator, a drive, or a traversal step would choose where the
        // instance's documents are written, so WorldSafeName refuses those by construction (empty, a reserved
        // character, or a bare '.'/'..'); there is no separate segment-safety re-check downstream.
        if (!WorldSafeName.TryParse(
            candidate: name,
            name: out _,
            reason: out var nameReason
        )) {
            reason = $"'{name}' is not a single safe path segment — the name IS the directory this instance's owned worlds live in, and {nameReason}";

            return false;
        }

        if (string.Equals(
            a: name,
            b: BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"'{BootInstanceName}' names the world this process booted with and cannot be reused";

            return false;
        }

        if (m_instances.ContainsKey(key: name)) {
            reason = $"an instance named '{name}' is already running";

            return false;
        }

        // Whatever the name resolves to under the platform's path grammar, the directory this host is about
        // to create must sit under the instances root, or nothing is created.
        string instancesRoot;
        string ownedWorlds;

        try {
            instancesRoot = InstancesRoot();
            ownedWorlds = OwnedWorldsDirectory(name: name);
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)) {
            reason = $"'{name}' does not form a path this platform can express — {exception.Message}";

            return false;
        }

        if (!ownedWorlds.StartsWith(
            comparisonType: PathComparison,
            value: instancesRoot
        )) {
            reason = $"'{name}' resolves its owned worlds to {ownedWorlds}, outside the instances root {instancesRoot}";

            return false;
        }

        if (!TryResolveDocumentPath(
            path: path,
            resolved: out var resolvedPath
        )) {
            reason = $"no file at '{path}', either as given or under {AppContext.BaseDirectory}";

            return false;
        }

        // A file-backed neighbour resolver, beside the instance document itself. A quilt neighbour started
        // this way clears the same cross-document adjacency proof a top-level --world boot does (see
        // WorldDefinitionLoader.TryResolve). No cloud-backed half here; a neighbour reachable only through
        // the cloud refuses by name like any other unreachable resolver.
        var instanceNeighbours = new WorldFileNeighbourResolver(baseDirectory: () => ((Path.GetDirectoryName(path: resolvedPath) is { Length: > 0 } instanceDirectory)
            ? instanceDirectory
            : AppContext.BaseDirectory));

        // The instance's own NAME is the seed ladder's instance rung, so two instances of one document draw
        // independently while each stays reproducible from (document, instance name, draw history).
        if (!WorldDefinitionLoader.TryLoadFile(
            definition: out var definition,
            instanceIdentity: name,
            neighbours: instanceNeighbours,
            path: resolvedPath,
            reason: out reason
        )) {
            return false;
        }

        var machines = new WorldMachineHost(
            screens: [],
            engines: []
        );
        WorldInstance started;

        // Construction touches the file system (the owned-world store creates its directory and seeds documents into
        // it). This runs on the FIXED-STEP THREAD — world.instance.start routes Simulation — where an escaping
        // exception kills the pump and takes every world in the process down with it, the boot world included. An IO
        // failure here is a refusal like any other; nothing about it is worth the whole session.
        try {
            var server = new WorldServer(
                definition: definition!,
                population: new WorldPopulation(definition: definition!),
                profiles: new WorldOwnedWorlds(
                    template: definition!,
                    directory: ownedWorlds,
                    machineId: m_machineId,
                    neighbours: new WorldFileNeighbourResolver(baseDirectory: () => ownedWorlds)
                ),
                envelope: new WorldRenderEnvelope(),
                machines: machines,
                instanceIdentity: name
            );
            var adjacencies = new WorldAdjacencyFields(
                instances: this,
                sourceInstanceName: name
            );

            server.Neighbours = instanceNeighbours;
            server.Adjacencies = adjacencies;

            // The identical two-line pattern WorldBootComposition wires for the boot instance's own transport (a
            // WorldServer implements IWorldServerHost directly — see LoopbackTransport's own remarks) — one
            // transport per instance uniformly, never a special case reserved for boot. Federation is the SAME
            // process-wide authenticator/subject a desktop's boot row signs under — a desktop is one authenticated
            // namespace, never one key per spawned row (see WorldFederationIdentity's own remarks).
            started = new WorldInstance(
                name: name,
                origin: () => resolvedPath,
                server: server,
                ownedMachines: machines,
                link: new LoopbackTransport(server: server),
                federation: Boot!.Federation,
                documentOrigin: new WorldFileOrigin(resolvedPath: resolvedPath),
                ownedAdjacencies: adjacencies
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)) {
            machines.Dispose();
            reason = $"'{name}' could not open its owned-world store at {ownedWorlds} — {exception.Message}";

            return false;
        }

        Admit(row: started);
        instance = started;
        reason = string.Empty;

        return true;
    }
    /// <summary>Retires a running instance and disposes what it owned. The boot instance is refused: retiring it
    /// would leave the container's client, seats, tape and console verbs holding a server nothing steps.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="reason">The refusal reason on failure.</param>
    /// <returns><see langword="true"/> when the instance was retired.</returns>
    public bool TryStop(string name, out string reason) {
        if (string.Equals(
            a: name,
            b: BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"'{BootInstanceName}' is the world this process booted with — close the process to stop it";

            return false;
        }

        if (!m_instances.TryGetValue(
            key: name,
            value: out var instance
        )) {
            reason = $"no instance named '{name}'";

            return false;
        }

        // A followed local seat keeps its roster participant/device binding while its body lives in this instance.
        // Removing the instance first would leave the router pointing at a name nothing steps, so no input or portal
        // crossing could ever bring that participant home. Automatic reaping reaches this method only AFTER a
        // committed transfer has republished the router, so this guard affects the explicit operator stop alone.
        for (var slot = 0; (slot < m_seats.SeatCount); slot++) {
            if (
                m_seats.IsOccupied(slot: slot) &&
                (m_seats.RoutedEndpoint(slot: slot) is { } endpoint) &&
                string.Equals(
                a: endpoint.Identity,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                reason = $"'{name}' is presenting local seat {(slot + 1)} — transfer that traveler out before stopping the instance";

                return false;
            }
        }

        _ = m_instances.Remove(key: name);
        if (m_authorityEndpoints.Remove(
            key: name,
            value: out var retiredEndpoint
        )) {
            retiredEndpoint.Dispose();
        }

        // An explicit stop clears retention once the local-follow guard above proves it cannot orphan presentation.
        // A later name reuse (a fresh world.instance.start under the same spelling) starts out with ordinary
        // reap-on-empty rather than inheriting a persistence flag from an instance that no longer exists.
        m_retainedInstances.Remove(item: name);
        // This is BOTH the explicit-stop AND the ReapIfEmpty apply path, so a resolver-minted name going away here —
        // by either route — is exactly the moment WorldSessionResolver needs to hear about: its cached generation
        // record for whatever (destination, scope key) minted this name is cleared, so the NEXT scoped resolution
        // mints a genuinely new generation rather than reusing a name nothing answers to any more. A no-op for a name
        // the resolver never minted.
        m_resolver.NotifyInstanceRetired(instanceName: name);
        instance.Dispose();
        reason = string.Empty;

        return true;
    }

    /// <summary>The boot row this host currently steps, or <see langword="null"/> for a boot-free host (a hosted
    /// silo admits no boot row). Every boot-only member below is a no-op while this is
    /// <see langword="null"/>.</summary>
    public WorldInstance? Boot => m_instances.GetValueOrDefault(key: BootInstanceName);
    /// <summary>Every admitted row's name, ordinal-sorted.</summary>
    public IReadOnlyList<string> Names => [.. m_instances.Keys.Order(comparer: StringComparer.Ordinal)];

    /// <summary>Initializes an empty registry — no row is admitted by construction. <see cref="AdmitBoot"/> admits a
    /// desktop's one boot row; <see cref="Admit"/> is the general door every other row (a desktop's own spawn/resolve
    /// arms, a hosted silo's activation mailbox) enters through.</summary>
    /// <param name="seats">The local seats this host embodies — <see cref="WorldEmbodiedSeats.None"/> for a host
    /// with none.</param>
    /// <param name="resolver">The transport-neutral local session resolver <see cref="ResolveAndEnqueueCoalescedTransfers"/>
    /// consumes.</param>
    /// <param name="machineId">This host's own persisted id, stamped into every mint this host performs.</param>
    /// <param name="stateRoot">The root every non-boot instance's owned-world store resolves its own directory under.</param>
    /// <param name="applicationStopping">Cancelled on host shutdown — closes every persistent federation lane before a
    /// companion authority observes an ordinary shutdown as a live-path outage.</param>
    /// <param name="admitsSpawn">Whether <see cref="TryStart"/> may mint a brand-new row from a document path — a
    /// desktop's own spawn/resolve arms need this; a hosted silo refuses it by name, since a row there exists only
    /// through the activation door.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldInstanceHost(IWorldEmbodiedSeats seats, WorldSessionResolver resolver, Guid machineId, string stateRoot, CancellationToken applicationStopping, bool admitsSpawn = true) {
        ArgumentNullException.ThrowIfNull(argument: seats);
        ArgumentNullException.ThrowIfNull(argument: resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: stateRoot);

        m_seats = seats;
        m_resolver = resolver;
        m_machineId = machineId;
        m_stateRoot = stateRoot;
        m_applicationStopping = applicationStopping;
        m_admitsSpawn = admitsSpawn;
    }

    /// <summary>Admits <paramref name="row"/> into the registry under its own name — the one place a row enters this
    /// host, whether that row is a desktop's boot instance, a desktop's spawned/resolved instance, or a hosted
    /// silo's activated grain.</summary>
    /// <param name="row">The fully constructed row (server wired, adjacencies attached, federation/origin set).</param>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A row of this name is already admitted.</exception>
    public void Admit(WorldInstance row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        if (m_instances.ContainsKey(key: row.Name)) {
            throw new ArgumentException(message: $"an instance named '{row.Name}' is already admitted", paramName: nameof(row));
        }

        row.Server.TransferForwarder = this;
        m_instances[row.Name] = row;
        _ = EndpointFor(instance: row);
    }
    /// <summary>Admits <paramref name="row"/> as this host's one boot row and seeds every embodied local seat's
    /// route to it — a desktop's one-time boot admission, never called by a boot-free host.</summary>
    /// <param name="row">The boot row, named <see cref="BootInstanceName"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="row"/> is not named <see cref="BootInstanceName"/>.</exception>
    public void AdmitBoot(WorldInstance row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        if (!string.Equals(
            a: row.Name,
            b: BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new ArgumentException(message: $"a boot row must be named '{BootInstanceName}'", paramName: nameof(row));
        }

        Admit(row: row);
        var bootEndpoint = EndpointFor(instance: row);
        // A world declaring fewer local seats than the host's seat ceiling (m_seats.SeatCount) has no entity-table
        // row for the seats it did not declare — Population.Capacity is the entity table's real size.
        var routedSeats = Math.Min(val1: m_seats.SeatCount, val2: row.Server.Population.Capacity);

        for (var slot = 0; (slot < routedSeats); slot++) {
            m_seats.PublishRoute(
                slot: slot,
                endpoint: bootEndpoint,
                entity: new WorldEntityAddress(
                    Authority: row.Server.AuthorityIdentity,
                    Index: slot,
                    Generation: row.Server.Population.Generation(index: slot)
                )
            );
        }
        m_seats.ConfigureLeave(leave: LeaveRosterSeat);
    }
    /// <summary>Releases a row held under <see cref="WorldInstance.AwaitingMirrors"/> — clears the hold and starts
    /// this row's own door, if it has one (readiness is a promise to step, so a held row's door stays unstarted
    /// until this call). The caller decides WHEN every adjacency handle this row depends on is primed or
    /// Unavailable by name; this method only performs the state transition once that decision is made.</summary>
    /// <param name="row">The held row to release.</param>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public void ReleaseHold(WorldInstance row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        row.AwaitingMirrors = false;

        if (
            (row.Door is { } door) &&
            (row.Server.Definition.Host.Listen is { Length: > 0 } listen)
        ) {
            door.Start(listen: listen);
        }
    }
    /// <summary>Substitutes the <see cref="IWorldPeerCall"/> a transfer resolving <paramref name="instanceName"/> as
    /// a LOCAL destination reaches — the one seam a caller may decorate to reach a reserved-but-uncommitted or
    /// in-doubt transfer in-process, exercising the same recovery path a lost transport answer takes over a real
    /// socket. Never consulted for a remote destination, and never set outside verification: production resolution
    /// always calls straight through to the row's own server.</summary>
    /// <param name="instanceName">The destination row's registry name.</param>
    /// <param name="fault">The decorator to substitute, or <see langword="null"/> to clear a previously set one.</param>
    public void SetPeerCallFault(string instanceName, IWorldPeerCall? fault) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: instanceName);

        if (fault is null) {
            _ = m_peerCallFaults.Remove(key: instanceName);
        } else {
            m_peerCallFaults[instanceName] = fault;
        }
    }

    /// <summary>Builds the peer call for a row resolved as a LOCAL destination, substituting a fault decorator
    /// registered through <see cref="SetPeerCallFault"/> when one is set for <paramref name="local"/>'s own name.</summary>
    private WorldPeerCall LocalPeerCall(WorldInstance local) => new(
        Local: local,
        Remote: null,
        Fault: m_peerCallFaults.GetValueOrDefault(key: local.Name)
    );

    /// <summary>Captures this row's own slice of the host engine's cross-instance tables — see
    /// <see cref="WorldAuthorityHostRowCheckpoint"/>. In-doubt transfers and forwarded bodies are captured as
    /// addresses (destination identity, mobility credential), never the live peer/forwarding arm — a restore
    /// re-materializes a fresh arm from the address on demand.</summary>
    /// <param name="row">The row to capture.</param>
    /// <returns>The row's checkpointed host-engine slice.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public WorldAuthorityHostRowCheckpoint CaptureRow(WorldInstance row) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var inDoubt = new List<WorldInDoubtTransferCheckpoint>();

        foreach (var pending in m_inDoubtTransfers) {
            if (!string.Equals(
                a: pending.Transfer.SourceInstance,
                b: row.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            var isRemote = (pending.TargetAuthority.Remote is not null);
            var targetAuthority = (pending.TargetAuthority.Local?.Server.AuthorityIdentity
                ?? (pending.TargetAuthority.Remote?.Authority
                ?? string.Empty)
            );

            inDoubt.Add(item: new WorldInDoubtTransferCheckpoint(
                CommitMembers: [.. pending.CommitMembers],
                Landed: [.. pending.Landed.Select(selector: static member => new WorldLandedMemberCheckpoint(
                    AdmissionGrants: member.AdmissionGrants,
                    BodyColor: member.BodyColor,
                    Designations: member.Designations,
                    DynamicState: member.DynamicState,
                    Mobility: member.Mobility,
                    Peer: member.Peer,
                    Position: member.Position,
                    SourceGrants: member.SourceGrants,
                    SourceSlot: member.SourceSlot,
                    TargetSlot: member.TargetSlot,
                    Yaw: member.Yaw
                ))],
                MemberCount: pending.MemberCount,
                SourceDeadlineTick: pending.SourceDeadlineTick,
                SourceInstance: pending.Transfer.SourceInstance,
                Spawned: pending.Spawned,
                TargetAuthority: targetAuthority,
                // A co-hosted target's endpoint is re-derivable from its own row on restore (EndpointFor); a remote
                // target's endpoint is live connection state a restore cannot ask anyone for, so it is captured here
                // and TargetName stays null — the doc contract this record has always stated.
                TargetEndpoint: (isRemote
                    ? pending.TargetAuthority.Remote!.Endpoint
                    : null
                ),
                TargetName: (isRemote
                    ? null
                    : pending.TargetName
                ),
                TransferId: pending.Transfer.TransferId
            ));
        }

        // Captured as data through IWorldForwardedAuthority.DescribeForCheckpoint — never the live lease/lane.
        // RestoreRow does not yet re-materialize a fresh arm from this data (that needs a per-world-id authority
        // directory this lane does not build), so a departed traveler's onward route still re-resolves the ordinary
        // way (TryFindRunningInstanceByOrigin/TryResolveObservedProjection) the next time something forwards to it —
        // the capture no longer silently drops the row, but the restore-side gap this replaced stays open, named.
        var forwarded = new List<WorldForwardedBodyCheckpoint>();

        foreach (var pair in m_forwardedBodies) {
            if (!ReferenceEquals(objA: pair.Key.Server, objB: row.Server)) {
                continue;
            }

            pair.Value.Authority.DescribeForCheckpoint(
                destinationAuthority: out var destinationAuthority,
                mobility: out var mobility
            );
            forwarded.Add(item: new WorldForwardedBodyCheckpoint(
                SourceIncarnation: pair.Key.Incarnation,
                DestinationAddress: new WorldEntityAddress(
                    Authority: destinationAuthority,
                    Index: pair.Value.BodyIndex,
                    Generation: 0
                ),
                DestinationBodyIndex: pair.Value.BodyIndex,
                Mobility: mobility
            ));
        }

        var appliedIds = m_appliedTransferIds
            .Where(predicate: entry => string.Equals(
                a: entry.SourceInstance,
                b: row.Name,
                comparisonType: StringComparison.Ordinal
            ))
            .Select(selector: entry => entry.TransferId)
            .ToArray();

        return new WorldAuthorityHostRowCheckpoint(
            AnnouncedCrossingHolds: [.. m_announcedCrossingHolds
                .Where(predicate: pair => string.Equals(a: pair.Key.Instance, b: row.Name, comparisonType: StringComparison.Ordinal))
                .Select(selector: pair => (pair.Key.Seat, pair.Value))],
            AppliedTransferHighWater: (m_appliedTransferHighWater.TryGetValue(
                key: row.Name,
                value: out var highWater)
                ? highWater
                : null
            ),
            AppliedTransferIds: appliedIds,
            ElapsedEngineTicks: row.ElapsedEngineTicks,
            ForwardedBodies: forwarded,
            FreshCounter: m_freshCounters.GetValueOrDefault(key: row.Name),
            InDoubtTransfers: inDoubt,
            IsPaused: row.IsPaused,
            NextTransferId: row.NextTransferId,
            PortalOccupancy: row.PortalOccupancy.Capture(),
            Retained: m_retainedInstances.Contains(item: row.Name),
            ScheduleAccumulatorTicks: row.ScheduleAccumulatorTicks,
            SeededArrivals: [.. m_seededArrivals
                .Where(predicate: pair => string.Equals(a: pair.Key.Instance, b: row.Name, comparisonType: StringComparison.Ordinal))
                .Select(selector: pair => (pair.Key.Seat, pair.Value))]
        );
    }
    /// <summary>Restores this row's own slice of the host engine's cross-instance tables from a previously captured
    /// checkpoint — the reciprocal of <see cref="CaptureRow"/>. A forwarded body or an in-doubt transfer whose
    /// destination is ALREADY admitted on this host (the co-hosted case) re-materializes a live, resolvable arm from
    /// the captured address — a fresh <see cref="WorldLocalForwardedAuthority"/> for a forwarded body,
    /// <see cref="LocalPeerCall"/> over the admitted destination row for an in-doubt transfer — so every row this
    /// restore admits must already be in the registry before this is called for any of them (see
    /// <see cref="CaptureRow"/>'s own remarks: a forwarded body's mobility credential is already the post-commit
    /// epoch, exactly what a live arm's constructor stores). A REMOTE destination's arm re-materializes lazily
    /// instead in both cases — nothing here dials a live peer call for one; not built by this lane.</summary>
    /// <param name="row">The row to restore onto — already admitted (<see cref="Admit"/>), not yet stepped.</param>
    /// <param name="slice">The captured host-engine slice to restore.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void RestoreRow(WorldInstance row, WorldAuthorityHostRowCheckpoint slice) {
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: slice);

        row.ScheduleAccumulatorTicks = slice.ScheduleAccumulatorTicks;
        row.ElapsedEngineTicks = slice.ElapsedEngineTicks;
        row.IsPaused = slice.IsPaused;
        row.NextTransferId = slice.NextTransferId;
        row.PortalOccupancy.Restore(rows: slice.PortalOccupancy);

        m_freshCounters[row.Name] = slice.FreshCounter;

        if (slice.Retained) {
            _ = m_retainedInstances.Add(item: row.Name);
        }

        if (slice.AppliedTransferHighWater is { } highWater) {
            m_appliedTransferHighWater[row.Name] = highWater;
        }

        foreach (var transferId in slice.AppliedTransferIds) {
            _ = m_appliedTransferIds.Add(item: (row.Name, transferId));
        }

        foreach (var row2 in slice.AnnouncedCrossingHolds) {
            m_announcedCrossingHolds[(row.Name, row2.Seat)] = row2.TransferId;
        }

        foreach (var row2 in slice.SeededArrivals) {
            m_seededArrivals[(row.Name, row2.Seat)] = row2.Border;
        }

        foreach (var forwarded in slice.ForwardedBodies) {
            WorldInstance? destination = null;

            foreach (var candidate in m_instances.Values) {
                if (string.Equals(
                    a: candidate.Server.AuthorityIdentity,
                    b: forwarded.DestinationAddress.Authority,
                    comparisonType: StringComparison.Ordinal
                )) {
                    destination = candidate;
                    break;
                }
            }

            if (destination is null) {
                // A remote (not co-hosted) destination: nothing here mints a live peer call for it — the traveler's
                // onward route re-resolves the ordinary way (TryFindRunningInstanceByOrigin/
                // TryResolveObservedProjection) the next time something forwards to it, exactly as an unresolved
                // forwarded body already behaves today outside a restore.
                continue;
            }

            m_forwardedBodies[(row.Server, forwarded.SourceIncarnation)] = new ForwardedBody(
                Authority: new WorldLocalForwardedAuthority(
                    server: destination.Server,
                    endpoint: (destination.Server.Definition.Host.Authority ?? EndpointFor(instance: destination).Identity),
                    sourceAuthority: $"{m_machineId:N}/{row.Name}",
                    mobility: forwarded.Mobility
                ),
                BodyIndex: forwarded.DestinationBodyIndex
            );
        }

        foreach (var pending in slice.InDoubtTransfers) {
            WorldInstance? target = null;

            foreach (var candidate in m_instances.Values) {
                if (string.Equals(
                    a: candidate.Server.AuthorityIdentity,
                    b: pending.TargetAuthority,
                    comparisonType: StringComparison.Ordinal
                )) {
                    target = candidate;
                    break;
                }
            }

            if (target is null) {
                // A remote target (TargetEndpoint captured at capture time) has no live WorldRemoteAuthority arm
                // this restore dials — the same gap ForwardedBodies names above, for the identical reason (no
                // per-world-id authority directory this lane builds). A co-hosted target this host has not (yet)
                // admitted alongside this row is the same outcome: the entry is dropped rather than retried against
                // nothing, and ReconcileInDoubtTransfers therefore never resolves it — named, not silently wrong.
                continue;
            }

            if (pending.CommitMembers.Count != pending.MemberCount) {
                // A retried Commit's own member-count check releases the destination's lease as a SIDE EFFECT of
                // refusing (WorldTransferEscrow.Commit compares against the reservation before it validates the
                // members it was handed), so letting a malformed capture through would not merely refuse the retry —
                // it would silently roll the whole transfer back as though the destination had lost the reservation.
                // Refused here, before any live call, so the checkpoint itself is what is named as wrong.
                Console.Error.WriteLine(value: $"[world.transfer: restore refused in-doubt transfer={pending.TransferId} for '{row.Name}' — commit member count {pending.CommitMembers.Count} does not match member count {pending.MemberCount}]");

                continue;
            }

            var landed = new List<LandedMember>(capacity: pending.Landed.Count);

            for (var ordinal = 0; (ordinal < pending.Landed.Count); ordinal++) {
                var member = pending.Landed[ordinal];
                // Profile is not part of the checkpointed landed-member shape — it is re-derived here from the
                // corresponding commit member at the SAME ordinal (see WorldLandedMemberCheckpoint's own remarks).
                var profile = ((ordinal < pending.CommitMembers.Count)
                    ? pending.CommitMembers[ordinal].Profile
                    : null
                );

                landed.Add(item: new LandedMember(
                    AdmissionGrants: member.AdmissionGrants,
                    BodyColor: member.BodyColor,
                    Designations: [.. member.Designations],
                    DynamicState: member.DynamicState,
                    Mobility: member.Mobility,
                    Peer: member.Peer,
                    Position: member.Position,
                    Profile: profile,
                    SourceGrants: member.SourceGrants,
                    // SourcePrincipal is stamped at construction but read by neither resolution path (see
                    // WorldLandedMemberCheckpoint's own remarks) — any value restores the same observable behavior.
                    SourcePrincipal: WorldPrincipal.Console,
                    SourceSlot: member.SourceSlot,
                    TargetSlot: member.TargetSlot,
                    Yaw: member.Yaw
                ));
            }

            m_inDoubtTransfers.Add(item: new InDoubtTransfer(
                CommitMembers: [.. pending.CommitMembers],
                Landed: landed,
                MemberCount: pending.MemberCount,
                SourceAuthority: row.Server.AuthorityIdentity,
                SourceDeadlineTick: pending.SourceDeadlineTick,
                Spawned: pending.Spawned,
                TargetAuthority: LocalPeerCall(local: target),
                TargetName: (pending.TargetName ?? target.Name),
                // Every other PendingTransfer field only feeds resolver-driven bookkeeping
                // (NoteResolvedTransferOutcome's tape narration, CloseAdjacencyAfterRefusal's adjacency clamp) that a
                // console-driven world.transfer — the only shape this host ever puts in doubt today — never
                // populates either; FrozenCohortSlots carries every landed member's own source slot so
                // HeldCrossingSeats still recognizes this row's outstanding crossing correctly.
                Transfer: new PendingTransfer(
                    ActingPrincipal: WorldPrincipal.Console,
                    AdjacencyCounterpart: null,
                    Arrival: WorldPortalArrival.Spawn,
                    Border: string.Empty,
                    BorderCapacity: null,
                    Continuum: null,
                    Counterpart: null,
                    Destination: TransferDestination.Existing(name: (pending.TargetName ?? pending.TargetAuthority)),
                    FrozenCohortSlots: [.. landed.Select(selector: static member => member.SourceSlot)],
                    FrozenGenerationId: null,
                    FrozenScopeKey: null,
                    FullPolicy: WorldTransferFullPolicy.Retry,
                    HoldSeconds: 0,
                    PartyAllOrNothing: false,
                    ResolvedDestinationRow: null,
                    Scope: TransferScope.Body,
                    SourceCrossingPoint: default,
                    SourceFrame: null,
                    SourceInstance: row.Name,
                    SourceSlot: ((landed.Count > 0) ? landed[0].SourceSlot : 0),
                    TestForceJoinRefusalOrdinal: null,
                    TransferId: pending.TransferId
                )
            ));
        }
    }

    /// <summary><c>world.rate</c>'s read-back payload for one instance — see <see cref="TryDescribeRate"/>.</summary>
    /// <param name="RateHz">The declared <c>simulation.rateHz</c>, verbatim (never validated against a "known
    /// rates" notion here — the document is the source of truth, and a follow-on derived-floor pass owns any
    /// further constraint).</param>
    /// <param name="Stopped"><see langword="true"/> when <paramref name="RateHz"/> is the durable stop (0) — the
    /// instance never steps, by the document's own authoring, regardless of the live pause lever.</param>
    /// <param name="Paused">The live pause lever's own state (<see cref="WorldInstance.IsPaused"/>) — independent of
    /// <paramref name="Stopped"/>, though <see cref="TryPause"/> refuses arming it on an already-stopped instance.</param>
    /// <param name="StepWidthTicks">The engine-tick step width (<c>50400 / RateHz</c>), or <see langword="null"/>
    /// when <paramref name="Stopped"/> (a stopped world has no step period to report).</param>
    /// <param name="CompletedTicks">This instance's own completed-tick count — frozen while stopped or paused.</param>
    public readonly record struct WorldInstanceRateStatus(int RateHz, bool Stopped, bool Paused, ulong? StepWidthTicks, ulong CompletedTicks);
}
