using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The process's running world instances, keyed by console-chosen name — the <i>host</i> of docs/world-model.md's
/// "The words": the machine or process running instances. The world this process booted with is one entry
/// (<see cref="BootInstanceName"/>) beside every instance started later, so nothing here treats one world as a
/// different kind of thing from another. Owns starting, stepping, reading back and retiring them.
/// </summary>
/// <remarks><para><b>What is flat and what is not.</b> The registry, the naming, the read-back and the retirement
/// path are flat: the boot world is a row like any other. The wiring is not, and cannot cheaply be — the boot
/// instance's <see cref="Server.WorldServer"/> is a container singleton that the client, the seats, the editor, the
/// replay tape, the socket door, the audio director, the render frame source and every mutating console verb resolve
/// directly. Flattening that needs a per-instance service scope so those consumers name an instance instead of the
/// container; until then the asymmetry is confined to two facts stated here rather than spread through the
/// vocabulary: the boot instance is stepped by <see cref="WorldServerStepShell"/> (which also drives the tape, the
/// console wait gate and the socket drain — bookkeeping no other instance has yet), and it is the only instance any
/// other verb can reach.</para>
/// <para><b>Seats and embodiment.</b> A non-boot instance now has its own local-seat
/// table like the boot instance's — the <c>player.*</c> verbs' <c>instance:&lt;name&gt;</c> token enters,
/// drive (warp/face/run/stop), and leave a seat inside a named instance, applying through that instance's own
/// <see cref="Server.WorldServer.ApplySession"/>/<see cref="Server.WorldServer.ApplyCommand"/> doors — the identical
/// path the boot instance's <c>player.*</c> verbs use, never a bypass. Seating carries the seated identity's declared
/// durable state in through the same cross-document durable channel (<see cref="Server.WorldOwnedWorlds.TryReadDurableState"/>)
/// the boot instance's own session-join already stages with — a snapshot taken once at entry; the instance then
/// advances its own copy. <see cref="ReapIfEmpty"/> is the lifetime rule over that occupancy: a caller that just
/// vacated an instance's last active entry reaps it through the same door <see cref="TryStop"/> already exposes by
/// name. A live TCP peer entering a spawned instance (composing the existing peer-admission door with this same
/// seating seam) remains an unbuilt stretch — see <c>WorldInstanceCommandModule</c>'s own remarks.</para>
/// <para><b>Still deliberately absent, and each is its own unit of work.</b> No per-instance replay tape (the tape
/// covers the boot instance only — see <see cref="WorldReplayTape"/>), socket door, addon runtime, or
/// grant-gated start (starting one is ungated today); no machines — an instance's <see cref="Server.WorldMachineHost"/>
/// is constructed empty, so a document declaring machine-sourced screens starts with every one of them dark (the
/// start echo counts them, so the absence is read back rather than discovered).</para>
/// <para><b>Per-instance scheduling (docs/world-model.md).</b> Each instance advances on its own
/// authored <c>simulation.rateHz</c>, never a shared build-wide rate: <see cref="StepInstancesBesideBoot"/> holds a
/// per-instance accumulator (<see cref="WorldInstance.ScheduleAccumulatorTicks"/>) of engine ticks banked against
/// the host's master timeline — the boot instance's own rate-derived cadence the fixed-step pump already drives
/// (<c>Puck.Launcher.LauncherHostLoop</c>), never a second clock this type invents. An instance steps once each
/// time its own accumulator crosses its own step width (<c>50400 / rateHz</c> engine ticks); a rate faster than the
/// master cadence steps more than once per master tick, a rate slower steps less than once. A live pause
/// (<see cref="WorldInstance.IsPaused"/>, driven by the <c>world.rate pause</c>/<c>resume</c> console verb) holds the
/// accumulator exactly where it is — nothing is banked toward a step that will not happen — so resuming continues
/// on the identical schedule with no skew. An authored rate of 0 is the durable stop (never divided by; the instance
/// stays resident and readable, simply never steps) and is entirely independent of the live pause lever. Neither a
/// stopped nor a paused instance is left inert, though: <see cref="Server.WorldServer.DrainAdministrative"/> still
/// applies its buffered document mutations/rebuilds/undo/addon-lifecycle ops every master tick — otherwise a
/// document mutation that would rate a stopped world back up could never itself apply, a permanent self-lock.
/// The boot instance is governed by the identical rule, special-cased only where <see cref="WorldServerStepShell"/>'s
/// own tape/wait-gate/socket bookkeeping requires (see <see cref="ShouldStepBoot"/>): the master pump's own cadence
/// is already derived from boot's own rate (falling back to the host loop's default cadence only while boot itself
/// is stopped/paused, so the window keeps rendering and the console keeps answering), so boot's own crossing is
/// trivial — a pause/rate-0 gate, never a second accumulator.</para>
/// <para><b>The name is a path segment.</b> An instance's owned worlds live in a directory named by its console name,
/// so admitting a name is admitting a filesystem location: <see cref="TryStart"/> refuses any name that is not one
/// safe segment, and independently refuses any name whose resolved store does not sit under the instances root. The
/// second rule is not redundant with the first — it is what makes the placement true whatever the platform's path
/// grammar turns out to do with a name.</para>
/// <para>Stepping folds into the same <c>IFixedStepSimulation.Step</c> call both boot shapes already drive — never a
/// second pump, a second host loop, or a second <c>IFixedStepSimulation</c> registration (the launcher's
/// <c>LauncherHostLoop</c> still admits exactly one). Single-threaded throughout: the fixed-step thread is the only
/// caller, and the verbs that mutate the registry route <c>Simulation</c> so they apply on it at a tick
/// boundary.</para></remarks>
internal sealed class WorldInstanceHost : IDisposable {
    /// <summary>The reserved name of the world this process booted with (<c>--world</c>, or the shipped default). A
    /// start request naming it is refused rather than shadowing it.</summary>
    public const string BootInstanceName = "boot";

    // Path containment is decided the way the platform decides it: case-insensitively where file names are.
    private static readonly StringComparison PathComparison = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private readonly Dictionary<string, WorldInstance> m_instances = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, WorldRemoteAuthority> m_remoteAuthorities = new(comparer: StringComparer.Ordinal);
    // Every instance shares the machine's own persisted id — it identifies the MACHINE, not a world, so minting a
    // fresh one per instance would both misreport the machine and put a Guid.NewGuid() on a boot path.
    private readonly Guid m_machineId;
    private readonly string m_stateRoot;
    // LAZY, the same reason WorldBindingCommandModule's own router field is: InputRouter's construction resolves
    // the CommandRegistry, which aggregates every module's GetCommands() at container-build time, so a direct
    // constructor dependency here would cycle. Resolved only when a transfer actually departs a seat.
    private readonly Func<InputRouter> m_router;
    // The BOOT instance's client-side participant table — the one instance whose seats a local client mirrors (see
    // this type's own "what is flat and what is not" remark). A transfer crossing that boundary emits the roster's
    // seat-vacated/seat-occupied facts through it; a transfer between two non-boot instances never touches it.
    private readonly PlayerRoster m_roster;
    // The traveler-follow router (stage 1) — this host is its ONE writer (see WorldSeatInstanceRouter's own
    // remarks): ApplyTransfer publishes every followed landed member, and LeaveRosterSeat resets a committed
    // departure to the vacated roster slot's boot default. Not lazy: WorldSeatInstanceRouter carries no dependency that could
    // cycle back through this type (it is a small fixed array, like WorldPerceptionAnchor).
    private readonly WorldSeatInstanceRouter m_seatRouter;
    // The local client — StepInstancesBesideBoot's per-instance loop calls its away-seat submission door
    // immediately before that instance's own Server.Step (stage 1's correctness-critical piece: an away seat's
    // input must apply at THAT instance's own next-tick coordinate, never boot's). Not lazy: WorldClient's own
    // construction never reaches back into this type (see this type's remarks on the router Func's OWN cycle,
    // which WorldClient's dependency graph does not share).
    private readonly WorldClient m_client;
    // The transport-neutral local resolver ResolveAndEnqueueCoalescedTransfers consumes to turn a
    // destinations row plus a traveling cohort into a scoped generation/instance name — see
    // WorldSessionResolver. TryStop notifies it so a reaped/stopped instance's cache entry does not
    // outlive the instance.
    private readonly WorldSessionResolver m_resolver;
    // The boot instance's own replay tape. ApplyTransfer calls NoteTransfer on it directly, since the live
    // cohort/resolver machinery this type owns sits one layer above Puck.World.Server, where the tape lives.
    // A no-op while the tape is Idle; recording only ever taps a transfer touching the BOOT instance (source
    // or destination), since the tape covers the boot instance alone.
    private readonly WorldReplayTape m_bootReplayTape;
    private readonly WorldFederationSecurity m_federationSecurity;

    /// <summary>Gets the instance this process booted with — the one an instance-addressed read-back reaches when it
    /// is given no <c>instance:</c> token.</summary>
    public WorldInstance Boot => m_instances[BootInstanceName];

    /// <summary>Initializes the registry with the boot world already admitted under
    /// <see cref="BootInstanceName"/>.</summary>
    /// <param name="bootServer">The container's authoritative server — the boot instance's own.</param>
    /// <param name="bootOrigin">The console's tracked document origin for the boot instance, read live.</param>
    /// <param name="bootOwnedWorlds">The boot instance's owned-world store, read for the machine id and the state
    /// root every later instance derives its own directory under.</param>
    /// <param name="router">The lazy input-router resolver — a departing transfer clears the source seat's
    /// input-layer held state through it (see <see cref="ApplyTransfer"/>).</param>
    /// <param name="roster">The boot instance's client-side participant table — a transfer across the boot boundary
    /// emits its seat-vacated/seat-occupied facts through it (see <see cref="ApplyTransfer"/>).</param>
    /// <param name="resolver">The transport-neutral local session resolver <see cref="ResolveAndEnqueueCoalescedTransfers"/>
    /// consumes (see <see cref="WorldSessionResolver"/>).</param>
    /// <param name="bootReplayTape">The boot instance's own replay tape — a transfer touching the boot instance
    /// records its decided outcome onto it (see <see cref="ApplyTransfer"/>).</param>
    /// <param name="seatRouter">The traveler-follow router (stage 1) — this type's ApplyTransfer is its one
    /// writer.</param>
    /// <param name="client">The local client — this type's StepInstancesBesideBoot calls its away-seat intent
    /// submission door.</param>
    /// <param name="bootLink">The boot instance's own transport (the container's <c>LoopbackTransport</c> singleton)
    /// — every instance now carries its own transport uniformly (see <see cref="WorldInstance.Link"/>'s own
    /// remarks); the boot row simply holds the one that already existed.</param>
    /// <param name="federationSecurity">The process-scoped credentials used for authenticated remote authorities.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldInstanceHost(WorldServer bootServer, WorldDefinitionSource bootOrigin, WorldOwnedWorlds bootOwnedWorlds, Func<InputRouter> router, PlayerRoster roster, WorldSessionResolver resolver, WorldReplayTape bootReplayTape, WorldSeatInstanceRouter seatRouter, WorldClient client, IServerLink bootLink, WorldFederationSecurity federationSecurity) {
        ArgumentNullException.ThrowIfNull(argument: bootServer);
        ArgumentNullException.ThrowIfNull(argument: bootOrigin);
        ArgumentNullException.ThrowIfNull(argument: bootOwnedWorlds);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: resolver);
        ArgumentNullException.ThrowIfNull(argument: bootReplayTape);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: bootLink);
        ArgumentNullException.ThrowIfNull(argument: federationSecurity);

        m_machineId = bootOwnedWorlds.MachineId;
        m_stateRoot = WorldStateRoot.Resolve();
        m_router = router;
        m_roster = roster;
        m_resolver = resolver;
        m_bootReplayTape = bootReplayTape;
        m_seatRouter = seatRouter;
        m_client = client;
        m_federationSecurity = federationSecurity;
        m_instances[BootInstanceName] = new WorldInstance(
            name: BootInstanceName,
            origin: () => bootOrigin.SourcePath,
            link: bootLink,
            server: bootServer,
            ownedMachines: null
        );
        m_roster.ConfigureLeave(leave: LeaveRosterSeat);
    }

    /// <summary>Every running instance's name, ordinal-sorted — the boot instance included.</summary>
    public IReadOnlyList<string> Names => [.. m_instances.Keys.Order(comparer: StringComparer.Ordinal)];

    /// <summary>Looks up a running instance by name.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="instance">The instance, when found.</param>
    /// <returns>Whether an instance is running under <paramref name="name"/>.</returns>
    public bool TryGet(string name, out WorldInstance? instance) => m_instances.TryGetValue(key: name, value: out instance);

    /// <summary>Resolves the world definition local seat <paramref name="slot"/> currently presents from, per its
    /// live <see cref="WorldSeatInstanceRouter"/> route — the one structure source every drag-time/read-back
    /// consumer that is not already sitting inside the routed instance's own context (unlike
    /// <see cref="AwaySeatSceneEmitter"/>, which already holds the destination's mirror directly) reads
    /// through, so a boot-anchored seat and a traveling one never derive "which document currently frames me" two
    /// different ways. <see cref="WorldSeatViewInput"/> (the live drag clamp) and <c>WorldViewCommandModule</c>'s
    /// <c>world.view.camera</c> echo are today's two callers.</summary>
    /// <param name="slot">The 0-based local roster slot.</param>
    /// <returns>The routed instance's own definition, or <see cref="WorldClient.Definition"/> (the boot document)
    /// for a boot-routed seat, an out-of-range slot, or a route naming an instance that has since stopped — the same
    /// defensive fallback <see cref="LeaveRosterSeat"/> already applies to a stale route.</returns>
    public WorldDefinition ResolveRoutedDefinition(int slot) {
        var location = m_seatRouter.Location(slot: slot);

        if (string.Equals(a: location.InstanceName, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
            return m_client.Definition;
        }

        if (m_remoteAuthorities.TryGetValue(key: location.InstanceName, value: out var remote)) {
            return remote.Definition;
        }

        return (m_instances.TryGetValue(key: location.InstanceName, value: out var instance) && (instance is not null)
            ? instance.Server.Definition
            : m_client.Definition);
    }

    /// <summary>Resolves a shared presentation consumer from one running source instance through the same scoped
    /// session identity portal entry uses. Presentation currently has no viewer identity, so only global
    /// destinations are admissible. Persisted destinations adopt an unambiguous running instance with the same
    /// document origin before minting, preserving the "return means home" rule for a view as well as a traveler.</summary>
    public bool TryResolveObservedDestination(WorldInstance source, string destinationName, out WorldInstance? target, out WorldSessionResolver.Resolved resolved, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: destinationName);

        target = null;
        resolved = default;

        if (WorldDefinitionRows.FindDestination(destinations: source.Server.Definition.Destinations, name: destinationName) is not { } destination) {
            reason = $"destination '{destinationName}' names no destinations row";

            return false;
        }

        if (destination.Scope != WorldDestinationScope.Global) {
            reason = "viewer-scoped destination on a shared screen surface awaits per-viewport binding work";

            return false;
        }

        if (WorldDefinitionRows.FindReference(references: source.Server.Definition.References, name: destination.Reference) is not { } reference) {
            reason = $"destination '{destinationName}' names references row '{destination.Reference}', which does not exist";

            return false;
        }

        var cohort = new[] { new WorldSessionResolver.CohortMember(Principal: WorldPrincipal.Seat(slot: 0), IdentityId: null) };
        var referencedDocument = ResolveReferenceDocument(source: source, documentPath: reference.Document);
        var canonicalDocument = CanonicalDocumentIdentity(documentPath: referencedDocument);

        if ((destination.Durability == WorldDestinationDurability.Persisted) &&
            !m_resolver.TryGetActive(destinationName: destination.Name.Value, durability: destination.Durability, scopeKey: WorldSessionResolver.GlobalScopeKey, referencedDocument: canonicalDocument, resolved: out _)) {
            if (TryFindRunningInstanceByOrigin(documentPath: referencedDocument, matchedName: out var matchedName, ambiguous: out var ambiguousNames)) {
                if (!m_resolver.TryAdopt(destination: destination, scopeKey: WorldSessionResolver.GlobalScopeKey, referencedDocument: canonicalDocument, instanceName: matchedName, resolved: out _, reason: out reason)) {
                    return false;
                }
            } else if (ambiguousNames is { Count: > 1 }) {
                reason = $"destination '{destinationName}' resolves document '{reference.Document}', matching {ambiguousNames.Count} running instances [{string.Join(separator: ",", values: ambiguousNames)}] by origin — ambiguous, refused rather than adopting one arbitrarily";

                return false;
            }
        }

        if (!m_resolver.TryResolve(sourceDefinition: source.Server.Definition, destination: destination, referencedDocument: canonicalDocument, cohort: cohort, resolved: out resolved, reason: out reason)) {
            return false;
        }

        if (m_instances.TryGetValue(key: resolved.InstanceName, value: out target)) {
            reason = string.Empty;

            return true;
        }

        if (ResolveByStableName(name: resolved.InstanceName, documentPath: referencedDocument, retain: (destination.Durability == WorldDestinationDurability.Persisted), resolved: out target, resolvedName: out _, spawned: out _, reason: out reason)) {
            return true;
        }

        m_resolver.AbortGeneration(instanceName: resolved.InstanceName);

        return false;
    }

    /// <summary>Finds the local roster seat currently following one concrete instance-local seat. This is the
    /// instance-addressed <c>player.leave</c> join: a raw instance leave must not bypass the roster/router half when
    /// the named body is the local traveler's current embodiment.</summary>
    public bool TryFindFollowedRosterSlot(string instanceName, int instanceSlot, out int rosterSlot) {
        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            var location = m_seatRouter.Location(slot: slot);

            if ((m_roster.Seat(slot: slot) is not null) &&
                string.Equals(a: location.InstanceName, b: instanceName, comparisonType: StringComparison.Ordinal) &&
                (location.InstanceSlot == instanceSlot)) {
                rosterSlot = slot;

                return true;
            }
        }

        rosterSlot = -1;

        return false;
    }

    // The ONE local-roster departure transaction. PlayerRoster routes both explicit leaves and device-orphan
    // dissolves here after its ordinary slot/occupancy guard. The authoritative body leaves the CURRENT routed
    // instance first; only an accepted reply clears held input, vacates the local participant, and resets the
    // presentation route. Reaping runs last, after the route no longer makes TryStop's traveler guard fire.
    private bool LeaveRosterSeat(int rosterSlot, WorldPrincipal actingPrincipal) {
        var location = m_seatRouter.Location(slot: rosterSlot);

        if (!m_instances.TryGetValue(key: location.InstanceName, value: out var instance)) {
            // Defensive repair for a stale route produced by an older build: the instance is already gone, so no
            // authoritative body remains to leave. Retire the local half instead of making the slot impossible to
            // reuse forever.
            _ = m_router().ClearSlotHeld(slot: rosterSlot);
            _ = m_roster.VacateSeat(slot: rosterSlot);
            m_seatRouter.Publish(slot: rosterSlot, instanceName: BootInstanceName, instanceSlot: rosterSlot);
            Console.Error.WriteLine(value: $"[player.leave: repaired stale route for player {(rosterSlot + 1)} — instance '{location.InstanceName}' no longer exists]");

            return true;
        }

        var accepted = false;

        instance.Link.SubmitSession(
            request: new SessionRequest.Leave(Principal: actingPrincipal, Slot: location.InstanceSlot),
            completion: reply => {
                if (!reply.Accepted) {
                    Console.Error.WriteLine(value: $"[player.leave denied: '{location.InstanceName}' seat {(location.InstanceSlot + 1)} — {reply.Reason}]");

                    return;
                }

                accepted = true;
            }
        );

        if (!accepted) {
            return false;
        }

        _ = m_router().ClearSlotHeld(slot: rosterSlot);
        _ = m_roster.VacateSeat(slot: rosterSlot);
        m_seatRouter.Publish(slot: rosterSlot, instanceName: BootInstanceName, instanceSlot: rosterSlot);

        if (!string.Equals(a: location.InstanceName, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
            _ = ReapIfEmpty(name: location.InstanceName);
        }

        return true;
    }

    /// <summary>Looks up a running instance's own transport (traveler-follow stage 1) — <c>WorldClient</c>'s
    /// away-seat intent submission door and an away view's mirror attach both resolve through this rather than a
    /// container singleton, since every instance (boot included — see <see cref="WorldInstance.Link"/>'s own
    /// remarks) now carries its own uniformly.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="link">The instance's transport, when found.</param>
    /// <returns>Whether an instance is running under <paramref name="name"/>.</returns>
    public bool TryGetLink(string name, out IServerLink? link) {
        if (m_remoteAuthorities.TryGetValue(key: name, value: out var authority)) {
            link = authority.Link;

            return true;
        }

        if (m_instances.TryGetValue(key: name, value: out var instance)) {
            link = instance.Link;
            return true;
        }

        link = null;

        return false;
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

        // The name is a directory segment, not just a label — it is the one component of this instance's
        // owned-worlds path. A name carrying a separator, a drive, or a traversal step would choose where the
        // instance's documents are written, so WorldSafeName refuses those by construction (empty, a reserved
        // character, or a bare '.'/'..'); there is no separate segment-safety re-check downstream.
        if (!WorldSafeName.TryParse(candidate: name, name: out _, reason: out var nameReason)) {
            reason = $"'{name}' is not a single safe path segment — the name IS the directory this instance's owned worlds live in, and {nameReason}";

            return false;
        }

        if (string.Equals(a: name, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
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

        if (!ownedWorlds.StartsWith(value: instancesRoot, comparisonType: PathComparison)) {
            reason = $"'{name}' resolves its owned worlds to {ownedWorlds}, outside the instances root {instancesRoot}";

            return false;
        }

        if (!TryResolveDocumentPath(path: path, resolved: out var resolvedPath)) {
            reason = $"no file at '{path}', either as given or under {AppContext.BaseDirectory}";

            return false;
        }

        // A file-backed neighbour resolver, beside the instance document itself. A quilt neighbour started
        // this way clears the same cross-document border-margin proof a top-level --world boot does (see
        // WorldDefinitionLoader.TryResolve). No cloud-backed half here; a neighbour reachable only through
        // the cloud refuses by name like any other unreachable resolver.
        var instanceNeighbours = new WorldFileNeighbourResolver(baseDirectory: () => Path.GetDirectoryName(path: resolvedPath) is { Length: > 0 } instanceDirectory ? instanceDirectory : AppContext.BaseDirectory);

        // The instance's own NAME is the seed ladder's instance rung, so two instances of one document draw
        // independently while each stays reproducible from (document, instance name, draw history).
        if (!WorldDefinitionLoader.TryLoadFile(path: resolvedPath, definition: out var definition, reason: out reason, instanceIdentity: name, neighbours: instanceNeighbours)) {
            return false;
        }

        var machines = new WorldMachineHost(screens: [], engines: []);
        WorldInstance started;

        // Construction touches the file system (the owned-world store creates its directory and seeds documents into
        // it). This runs on the FIXED-STEP THREAD — world.instance.start routes Simulation — where an escaping
        // exception kills the pump and takes every world in the process down with it, the boot world included. An IO
        // failure here is a refusal like any other; nothing about it is worth the whole session.
        try {
            var server = new WorldServer(
                definition: definition!,
                population: new WorldPopulation(definition: definition!),
                profiles: new WorldOwnedWorlds(template: definition!, directory: ownedWorlds, machineId: m_machineId, neighbours: new WorldFileNeighbourResolver(baseDirectory: () => ownedWorlds)),
                envelope: new WorldRenderEnvelope(),
                machines: machines,
                instanceIdentity: name
            );
            var borderMargin = new WorldBorderMarginFields(instances: this, sourceInstanceName: name);

            server.Neighbours = instanceNeighbours;
            server.BorderMargin = borderMargin;

            // The identical two-line pattern WorldBootComposition wires for the boot instance's own transport (a
            // WorldServer implements IWorldServerHost directly — see LoopbackTransport's own remarks) — one
            // transport per instance uniformly, never a special case reserved for boot (traveler-follow stage 1).
            started = new WorldInstance(
                name: name,
                origin: () => resolvedPath,
                server: server,
                ownedMachines: machines,
                link: new LoopbackTransport(server: server),
                ownedBorderMargin: borderMargin
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException)) {
            machines.Dispose();
            reason = $"'{name}' could not open its owned-world store at {ownedWorlds} — {exception.Message}";

            return false;
        }

        m_instances[name] = started;
        instance = started;
        reason = string.Empty;

        return true;
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
        if (string.Equals(a: name, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
            return false;
        }

        if (m_retainedInstances.Contains(item: name)) {
            return false;
        }

        if (!m_instances.TryGetValue(key: name, value: out var instance) || (instance.Server.Population.ActiveCount() > 0)) {
            return false;
        }

        return TryStop(name: name, reason: out _);
    }

    /// <summary>Retires a running instance and disposes what it owned. The boot instance is refused: retiring it
    /// would leave the container's client, seats, tape and console verbs holding a server nothing steps.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="reason">The refusal reason on failure.</param>
    /// <returns><see langword="true"/> when the instance was retired.</returns>
    public bool TryStop(string name, out string reason) {
        if (string.Equals(a: name, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
            reason = $"'{BootInstanceName}' is the world this process booted with — close the process to stop it";

            return false;
        }

        if (!m_instances.TryGetValue(key: name, value: out var instance)) {
            reason = $"no instance named '{name}'";

            return false;
        }

        // A followed local seat keeps its roster participant/device binding while its body lives in this instance.
        // Removing the instance first would leave the router pointing at a name nothing steps, so no input or portal
        // crossing could ever bring that participant home. Automatic reaping reaches this method only AFTER a
        // committed transfer has republished the router, so this guard affects the explicit operator stop alone.
        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            var location = m_seatRouter.Location(slot: slot);

            if ((m_roster.Seat(slot: slot) is not null) && string.Equals(a: location.InstanceName, b: name, comparisonType: StringComparison.Ordinal)) {
                reason = $"'{name}' is presenting local seat {(slot + 1)} — transfer that traveler out before stopping the instance";

                return false;
            }
        }

        _ = m_instances.Remove(key: name);

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

    /// <summary>The directory an instance's owned worlds live under — derived from its name so two instances never
    /// share a store, and reported by <c>world.instance.status</c> so the placement is read back rather than
    /// inferred. Normalized, so the answer is where files actually land rather than the spelling that got there;
    /// <see cref="TryStart"/> refuses any name whose answer escapes the instances root.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>The absolute owned-worlds directory for that instance.</returns>
    public string OwnedWorldsDirectory(string name) =>
        Path.GetFullPath(path: (string.Equals(a: name, b: BootInstanceName, comparisonType: StringComparison.Ordinal)
            ? Path.Combine(path1: m_stateRoot, path2: "owned-worlds")
            : Path.Combine(path1: InstancesRoot(), path2: name, path3: "owned-worlds")));

    /// <summary>Advances every instance except the boot one on its own authored schedule — the boot instance is
    /// handled by <see cref="WorldServerStepShell"/> separately (see <see cref="ShouldStepBoot"/>), which also
    /// carries the tape/wait-gate/socket bookkeeping only it needs. Each instance banks <paramref name="masterDeltaTicks"/>
    /// into its own <see cref="WorldInstance.ScheduleAccumulatorTicks"/> and steps once per whole crossing of its own
    /// step width (<c>50400 / rateHz</c> engine ticks) — a rate faster than the master cadence steps more than once
    /// per call, a rate slower steps less than once. An instance whose authored rate is the durable stop (0), or
    /// whose live <see cref="WorldInstance.IsPaused"/> lever holds it, banks nothing (the accumulator holds exactly
    /// where it is, so a later resume continues on the identical schedule with no skew) but still receives an
    /// administrative drain (<see cref="Server.WorldServer.DrainAdministrative"/>) so a buffered document mutation
    /// can still apply — never a permanent self-lock.</summary>
    /// <remarks><b>Portal scan cadence, per step.</b> Every actual
    /// <see cref="Server.WorldServer.Step"/> call below is followed immediately by
    /// <see cref="ScanInstancePortals"/> for that same instance, reading its own just-settled post-step state —
    /// never a single scan of all instances once per master call: scanning per step keeps the trigger's own
    /// slab-depth argument intact (per-scan displacement equals per-step displacement, never per-master-call
    /// displacement) for an instance that steps several times per call, and
    /// means a non-stepping instance is never scanned — a pre-pause "inside" state stays latched in
    /// <see cref="WorldInstance.PortalOccupancy"/> exactly where the pause caught it, firing only once resume
    /// produces a genuine new edge. A transfer a step enqueues here still drains at this host's one fixed drain
    /// point (<see cref="DrainPendingTransfers"/>, called once per master call, before any instance steps) — the
    /// next master call's drain, for a transfer enqueued by a step that happened during this one, which is the
    /// honest cross-world semantics: a transfer is a host act, not a step-local one.</remarks>
    /// <param name="masterDeltaTicks">The host's own master timeline advance for this call — the same quantum the
    /// fixed-step pump already produced (<see cref="FixedStepContext.StepTicks"/> from the call that invoked this),
    /// never a second clock this type samples on its own.</param>
    public void StepInstancesBesideBoot(ulong masterDeltaTicks) {
        if (m_instances.Count < 2) {
            return;
        }

        // Ordinal name order, so the step sequence is a property of the names rather than of insertion history.
        // Instances never observe one another, so the order carries no cross-instance meaning — only each one's own
        // trajectory matters, and that is what makes iterating a hash map by sorted key honest here.
        foreach (var name in Names) {
            if (string.Equals(a: name, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            var instance = m_instances[name];
            var rateHz = instance.Server.Definition.SimulationRateHz;

            if ((rateHz <= 0) || instance.IsPaused) {
                _ = instance.Server.DrainAdministrative();

                continue;
            }

            instance.ScheduleAccumulatorTicks += masterDeltaTicks;

            var stepWidth = EngineTicks.PerRate(ratePerSecond: (uint)rateHz);

            while (instance.ScheduleAccumulatorTicks >= stepWidth) {
                instance.ScheduleAccumulatorTicks -= stepWidth;

                var tick = instance.CompletedTicks;
                // Accumulated, never re-derived from (tick + 1) * stepWidth — a product re-derivation breaks
                // the instant this instance's own rate changes (see WorldInstance.ElapsedEngineTicks).
                var elapsedTicks = (instance.ElapsedEngineTicks + stepWidth);
                var context = new FixedStepContext(ElapsedTicks: elapsedTicks, StepTicks: stepWidth, Tick: tick);

                // An away-routed seat's device intent is submitted here, immediately before this instance's
                // own Step call, at this instance's own next-tick coordinate (tick + 1) — never boot's clock.
                // A fast instance stepping several times per master call submits fresh input before each step.
                m_client.SubmitAwaySeatIntents(instanceName: name, tick: (tick + 1UL), link: instance.Link, definition: instance.Server.Definition);

                instance.Server.Step(context: in context);
                instance.ElapsedEngineTicks = elapsedTicks;
                ScanInstancePortals(instance: instance);

                // Server.Step installs any pending definition swap (world.load/.reset/.reload) before
                // advancing, so a mid-batch rate change makes the cached stepWidth stale for further
                // iterations. Stop the batch here instead — the leftover ScheduleAccumulatorTicks carries
                // over untouched to the next StepInstancesBesideBoot call, which reads the fresh rate and
                // resumes correctly; tick numbering and ElapsedEngineTicks stay contiguous across the
                // boundary since neither is reset by this break.
                if (instance.Server.Definition.SimulationRateHz != rateHz) {
                    break;
                }
            }
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
        if (!m_instances.TryGetValue(key: BootInstanceName, value: out var boot)) {
            // Unreachable in practice — the constructor always admits the boot instance — but a registry that could
            // somehow lose it should refuse to step rather than throw mid-tick.
            return false;
        }

        var rateHz = boot.Server.Definition.SimulationRateHz;

        return (!boot.IsPaused && (rateHz > 0) && (stepTicks == EngineTicks.PerRate(ratePerSecond: (uint)rateHz)));
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
        if (!m_instances.TryGetValue(key: name, value: out var instance)) {
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
        if (!m_instances.TryGetValue(key: name, value: out var instance)) {
            wasPaused = false;
            reason = $"no instance named '{name}'";

            return false;
        }

        wasPaused = instance.IsPaused;
        instance.IsPaused = false;
        reason = string.Empty;

        return true;
    }

    /// <summary>Read-back for <c>world.rate</c>: one instance's declared rate, live schedule state, step width and
    /// completed ticks — the boot instance included, under its reserved name.</summary>
    /// <param name="name">The instance name.</param>
    /// <param name="status">The described status, when found.</param>
    /// <param name="reason">The refusal reason on failure (an unknown name only).</param>
    /// <returns><see langword="true"/> when the instance exists.</returns>
    public bool TryDescribeRate(string name, out WorldInstanceRateStatus status, out string reason) {
        if (!m_instances.TryGetValue(key: name, value: out var instance)) {
            status = default;
            reason = $"no instance named '{name}'";

            return false;
        }

        var rateHz = instance.Server.Definition.SimulationRateHz;
        var stopped = (rateHz <= 0);

        status = new WorldInstanceRateStatus(
            RateHz: rateHz,
            Stopped: stopped,
            Paused: instance.IsPaused,
            StepWidthTicks: (stopped ? (ulong?)null : EngineTicks.PerRate(ratePerSecond: (uint)rateHz)),
            CompletedTicks: instance.CompletedTicks
        );
        reason = string.Empty;

        return true;
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
    internal readonly record struct WorldInstanceRateStatus(int RateHz, bool Stopped, bool Paused, ulong? StepWidthTicks, ulong CompletedTicks);

    /// <summary>How a queued transfer's destination instance is resolved at drain time — see
    /// <see cref="TransferDestination"/> for the per-case payload and <see cref="TryResolveDestination"/> for the
    /// resolution itself.</summary>
    internal enum TransferLifetime {
        /// <summary>The target must already be running under a given name (<c>world.instance.start</c> first) — the
        /// original, step-1 form. Refused by name when no instance answers to it.</summary>
        Existing,

        /// <summary>A brand-new instance, deterministically named from a site plus this host's per-site draw counter
        /// (see <see cref="MintFreshInstanceName"/>) — a fresh transfer is a new draw roll for that destination.
        /// Reaped like any other transient instance once its last occupant leaves (never retained).</summary>
        Fresh,

        /// <summary>A stable, caller-named instance: started from the destination document if not already running,
        /// else reused as-is. Retained (see <see cref="m_retainedInstances"/>) from the moment a transfer resolves
        /// it — two transfers naming the same persistent instance are two doors into one place, and the second must
        /// find the first traveler's instance still standing even if it is momentarily empty.</summary>
        Persistent,

        /// <summary>A name already computed by <see cref="WorldSessionResolver.TryResolve"/> — started from the
        /// destination document if not already running, else reused as-is, exactly like <see cref="Persistent"/>,
        /// but retained only when the resolved destination's own <see cref="WorldDestinationDurability"/> is
        /// <see cref="WorldDestinationDurability.Persisted"/> (see <see cref="TransferDestination.Retain"/>) — an
        /// Ephemeral-durability resolution reaps normally through the ordinary <see cref="ReapIfEmpty"/> rule the
        /// moment its occupancy hits zero, which is what lets <see cref="WorldSessionResolver.NotifyInstanceRetired"/>
        /// observe the generation actually ending (docs/world-model.md "Durability, scope and generation").</summary>
        Resolved,
    }

    /// <summary>A queued transfer's destination, as the console verb expressed it — resolved to a live
    /// <see cref="WorldInstance"/> exactly once per transfer by <see cref="TryResolveDestination"/> (a <c>party</c>
    /// transfer's whole member set shares that one resolution, so a <see cref="TransferLifetime.Fresh"/> destination
    /// mints its name once for the whole party, never once per body).</summary>
    internal readonly record struct TransferDestination {
        private TransferDestination(TransferLifetime lifetime, string? name, string? documentPath, string? site, bool retain, string? authority) {
            Lifetime = lifetime;
            Name = name;
            DocumentPath = documentPath;
            Site = site;
            Retain = retain;
            Authority = authority;
        }

        /// <summary>How this destination resolves.</summary>
        public TransferLifetime Lifetime { get; }

        /// <summary>The caller-named instance name — set for <see cref="TransferLifetime.Existing"/>,
        /// <see cref="TransferLifetime.Persistent"/>, and <see cref="TransferLifetime.Resolved"/>,
        /// <see langword="null"/> for <see cref="TransferLifetime.Fresh"/> (whose name is minted, never named).</summary>
        public string? Name { get; }

        /// <summary>The world document to start the instance from if it is not already running — set for
        /// <see cref="TransferLifetime.Fresh"/>, <see cref="TransferLifetime.Persistent"/>, and
        /// <see cref="TransferLifetime.Resolved"/>.</summary>
        public string? DocumentPath { get; }

        /// <summary>The site identifier a <see cref="TransferLifetime.Fresh"/> destination's name is drawn under —
        /// see <see cref="MintFreshInstanceName"/>.</summary>
        public string? Site { get; }

        /// <summary>Whether a <see cref="TransferLifetime.Resolved"/> destination is retained through an occupancy
        /// dip to zero (see <see cref="m_retainedInstances"/>) — ignored for every other lifetime, which each carry
        /// their own fixed retention rule.</summary>
        public bool Retain { get; }

        /// <summary>An operator-selected remote authority for this run, overriding the document endpoint.</summary>
        public string? Authority { get; }

        /// <summary>An already-running instance named <paramref name="name"/> — refused at resolve time if none
        /// answers to it.</summary>
        public static TransferDestination Existing(string name) => new(lifetime: TransferLifetime.Existing, name: name, documentPath: null, site: null, retain: false, authority: null);

        /// <summary>A brand-new instance, deterministically named from <paramref name="site"/>'s draw counter and
        /// started from <paramref name="documentPath"/>.</summary>
        public static TransferDestination Fresh(string site, string documentPath) => new(lifetime: TransferLifetime.Fresh, name: null, documentPath: documentPath, site: site, retain: false, authority: null);

        /// <summary>A stable instance named <paramref name="name"/> — reused if already running, else started from
        /// <paramref name="documentPath"/>.</summary>
        public static TransferDestination Persistent(string name, string documentPath) => new(lifetime: TransferLifetime.Persistent, name: name, documentPath: documentPath, site: null, retain: false, authority: null);

        /// <summary>A name already computed by <see cref="WorldSessionResolver.TryResolve"/> — reused if already
        /// running, else started from <paramref name="documentPath"/>; retained through an occupancy dip to zero
        /// only when <paramref name="retain"/> (the resolved destination's own durability being Persisted).</summary>
        public static TransferDestination Resolved(string name, string documentPath, bool retain) => new(lifetime: TransferLifetime.Resolved, name: name, documentPath: documentPath, site: null, retain: retain, authority: null);

        /// <summary>The normal boot composition routed through a remote authority selected for this run.</summary>
        public static TransferDestination Remote(string name, string documentPath, string authority) => new(lifetime: TransferLifetime.Resolved, name: name, documentPath: documentPath, site: null, retain: true, authority: authority);
    }

    /// <summary>Which of a source instance's local seats a queued transfer moves — see
    /// <see cref="PendingTransfer.Scope"/>.</summary>
    internal enum TransferScope {
        /// <summary>One named seat.</summary>
        Body,

        /// <summary>The source instance's whole active local-seat set (0..<see cref="Server.WorldPopulation.LocalSeatCount"/>-1),
        /// computed from live state at drain time, landing together in one destination — never one instance per
        /// member.</summary>
        Party,
    }

    /// <summary>One same-process body (or party) transfer queued for this host's one fixed drain point (see
    /// <see cref="DrainPendingTransfers"/>) — captured at enqueue time as the request shape only. Every live-state
    /// check (both instances still running, the source seat(s) still active, a free destination seat, Drive
    /// authority) runs at drain time against whatever state that tick actually holds, mirroring
    /// <see cref="Server.WorldServer"/>'s own pending-ops FIFO (compose/validate at apply, never at submit).</summary>
    /// <param name="SourceInstance">The console-facing name of the instance the seat(s) currently occupy.</param>
    /// <param name="Scope">Whether this moves one named seat or the source's whole active local-seat set.</param>
    /// <param name="SourceSlot">The source instance's 0-based local seat — ignored when <paramref name="Scope"/> is
    /// <see cref="TransferScope.Party"/>.</param>
    /// <param name="Destination">How the destination instance resolves.</param>
    /// <param name="ActingPrincipal">The principal that submitted the transfer.</param>
    /// <param name="ResolvedDestinationRow">The destination row a <see cref="WorldSessionResolver.TryResolve"/> call
    /// proved this cohort against at scan time — populated only for <see cref="TransferLifetime.Resolved"/> (a
    /// diegetic portal crossing; <see cref="Puck.World.WorldInstanceCommandModule"/>'s console <c>world.transfer</c>
    /// never touches the resolver at all). This is what lets <see cref="ApplyTransfer"/>
    /// re-verify the frozen scope key and, if the cached instance no longer runs, re-resolve through the resolver
    /// rather than guessing.</param>
    /// <param name="FrozenCohortSlots">The exact local-seat slots the resolve proved — a <c>body</c> crossing's own
    /// single entering seat, or a <c>party</c> crossing's whole active local-seat set as it stood at scan time.
    /// <see cref="ApplyTransfer"/> applies to exactly this frozen set rather than recomputing it live at drain
    /// (a cohort TOCTOU fix) — a member no longer active by drain time still refuses that
    /// member's own move by name, exactly as before; nothing here changes who is allowed to travel, only where the
    /// set of "who" is read from. <see langword="null"/> for a non-resolver transfer.</param>
    /// <param name="FrozenScopeKey">The scope key the resolve produced — re-derived live from
    /// <see cref="FrozenCohortSlots"/>' still-active members immediately before this transfer applies; a mismatch
    /// refuses the whole transfer (membership drifted between scan and drain, so the frozen proof no longer holds).
    /// <see langword="null"/> for a non-resolver transfer.</param>
    /// <param name="FrozenGenerationId">The resolver-issued generation id the resolve produced — carried purely for
    /// <see cref="ApplyTransfer"/>'s own tape narration (<see cref="WorldReplayTape.NoteTransfer"/>); never
    /// re-verified (the scope-key re-derivation above is what proves the resolution still holds).
    /// <see langword="null"/> for a non-resolver transfer.</param>
    /// <param name="TransferId">The transfer id this particular queued crossing carries — minted deterministically at
    /// enqueue time (docs/world-model.md) unless a caller supplied one explicitly (console
    /// <c>world.transfer</c>'s <c>transfer:&lt;id&gt;</c> token, the retry/idempotence verification seam). Threaded
    /// through every echo this transfer produces and checked against <see cref="m_appliedTransferIds"/> before
    /// anything else at drain time.</param>
    /// <param name="TestForceJoinRefusalOrdinal">Test-only (see <see cref="ApplyTransfer"/>'s own remarks on why a
    /// live document-authored join refusal is unreachable once reservation pre-checks capacity and destination Drive
    /// standing): when set, the N-th (1-based, in <see cref="FrozenCohortSlots"/>/member order) member's destination
    /// join is forced to refuse once, exercising the abort/rollback path directly. Only ever set by console
    /// <c>world.transfer</c>'s <c>forcejoinrefusal:&lt;n&gt;</c> token — never by a diegetic portal crossing.</param>
    /// <param name="Arrival">Where each landed member's own pose lands (see <c>Puck.World.WorldPlacementPortal.Arrival</c>).
    /// Default <c>Spawn</c> — the destination's ordinary seat spawn point, unchanged for a non-resolver transfer
    /// (console <c>world.transfer</c> never authors mapped arrival).</param>
    /// <param name="Counterpart">The destination document's border placementId/face a <c>Mapped</c> arrival maps
    /// onto (see <c>Puck.World.WorldPortalCounterpart</c>) — resolved against the destination's own delivered
    /// definition at drain time, never at scan time. <see langword="null"/> for <c>Spawn</c>.</param>
    /// <param name="SourceSeamPosition">The source portal face's own seam point — F_s, <c>Frame.PointAt(SeamU,
    /// SeamV)</c> captured at scan time from <see cref="WorldFaceCatalog"/>/<see cref="WorldFaceRegion.Sweep"/>, so a
    /// traveler leaves from the exact point its segment crossed the face rather than from the door's own center.
    /// Ignored for <c>Spawn</c>.</param>
    /// <param name="SourceFaceYawRadians">The source portal face's own frame heading — F_s. Ignored for
    /// <c>Spawn</c>.</param>
    /// <param name="SourceFaceSeamU">The captured crossing's own in-plane coordinate along the source frame's
    /// <c>Right</c> — carried alongside <see cref="SourceSeamPosition"/> so the destination side can apply the same
    /// coordinate to the counterpart's own frame, the mapped image of the source seam rather than a fresh sample.
    /// Ignored for <c>Spawn</c>.</param>
    /// <param name="SourceFaceSeamV">The captured crossing's own in-plane coordinate along the source frame's
    /// <c>Up</c>. Ignored for <c>Spawn</c>.</param>
    /// <param name="MemberSeams">Per-member seam overrides, keyed by source slot — a member with an entry here maps
    /// through the exact point its own crossing swept (<see cref="MemberSeam"/>) rather than
    /// <see cref="SourceSeamPosition"/>/<see cref="SourceFaceSeamU"/>/<see cref="SourceFaceSeamV"/>, which remain the
    /// fallback for a cohort member with no entry — a <c>party</c>-travel passenger swept along without personally
    /// crossing the aperture. <see langword="null"/> for a non-resolver transfer (console <c>world.transfer</c>,
    /// which carries no per-hit data at all) or <c>Spawn</c>.</param>
    /// <param name="HoldSeconds">The authored binding lease duration.</param>
    /// <param name="FullPolicy">The authored full-border retry policy.</param>
    /// <param name="PartyAllOrNothing">Whether the cohort binds as one transaction.</param>
    /// <param name="BorderCapacity">The optional authored capacity for this border.</param>
    /// <param name="Border">The stable source border identity used by destination admission.</param>
    /// <param name="ScopeProofAlreadyVerified">Internal split-party marker: the parent already re-verified the
    /// frozen cohort's membership proof before creating one-member transactions against its one resolved target.</param>
    private readonly record struct PendingTransfer(
        string SourceInstance,
        TransferScope Scope,
        int SourceSlot,
        TransferDestination Destination,
        WorldPrincipal ActingPrincipal,
        WorldDestination? ResolvedDestinationRow,
        IReadOnlyList<int>? FrozenCohortSlots,
        string? FrozenScopeKey,
        ulong? FrozenGenerationId,
        ulong TransferId,
        int? TestForceJoinRefusalOrdinal,
        WorldPortalArrival Arrival,
        string? Counterpart,
        FixedVector3 SourceSeamPosition,
        FixedQ4816 SourceFaceYawRadians,
        FixedQ4816 SourceFaceSeamU,
        FixedQ4816 SourceFaceSeamV,
        IReadOnlyDictionary<int, MemberSeam>? MemberSeams,
        double HoldSeconds,
        WorldTransferFullPolicy FullPolicy,
        bool PartyAllOrNothing,
        int? BorderCapacity,
        string Border,
        bool ScopeProofAlreadyVerified = false
    );

    private readonly Queue<PendingTransfer> m_pendingTransfers = new();
    private readonly List<InDoubtTransfer> m_inDoubtTransfers = [];
    // Every transfer id this host has drained (committed or aborted). A pure function of enqueue/drain
    // order, never wall-clock or RNG — checked first in ApplyTransfer so a retry-shaped duplicate (the same
    // id resubmitted, e.g. world.transfer's transfer:<id> token) refuses by name rather than double-landing.
    // A diegetic portal crossing always mints a fresh id, so only an explicitly supplied id can collide.
    private readonly HashSet<(string SourceInstance, ulong TransferId)> m_appliedTransferIds = new();
    // Advances by exactly one per EnqueueTransfer call — a pure function of enqueue order, never wall-clock,
    // RNG, or tick-of-entry. Separate from the resolver's own generation id: a transfer id names one
    // crossing attempt, a generation id names the destination session many crossings can share. Sits
    // outside the boot-only replay tape; m_bootReplayTape.NoteTransfer records a minted id's later decided
    // outcome, never the minting act itself.
    private ulong m_nextTransferId;

    // Deterministic, resolver-ordered — the ONE place a transfer id is minted.
    private ulong MintTransferId() => m_nextTransferId++;

    private ulong MintUnappliedTransferId(string sourceInstance) {
        ulong transferId;
        do {
            transferId = MintTransferId();
        } while (m_appliedTransferIds.Contains(item: (sourceInstance, transferId)));

        return transferId;
    }

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

    /// <summary>Queues a same-process transfer for this host's next <see cref="DrainPendingTransfers"/> call —
    /// <c>world.transfer</c> is the only caller today. Enqueuing never fails: every check that can refuse (an
    /// unknown or unstartable instance, an out-of-range/empty/absent source seat, no free destination seat, a denied
    /// Drive grant) runs at drain time, so a refusal is reported once, at the same fixed point the transfer would
    /// otherwise have applied at — exactly like a rejected <see cref="Server.WorldServer"/> mutation.</summary>
    /// <param name="sourceInstance">The console-facing name of the instance the seat(s) currently occupy.</param>
    /// <param name="scope">Whether this moves one named seat or the source's whole active local-seat set.</param>
    /// <param name="sourceSlot">The source instance's 0-based local seat — ignored when <paramref name="scope"/> is
    /// <see cref="TransferScope.Party"/> (the member set is read live at drain time instead).</param>
    /// <param name="destination">How the destination instance resolves — see <see cref="TransferDestination"/>.</param>
    /// <param name="actingPrincipal">The principal that submitted the transfer — threaded unchanged through both the
    /// leave-side Drive check and the destination's own <c>ApplySession(Join)</c> for every member, so each
    /// arrival's authority is attributed to the same principal that left rather than a principal this door
    /// invents.</param>
    /// <param name="resolvedDestinationRow">The destination row a <see cref="WorldSessionResolver.TryResolve"/> call
    /// proved <paramref name="frozenCohortSlots"/> against — see <see cref="PendingTransfer.ResolvedDestinationRow"/>.
    /// Omit for a non-resolver transfer (console <c>world.transfer</c>'s raw forms).</param>
    /// <param name="frozenCohortSlots">The exact local-seat slots the resolve proved — see
    /// <see cref="PendingTransfer.FrozenCohortSlots"/>. Omit for a non-resolver transfer.</param>
    /// <param name="frozenScopeKey">The scope key the resolve produced — see
    /// <see cref="PendingTransfer.FrozenScopeKey"/>. Omit for a non-resolver transfer.</param>
    /// <param name="frozenGenerationId">The resolver-issued generation id the resolve produced — see
    /// <see cref="PendingTransfer.FrozenGenerationId"/>. Omit for a non-resolver transfer.</param>
    /// <param name="explicitTransferId">An explicit transfer id to carry instead of minting a fresh one — the
    /// retry/idempotence verification seam (console <c>world.transfer</c>'s <c>transfer:&lt;id&gt;</c> token only; a
    /// diegetic portal crossing never supplies this). Omit to mint a fresh, deterministically-ordered id.</param>
    /// <param name="testForceJoinRefusalOrdinal">Test-only — see <see cref="PendingTransfer.TestForceJoinRefusalOrdinal"/>.
    /// Omit outside verification.</param>
    /// <param name="arrival">Where each landed member's own pose lands — see <see cref="PendingTransfer.Arrival"/>.
    /// Omit for the ordinary spawn arrival (console <c>world.transfer</c>'s own form).</param>
    /// <param name="counterpart">The destination document's border placementId/face a <c>Mapped</c> arrival maps
    /// onto — see <see cref="PendingTransfer.Counterpart"/>. Omit for the ordinary spawn arrival.</param>
    /// <param name="sourceSeamPosition">The source portal face's own seam point — see
    /// <see cref="PendingTransfer.SourceSeamPosition"/>. Omit for the ordinary spawn arrival.</param>
    /// <param name="sourceFaceYawRadians">The source portal face's own frame heading — see
    /// <see cref="PendingTransfer.SourceFaceYawRadians"/>. Omit for the ordinary spawn arrival.</param>
    /// <param name="sourceFaceSeamU">The captured crossing's own in-plane coordinate along the source frame's
    /// <c>Right</c> — see <see cref="PendingTransfer.SourceFaceSeamU"/>. Omit for the ordinary spawn arrival.</param>
    /// <param name="sourceFaceSeamV">The captured crossing's own in-plane coordinate along the source frame's
    /// <c>Up</c> — see <see cref="PendingTransfer.SourceFaceSeamV"/>. Omit for the ordinary spawn arrival.</param>
    /// <param name="memberSeams">Per-member seam overrides — see <see cref="PendingTransfer.MemberSeams"/>. Omit for
    /// a non-resolver transfer or the ordinary spawn arrival.</param>
    /// <param name="holdSeconds">The binding lease duration authored by the source world.</param>
    /// <param name="fullPolicy">Whether a full refusal remains retryable.</param>
    /// <param name="partyAllOrNothing">Whether the cohort binds as one transaction.</param>
    /// <param name="borderCapacity">The optional capacity authored on the crossed border.</param>
    /// <param name="border">The stable border identity used for destination admission accounting.</param>
    /// <returns>The transfer id this call's queued crossing will carry (freshly minted unless
    /// <paramref name="explicitTransferId"/> was supplied) — so a caller that wants to echo or later retry it has the
    /// value without re-deriving the enqueue order itself.</returns>
    public ulong EnqueueTransfer(string sourceInstance, TransferScope scope, int sourceSlot, TransferDestination destination, WorldPrincipal actingPrincipal, WorldDestination? resolvedDestinationRow = null, IReadOnlyList<int>? frozenCohortSlots = null, string? frozenScopeKey = null, ulong? frozenGenerationId = null, ulong? explicitTransferId = null, int? testForceJoinRefusalOrdinal = null, WorldPortalArrival arrival = WorldPortalArrival.Spawn, string? counterpart = null, FixedVector3 sourceSeamPosition = default, FixedQ4816 sourceFaceYawRadians = default, FixedQ4816 sourceFaceSeamU = default, FixedQ4816 sourceFaceSeamV = default, IReadOnlyDictionary<int, MemberSeam>? memberSeams = null, double holdSeconds = 2.0, WorldTransferFullPolicy fullPolicy = WorldTransferFullPolicy.Retry, bool partyAllOrNothing = true, int? borderCapacity = null, string? border = null) {
        var transferId = (explicitTransferId ?? MintTransferId());

        m_pendingTransfers.Enqueue(item: new PendingTransfer(SourceInstance: sourceInstance, Scope: scope, SourceSlot: sourceSlot, Destination: destination, ActingPrincipal: actingPrincipal, ResolvedDestinationRow: resolvedDestinationRow, FrozenCohortSlots: frozenCohortSlots, FrozenScopeKey: frozenScopeKey, FrozenGenerationId: frozenGenerationId, TransferId: transferId, TestForceJoinRefusalOrdinal: testForceJoinRefusalOrdinal, Arrival: arrival, Counterpart: counterpart, SourceSeamPosition: sourceSeamPosition, SourceFaceYawRadians: sourceFaceYawRadians, SourceFaceSeamU: sourceFaceSeamU, SourceFaceSeamV: sourceFaceSeamV, MemberSeams: memberSeams, HoldSeconds: holdSeconds, FullPolicy: fullPolicy, PartyAllOrNothing: partyAllOrNothing, BorderCapacity: borderCapacity, Border: (border ?? "transfer")));

        return transferId;
    }

    /// <summary>Drains every queued transfer at this host's one fixed point in its per-tick driving sequence —
    /// <c>WorldSimulation</c>/<c>HeadlessWorldSimulation</c> call this before stepping the boot instance or any other
    /// instance this tick (mirroring where <c>WorldServer.DrainPendingOps</c> sits relative to the rest of
    /// <c>WorldServer.Step</c>'s own body), so a transfer that lands this tick is reflected in both instances' very
    /// next <c>Server.Step</c> this same tick — every traveler is advanced exactly once, by whichever instance now
    /// holds it, never by both and never by neither.</summary>
    public void DrainPendingTransfers() {
        ReconcileInDoubtTransfers();

        while (m_pendingTransfers.TryDequeue(result: out var transfer)) {
            ApplyTransfer(transfer: in transfer);
        }
    }

    private void ReconcileInDoubtTransfers() {
        for (var index = 0; index < m_inDoubtTransfers.Count;) {
            var pending = m_inDoubtTransfers[index];
            var transfer = pending.Transfer;

            try {
                var status = pending.TargetAuthority.Status(sourceAuthority: pending.SourceAuthority, transferId: pending.Transfer.TransferId);

                if (status == WorldTransferStatus.Reserved) {
                    if (m_instances.TryGetValue(key: pending.Transfer.SourceInstance, value: out var source)
                        && ((source.Server.NextInputTick - 1UL) >= pending.SourceDeadlineTick)) {
                        pending.TargetAuthority.Abort(sourceAuthority: pending.SourceAuthority, transferId: pending.Transfer.TransferId);
                        status = pending.TargetAuthority.Status(sourceAuthority: pending.SourceAuthority, transferId: pending.Transfer.TransferId);
                    } else if (pending.TargetAuthority.Commit(sourceAuthority: pending.SourceAuthority, transferId: pending.Transfer.TransferId, members: pending.CommitMembers, reason: out _)) {
                        status = WorldTransferStatus.Committed;
                    } else {
                        status = pending.TargetAuthority.Status(sourceAuthority: pending.SourceAuthority, transferId: pending.Transfer.TransferId);
                    }
                }

                if (status == WorldTransferStatus.Committed) {
                    m_inDoubtTransfers.RemoveAt(index: index);
                    Console.Error.WriteLine(value: $"[world.transfer: transfer={pending.Transfer.TransferId} RESOLVED committed at '{pending.TargetName}' after an ambiguous acknowledgement]");
                    FinalizeCommittedTransfer(transfer: in transfer, targetAuthority: pending.TargetAuthority, targetName: pending.TargetName, spawned: pending.Spawned, landed: pending.Landed, memberCount: pending.MemberCount);
                    continue;
                }

                if (status == WorldTransferStatus.Missing) {
                    if (!m_instances.TryGetValue(key: pending.Transfer.SourceInstance, value: out var source)) {
                        index++;
                        continue;
                    }

                    foreach (var member in pending.Landed) {
                        source.Server.Population.RestoreDetachedSeat(slot: member.SourceSlot, profile: member.Profile, position: member.Position, yawRadians: member.Yaw, dynamicState: member.DynamicState, designations: member.Designations);
                    }

                    m_inDoubtTransfers.RemoveAt(index: index);
                    Console.Error.WriteLine(value: $"[world.transfer: transfer={pending.Transfer.TransferId} RESOLVED absent at '{pending.TargetName}' — every member restored to '{pending.Transfer.SourceInstance}' from retained recovery state]");
                    if (pending.Spawned) { ReapIfEmpty(name: pending.TargetName); }
                    NoteResolvedTransferOutcome(transfer: in transfer, sourceName: pending.Transfer.SourceInstance, targetName: pending.TargetName, outcome: "aborted:in-doubt-resolved-missing");
                    continue;
                }
            } catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException) {
                // Still ambiguous. Keep the exact recovery and commit records; the next fixed-point drain retries.
            }

            index++;
        }
    }

    /// <summary>Scans the boot instance's document for portal faces (a <see cref="WorldPlacementFace"/> carrying a
    /// <see cref="WorldPlacementPortal"/> facet) against its own active local seats, and enqueues a transfer for each
    /// edge — a seat whose body was outside the face's enterable volume last scan and is inside it now (see
    /// <see cref="WorldInstance.PortalOccupancy"/>). Called from <c>WorldSimulation</c>/<c>HeadlessWorldSimulation</c>
    /// immediately after boot's own <c>Server.Step</c> this master call (only when boot actually stepped — a caller
    /// that skipped the step because boot is paused/rate-0 must skip this call too, exactly like every other
    /// non-stepped instance is simply never scanned inside <see cref="StepInstancesBesideBoot"/>), so this reads
    /// boot's own just-settled post-step state — a pure function of that settled, replay-covered sim state — no
    /// wall-clock, RNG, or float ever reaches a decision (every comparison below runs in fixed point; the
    /// placement's authored float Position/YawDegrees are quantized to fixed point exactly once per portal per scan,
    /// the same boundary <c>Server.WorldEventFeed.CollectRegions</c> already crosses for region sensing). Every
    /// other instance's own portals are scanned per-step inside <see cref="StepInstancesBesideBoot"/> instead — see
    /// that method's own remarks on why a single once-per-master-call scan
    /// under- and over-scans a faster or paused/stopped instance.</summary>
    public void ScanBootPortalTriggers() {
        if (m_instances.TryGetValue(key: BootInstanceName, value: out var boot)) {
            ScanInstancePortals(instance: boot);
        }
    }

    // One edge-triggered portal hit, collected during a scan rather than acted on immediately — see
    // ScanInstancePortals' own remarks on why every hit in one scan is gathered before any of them resolves. Claim
    // carries the crossing parameter and the face's own identity, which is what decides a seat's ONE winner when its
    // step crosses several faces.
    private readonly record struct PortalEdgeHit(WorldPlacement Placement, WorldPlacementFace Face, WorldPlacementPortal Portal, int Seat, WorldFaceFrame Frame, FixedQ4816 SeamU, FixedQ4816 SeamV, WorldFaceCrossingClaim Claim);

    // One cohort member's OWN swept-crossing seam, captured from ITS OWN hit — never borrowed from a different
    // member's crossing. A party crossing the same door abreast at different lateral offsets must map each member
    // through the point IT actually swept through; sharing one group-level (SeamU, SeamV) across every member
    // mirrors every member except whichever hit happened to open the coalesced group first.
    internal readonly record struct MemberSeam(FixedVector3 SourcePosition, FixedQ4816 SeamU, FixedQ4816 SeamV);

    // One instance's own portal scan: every placement's every portal-carrying face, against every active local
    // seat. Placement/face iteration order is the document's own declared order; seat order is ascending
    // 0..LocalSeatCount-1 — deterministic within one process run, though this scan's queue sits outside the
    // boot-only replay tape (see m_freshCounters).
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
    private void ScanInstancePortals(WorldInstance instance) {
        var definition = instance.Server.Definition;
        var population = instance.Server.Population;
        var catalog = WorldFaceCatalog.For(definition: definition);
        var crossingFloor = WorldFacePortalPolicy.CrossingFloor(definition: definition);
        var winners = new PortalEdgeHit?[WorldPopulation.LocalSeatCount];

        foreach (var placement in definition.Placements) {
            if ((placement is null) || (placement.FaceSources is not { Count: > 0 } faces)) {
                continue;
            }

            foreach (var face in faces) {
                if ((face.Portal is not { } portal) ||
                    !catalog.TryFind(placementId: placement.Id, faceName: face.Face, out var row) ||
                    !WorldFacePortalPolicy.TryAperture(row: in row, crossingFloor: crossingFloor, aperture: out var aperture)) {
                    // A face with no aperture mapping is refused by name at validation, so reaching here means the
                    // document never declared the face at all — nothing to scan.
                    continue;
                }

                ScanPortalFace(instance: instance, population: population, placement: placement, face: face, portal: portal, aperture: aperture!, winners: winners);
            }
        }

        var hits = new List<PortalEdgeHit>();

        foreach (var winner in winners) {
            if (winner is { } hit) {
                hits.Add(item: hit);
            }
        }

        if (hits.Count > 0) {
            ResolveAndEnqueueCoalescedTransfers(instance: instance, hits: hits);
        }
    }

    // One portal face against every local seat. The face's geometry is the shared per-revision derivation
    // (WorldFaceCatalog) — the SAME frame rendering draws and arrival maps through, so a rotated or shape-offset door
    // triggers exactly where it is drawn. The region test sweeps the segment from the body's previous scan origin
    // (WorldBody.FixedPreviousPosition) to its current one, so no speed, rate, or motion program can tunnel a body
    // through a face between two samples.
    private void ScanPortalFace(WorldInstance instance, WorldPopulation population, WorldPlacement placement, WorldPlacementFace face, WorldPlacementPortal portal, WorldFaceAperture aperture, PortalEdgeHit?[] winners) {
        for (var seat = 0; (seat < WorldPopulation.LocalSeatCount); seat++) {
            if (!population.IsActive(index: seat) || (population.EntryBody(index: seat) is not { } body)) {
                instance.PortalOccupancy.Forget(placementId: placement.Id, faceName: face.Face, seat: seat);

                continue;
            }

            var crossing = WorldFaceRegion.Sweep(aperture: aperture, from: body.FixedPreviousPosition, to: body.FixedPosition);
            var fired = instance.PortalOccupancy.Observe(
                placementId: placement.Id,
                faceName: face.Face,
                seat: seat,
                inside: crossing.Inside,
                crossed: crossing.Crossed
            );

            if (!fired) {
                continue;
            }

            var claim = new WorldFaceCrossingClaim(PlacementId: placement.Id, FaceName: face.Face, Parameter: crossing.Parameter);

            if ((winners[seat] is { } standing) && !claim.Outranks(other: standing.Claim)) {
                continue;
            }

            winners[seat] = new PortalEdgeHit(Placement: placement, Face: face, Portal: portal, Seat: seat, Frame: crossing.Frame, SeamU: crossing.SeamU, SeamV: crossing.SeamV, Claim: claim);
        }
    }

    // An arriving traveler's own occupancy, latched at the instant it lands rather than discovered by the
    // next scan. The mapped isometry sets a traveler down against its counterpart's threshold, and a Spawn
    // arrival can land a seat inside any door the spawn point happens to sit in front of; either way, the
    // body did not walk in, so its first scan there must not read as an entry edge. A degenerate segment
    // (the landing collapses previous to current — WorldBody.Pose) makes the swept test the point test, so
    // the region's own Inside answer is exactly what the next scan would latch.
    private static void SeedArrivalOccupancy(WorldInstance instance, int seat) {
        if ((seat < 0) || (seat >= WorldPopulation.LocalSeatCount) || (instance.Server.Population.EntryBody(index: seat) is not { } body)) {
            return;
        }

        var definition = instance.Server.Definition;
        var catalog = WorldFaceCatalog.For(definition: definition);
        var crossingFloor = WorldFacePortalPolicy.CrossingFloor(definition: definition);

        foreach (var placement in definition.Placements) {
            if ((placement is null) || (placement.FaceSources is not { Count: > 0 } faces)) {
                continue;
            }

            foreach (var face in faces) {
                if ((face.Portal is null) ||
                    !catalog.TryFind(placementId: placement.Id, faceName: face.Face, out var row) ||
                    !WorldFacePortalPolicy.TryAperture(row: in row, crossingFloor: crossingFloor, aperture: out var aperture)) {
                    continue;
                }

                if (WorldFaceRegion.Sweep(aperture: aperture!, from: body.FixedPosition, to: body.FixedPosition).Inside) {
                    instance.PortalOccupancy.SeedInside(placementId: placement.Id, faceName: face.Face, seat: seat);
                }
            }
        }
    }

    // The key includes the source door's complete mapping identity. Doors sharing a destination and scope therefore
    // retain their own arrival frames without relying on the document model's one-portal-per-face rule.
    private readonly record struct CoalescedPortalGroupKey(string DestinationName, string ScopeKey, string SourcePlacementId, string SourceFace, WorldPortalArrival Arrival, string? Counterpart);

    // One destination, scope, and source-door group accumulated before enqueueing a transfer.
    private sealed class CoalescedPortalGroup {
        public required WorldDestination Destination { get; init; }
        public required string ReferenceDocument { get; init; }
        public required WorldPortalTravel Travel { get; init; }
        public required double HoldSeconds { get; init; }
        public required WorldTransferFullPolicy FullPolicy { get; init; }
        public required bool PartyAllOrNothing { get; init; }
        public required int? BorderCapacity { get; init; }
        public required string Border { get; init; }
        public TransferScope Scope { get; set; }
        // The frame is captured when the source face is scanned, so later document mutation cannot move it. A member
        // with a hit uses MemberSeams; the shared seam is only the fallback for a party passenger without its own hit.
        // The destination applies (-u, v), the horizontal image produced by the arrival isometry's half turn.
        public required WorldPortalArrival Arrival { get; init; }
        public required string? Counterpart { get; init; }
        public required FixedVector3 SourceSeamPosition { get; init; }
        public required FixedQ4816 SourceFaceYawRadians { get; init; }
        public required FixedQ4816 SourceFaceSeamU { get; init; }
        public required FixedQ4816 SourceFaceSeamV { get; init; }
        public readonly SortedSet<int> Slots = new();
        public readonly List<string> Descriptions = new();
        // Every crossing seat's own seam; seats absent from this map use the party fallback above.
        public readonly Dictionary<int, MemberSeam> MemberSeams = new();
    }

    // Resolves this scan's hits, coalesces identical mappings, and enqueues one transfer per group. The order list
    // preserves first-seen scan order, keeping transfer and generation ids independent of dictionary enumeration.
    private void ResolveAndEnqueueCoalescedTransfers(WorldInstance instance, List<PortalEdgeHit> hits) {
        var groups = new Dictionary<CoalescedPortalGroupKey, CoalescedPortalGroup>();
        var order = new List<CoalescedPortalGroupKey>();

        foreach (var hit in hits) {
            if (WorldDefinitionRows.FindDestination(destinations: instance.Server.Definition.Destinations, name: hit.Portal.Destination) is not { } destination) {
                Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}'/{hit.Placement.Id}/{hit.Face.Face} refused (destination '{hit.Portal.Destination}' names no destinations row)]");

                continue;
            }

            if (WorldDefinitionRows.FindReference(references: instance.Server.Definition.References, name: destination.Reference) is not { } reference) {
                Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}'/{hit.Placement.Id}/{hit.Face.Face} refused (destination '{hit.Portal.Destination}' names references row '{destination.Reference}', which does not exist)]");

                continue;
            }

            // Mirrors WorldPlacementCommandModule.DescribePortals' own resolution order exactly: the facet's own
            // travel, else the document's portals.portalDefaults.travel, else 'body' when the world declares no
            // portals section.
            var defaultTravel = (instance.Server.Definition.Portals?.PortalDefaults.Travel ?? WorldPortalTravel.Body);
            var travel = (hit.Portal.Travel ?? defaultTravel);
            var scope = ((travel == WorldPortalTravel.Party) ? TransferScope.Party : TransferScope.Body);
            var portalDefaults = (instance.Server.Definition.Portals?.PortalDefaults ?? new WorldPortalDefaults(Travel: WorldPortalTravel.Body));

            // This hit's own candidate cohort — the source instance's whole active local-seat set for a
            // `party` door, or just the entering seat for `body`. Read live, not cached.
            var cohortSlots = ((scope == TransferScope.Party) ? ActiveLocalSeats(server: instance.Server) : [hit.Seat]);
            var cohort = BuildCohort(server: instance.Server, slots: cohortSlots);

            if (!m_resolver.TryDeriveScopeKey(sourceDefinition: instance.Server.Definition, destination: destination, cohort: cohort, scopeKey: out var scopeKey, reason: out var scopeReason)) {
                Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}'/{hit.Placement.Id}/{hit.Face.Face} refused (destination '{hit.Portal.Destination}' — {scopeReason})]");

                continue;
            }

            var key = new CoalescedPortalGroupKey(DestinationName: destination.Name.Value, ScopeKey: scopeKey, SourcePlacementId: hit.Placement.Id, SourceFace: hit.Face.Face, Arrival: hit.Portal.Arrival, Counterpart: hit.Portal.Counterpart);
            var hitSeamPosition = hit.Frame.PointAt(u: hit.SeamU, v: hit.SeamV);

            if (!groups.TryGetValue(key: key, value: out var group)) {
                group = new CoalescedPortalGroup {
                    Destination = destination,
                    ReferenceDocument = reference.Document,
                    Travel = travel,
                    HoldSeconds = portalDefaults.HoldSeconds,
                    FullPolicy = portalDefaults.Full,
                    PartyAllOrNothing = portalDefaults.PartyAllOrNothing,
                    BorderCapacity = hit.Portal.Capacity,
                    Border = $"{hit.Placement.Id}/{hit.Face.Face}",
                    Scope = scope,
                    Arrival = hit.Portal.Arrival,
                    Counterpart = hit.Portal.Counterpart,
                    SourceSeamPosition = hitSeamPosition,
                    SourceFaceYawRadians = hit.Frame.PlanarYawRadians,
                    SourceFaceSeamU = hit.SeamU,
                    SourceFaceSeamV = hit.SeamV,
                };
                groups[key] = group;
                order.Add(item: key);
            } else if ((scope == TransferScope.Party) && (group.Scope != TransferScope.Party)) {
                // A party-travel hit widens an already-open body-travel group's own reported scope — the merged
                // cohort below is what ApplyTransfer actually moves either way (it prefers FrozenCohortSlots over
                // Scope whenever both are present), so this only affects what the enqueue echo/verb narrates.
                group.Scope = TransferScope.Party;
            }

            // THIS hit's own seam, recorded by seat — every hit that lands in this group gets its own entry, not
            // just the one that opened it, so a party crossing abreast maps each member through the point IT
            // actually swept rather than mirroring every member but the first through the group's shared fallback.
            group.MemberSeams[hit.Seat] = new MemberSeam(SourcePosition: hitSeamPosition, SeamU: hit.SeamU, SeamV: hit.SeamV);

            foreach (var slot in cohortSlots) {
                group.Slots.Add(item: slot);
            }

            group.Descriptions.Add(item: $"{hit.Placement.Id}/{hit.Face.Face} seat {(hit.Seat + 1)}");
        }

        foreach (var key in order) {
            EnqueueCoalescedGroup(instance: instance, group: groups[key]);
        }
    }

    // One (destination, scope key) group's own single resolve+enqueue — the ONE resolver call and ONE
    // EnqueueTransfer call the whole merged cohort shares, mirroring the pre-coalescing single-hit TriggerPortal's
    // own body exactly except for operating over a cohort that may span more than one hit.
    private void EnqueueCoalescedGroup(WorldInstance instance, CoalescedPortalGroup group) {
        var cohortSlots = group.Slots.ToArray();
        var cohort = BuildCohort(server: instance.Server, slots: cohortSlots);

        // The resolver's own cache-key identity is canonical, resolved once here — never the raw
        // group.ReferenceDocument string (TryFindRunningInstanceByOrigin below still takes the raw path since
        // it already canonicalizes both sides internally).
        // A references row is document-relative: resolve it beside the source instance before assigning a
        // resolver identity or starting a preview/transfer, rather than falling back to AppContext's copied
        // Assets tree, which can make an explicitly booted document and its own return reference look like
        // different origins.
        var referencedDocument = ResolveReferenceDocument(source: instance, documentPath: group.ReferenceDocument);
        var canonicalDocument = CanonicalDocumentIdentity(documentPath: referencedDocument);

        // A destination whose resolved document is the same document a RUNNING instance was already started
        // from — the boot instance especially — resolves to THAT instance, never minting a second one. Runs
        // before the ordinary resolve below, and only changes anything on a pair's first resolution:
        // WorldSessionResolver.TryGetActive gates it, so a key with an active generation is left alone (the
        // resolver's own cache always wins). A scope-key derivation failure here is not reported — the
        // ordinary TryResolve call below re-derives the identical key and reports the same refusal.
        //
        // Persisted-only: an EPHEMERAL destination's generations are resolver-minted by definition (the
        // first scoped resolution mints a fresh instance), so adopting a foreign already-running instance
        // for one would hand an ephemeral traveler someone else's live session purely because it shares the
        // destination's document. Only a PERSISTED destination's stable identity legitimately means the
        // same document, already running, is this destination — so this origin scan is narrowed to
        // persisted rows; ephemeral crossings use the ordinary TryResolve mint-or-reuse path below.
        if ((group.Destination.Durability == WorldDestinationDurability.Persisted) &&
            m_resolver.TryDeriveScopeKey(sourceDefinition: instance.Server.Definition, destination: group.Destination, cohort: cohort, scopeKey: out var homeScopeKey, reason: out _) &&
            !m_resolver.TryGetActive(destinationName: group.Destination.Name.Value, durability: group.Destination.Durability, scopeKey: homeScopeKey, referencedDocument: canonicalDocument, resolved: out _)) {
            if (TryFindRunningInstanceByOrigin(documentPath: referencedDocument, matchedName: out var matchedName, ambiguous: out var ambiguousNames)) {
                m_resolver.TryAdopt(destination: group.Destination, scopeKey: homeScopeKey, referencedDocument: canonicalDocument, instanceName: matchedName, resolved: out _, reason: out _);
            } else if (ambiguousNames is { Count: > 1 }) {
                Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}' {string.Join(separator: ", ", values: group.Descriptions)} refused (destination '{group.Destination.Name}' resolves document '{group.ReferenceDocument}', matching {ambiguousNames.Count} running instances [{string.Join(separator: ",", values: ambiguousNames)}] by origin — ambiguous, refused rather than adopting one arbitrarily)]");

                return;
            }
        }

        if (!m_resolver.TryResolve(sourceDefinition: instance.Server.Definition, destination: group.Destination, referencedDocument: canonicalDocument, cohort: cohort, resolved: out var resolvedSession, reason: out var resolveReason)) {
            Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}' {string.Join(separator: ", ", values: group.Descriptions)} refused (destination '{group.Destination.Name}' — {resolveReason})]");

            return;
        }

        // The resolver already decided which scoped instance this cohort lands in (reused if a generation
        // is already active, freshly minted otherwise); TransferLifetime.Resolved's job is only
        // start-if-absent-else-reuse against that name — retained (never auto-reaped) exactly when the
        // destination's own durability is Persisted, mirroring the Persistent lifetime.
        var transferDestination = TransferDestination.Resolved(name: resolvedSession.InstanceName, documentPath: referencedDocument, retain: (group.Destination.Durability == WorldDestinationDurability.Persisted));

        // The lowest triggering seat's own principal, mirroring world.transfer's own identity-continuity
        // thread. The specific choice among a merged group's several triggering seats is immaterial —
        // MemberTravelPrincipal already re-derives every other member's own Seat principal independently, so
        // whichever seat is named here only affects itself.
        var actingPrincipal = WorldPrincipal.Seat(slot: cohortSlots[0]);
        var transferId = EnqueueTransfer(sourceInstance: instance.Name, scope: group.Scope, sourceSlot: cohortSlots[0], destination: transferDestination, actingPrincipal: actingPrincipal, resolvedDestinationRow: group.Destination, frozenCohortSlots: cohortSlots, frozenScopeKey: resolvedSession.ScopeKey, frozenGenerationId: resolvedSession.GenerationId, arrival: group.Arrival, counterpart: group.Counterpart, sourceSeamPosition: group.SourceSeamPosition, sourceFaceYawRadians: group.SourceFaceYawRadians, sourceFaceSeamU: group.SourceFaceSeamU, sourceFaceSeamV: group.SourceFaceSeamV, memberSeams: group.MemberSeams, holdSeconds: group.HoldSeconds, fullPolicy: group.FullPolicy, partyAllOrNothing: group.PartyAllOrNothing, borderCapacity: group.BorderCapacity, border: group.Border);

        Console.Out.WriteLine(value: $"[world.portal: '{instance.Name}' {string.Join(separator: ", ", values: group.Descriptions)} entered -> queued transfer={transferId} to '{group.Destination.Name}' (durability={WorldDestinationTokens.DurabilityToken(durability: group.Destination.Durability)} scope={WorldDestinationTokens.ScopeToken(scope: group.Destination.Scope)} travel={WorldDestinationTokens.TravelToken(travel: group.Travel)} arrival={WorldDestinationTokens.ArrivalToken(arrival: group.Arrival)} generation={resolvedSession.GenerationId}{(resolvedSession.IsNewGeneration ? " (new)" : "")} instance={resolvedSession.InstanceName} cohort=[{string.Join(separator: ",", values: cohortSlots.Select(selector: static slot => (slot + 1)))}])]");
    }

    // The candidate cohort a set of local seats resolves as — read live off the server, shared by every resolver
    // call this file makes (the per-hit TryDeriveScopeKey probe and the per-group TryResolve mint/reuse alike).
    private static WorldSessionResolver.CohortMember[] BuildCohort(WorldServer server, IReadOnlyList<int> slots) {
        var cohort = new WorldSessionResolver.CohortMember[slots.Count];

        for (var index = 0; (index < slots.Count); index++) {
            var slot = slots[index];

            cohort[index] = new WorldSessionResolver.CohortMember(
                Principal: WorldPrincipal.Seat(slot: slot),
                IdentityId: server.Population.EntryBody(index: slot)?.Profile?.Id
            );
        }

        return cohort;
    }

    // The next deterministic fresh-instance name for a SITE: "<site>-<n>", n the site's own draw counter (see
    // m_freshCounters). Never wall-clock, RNG, or tick-of-entry — see that field's own remarks for why this is
    // deterministic within one process run rather than "replay-stable" (the tape does not cover this queue).
    private string MintFreshInstanceName(string site) {
        var ordinal = m_freshCounters.GetValueOrDefault(key: site);

        m_freshCounters[site] = (ordinal + 1);

        return $"{site}-{ordinal}";
    }

    // Resolves (spawning or starting as needed) a queued transfer's destination — the one place a
    // TransferDestination becomes a live WorldInstance, so a party's whole member set shares this single
    // resolution (a Fresh destination mints its name once here, not once per member). `spawned` is true
    // only when this call started a brand-new instance (Fresh always; Persistent only when it was not
    // already running) — ApplyTransfer reads it to decide whether an empty destination is worth reaping
    // when every member's join fails. `source` is read only by the Resolved case; every other lifetime
    // resolves from `transfer.Destination` alone.
    private bool TryResolveDestination(in PendingTransfer transfer, WorldInstance source, out WorldInstance? resolved, out string resolvedName, out bool spawned, out string reason) {
        var destination = transfer.Destination;

        switch (destination.Lifetime) {
            case TransferLifetime.Existing:
                resolvedName = destination.Name!;
                spawned = false;

                if (!m_instances.TryGetValue(key: resolvedName, value: out resolved)) {
                    reason = $"no instance named '{resolvedName}'";

                    return false;
                }

                reason = string.Empty;

                return true;

            case TransferLifetime.Persistent:
                return ResolveByStableName(name: destination.Name!, documentPath: destination.DocumentPath!, retain: true, resolved: out resolved, resolvedName: out resolvedName, spawned: out spawned, reason: out reason);

            case TransferLifetime.Resolved:
                return ResolveTransferDestination(transfer: in transfer, source: source, resolved: out resolved, resolvedName: out resolvedName, spawned: out spawned, reason: out reason);

            case TransferLifetime.Fresh:
                resolvedName = MintFreshInstanceName(site: destination.Site!);

                if (!TryStart(name: resolvedName, path: destination.DocumentPath!, instance: out resolved, reason: out reason)) {
                    spawned = false;

                    return false;
                }

                spawned = true;

                return true;

            default:
                resolved = null;
                resolvedName = string.Empty;
                spawned = false;
                reason = $"unrecognized transfer lifetime '{destination.Lifetime}'";

                return false;
        }
    }

    // Reuse-if-running exactly like Persistent (only the retention rule differs — see
    // TransferDestination.Resolved) unless the resolver-minted name is no longer running. Reaching TryStart
    // under that stale name directly, the way the Persistent path always has, would restart an instance
    // behind the resolver's own back: whatever TryStop call retired the original already cleared
    // WorldSessionResolver's cache entry via NotifyInstanceRetired, so a blind restart would make one live
    // again with the resolver never told, and the next traveler's resolve would mint a second, different
    // generation for what should be one scoped session. Re-resolving through the resolver instead — using
    // the frozen cohort's still-active members against the frozen destination row — keeps cache and reality
    // from diverging.
    private bool ResolveTransferDestination(in PendingTransfer transfer, WorldInstance source, out WorldInstance? resolved, out string resolvedName, out bool spawned, out string reason) {
        var destination = transfer.Destination;

        if (m_instances.ContainsKey(key: destination.Name!)) {
            return ResolveByStableName(name: destination.Name!, documentPath: destination.DocumentPath!, retain: destination.Retain, resolved: out resolved, resolvedName: out resolvedName, spawned: out spawned, reason: out reason);
        }

        if ((transfer.ResolvedDestinationRow is not { } destinationRow) || (transfer.FrozenCohortSlots is not { } frozenSlots)) {
            // Unreachable for a genuine Resolved-lifetime transfer — EnqueueCoalescedGroup, the only minter of this
            // lifetime, always populates both. Falls back to the ordinary reuse-if-running/else-start path rather
            // than throwing if that invariant is ever violated.
            resolved = null;
            resolvedName = string.Empty;
            spawned = false;
            reason = $"'{destination.Name}' carries no frozen resolution context to re-resolve through — refused rather than restarting it blind";

            return false;
        }

        var liveCohort = LiveCohortForFrozenSlots(server: source.Server, frozenSlots: frozenSlots);

        if (liveCohort.Count == 0) {
            resolved = null;
            resolvedName = string.Empty;
            spawned = false;
            reason = $"'{destination.Name}' is no longer running, and no frozen cohort member is still active in '{source.Name}' to re-resolve it through";

            return false;
        }

        if (!m_resolver.TryResolve(sourceDefinition: source.Server.Definition, destination: destinationRow, referencedDocument: CanonicalDocumentIdentity(documentPath: destination.DocumentPath!), cohort: liveCohort, resolved: out var reResolved, reason: out var resolveReason)) {
            resolved = null;
            resolvedName = string.Empty;
            spawned = false;
            reason = $"'{destination.Name}' is no longer running, and re-resolving it failed ({resolveReason})";

            return false;
        }

        return ResolveByStableName(name: reResolved.InstanceName, documentPath: destination.DocumentPath!, retain: destination.Retain, resolved: out resolved, resolvedName: out resolvedName, spawned: out spawned, reason: out reason);
    }

    // Re-derives the FROZEN cohort's current membership from the source instance's live state — for the members
    // STILL ACTIVE only. A member no longer active contributes nothing here (it is refused individually, by name, at
    // the ordinary per-member transfer step — never folded into a re-verification it can no longer prove anything
    // for).
    private static IReadOnlyList<WorldSessionResolver.CohortMember> LiveCohortForFrozenSlots(WorldServer server, IReadOnlyList<int> frozenSlots) {
        var members = new List<WorldSessionResolver.CohortMember>(capacity: frozenSlots.Count);

        foreach (var slot in frozenSlots) {
            if (!server.Population.IsActive(index: slot)) {
                continue;
            }

            members.Add(item: new WorldSessionResolver.CohortMember(
                Principal: WorldPrincipal.Seat(slot: slot),
                IdentityId: server.Population.EntryBody(index: slot)?.Profile?.Id
            ));
        }

        return members;
    }

    // The shared "reuse-if-running, else start" resolution both TransferLifetime.Persistent and .Resolved use — the
    // ONLY difference between them is whether the retention rule is fixed (Persistent, always retained) or carried
    // per-call (Resolved, from the resolver's own destination durability). Extracted so the name-collision fence
    // below is written, and kept correct, exactly once.
    private bool ResolveByStableName(string name, string documentPath, bool retain, out WorldInstance? resolved, out string resolvedName, out bool spawned, out string reason) {
        resolvedName = name;

        if (m_instances.TryGetValue(key: resolvedName, value: out resolved)) {
            spawned = false;

            // A name-collision fence: a stable-named destination reuses an already-running instance by name
            // alone, so this verifies it was started from the same document — two doors authoring the
            // identical name against different reference documents would otherwise silently route a
            // traveler into whichever world happened to claim the name first. Resolve both sides through
            // the same probe TryStart itself uses (rooted/relative/base-directory/shipped-worlds), so a
            // spelling difference alone never false-refuses.
            if (!TryResolveDocumentPath(path: documentPath, resolved: out var expectedPath) ||
                !TryResolveDocumentPath(path: resolved.SourcePath, resolved: out var actualPath) ||
                !string.Equals(a: expectedPath, b: actualPath, comparisonType: PathComparison)) {
                reason = $"'{resolvedName}' is already running from '{resolved.SourcePath}', not the document this destination names ('{documentPath}') — a stable-named destination must resolve the same document everywhere it is authored";
                resolved = null;

                return false;
            }

            // Reached by name — from this point on it is retained (if `retain`) even if it happens to be empty right
            // now (e.g. it was only ever started, never yet joined).
            if (retain) {
                m_retainedInstances.Add(item: resolvedName);
            }

            reason = string.Empty;

            return true;
        }

        if (!TryStart(name: resolvedName, path: documentPath, instance: out resolved, reason: out reason)) {
            spawned = false;

            return false;
        }

        spawned = true;

        if (retain) {
            m_retainedInstances.Add(item: resolvedName);
        }

        return true;
    }

    // One landed member's captured state — enough to restore it exactly at the source if the transfer
    // aborts after it already joined the destination. Position/yaw/dynamic state/designations are all
    // captured before TryDetachSeatForTransfer runs (which discards them). See
    // WorldPopulation.RestoreDetachedSeat for why position+yaw alone reconstructs a grounded-model body's
    // orientation bit-for-bit, WorldBody.TransferState for what dynamic state carries (velocity, dash
    // overlay, in-flight timed presses), and WorldPopulation.CaptureDesignations for why designations need
    // their own separate capture.
    private readonly record struct LandedMember(int SourceSlot, int TargetSlot, WorldIdentity? Profile, FixedVector3 Position, FixedQ4816 Yaw, WorldBody.TransferState DynamicState, int[] Designations);

    private sealed record InDoubtTransfer(
        PendingTransfer Transfer,
        TransferAuthority TargetAuthority,
        string SourceAuthority,
        string TargetName,
        bool Spawned,
        ulong SourceDeadlineTick,
        List<LandedMember> Landed,
        List<WorldTransferCommitMember> CommitMembers,
        int MemberCount
    );

    // The transfer contract is authority-shaped. A local row invokes the same server escrow directly; a remote row
    // serializes that contract over TCP. No transfer logic branches on colocation below this adapter.
    private readonly record struct TransferAuthority(WorldInstance? Local, WorldRemoteAuthority? Remote) {
        public bool IsRemote => (Remote is not null);
        public WorldDefinition Definition => (Local?.Server.Definition ?? Remote!.Definition);
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) =>
            (Local is not null ? Local.Server.ReserveTransfer(request: request) : Remote!.Reserve(request: request with { RemoteAdmission = true }));
        public bool Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) =>
            (Local is not null ? Local.Server.CommitTransfer(sourceAuthority: sourceAuthority, transferId: transferId, members: members, reason: out reason) : Remote!.Commit(sourceAuthority: sourceAuthority, transferId: transferId, members: members, reason: out reason));
        public void Abort(string sourceAuthority, ulong transferId) {
            if (Local is not null) {
                Local.Server.AbortTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
            } else {
                Remote!.Abort(sourceAuthority: sourceAuthority, transferId: transferId);
            }
        }
        public WorldTransferStatus Status(string sourceAuthority, ulong transferId) =>
            (Local is not null ? Local.Server.TransferStatus(sourceAuthority: sourceAuthority, transferId: transferId) : Remote!.Status(sourceAuthority: sourceAuthority, transferId: transferId));
    }

    private bool TryResolveTransferAuthority(in PendingTransfer transfer, WorldInstance source, out TransferAuthority authority, out string resolvedName, out bool spawned, out string reason) {
        if ((transfer.Destination.DocumentPath is { } documentPath) && TryResolveDocumentPath(path: documentPath, resolved: out var resolvedPath)) {
            var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => Path.GetDirectoryName(path: resolvedPath) is { Length: > 0 } directory ? directory : AppContext.BaseDirectory);

            if (WorldDefinitionLoader.TryLoadFile(path: resolvedPath, definition: out var definition, reason: out var loadReason, instanceIdentity: (transfer.Destination.Name ?? transfer.Destination.Site ?? "remote"), neighbours: neighbours) &&
                (definition is not null) &&
                ((transfer.Destination.Authority ?? definition.Host.Authority) is { Length: > 0 } endpoint)) {
                resolvedName = (transfer.Destination.Name ?? transfer.Destination.Site ?? endpoint);

                try {
                    if (!m_remoteAuthorities.TryGetValue(key: resolvedName, value: out var remote) || !string.Equals(a: remote.Endpoint, b: endpoint, comparisonType: StringComparison.Ordinal)) {
                        remote?.Dispose();
                        remote = new WorldRemoteAuthority(endpoint: endpoint, placeholder: definition, security: m_federationSecurity, observerAuthority: $"{m_machineId:N}/observer");
                        m_remoteAuthorities[resolvedName] = remote;
                    }

                    authority = new TransferAuthority(Local: null, Remote: remote);
                    spawned = false;
                    reason = string.Empty;
                    return true;
                } catch (FormatException exception) {
                    authority = default;
                    spawned = false;
                    reason = exception.Message;
                    return false;
                }
            } else if (definition is null && loadReason.Length > 0) {
                authority = default;
                resolvedName = string.Empty;
                spawned = false;
                reason = loadReason;
                return false;
            }
        }

        // A windowed source may already hold a colocated projection cache for a portal screen. That cache is not
        // transfer authority: an authored remote endpoint still wins above, and only a document with no remote
        // authority may short-circuit through an existing colocated instance here.
        if ((transfer.Destination.Name is { } existingName) && m_instances.TryGetValue(key: existingName, value: out var existing)) {
            authority = new TransferAuthority(Local: existing, Remote: null);
            resolvedName = existingName;
            spawned = false;
            reason = string.Empty;
            return true;
        }

        if (TryResolveDestination(transfer: in transfer, source: source, resolved: out var local, resolvedName: out resolvedName, spawned: out spawned, reason: out reason)) {
            authority = new TransferAuthority(Local: local!, Remote: null);
            return true;
        }

        authority = default;
        return false;
    }

    /// <summary>Returns the authority/body currently followed by one local roster slot.</summary>
    public SeatLocation SeatLocation(int slot) => m_seatRouter.Location(slot: slot);

    /// <summary>Remote-capable form of <see cref="TryResolveObservedDestination"/>. It resolves the same global
    /// session but returns its projection contract instead of requiring a local <see cref="WorldInstance"/>.</summary>
    public bool TryResolveObservedProjection(WorldInstance source, string destinationName, out string instanceName, out ulong generationId, out WorldDefinition? definition, out Func<IClientSink, IDisposable>? attach, out string reason) {
        instanceName = string.Empty;
        generationId = 0;
        definition = null;
        attach = null;

        if (WorldDefinitionRows.FindDestination(destinations: source.Server.Definition.Destinations, name: destinationName) is not { } destination ||
            destination.Scope != WorldDestinationScope.Global ||
            WorldDefinitionRows.FindReference(references: source.Server.Definition.References, name: destination.Reference) is not { } reference) {
            reason = $"destination '{destinationName}' is absent, non-global, or names no reference";
            return false;
        }

        var cohort = new[] { new WorldSessionResolver.CohortMember(Principal: WorldPrincipal.Seat(slot: 0), IdentityId: null) };
        var referencedDocument = ResolveReferenceDocument(source: source, documentPath: reference.Document);
        var canonicalDocument = CanonicalDocumentIdentity(documentPath: referencedDocument);

        if (!m_resolver.TryResolve(sourceDefinition: source.Server.Definition, destination: destination, referencedDocument: canonicalDocument, cohort: cohort, resolved: out var resolved, reason: out reason)) {
            return false;
        }

        instanceName = resolved.InstanceName;
        generationId = resolved.GenerationId;

        if (TryGetProjection(name: instanceName, definition: out definition, attach: out attach, envelope: out _, borderMargin: out _)) {
            reason = string.Empty;
            return true;
        }

        if (!TryResolveDocumentPath(path: referencedDocument, resolved: out var resolvedPath)) {
            reason = $"no referenced world document at '{referencedDocument}'";
            return false;
        }

        var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => Path.GetDirectoryName(path: resolvedPath) is { Length: > 0 } directory ? directory : AppContext.BaseDirectory);
        if (!WorldDefinitionLoader.TryLoadFile(path: resolvedPath, definition: out var loaded, reason: out reason, instanceIdentity: instanceName, neighbours: neighbours) || loaded is null) {
            return false;
        }

        if (loaded.Host.Authority is { Length: > 0 } endpoint) {
            try {
                var remote = new WorldRemoteAuthority(endpoint: endpoint, placeholder: loaded, security: m_federationSecurity, observerAuthority: $"{m_machineId:N}/observer");
                m_remoteAuthorities[instanceName] = remote;
                definition = loaded;
                attach = remote.AttachSink;
                reason = string.Empty;
                return true;
            } catch (FormatException exception) {
                reason = exception.Message;
                return false;
            }
        }

        if (ResolveByStableName(name: instanceName, documentPath: referencedDocument, retain: (destination.Durability == WorldDestinationDurability.Persisted), resolved: out var local, resolvedName: out _, spawned: out _, reason: out reason) && local is not null) {
            definition = local.Server.Definition;
            attach = local.Server.AttachSink;
            return true;
        }

        return false;
    }

    /// <summary>Resolves the continuous projection channel for a local or remote authority already named by the
    /// session router. Definition and per-tick records cross the same observer contract.</summary>
    public bool TryGetProjection(string name, out WorldDefinition? definition, out Func<IClientSink, IDisposable>? attach, out WorldRenderEnvelope? envelope, out IWorldBorderMarginSource? borderMargin) {
        if (m_remoteAuthorities.TryGetValue(key: name, value: out var remote)) {
            definition = remote.Definition;
            attach = remote.AttachSink;
            envelope = null;
            borderMargin = null;
            return true;
        }

        if (m_instances.TryGetValue(key: name, value: out var instance)) {
            definition = instance.Server.Definition;
            attach = instance.Server.AttachSink;
            envelope = instance.Server.Envelope;
            borderMargin = instance.Server.BorderMargin;
            return true;
        }

        definition = null;
        attach = null;
        envelope = null;
        borderMargin = null;
        return false;
    }

    // Applies one queued transfer as one transaction: resolve the frozen cohort -> reserve the destination's
    // exact target slots (capacity and destination Drive standing, both pre-checked before any member
    // detaches) -> detach every member with its pose captured -> commit every join into its reserved slot ->
    // if any join still fails (a refusal class reservation cannot pre-check, or the test-only injection
    // point below), abort: every already-landed member returns to its exact source pose, nothing partially
    // lands. No Server.Step of any instance runs between the first detach and the last decision, so a party
    // lands together, aborts together, or never leaves at all.
    private void ApplyTransfer(in PendingTransfer transfer) {
        // Idempotence: checked first, before this transfer touches anything — a retry-shaped duplicate (the
        // same transfer id submitted again) refuses by name rather than double-landing. A diegetic crossing
        // can never collide here on its own (it always mints a fresh id); only an explicitly-supplied id
        // (the verification seam) can.
        var appliedKey = (transfer.SourceInstance, transfer.TransferId);
        if (!m_appliedTransferIds.Add(item: appliedKey)) {
            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused (already applied — refused rather than double-landing)]");

            return;
        }

        if (!m_instances.TryGetValue(key: transfer.SourceInstance, value: out var source)) {
            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused (no instance named '{transfer.SourceInstance}')]");

            return;
        }

        // A resolver-driven transfer's membership/authorization was proven once, at scan time, against the
        // cohort the scan read then. Re-deriving the scope key from that same frozen cohort's still-active
        // members, right here, before this transfer touches anything, catches a seat replaced, a membership
        // row mutated, or a party member who joined after the scan — never silently re-resolving (which
        // would land an unproven cohort in the previously resolved scoped session), always refusing the
        // whole transfer by name when the frozen proof no longer holds. Refusing only when every frozen
        // member had departed would let a partial departure silently move the survivor alone — the frozen
        // cohort's own proof was for both of them together, so a proof missing even one member is an
        // expired proof; refuse the whole transfer rather than moving a subset nobody re-verified. A no-op
        // for a non-resolver transfer (console world.transfer's raw forms carry no frozen scope key to
        // re-verify).
        if (!transfer.ScopeProofAlreadyVerified && (transfer.ResolvedDestinationRow is { } frozenDestinationRow) && (transfer.FrozenScopeKey is { } frozenScopeKey) && (transfer.FrozenCohortSlots is { } frozenSlotsForScopeCheck)) {
            var liveCohortForScopeCheck = LiveCohortForFrozenSlots(server: source.Server, frozenSlots: frozenSlotsForScopeCheck);
            string driftReason;

            if (liveCohortForScopeCheck.Count == 0) {
                driftReason = "no frozen cohort member is still active to re-verify membership against";
            } else if (liveCohortForScopeCheck.Count != frozenSlotsForScopeCheck.Count) {
                driftReason = $"only {liveCohortForScopeCheck.Count} of the frozen cohort's {frozenSlotsForScopeCheck.Count} member(s) are still active — the frozen proof was for the WHOLE cohort together, so a partial departure expires it rather than moving the survivors alone";
            } else if (!m_resolver.TryDeriveScopeKey(sourceDefinition: source.Server.Definition, destination: frozenDestinationRow, cohort: liveCohortForScopeCheck, scopeKey: out var liveScopeKey, reason: out var scopeReason)) {
                driftReason = scopeReason;
            } else if (!string.Equals(a: liveScopeKey, b: frozenScopeKey, comparisonType: StringComparison.Ordinal)) {
                driftReason = $"now resolves scope key '{liveScopeKey}' instead";
            } else {
                driftReason = string.Empty;
            }

            if (driftReason.Length > 0) {
                Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused (membership drifted between scan and drain in '{transfer.SourceInstance}' — the cohort no longer proves scope key '{frozenScopeKey}': {driftReason})]");

                return;
            }
        }

        // The member slots this transfer moves: the frozen cohort whenever one was proven — a resolver-driven
        // crossing's exact scanned slots, `body` or `party` alike (a coalesced group's merged cohort is
        // carried exactly this way too — see ResolveAndEnqueueCoalescedTransfers), never recomputed live
        // here, so a member who joined the source after the scan never rides along unproven. A non-resolver
        // `party` transfer (console world.transfer, which carries no frozen cohort) falls back to the
        // source's whole active local-seat set read live. A non-resolver `body` transfer is always just the
        // one requested seat.
        int[] members;

        if (transfer.FrozenCohortSlots is { } frozenSlots) {
            members = [.. frozenSlots];
        } else if (transfer.Scope == TransferScope.Party) {
            members = ActiveLocalSeats(server: source.Server);
        } else {
            members = [transfer.SourceSlot];
        }

        if (members.Length == 0) {
            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused (no active local seat in '{transfer.SourceInstance}' to party-transfer)]");

            return;
        }

        // A destination naming the SAME instance as the source is refused up front for Existing/Persistent, both of
        // which know their name before any spawn. A Fresh destination cannot self-target by construction (a freshly
        // minted name is never one already running), so there is nothing to pre-check for it here.
        if ((transfer.Destination.Name is { } destinationName) && string.Equals(a: transfer.SourceInstance, b: destinationName, comparisonType: StringComparison.Ordinal)) {
            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused ('{transfer.SourceInstance}' names both the source and the target)]");

            return;
        }

        if (!TryResolveTransferAuthority(transfer: in transfer, source: source, authority: out var targetAuthority, resolvedName: out var targetName, spawned: out var spawned, reason: out var destinationReason)) {
            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused ({destinationReason})]");

            // A resolve minted this generation's cache entry before this drain ever attempted to start or
            // reuse the instance it names; that attempt just failed outright (an unstartable reference
            // document, a stable-named collision fence), so nothing will ever back this generation — retire
            // it now rather than leaving world.destinations reporting a dead active generation forever. A
            // refusal that fires before this point, like the source-missing check above, never aborts —
            // another pending transfer in the same drain batch may still be racing to make the same name
            // real.
            if ((transfer.Destination.Lifetime == TransferLifetime.Resolved) && (transfer.Destination.Name is { } abortedName)) {
                m_resolver.AbortGeneration(instanceName: abortedName);
            }

            NoteResolvedTransferOutcome(transfer: in transfer, sourceName: transfer.SourceInstance, targetName: string.Empty, outcome: $"refused-destination:{destinationReason}");

            return;
        }

        // Reserve through the destination authority's escrow even when both authorities happen to be colocated.
        // Loopback is only a transport optimization beneath this contract; it is not a second transfer path.
        var sourceTick = (source.Server.NextInputTick - 1UL);
        var sourceRate = source.Server.Definition.SimulationRateHz;

        if ((sourceRate <= 0) || !FixedTickConversion.TryDurationEngineTicksExact(seconds: (decimal)transfer.HoldSeconds, ticks: out var holdEngineTicks)) {
            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused (source lease cannot be expressed exactly across the {FixedTickConversion.TicksPerSecond} engine-tick bridge)]");

            if (spawned) {
                ReapIfEmpty(name: targetName);
            }

            return;
        }

        var sourceStepTicks = (FixedTickConversion.TicksPerSecond / checked((ulong)sourceRate));
        var holdSourceSteps = Math.Max(1UL, checked((holdEngineTicks + sourceStepTicks - 1UL) / sourceStepTicks));
        var reservationMembers = new WorldTransferReservationMember[members.Length];

        for (var reservationIndex = 0; (reservationIndex < members.Length); reservationIndex++) {
            var sourceSlot = members[reservationIndex];
            reservationMembers[reservationIndex] = new WorldTransferReservationMember(Principal: MemberTravelPrincipal(transfer: in transfer, slot: sourceSlot), PreferredSlot: sourceSlot, Identity: source.Server.Population.EntryBody(index: sourceSlot)?.Profile);
        }

        var sourceAuthority = $"{m_machineId:N}/{transfer.SourceInstance}";
        var reservationRequest = new WorldTransferReservationRequest(
            TransferId: transfer.TransferId,
            SourceAuthority: sourceAuthority,
            SourceRateHz: sourceRate,
            SourceTick: sourceTick,
            DeadlineSourceTick: checked(sourceTick + holdSourceSteps),
            Border: transfer.Border,
            BorderCapacity: transfer.BorderCapacity,
            PartyAllOrNothing: transfer.PartyAllOrNothing,
            RemoteAdmission: false,
            Members: reservationMembers
        );
        WorldTransferReservationReply reservation;

        try {
            reservation = targetAuthority.Reserve(request: reservationRequest);
        } catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException) {
            reservation = WorldTransferReservationReply.Refused(reason: $"authority transport failed before reserve: {exception.Message}");
        }

        if (!reservation.Accepted) {
            var capacityRefusal = reservation.Reason.Contains(value: " is full ", comparisonType: StringComparison.Ordinal)
                || reservation.Reason.Contains(value: "no free body index", comparisonType: StringComparison.Ordinal);
            var willRetry = (capacityRefusal && (transfer.FullPolicy == WorldTransferFullPolicy.Retry));
            var retryText = (willRetry ? "client will retry; no destination queue was created" : "terminal refusal; no queue was created");
            var reserveReason = $"'{targetName}' refused reservation ({reservation.Reason}; {retryText})";
            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused ({reserveReason}) — the whole transfer is held, no reservation leaked]");

            if (spawned) {
                ReapIfEmpty(name: targetName);
            }

            NoteResolvedTransferOutcome(transfer: in transfer, sourceName: transfer.SourceInstance, targetName: targetName, outcome: $"refused-reservation:{reserveReason}");

            if (willRetry) {
                _ = m_appliedTransferIds.Remove(item: appliedKey);
                m_pendingTransfers.Enqueue(item: transfer);
            }

            return;
        }

        if ((reservation.BodyIndices.Count != members.Length) || (reservation.DestinationDefinition is null)) {
            try {
                targetAuthority.Abort(sourceAuthority: sourceAuthority, transferId: transfer.TransferId);
            } catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException) {
                // A malformed remote acceptance is never consumed. Its bounded destination lease is the fallback
                // if this best-effort abort cannot reach the peer.
            }

            var malformedReason = $"'{targetName}' returned a malformed accepted reservation (expected {members.Length} body indices and a destination definition)";
            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused ({malformedReason}) — every source member remains attached]");
            if (spawned) { ReapIfEmpty(name: targetName); }
            NoteResolvedTransferOutcome(transfer: in transfer, sourceName: transfer.SourceInstance, targetName: targetName, outcome: $"refused-reservation:{malformedReason}");
            return;
        }

        // Resolve ONCE for the party before splitting: a Fresh target is one shared instance, and a resolver-driven
        // party keeps the one generation its frozen proof named. Each member then receives an independent transfer
        // identity, lease, reserve/commit verdict and rollback boundary against that already-resolved target. A full
        // destination may land an earlier member and refuse a later one — the authored distinction from atomic.
        if (!transfer.PartyAllOrNothing && (members.Length > 1)) {
            var splitDestination = (targetAuthority.Remote is { } remote
                ? TransferDestination.Remote(name: targetName, documentPath: transfer.Destination.DocumentPath!, authority: remote.Endpoint)
                : TransferDestination.Existing(name: targetName));

            for (var ordinal = 0; ordinal < members.Length; ordinal++) {
                var member = members[ordinal];
                IReadOnlyDictionary<int, MemberSeam>? memberSeams = null;

                if ((transfer.MemberSeams is not null) && transfer.MemberSeams.TryGetValue(key: member, value: out var seam)) {
                    memberSeams = new Dictionary<int, MemberSeam> { [member] = seam };
                }

                ApplyTransfer(transfer: transfer with {
                    TransferId = MintUnappliedTransferId(sourceInstance: transfer.SourceInstance),
                    Scope = TransferScope.Body,
                    SourceSlot = member,
                    Destination = splitDestination,
                    FrozenCohortSlots = [member],
                    PartyAllOrNothing = true,
                    MemberSeams = memberSeams,
                    TestForceJoinRefusalOrdinal = ((transfer.TestForceJoinRefusalOrdinal == ordinal) ? 0 : null),
                    ScopeProofAlreadyVerified = true,
                });
            }

            if (spawned) { ReapIfEmpty(name: targetName); }
            return;
        }

        var reservedSlots = reservation.BodyIndices.ToArray();
        var destinationDefinition = reservation.DestinationDefinition;

        // Whole-transfer ALL-OR-NOTHING across SOURCE-side authorization too (destination standing was just proven
        // by the reservation above): pre-check every member's own LEAVE standing — Drive over its own body under
        // its travelling principal — BEFORE any member leaves, so a member blocked by a drive gate (a revoked grant
        // today, a combat CC/death gate later) refuses the WHOLE party rather than letting the rest split off while
        // it strands at the source. One blocked member names itself and why.
        foreach (var slot in members) {
            var standingPrincipal = MemberTravelPrincipal(transfer: in transfer, slot: slot);

            if (source.Server.Grants.Allows(principal: standingPrincipal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: slot)) is { IsAllowed: false } standing) {
                targetAuthority.Abort(sourceAuthority: sourceAuthority, transferId: transfer.TransferId);
                Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} refused ({standingPrincipal.Describe()} cannot leave '{transfer.SourceInstance}' seat {(slot + 1)} — {standing.DescribeDenial()}); the whole transfer is held]");

                if (spawned) {
                    ReapIfEmpty(name: targetName);
                }

                NoteResolvedTransferOutcome(transfer: in transfer, sourceName: transfer.SourceInstance, targetName: targetName, outcome: $"refused-source-standing:{standingPrincipal.Describe()}");

                return;
            }
        }

        // Mapped arrival's own counterpart resolution — a group-level fact (every member of this transfer
        // maps through the same portal pair), resolved once here against the destination's own delivered
        // definition, never at scan time, since cross-document existence cannot be checked at boot. A
        // resolution failure feeds the same abortReason/unwind mechanism the per-member join-refusal path
        // below uses — with nothing yet detached, the unwind loop after it is simply a no-op, and the
        // ABORTED line names why by quoting the exact counterpart string that failed to resolve.
        //
        // The counterpart resolve proves the authored anchor exists; the arrival frame itself is that
        // face's own derived frame (WorldFaceCatalog), exactly like the source side
        // (SourceSeamPosition/YawRadians, captured at scan). Both ends read the same derivation rendering
        // draws from, so scan, arrival, and the drawn door can never disagree about where a face sits.
        //
        // destinationFacePosition is the counterpart's own seam point at (-u, v) for the group's captured
        // source crossing — the fallback for a member with no entry in transfer.MemberSeams (a party
        // passenger swept along without personally crossing). A member with its own entry maps through its
        // own (-u, v) applied to counterpartFrame instead, inside the per-member loop below — the mapped
        // image of that member's own seam, never a fresh sample. Reusing a captured SeamU/SeamV against the
        // destination frame either way keeps a paired border's oppositely-oriented face frames in
        // one-to-one correspondence.
        var destinationFacePosition = FixedVector3.Zero;
        var destinationFaceYawRadians = FixedQ4816.Zero;
        var counterpartFrame = default(WorldFaceFrame);
        string? abortReason = null;

        if (transfer.Arrival == WorldPortalArrival.Mapped) {
            if (WorldPortalCounterpart.TryResolve(definition: destinationDefinition, counterpart: transfer.Counterpart, placement: out var counterpartPlacement, face: out var counterpartFace, reason: out var counterpartReason)) {
                if (WorldFaceCatalog.For(definition: destinationDefinition).TryFind(placementId: counterpartPlacement!.Id, faceName: counterpartFace!.Face, out var counterpartRow)) {
                    counterpartFrame = counterpartRow.Frame;
                    destinationFacePosition = WorldPortalArrivalMath.CounterpartSeam(destinationFrame: counterpartRow.Frame, seamU: transfer.SourceFaceSeamU, seamV: transfer.SourceFaceSeamV);
                    destinationFaceYawRadians = counterpartRow.Frame.PlanarYawRadians;
                } else {
                    abortReason = $"mapped arrival's counterpart '{transfer.Counterpart}' names no DECLARED creation face in '{targetName}'";
                }
            } else {
                abortReason = $"mapped arrival's {counterpartReason} in '{targetName}'";
            }
        }

        // Detach every source member first, then send one cohort commit to the destination escrow. The source
        // remains the lease authority until that commit is acknowledged; a refused commit restores every body.
        var landed = new List<LandedMember>(capacity: members.Length);
        var commitMembers = new List<WorldTransferCommitMember>(capacity: members.Length);

        for (var index = 0; ((abortReason is null) && (index < members.Length)); index++) {
            var sourceSlot = members[index];
            var reservedSlot = reservedSlots[index];
            var memberPrincipal = MemberTravelPrincipal(transfer: in transfer, slot: sourceSlot);

            if (!TryDetachAndCaptureMember(source: source, sourceSlot: sourceSlot, sourceName: transfer.SourceInstance, actingPrincipal: memberPrincipal, profile: out var profile, position: out var position, yaw: out var yaw, dynamicState: out var dynamicState, designations: out var designations)) {
                abortReason = $"source member seat {(sourceSlot + 1)} could not detach after reservation";
                break;
            }

            landed.Add(item: new LandedMember(SourceSlot: sourceSlot, TargetSlot: reservedSlot, Profile: profile, Position: position, Yaw: yaw, DynamicState: dynamicState, Designations: designations));
            var arrivalPosition = position;
            var arrivalYaw = yaw;
            var arrivalPlanarVelocity = dynamicState.PlanarVelocity;
            var arrivalVerticalVelocity = dynamicState.VerticalVelocity;

            // Overrides the destination's own fresh spawn pose with the positional-continuity mapping
            // (WorldPortalArrivalMath.ComputeArrival), then rotates the captured velocity the same way —
            // after the ordinary join above already embodied this member fresh under the destination's own
            // kit (appearance/grants/action-track state untouched; see
            // WorldPopulation.ApplyMappedArrival). destinationFacePosition/YawRadians were resolved once
            // for the whole group before this loop started; destinationFaceYawRadians and counterpartFrame
            // are shared (one door, one frame), but the position each member maps through is per-member — a
            // member with its own entry in transfer.MemberSeams maps through the point it actually crossed
            // on both ends, never the group's shared fallback, since two seats crossing abreast at
            // different lateral offsets would otherwise both map through whichever seat's hit happened to
            // open the coalesced group.
            if (transfer.Arrival == WorldPortalArrival.Mapped) {
                var memberSourcePosition = transfer.SourceSeamPosition;
                var memberDestinationPosition = destinationFacePosition;

                if ((transfer.MemberSeams is not null) && transfer.MemberSeams.TryGetValue(key: sourceSlot, value: out var memberSeam)) {
                    memberSourcePosition = memberSeam.SourcePosition;
                    memberDestinationPosition = WorldPortalArrivalMath.CounterpartSeam(destinationFrame: in counterpartFrame, seamU: memberSeam.SeamU, seamV: memberSeam.SeamV);
                }

                var mapped = WorldPortalArrivalMath.ComputeArrival(
                    travelerPosition: position,
                    travelerYawRadians: yaw,
                    travelerPlanarVelocity: dynamicState.PlanarVelocity,
                    travelerVerticalVelocity: dynamicState.VerticalVelocity,
                    sourcePosition: memberSourcePosition,
                    sourceYawRadians: transfer.SourceFaceYawRadians,
                    destinationPosition: memberDestinationPosition,
                    destinationYawRadians: destinationFaceYawRadians
                );

                arrivalPosition = mapped.Position;
                arrivalYaw = mapped.YawRadians;
                arrivalPlanarVelocity = mapped.PlanarVelocity;
                arrivalVerticalVelocity = mapped.VerticalVelocity;
            }

            commitMembers.Add(item: new WorldTransferCommitMember(Profile: profile, HasMappedArrival: (transfer.Arrival == WorldPortalArrival.Mapped), Position: arrivalPosition, YawRadians: arrivalYaw, PlanarVelocity: arrivalPlanarVelocity, VerticalVelocity: arrivalVerticalVelocity));
        }

        if ((abortReason is null) && (transfer.TestForceJoinRefusalOrdinal is { } forcedOrdinal)) {
            abortReason = $"TEST-ONLY forced refusal before escrow commit at member {forcedOrdinal} (world.transfer ... forcejoinrefusal:<n>)";
        }

        if (abortReason is null) {
            try {
                if (!targetAuthority.Commit(sourceAuthority: sourceAuthority, transferId: transfer.TransferId, members: commitMembers, reason: out var commitReason)) {
                    abortReason = $"'{targetName}' refused reserved commit ({commitReason})";
                }
            } catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException) {
                // Preserve every source recovery record and the exact commit payload. Subsequent fixed-point drains
                // query the destination's idempotent status and either publish the committed route, retry the live
                // lease, or restore the source after a confirmed missing/expired reservation. Never infer from an
                // I/O exception whether the destination applied the commit.
                m_inDoubtTransfers.Add(item: new InDoubtTransfer(
                    Transfer: transfer,
                    TargetAuthority: targetAuthority,
                    SourceAuthority: sourceAuthority,
                    TargetName: targetName,
                    Spawned: spawned,
                    SourceDeadlineTick: reservationRequest.DeadlineSourceTick,
                    Landed: landed,
                    CommitMembers: commitMembers,
                    MemberCount: members.Length
                ));
                Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} IN-DOUBT ('{targetName}' commit acknowledgement was lost: {exception.Message}) — recovery state retained for status reconciliation]");
                return;
            }
        }

        if (abortReason is not null) {
            if (landed.Count > 0) {
                targetAuthority.Abort(sourceAuthority: sourceAuthority, transferId: transfer.TransferId);
            }

            foreach (var member in landed) {
                source.Server.Population.RestoreDetachedSeat(slot: member.SourceSlot, profile: member.Profile, position: member.Position, yawRadians: member.Yaw, dynamicState: member.DynamicState, designations: member.Designations);
            }

            Console.Error.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} ABORTED ({abortReason}) — every landed member returned to '{transfer.SourceInstance}' at its exact source pose]");

            if (spawned) {
                ReapIfEmpty(name: targetName);
            }

            NoteResolvedTransferOutcome(transfer: in transfer, sourceName: transfer.SourceInstance, targetName: targetName, outcome: $"aborted:{abortReason}");

            return;
        }

        FinalizeCommittedTransfer(transfer: in transfer, targetAuthority: targetAuthority, targetName: targetName, spawned: spawned, landed: landed, memberCount: members.Length);
    }

    private void FinalizeCommittedTransfer(in PendingTransfer transfer, TransferAuthority targetAuthority, string targetName, bool spawned, List<LandedMember> landed, int memberCount) {

        // A traveler set down on a door's own threshold reads as a fresh entry edge on the destination's
        // next scan and is bounced straight back, so every face an arriving body already stands inside is
        // latched rather than discovered as a crossing. Seeded here, for the whole cohort at once, rather
        // than per member as each lands — the landing loop can still abort, and the unwind above restores
        // bodies, not latches, so a per-member seed would outlive its own member. Commit-time seeding needs
        // no inverse operation to keep in sync with rollback.
        foreach (var member in landed) {
            if (targetAuthority.Local is { } target) {
                SeedArrivalOccupancy(instance: target, seat: member.TargetSlot);
            }
        }

        // COMMIT: the whole cohort's join is certain, so the CLIENT-side state that mirrors and ROUTES a seat catches
        // up here — and only here, after every member's outcome is known, so an aborted member is never seen to have
        // left at all (see LandedMember's own remarks). The transfer's authoritative body work is already complete;
        // this route decides where subsequent presentation and input submissions follow it.
        foreach (var member in landed) {
            // TRAVELER-FOLLOW STAGE 1: any LOCAL roster slot the router currently has presenting from
            // (transfer.SourceInstance, member.SourceSlot) moves WITH this member — unconditional across
            // boot<->anywhere and anywhere<->anywhere, the ONE new write this stage adds. At most one roster slot
            // ever matches (a followed seat's own location is exactly its own presenting body), but the walk costs
            // O(4) regardless of which instance is source or destination.
            var followed = false;

            for (var followedSlot = 0; (followedSlot < WorldSeatBindings.SeatCount); followedSlot++) {
                var location = m_seatRouter.Location(slot: followedSlot);

                if (!string.Equals(a: location.InstanceName, b: transfer.SourceInstance, comparisonType: StringComparison.Ordinal) || (location.InstanceSlot != member.SourceSlot)) {
                    continue;
                }

                followed = true;
                // The seat's input-layer held state — destination-embodies doctrine: a traveler arrives in a new
                // world in a neutral stance, so a BindingEntryMode.Toggle latch (sprint held ON, say) does not ride
                // through the door. Generalized from the old boot-only call: an away<->away crossing carries the
                // identical doctrine, and previously never cleared it at all.
                _ = m_router().ClearSlotHeld(slot: followedSlot);
                m_seatRouter.Publish(slot: followedSlot, instanceName: targetName, instanceSlot: member.TargetSlot);
            }

            // Scoped to the BOOT instance on each side independently, because that is the only instance a
            // local client mirrors — a transfer between two non-boot instances touches neither, and an
            // unscoped write would clear or fill a boot seat belonging to somebody who never moved. A
            // followed seat's local participant does not vacate when it departs boot — it relocates (the
            // router publish above already records exactly where), so the roster's own occupied/device-bound
            // state stays as it was through the whole trip, and WorldClient.SubmitAwaySeatIntents keeps
            // reading a live seat rather than a vacated one. A followed seat returning to boot symmetrically
            // skips OccupySeat below: the slot was never vacated, so it is already occupied under the same
            // participant that left.
            if (!followed && string.Equals(a: transfer.SourceInstance, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                // The roster's own seat-vacated fact — the SAME one player.leave emits, from a second producer.
                _ = m_roster.VacateSeat(slot: member.SourceSlot);
            }

            // The mirror fact, for a traveler landing in the instance the client mirrors.
            if (!followed && string.Equals(a: targetName, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                _ = m_roster.OccupySeat(slot: member.TargetSlot, profile: member.Profile);
            }

            // The accepted transfer echoes its full decision on STDOUT — departed source seat, arrived target seat,
            // the transfer id, and the arrival pose read from the target's OWN snapshot (PlayerWhere is 1-based,
            // hence TargetSlot + 1) — so a caller reads the outcome here rather than inferring it from a later
            // world.instance.seats.
            var arrival = (targetAuthority.Local is { } localTarget
                ? localTarget.Server.Answer(query: new WorldQuery.PlayerWhere(Index: (member.TargetSlot + 1)))
                : new QueryAnswer(Text: $"remote authority {targetAuthority.Remote!.Endpoint} body:{member.TargetSlot}"));

            Console.Out.WriteLine(value: $"[world.transfer: transfer={transfer.TransferId} '{transfer.SourceInstance}' seat {(member.SourceSlot + 1)} departed -> '{targetName}' seat {(member.TargetSlot + 1)} arrived{((member.Profile is not null) ? $" as {member.Profile.Id}" : " (anonymous)")} — {arrival.Text}]");
        }

        // A freshly spawned destination that seated NOBODY (every member skipped at detach — see the defense-in-
        // depth branch above) is worth cleaning up rather than leaking an empty one-shot instance. ReapIfEmpty
        // already refuses a RETAINED (persistent) name.
        if (spawned && (landed.Count == 0)) {
            ReapIfEmpty(name: targetName);
        }

        // A SOURCE that this transfer just emptied is reaped by the SAME rule as any other departure.
        ReapIfEmpty(name: transfer.SourceInstance);

        // Only when boot is the SOURCE does the tape need to know which slots actually left — see
        // NoteResolvedTransferOutcome's own remarks; a boot-as-destination arrival is structurally unreplayable, so
        // it carries nothing here regardless of how many members landed.
        var departedBootSlots = (string.Equals(a: transfer.SourceInstance, b: BootInstanceName, comparisonType: StringComparison.Ordinal)
            ? landed.ConvertAll(converter: static member => member.SourceSlot)
            : []);

        NoteResolvedTransferOutcome(transfer: in transfer, sourceName: transfer.SourceInstance, targetName: targetName, outcome: $"committed:{landed.Count}/{memberCount}", departedBootSlots: departedBootSlots);
    }

    // The source's active local seats (0..LocalSeatCount-1), in slot order — a non-resolver `party` transfer's
    // member set, read live (a resolver-driven transfer's own member set is its FROZEN cohort instead — see
    // ApplyTransfer's own remarks).
    private static int[] ActiveLocalSeats(WorldServer server) {
        var members = new List<int>(capacity: WorldPopulation.LocalSeatCount);

        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            if (server.Population.IsActive(index: slot)) {
                members.Add(item: slot);
            }
        }

        return [.. members];
    }

    // A party member's travelling principal. A Seat-kind acting principal's own Drive claim covers only its
    // own body everywhere, and the destination reseeds its grants from scratch (never inheriting the
    // source's), so a `party` member other than the one that actually crossed can never be authorized under
    // the crossing seat's identity — it travels under its own Seat identity instead. The crossing member
    // itself, and every member under a Console-kind acting principal (whose Drive/all wildcard already
    // covers them all), keep the original acting principal. Used for the reservation, the pre-leave
    // standing check, and the leave+join itself, so none of the three can ever disagree on who a member
    // travels as.
    private static WorldPrincipal MemberTravelPrincipal(in PendingTransfer transfer, int slot) =>
        (((transfer.ActingPrincipal.Kind == PrincipalKind.Seat) && (transfer.ActingPrincipal.Index != slot))
            ? WorldPrincipal.Seat(slot: slot)
            : transfer.ActingPrincipal);

    // Leave(source) with its pose captured before the detach discards it — the abort-restoration half of an
    // atomic body transfer. Never player.leave <slot> instance:<name> / ReapIfEmpty / ApplySession(Leave):
    // those are destructive (park-with-grace still advances a parked body, and ReapIfEmpty would retire the
    // source out from under a transfer still in flight) — see WorldPopulation.TryDetachSeatForTransfer. The
    // Drive/leave standing re-check here is defensive: ApplyTransfer's own pre-check loop already proved it
    // for every still-active member immediately before this runs, and is never load-bearing on its own.
    private static bool TryDetachAndCaptureMember(WorldInstance source, int sourceSlot, string sourceName, WorldPrincipal actingPrincipal, out WorldIdentity? profile, out FixedVector3 position, out FixedQ4816 yaw, out WorldBody.TransferState dynamicState, out int[] designations) {
        profile = null;
        position = default;
        yaw = default;
        dynamicState = default;
        designations = [];

        if ((uint)sourceSlot >= WorldPopulation.LocalSeatCount) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (seat {(sourceSlot + 1)} out of range in '{sourceName}')]");

            return false;
        }

        if (!source.Server.Population.IsActive(index: sourceSlot) || (source.Server.Population.EntryBody(index: sourceSlot) is not { } body)) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (seat {(sourceSlot + 1)} is not active in '{sourceName}')]");

            return false;
        }

        if (source.Server.Grants.Allows(principal: actingPrincipal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: sourceSlot)) is { IsAllowed: false } leaveVerdict) {
            Console.Error.WriteLine(value: $"[world.transfer: refused ({actingPrincipal.Describe()} cannot leave '{sourceName}' seat {(sourceSlot + 1)} — {leaveVerdict.DescribeDenial()})]");

            return false;
        }

        // Captured before the detach — TryDetachSeatForTransfer discards pose, dynamic state, and
        // designations entirely (it only ever preserves the seat's Profile), so this is the one moment the
        // body's exact position/yaw, its perceivable dynamic state (velocity, dash overlay, in-flight timed
        // presses — see WorldBody.CaptureTransferState), and the seat's own designation register (an
        // Entry-level fact outside WorldBody's own reach — see WorldPopulation.CaptureDesignations) are all
        // still readable.
        position = body.FixedPosition;
        yaw = body.FixedYaw;
        dynamicState = body.CaptureTransferState();
        designations = source.Server.Population.CaptureDesignations(slot: sourceSlot);

        if (!source.Server.Population.TryDetachSeatForTransfer(slot: sourceSlot, profile: out profile)) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (seat {(sourceSlot + 1)} in '{sourceName}' has no body to transfer)]");

            return false;
        }

        return true;
    }

    // Records a resolver-driven transfer's decided outcome onto the boot instance's own replay tape — a
    // no-op for a non-resolver transfer (console world.transfer's raw ephemeral/persisted/existing forms
    // carry no destination row/scope key/generation id to report) and a no-op unless the crossing touches
    // the boot instance as source or destination (the tape's own scope). `departedBootSlots` defaults
    // empty — every call site but the committed one passes nothing, correctly: a refusal or an abort leaves
    // boot's own population untouched by definition.
    private void NoteResolvedTransferOutcome(in PendingTransfer transfer, string sourceName, string targetName, string outcome, IReadOnlyList<int>? departedBootSlots = null) {
        if ((transfer.ResolvedDestinationRow is not { } row) || (transfer.FrozenScopeKey is not { } scopeKey) || (transfer.FrozenGenerationId is not { } generationId)) {
            return;
        }

        if (!string.Equals(a: sourceName, b: BootInstanceName, comparisonType: StringComparison.Ordinal) &&
            !string.Equals(a: targetName, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
            return;
        }

        m_bootReplayTape.NoteTransfer(transferId: transfer.TransferId, destinationName: row.Name.Value, scopeKey: scopeKey, generationId: generationId, outcome: outcome, departedBootSlots: (departedBootSlots ?? []));
    }

    /// <summary>Disposes every instance this host owns. The boot instance's own graph belongs to the container and
    /// is untouched.</summary>
    public void Dispose() {
        foreach (var instance in m_instances.Values) {
            instance.Dispose();
        }

        foreach (var authority in m_remoteAuthorities.Values) {
            authority.Dispose();
        }

        m_instances.Clear();
        m_remoteAuthorities.Clear();
    }

    // The directory every non-boot instance's store hangs under, separator-terminated so a prefix test is a containment
    // test rather than a sibling-name test ("…/instances-other" must not read as inside "…/instances").
    private string InstancesRoot() => (Path.GetFullPath(path: Path.Combine(path1: m_stateRoot, path2: "instances")).TrimEnd(trimChar: Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

    // The "return means home" origin scan: every running instance's own resolved document path against the
    // destination's, through the same TryResolveDocumentPath probes ResolveByStableName's name-collision
    // fence already uses, so a spelling difference alone never false-refuses or false-matches. Names order
    // (ordinal) for determinism; a stopped instance is invisible by construction (removed from m_instances
    // by TryStop already). Two or more matches is reported ambiguous rather than adopting one arbitrarily.
    private bool TryFindRunningInstanceByOrigin(string documentPath, out string matchedName, out IReadOnlyList<string>? ambiguous) {
        matchedName = string.Empty;
        ambiguous = null;

        if (!TryResolveDocumentPath(path: documentPath, resolved: out var targetPath)) {
            return false;
        }

        List<string>? matches = null;

        foreach (var name in Names) {
            if (!TryResolveDocumentPath(path: m_instances[name].SourcePath, resolved: out var candidatePath) ||
                !string.Equals(a: targetPath, b: candidatePath, comparisonType: PathComparison)) {
                continue;
            }

            (matches ??= new List<string>()).Add(item: name);
        }

        if (matches is not { Count: > 0 }) {
            return false;
        }

        if (matches.Count > 1) {
            ambiguous = matches;

            return false;
        }

        matchedName = matches[0];

        return true;
    }

    // Mirrors WorldDefinitionLoader.TryResolve's explicit-path handling, plus a shipped-asset fallback so a
    // console verb can name "Assets/worlds/jump.world.json" regardless of the process's current directory
    // (the boot path needs the fallback only for its own default document; a named instance is always
    // explicit, so it needs both). A third probe under the shipped worlds directory itself is what lets a
    // portal facet's destination resolve a `references` row authored as a bare shipped-world filename
    // ("dive.world.json", exactly how play.world.json's own references section spells it). A rooted or
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
    internal static string CanonicalDocumentIdentity(string documentPath) =>
        (TryResolveDocumentPath(path: documentPath, resolved: out var resolved) ? resolved : documentPath);

    // A references row's locator is relative to the document that AUTHORS it, never to the process's output
    // directory. This is the one host-side fold shared by observed previews and traveler entry; both must name the
    // same physical origin or the resolver can assign two generations to one seamless circuit.
    internal static string ResolveReferenceDocument(WorldInstance source, string documentPath) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (!Path.IsPathRooted(path: documentPath)) {
            try {
                if (Path.GetDirectoryName(path: source.SourcePath) is { Length: > 0 } sourceDirectory) {
                    var besideSource = Path.GetFullPath(path: Path.Combine(path1: sourceDirectory, path2: documentPath));

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

    private static bool TryResolveDocumentPath(string path, out string resolved) {
        try {
            var direct = Path.GetFullPath(path: path);

            if (File.Exists(path: direct)) {
                resolved = direct;

                return true;
            }

            var fallback = Path.GetFullPath(path: Path.Combine(path1: AppContext.BaseDirectory, path2: path));

            if (File.Exists(path: fallback)) {
                resolved = fallback;

                return true;
            }

            var shippedWorlds = Path.GetFullPath(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "worlds", path4: path));

            if (File.Exists(path: shippedWorlds)) {
                resolved = shippedWorlds;

                return true;
            }
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            // A path the OS cannot even form is a path with no file at it, which is exactly what the caller refuses
            // by name — swallowing here keeps one refusal sentence instead of two spellings of "not found".
        }

        resolved = string.Empty;

        return false;
    }
}
