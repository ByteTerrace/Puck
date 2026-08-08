using System.Numerics;
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
/// path are flat: the boot world is a row like any other. The WIRING is not, and cannot cheaply be — the boot
/// instance's <see cref="Server.WorldServer"/> is a container singleton that the client, the seats, the editor, the
/// replay tape, the socket door, the audio director, the render frame source and every mutating console verb resolve
/// DIRECTLY. Flattening that needs a per-instance service scope so those consumers name an instance instead of the
/// container; until then the asymmetry is confined to two facts stated here rather than spread through the
/// vocabulary: the boot instance is stepped by <see cref="WorldServerStepShell"/> (which also drives the tape, the
/// console wait gate and the socket drain — bookkeeping no other instance has yet), and it is the only instance any
/// other verb can reach.</para>
/// <para><b>Seats and embodiment (instance embodiment, 2026-08-06).</b> A non-boot instance now has its own local-seat
/// table like the boot instance's — the <c>player.*</c> verbs' <c>instance:&lt;name&gt;</c> token enters,
/// drive (warp/face/run/stop), and leave a seat inside a NAMED instance, applying through that instance's OWN
/// <see cref="Server.WorldServer.ApplySession"/>/<see cref="Server.WorldServer.ApplyCommand"/> doors — the identical
/// path the boot instance's <c>player.*</c> verbs use, never a bypass. Seating carries the seated identity's declared
/// durable state in through the SAME cross-document durable channel (<see cref="Server.WorldOwnedWorlds.TryReadDurableState"/>)
/// the boot instance's own session-join already stages with — a snapshot taken ONCE at entry; the instance then
/// advances its own copy. <see cref="ReapIfEmpty"/> is the lifetime rule over that occupancy: a caller that just
/// vacated an instance's last active entry reaps it through the same door <see cref="TryStop"/> already exposes by
/// name. A live TCP peer entering a spawned instance (composing the existing peer-admission door with this same
/// seating seam) remains an UNBUILT stretch — see <c>WorldInstanceCommandModule</c>'s own remarks.</para>
/// <para><b>Still deliberately absent, and each is its own unit of work.</b> No per-instance replay tape, socket
/// door, addon runtime, or grant-gated start (starting one is ungated today); NO MACHINES — an instance's
/// <see cref="Server.WorldMachineHost"/> is constructed empty, so a document declaring machine-sourced screens starts
/// with every one of them dark (the start echo counts them, so the absence is read back rather than discovered); and
/// NO independent tick rate — every instance steps once per boot tick at the same 240 Hz, because differing rates
/// stop "tick-stamped" being a shared coordinate (docs/world-model.md, "Authored tick rate per world").</para>
/// <para><b>The name is a path segment.</b> An instance's owned worlds live in a directory named by its console name,
/// so admitting a name is admitting a filesystem location: <see cref="TryStart"/> refuses any name that is not one
/// safe segment, and independently refuses any name whose resolved store does not sit under the instances root. The
/// second rule is not redundant with the first — it is what makes the placement true whatever the platform's path
/// grammar turns out to do with a name.</para>
/// <para>Stepping folds into the SAME <c>IFixedStepSimulation.Step</c> call both boot shapes already drive — never a
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

    /// <summary>Initializes the registry with the boot world already admitted under
    /// <see cref="BootInstanceName"/>.</summary>
    /// <param name="bootServer">The container's authoritative server — the boot instance's own.</param>
    /// <param name="bootOrigin">The console's tracked document origin for the boot instance, read live.</param>
    /// <param name="bootOwnedWorlds">The boot instance's owned-world store, read for the machine id and the state
    /// root every later instance derives its own directory under.</param>
    /// <param name="router">The lazy input-router resolver — a departing transfer clears the source seat's
    /// input-layer held state through it (see <see cref="TryTransferMember"/>).</param>
    /// <param name="roster">The boot instance's client-side participant table — a transfer across the boot boundary
    /// emits its seat-vacated/seat-occupied facts through it (see <see cref="TryTransferMember"/>).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldInstanceHost(WorldServer bootServer, WorldDefinitionSource bootOrigin, WorldOwnedWorlds bootOwnedWorlds, Func<InputRouter> router, PlayerRoster roster) {
        ArgumentNullException.ThrowIfNull(argument: bootServer);
        ArgumentNullException.ThrowIfNull(argument: bootOrigin);
        ArgumentNullException.ThrowIfNull(argument: bootOwnedWorlds);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: roster);

        m_machineId = bootOwnedWorlds.MachineId;
        m_stateRoot = WorldStateRoot.Resolve();
        m_router = router;
        m_roster = roster;
        m_instances[BootInstanceName] = new WorldInstance(
            name: BootInstanceName,
            origin: () => bootOrigin.SourcePath,
            server: bootServer,
            ownedMachines: null
        );
    }

    /// <summary>Every running instance's name, ordinal-sorted — the boot instance included.</summary>
    public IReadOnlyList<string> Names => [.. m_instances.Keys.Order(comparer: StringComparer.Ordinal)];

    /// <summary>Looks up a running instance by name.</summary>
    /// <param name="name">The console-facing instance name.</param>
    /// <param name="instance">The instance, when found.</param>
    /// <returns>Whether an instance is running under <paramref name="name"/>.</returns>
    public bool TryGet(string name, out WorldInstance? instance) => m_instances.TryGetValue(key: name, value: out instance);

    /// <summary>Starts a new instance from a world document and admits it under <paramref name="name"/>. Constructs a
    /// fresh <see cref="WorldPopulation"/>, <see cref="WorldRenderEnvelope"/>, <see cref="WorldOwnedWorlds"/> (its own
    /// directory, never shared) and an EMPTY <see cref="WorldMachineHost"/> — nothing shared with any other
    /// instance.</summary>
    /// <param name="name">The console-facing name, which is ALSO the directory segment this instance's owned worlds
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

        // The name is a DIRECTORY SEGMENT before it is a label — it is the one component of this instance's
        // owned-worlds path. A name carrying a separator, a drive, or a traversal step therefore chooses where the
        // instance's documents are written: '..' resolves onto the BOOT catalog's own directory (two live stores over
        // one directory, and the reserved-name refusal bypassed by spelling it differently), '../..' seeds world
        // documents OUTSIDE the state root entirely, and a rooted name discards the state root altogether.
        // WorldSafeName refuses all of that BY CONSTRUCTION — empty, a reserved character, or a bare '.'/'..' — so
        // this is the one check left; there is no separate segment-safety re-check downstream.
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

        // Belt and braces: the segment rule is what an operator reads, this is what makes the placement TRUE. Whatever
        // the name turns out to mean to the platform's path grammar, the directory this host is about to create must
        // sit under the instances root, or nothing is created.
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

        // The instance's own NAME is the seed ladder's instance rung, so two instances of one document draw
        // independently while each stays reproducible from (document, instance name, draw history).
        if (!WorldDefinitionLoader.TryLoadFile(path: resolvedPath, definition: out var definition, reason: out reason, instanceIdentity: name)) {
            return false;
        }

        var machines = new WorldMachineHost(screens: [], engines: []);
        WorldInstance started;

        // Construction touches the file system (the owned-world store creates its directory and seeds documents into
        // it). This runs on the FIXED-STEP THREAD — world.instance.start routes Simulation — where an escaping
        // exception kills the pump and takes every world in the process down with it, the boot world included. An IO
        // failure here is a refusal like any other; nothing about it is worth the whole session.
        try {
            started = new WorldInstance(
                name: name,
                origin: () => resolvedPath,
                server: new WorldServer(
                    definition: definition!,
                    population: new WorldPopulation(definition: definition!),
                    profiles: new WorldOwnedWorlds(template: definition!, directory: ownedWorlds, machineId: m_machineId),
                    envelope: new WorldRenderEnvelope(),
                    machines: machines,
                    instanceIdentity: name
                ),
                ownedMachines: machines
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

    /// <summary>Reaps a running instance whose seat occupancy just hit zero — a LIFETIME RULE over the occupancy fact
    /// <see cref="Server.WorldPopulation.ActiveCount"/> already reports (see that member's own remarks on why it is
    /// ALREADY per-instance scoped, which is what makes reading it here honest), never bespoke teardown: any caller
    /// that just vacated an instance's last occupied slot calls this, and it is the SAME <see cref="TryStop"/> path
    /// <c>world.instance.stop</c> uses, applied by rule instead of by name. A no-op — never a refusal — for the boot
    /// instance (which <see cref="TryStop"/> refuses outright), a RETAINED instance (see <see cref="m_retainedInstances"/>
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

        if (!m_instances.Remove(key: name, value: out var instance)) {
            reason = $"no instance named '{name}'";

            return false;
        }

        // An explicit stop always wins, retained or not — unlike ReapIfEmpty's automatic rule, this is an operator
        // (or console script) asking for THIS name to go away right now. Clearing retention means a later name reuse
        // (a fresh world.instance.start under the same spelling) starts out with ordinary reap-on-empty rather than
        // inheriting a persistence flag from an instance that no longer exists.
        m_retainedInstances.Remove(item: name);
        instance.Dispose();
        reason = string.Empty;

        return true;
    }

    /// <summary>The directory an instance's owned worlds live under — derived from its name so two instances never
    /// share a store, and reported by <c>world.instance.status</c> so the placement is read back rather than
    /// inferred. NORMALIZED, so the answer is where files actually land rather than the spelling that got there;
    /// <see cref="TryStart"/> refuses any name whose answer escapes the instances root.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>The absolute owned-worlds directory for that instance.</returns>
    public string OwnedWorldsDirectory(string name) =>
        Path.GetFullPath(path: (string.Equals(a: name, b: BootInstanceName, comparisonType: StringComparison.Ordinal)
            ? Path.Combine(path1: m_stateRoot, path2: "owned-worlds")
            : Path.Combine(path1: InstancesRoot(), path2: name, path3: "owned-worlds")));

    /// <summary>Steps every instance EXCEPT the boot one, which <see cref="WorldServerStepShell"/> has already
    /// stepped this tick along with the tape, wait-gate and socket bookkeeping only it carries. Each instance
    /// advances its own tick sequence by the boot instance's step size, so every world in this process runs at one
    /// rate.</summary>
    /// <param name="stepTicks">The fixed step size in engine ticks, taken from the boot instance's own
    /// <see cref="FixedStepContext"/>.</param>
    public void StepInstancesBesideBoot(ulong stepTicks) {
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
            var tick = instance.CompletedTicks;
            var context = new FixedStepContext(ElapsedTicks: ((tick + 1UL) * stepTicks), StepTicks: stepTicks, Tick: tick);

            instance.Server.Step(context: in context);
        }
    }

    /// <summary>How a queued transfer's destination instance is resolved at DRAIN time — see
    /// <see cref="TransferDestination"/> for the per-case payload and <see cref="TryResolveDestination"/> for the
    /// resolution itself.</summary>
    internal enum TransferLifetime {
        /// <summary>The target must already be running under a given name (<c>world.instance.start</c> first) — the
        /// original, step-1 form. Refused by name when no instance answers to it.</summary>
        Existing,

        /// <summary>A BRAND-NEW instance, deterministically named from a site plus this host's per-site draw counter
        /// (see <see cref="MintFreshInstanceName"/>) — a fresh transfer is a NEW draw roll for that destination.
        /// Reaped like any other transient instance once its last occupant leaves (never retained).</summary>
        Fresh,

        /// <summary>A STABLE, caller-named instance: started from the destination document if not already running,
        /// else reused as-is. RETAINED (see <see cref="m_retainedInstances"/>) from the moment a transfer resolves
        /// it — two transfers naming the same persistent instance are two doors into one place, and the second must
        /// find the first traveler's instance still standing even if it is momentarily empty.</summary>
        Persistent,
    }

    /// <summary>A queued transfer's destination, as the console verb expressed it — resolved to a live
    /// <see cref="WorldInstance"/> exactly once per transfer by <see cref="TryResolveDestination"/> (a <c>party</c>
    /// transfer's whole member set shares that ONE resolution, so a <see cref="TransferLifetime.Fresh"/> destination
    /// mints its name once for the whole party, never once per body).</summary>
    internal readonly record struct TransferDestination {
        private TransferDestination(TransferLifetime lifetime, string? name, string? documentPath, string? site) {
            Lifetime = lifetime;
            Name = name;
            DocumentPath = documentPath;
            Site = site;
        }

        /// <summary>How this destination resolves.</summary>
        public TransferLifetime Lifetime { get; }

        /// <summary>The caller-named instance name — set for <see cref="TransferLifetime.Existing"/> and
        /// <see cref="TransferLifetime.Persistent"/>, <see langword="null"/> for <see cref="TransferLifetime.Fresh"/>
        /// (whose name is MINTED, never named).</summary>
        public string? Name { get; }

        /// <summary>The world document to start the instance from if it is not already running — set for
        /// <see cref="TransferLifetime.Fresh"/> and <see cref="TransferLifetime.Persistent"/>.</summary>
        public string? DocumentPath { get; }

        /// <summary>The site identifier a <see cref="TransferLifetime.Fresh"/> destination's name is drawn under —
        /// see <see cref="MintFreshInstanceName"/>.</summary>
        public string? Site { get; }

        /// <summary>An already-running instance named <paramref name="name"/> — refused at resolve time if none
        /// answers to it.</summary>
        public static TransferDestination Existing(string name) => new(lifetime: TransferLifetime.Existing, name: name, documentPath: null, site: null);

        /// <summary>A brand-new instance, deterministically named from <paramref name="site"/>'s draw counter and
        /// started from <paramref name="documentPath"/>.</summary>
        public static TransferDestination Fresh(string site, string documentPath) => new(lifetime: TransferLifetime.Fresh, name: null, documentPath: documentPath, site: site);

        /// <summary>A stable instance named <paramref name="name"/> — reused if already running, else started from
        /// <paramref name="documentPath"/>.</summary>
        public static TransferDestination Persistent(string name, string documentPath) => new(lifetime: TransferLifetime.Persistent, name: name, documentPath: documentPath, site: null);
    }

    /// <summary>Which of a source instance's local seats a queued transfer moves — see
    /// <see cref="PendingTransfer.Scope"/>.</summary>
    internal enum TransferScope {
        /// <summary>One named seat.</summary>
        Body,

        /// <summary>The source instance's WHOLE active local-seat set (0..<see cref="Server.WorldPopulation.LocalSeatCount"/>-1),
        /// computed from LIVE state at drain time, landing together in ONE destination — never one instance per
        /// member.</summary>
        Party,
    }

    /// <summary>One same-process body (or party) transfer queued for this host's ONE fixed drain point (see
    /// <see cref="DrainPendingTransfers"/>) — captured at ENQUEUE time as the request shape only. Every live-state
    /// check (both instances still running, the source seat(s) still active, a free destination seat, Drive
    /// authority) runs at DRAIN time against whatever state that tick actually holds, mirroring
    /// <see cref="Server.WorldServer"/>'s own pending-ops FIFO (compose/validate at apply, never at submit).</summary>
    private readonly record struct PendingTransfer(string SourceInstance, TransferScope Scope, int SourceSlot, TransferDestination Destination, WorldPrincipal ActingPrincipal);

    private readonly Queue<PendingTransfer> m_pendingTransfers = new();

    // Per-SITE fresh-instance draw counters — the seed ladder's instance rung is the running instance's OWN name
    // (WorldGeneratorEngine.ComputeSeedState), so a fresh transfer's whole point is to mint a name no earlier draw at
    // that site ever used. Advances by exactly one per MintFreshInstanceName call, a PURE function of call sequence —
    // never wall-clock, RNG, or tick-of-entry — mirroring WorldHandleTable.m_nextGeneration's own "moves only when a
    // caller asks, never with time" shape. Replay-stable because DrainPendingTransfers processes the pending FIFO in
    // the SAME order on every run of the SAME input, so the Nth fresh transfer drained for a site always mints the
    // SAME name.
    private readonly Dictionary<string, int> m_freshCounters = new(comparer: StringComparer.Ordinal);

    // Names a persistent-lifetime transfer has resolved at least once — retained through an occupancy dip to zero
    // (ReapIfEmpty refuses them by name) so a second traveler's transfer still finds the first traveler's instance
    // standing. Marked in TryResolveDestination whether that call started the instance or found it already running;
    // cleared by an explicit TryStop, which always wins over retention.
    private readonly HashSet<string> m_retainedInstances = new(comparer: StringComparer.Ordinal);

    /// <summary>Queues a same-process transfer for this host's next <see cref="DrainPendingTransfers"/> call —
    /// <c>world.transfer</c> is the only caller today. Enqueuing never fails: every check that can refuse (an
    /// unknown or unstartable instance, an out-of-range/empty/absent source seat, no free destination seat, a denied
    /// Drive grant) runs at drain time, so a refusal is reported once, at the SAME fixed point the transfer would
    /// otherwise have applied at — exactly like a rejected <see cref="Server.WorldServer"/> mutation.</summary>
    /// <param name="sourceInstance">The console-facing name of the instance the seat(s) currently occupy.</param>
    /// <param name="scope">Whether this moves one named seat or the source's whole active local-seat set.</param>
    /// <param name="sourceSlot">The source instance's 0-based local seat — ignored when <paramref name="scope"/> is
    /// <see cref="TransferScope.Party"/> (the member set is read live at drain time instead).</param>
    /// <param name="destination">How the destination instance resolves — see <see cref="TransferDestination"/>.</param>
    /// <param name="actingPrincipal">The principal that submitted the transfer — threaded UNCHANGED through both the
    /// leave-side Drive check and the destination's own <c>ApplySession(Join)</c> for every member, so each
    /// arrival's authority is attributed to the SAME principal that left rather than a principal this door
    /// invents.</param>
    public void EnqueueTransfer(string sourceInstance, TransferScope scope, int sourceSlot, TransferDestination destination, WorldPrincipal actingPrincipal) =>
        m_pendingTransfers.Enqueue(item: new PendingTransfer(SourceInstance: sourceInstance, Scope: scope, SourceSlot: sourceSlot, Destination: destination, ActingPrincipal: actingPrincipal));

    /// <summary>Drains every queued transfer at this host's ONE fixed point in its per-tick driving sequence —
    /// <c>WorldSimulation</c>/<c>HeadlessWorldSimulation</c> call this BEFORE stepping the boot instance or any other
    /// instance this tick (mirroring where <c>WorldServer.DrainPendingOps</c> sits relative to the rest of
    /// <c>WorldServer.Step</c>'s own body), so a transfer that lands this tick is reflected in BOTH instances' very
    /// next <c>Server.Step</c> this SAME tick — every traveler is advanced exactly once, by whichever instance now
    /// holds it, never by both and never by neither.</summary>
    public void DrainPendingTransfers() {
        while (m_pendingTransfers.TryDequeue(result: out var transfer)) {
            ApplyTransfer(transfer: in transfer);
        }
    }

    // The portal-entry trigger's enterable-volume envelope — one artifact (the face's own placement transform), one
    // truth: no separate authored region exists (see WorldPlacementRegion for the OTHER, explicitly-authored sensing
    // volume this deliberately does not reuse — a region's radius is its own authored fact, a portal's volume is
    // derived from geometry the door ALREADY carries). Chosen generously rather than tightly fitted to the
    // portal-frame creation's own box (half-extents ~1.02x0.68x0.05 world units at its default scale, per
    // ShapeDocument.Scale x CreationGeometry's 0.34 base half-extent) because a trigger that must be walked through
    // exactly, not just past, is a worse door than one with margin: HalfWidth covers the frame's width plus
    // sidestep room, HalfHeight reaches from well below a body's origin to well above it regardless of whether that
    // origin sits at the feet or the hip (this engine's body-origin convention is not pinned by any doc this facet
    // can cite), and FrontDepth is deep enough that a body advancing at ordinary walking speed cannot cross the
    // whole slab between two 240Hz ticks without ever sampling inside it.
    private const float PortalHalfWidth = 1.5f;
    private const float PortalFrontDepth = 2.0f;
    private const float PortalHalfHeight = 2.0f;

    /// <summary>Scans every running instance's document for portal faces (a <see cref="WorldPlacementFace"/> carrying
    /// a <see cref="WorldPlacementPortal"/> facet) against that instance's own active local seats, and enqueues a
    /// transfer for each EDGE — a seat whose body was outside the face's enterable volume last scan and is inside it
    /// now (see <see cref="WorldInstance.PortalOccupancy"/>). Called from <c>WorldSimulation</c>/
    /// <c>HeadlessWorldSimulation</c> immediately BEFORE <see cref="DrainPendingTransfers"/>, in the SAME tick: this
    /// scan reads positions as they stood at the end of the PREVIOUS tick's <c>Server.Step</c> (this tick's Step has
    /// not run yet), a pure function of that settled, replay-covered sim state — no wall-clock, RNG, or float ever
    /// reaches a decision (every comparison below runs in fixed point; the placement's authored float Position/
    /// YawDegrees are quantized to fixed point exactly once per portal per tick, the same boundary
    /// <c>Server.WorldEventFeed.CollectRegions</c> already crosses for region sensing). The scan-then-drain ordering
    /// is what lets a body's very first tick inside a portal's volume also be the tick its transfer lands.</summary>
    public void ScanPortalTriggers() {
        foreach (var name in Names) {
            if (!m_instances.TryGetValue(key: name, value: out var instance)) {
                continue;
            }

            ScanInstancePortals(instance: instance);
        }
    }

    // One instance's own portal scan: every placement's every portal-carrying face, against every active local seat.
    // Placement/face iteration order is the document's own declared order (already deterministic); seat order is
    // ascending 0..LocalSeatCount-1 — a fixed, replay-stable walk with no dependency on insertion or wall-clock.
    private void ScanInstancePortals(WorldInstance instance) {
        var definition = instance.Server.Definition;
        var population = instance.Server.Population;

        foreach (var placement in definition.Placements) {
            if ((placement is null) || (placement.FaceSources is not { Count: > 0 } faces)) {
                continue;
            }

            foreach (var face in faces) {
                if (face.Portal is not { } portal) {
                    continue;
                }

                ScanPortalFace(instance: instance, population: population, placement: placement, face: face, portal: portal);
            }
        }
    }

    // One portal face's own envelope (computed once, then tested against every local seat) — the face-local frame is
    // (Right, Up = world +Y, Normal), Normal the OUTWARD direction the enterable volume extends along, mirroring the
    // SAME local +Z-is-forward convention Client.WorldCreationFacets' derived screen billboards already assume
    // (Right = +X, Up = +Y, so Right x Up = +Z) — rotated here by the placement's own authored yaw, unlike that
    // simplified billboard, because a portal's front side has to be the SAME side regardless of which wall it is
    // mounted against (see play.world.json's east/west walls at yawDegrees=90). Float geometry, quantized to fixed
    // point ONCE per portal per tick at the end (WorldColliderSet.BuildColliders follows the identical
    // rotate-in-float-then-ToFixed shape for the same authored Position/YawDegrees fields). The result is
    // REVISION-CONSTANT — it reads only authored Position/YawDegrees, identical every tick until a document revision
    // moves them; computing it at revision-build (where colliders do) is the posture-symmetric optimization, deferred
    // here to keep the scan self-contained.
    private void ScanPortalFace(WorldInstance instance, WorldPopulation population, WorldPlacement placement, WorldPlacementFace face, WorldPlacementPortal portal) {
        var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (placement.YawDegrees * (MathF.PI / 180f)));
        var center = FixedVector3.FromVector3(value: placement.Position);
        var normal = FixedVector3.FromVector3(value: Vector3.Transform(value: Vector3.UnitZ, rotation: rotation)).Normalize();
        var right = FixedVector3.FromVector3(value: Vector3.Transform(value: Vector3.UnitX, rotation: rotation)).Normalize();
        var halfWidth = FixedQ4816.FromDouble(value: PortalHalfWidth);
        var halfHeight = FixedQ4816.FromDouble(value: PortalHalfHeight);
        var frontDepth = FixedQ4816.FromDouble(value: PortalFrontDepth);

        for (var seat = 0; (seat < WorldPopulation.LocalSeatCount); seat++) {
            var key = (PlacementId: placement.Id, Face: face.Face, Seat: seat);

            if (!population.IsActive(index: seat) || (population.EntryBody(index: seat) is not { } body)) {
                // An inactive seat contributes nothing and carries no stale "inside" state forward — the SAME
                // "contributes nothing" rule Server.WorldEventFeed.CollectRegions applies to an unresolved region
                // center, so a seat that leaves mid-transit and later rejoins the same slot re-arms rather than
                // firing on its very first tick back.
                instance.PortalOccupancy.Remove(key: key);

                continue;
            }

            var delta = (body.FixedPosition - center);
            var alongNormal = FixedVector3.Dot(left: delta, right: normal);
            var alongRight = FixedVector3.Dot(left: delta, right: right);
            var alongUp = delta.Y;

            var inside = ((alongNormal >= FixedQ4816.Zero) && (alongNormal <= frontDepth) &&
                (FixedQ4816.Abs(value: alongRight) <= halfWidth) &&
                (FixedQ4816.Abs(value: alongUp) <= halfHeight));

            var wasInside = (instance.PortalOccupancy.TryGetValue(key: key, value: out var previous) && previous);

            instance.PortalOccupancy[key] = inside;

            if (inside && !wasInside) {
                TriggerPortal(instance: instance, placement: placement, face: face, portal: portal, seat: seat);
            }
        }
    }

    // Fires one EDGE-triggered portal entry: resolves the facet to a TransferDestination/TransferScope/acting
    // principal and enqueues it on the SAME FIFO world.transfer already feeds — never a bypass, and never applied
    // here directly (DrainPendingTransfers, called right after this scan, is the one place a transfer actually
    // moves anyone).
    private void TriggerPortal(WorldInstance instance, WorldPlacement placement, WorldPlacementFace face, WorldPlacementPortal portal, int seat) {
        if (WorldDefinitionRows.FindReference(references: instance.Server.Definition.References, name: portal.Destination) is not { } reference) {
            Console.Error.WriteLine(value: $"[world.portal: '{instance.Name}'/{placement.Id}/{face.Face} refused (destination '{portal.Destination}' names no references row)]");

            return;
        }

        // Mirrors WorldPlacementCommandModule.DescribePortals' own resolution order exactly: the facet's own travel,
        // else the document's portals.portalDefaults.travel, else 'body' when the world declares no portals section.
        var defaultTravel = (instance.Server.Definition.Portals?.PortalDefaults.Travel ?? WorldPortalTravel.Body);
        var travel = (portal.Travel ?? defaultTravel);
        var scope = ((travel == WorldPortalTravel.Party) ? TransferScope.Party : TransferScope.Body);

        // fresh/persistent are the only two lifetimes a document can author (see WorldPortalLifetime's own remarks) —
        // 'Existing' is a live console-only fact no author can pin ahead of time, so it never appears here. A fresh
        // door's SITE is this face's own stable address (instance_placement_face — UNDERSCORE-joined, never ':',
        // which WorldSafeName's reserved set refuses; MintFreshInstanceName would otherwise mint an unstartable
        // name and silently swallow every fresh transfer through this door), so two different doors never share one
        // draw counter and the SAME door drawn twice (two travelers, or the same traveler leaving and re-entering)
        // mints successive, replay-stable names ("<site>-0", "<site>-1", ...).
        var site = $"{instance.Name}_{placement.Id}_{face.Face}";
        var destination = ((portal.Lifetime == WorldPortalLifetime.Persistent)
            ? TransferDestination.Persistent(name: portal.Instance!.Value.Value, documentPath: reference.Document)
            : TransferDestination.Fresh(site: site, documentPath: reference.Document));

        // The entering seat's OWN principal — identity continuity, exactly like world.transfer's own caller threads
        // the submitting principal through both halves of the move rather than inventing one at the door.
        var actingPrincipal = WorldPrincipal.Seat(slot: seat);

        EnqueueTransfer(sourceInstance: instance.Name, scope: scope, sourceSlot: seat, destination: destination, actingPrincipal: actingPrincipal);
        Console.Out.WriteLine(value: $"[world.portal: '{instance.Name}' seat {(seat + 1)} entered {placement.Id}/{face.Face} -> queued transfer to '{portal.Destination}' (lifetime={WorldPortalTokens.LifetimeToken(lifetime: portal.Lifetime)} travel={WorldPortalTokens.TravelToken(travel: travel)})]");
    }

    // The next deterministic fresh-instance name for a SITE: "<site>-<n>", n the site's own draw counter (see
    // m_freshCounters). Never wall-clock, RNG, or tick-of-entry — see that field's own remarks for why this is
    // replay-stable.
    private string MintFreshInstanceName(string site) {
        var ordinal = m_freshCounters.GetValueOrDefault(key: site);

        m_freshCounters[site] = (ordinal + 1);

        return $"{site}-{ordinal}";
    }

    // Resolves (spawning or starting as needed) a queued transfer's destination — the ONE place a TransferDestination
    // becomes a live WorldInstance, so a party's whole member set shares this SINGLE resolution (a Fresh destination
    // mints its name once here, not once per member). `spawned` is true only when THIS call started a BRAND-NEW
    // instance (Fresh always; Persistent only when it was not already running) — ApplyTransfer reads it to decide
    // whether an empty destination is worth reaping when every member's join fails.
    private bool TryResolveDestination(TransferDestination destination, out WorldInstance? resolved, out string resolvedName, out bool spawned, out string reason) {
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
                resolvedName = destination.Name!;

                if (m_instances.TryGetValue(key: resolvedName, value: out resolved)) {
                    spawned = false;
                    // Reached by name through a persistent-lifetime transfer — from this point on it is retained
                    // even if it happens to be empty right now (e.g. it was only ever started, never yet joined).
                    m_retainedInstances.Add(item: resolvedName);
                    reason = string.Empty;

                    return true;
                }

                if (!TryStart(name: resolvedName, path: destination.DocumentPath!, instance: out resolved, reason: out reason)) {
                    spawned = false;

                    return false;
                }

                spawned = true;
                m_retainedInstances.Add(item: resolvedName);

                return true;

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

    // Applies one queued transfer: resolves its member set (one seat, or the source's whole active local-seat set
    // for a party) and its destination (spawning or starting it at most ONCE for the whole transfer), then moves
    // each member through TryTransferMember — no Server.Step of any instance runs between the first and the last
    // member, so a party lands together rather than in sibling instances or interleaved with other sim advancement.
    private void ApplyTransfer(in PendingTransfer transfer) {
        if (!m_instances.TryGetValue(key: transfer.SourceInstance, value: out var source)) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (no instance named '{transfer.SourceInstance}')]");

            return;
        }

        // The member slots this transfer moves: the source's WHOLE active local-seat set for `party` (read live,
        // right now — the atomic drain is what makes "active" mean the same thing for every member below), or just
        // the one requested seat for `body`.
        var members = ((transfer.Scope == TransferScope.Party) ? ActiveLocalSeats(server: source.Server) : [transfer.SourceSlot]);

        if (members.Length == 0) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (no active local seat in '{transfer.SourceInstance}' to party-transfer)]");

            return;
        }

        // A destination naming the SAME instance as the source is refused up front for Existing/Persistent, both of
        // which know their name before any spawn. A Fresh destination cannot self-target by construction (a freshly
        // minted name is never one already running), so there is nothing to pre-check for it here.
        if ((transfer.Destination.Name is { } destinationName) && string.Equals(a: transfer.SourceInstance, b: destinationName, comparisonType: StringComparison.Ordinal)) {
            Console.Error.WriteLine(value: $"[world.transfer: refused ('{transfer.SourceInstance}' names both the source and the target)]");

            return;
        }

        if (!TryResolveDestination(destination: transfer.Destination, resolved: out var target, resolvedName: out var targetName, spawned: out var spawned, reason: out var destinationReason)) {
            Console.Error.WriteLine(value: $"[world.transfer: refused ({destinationReason})]");

            return;
        }

        // Whole-transfer ALL-OR-NOTHING: every member needs a free destination seat, checked BEFORE any member
        // leaves. A party of N entering a destination with fewer than N free seats stays WHOLLY at the source rather
        // than splitting across two worlds (stranding's uglier cousin — a partial move would leave some members here
        // and some there). A `body` (one member) is covered trivially. A destination this transfer freshly spawned
        // but cannot fill is reaped here, not leaked (ReapIfEmpty no-ops a retained persistent name).
        var freeSeats = CountFreeLocalSeats(server: target!.Server);

        if (freeSeats < members.Length) {
            Console.Error.WriteLine(value: $"[world.transfer: refused ('{targetName}' has {freeSeats} free local seat(s), the transfer needs {members.Length})]");

            if (spawned) {
                ReapIfEmpty(name: targetName);
            }

            return;
        }

        // Whole-transfer ALL-OR-NOTHING across AUTHORIZATION too, not just capacity (CountFreeLocalSeats above):
        // pre-check every member's own leave standing — Drive over its own body under its travelling principal —
        // BEFORE any member leaves, so a member blocked by a drive gate (a revoked grant today, a combat CC/death
        // gate later) refuses the WHOLE party rather than letting the rest split off to the destination while it
        // strands at the source. One blocked member names itself and why. A "jailed member stays, the rest travels
        // on" flavor is legitimate FUTURE authored policy — never a silent engine split.
        foreach (var slot in members) {
            var standingPrincipal = MemberTravelPrincipal(transfer: in transfer, slot: slot);

            if (source.Server.Grants.Allows(principal: standingPrincipal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: slot)) is { IsAllowed: false } standing) {
                Console.Error.WriteLine(value: $"[world.transfer: refused ({standingPrincipal.Describe()} cannot leave '{transfer.SourceInstance}' seat {(slot + 1)} — {standing.DescribeDenial()}); the whole transfer is held]");

                if (spawned) {
                    ReapIfEmpty(name: targetName);
                }

                return;
            }
        }

        var landed = 0;

        foreach (var slot in members) {
            var memberPrincipal = MemberTravelPrincipal(transfer: in transfer, slot: slot);

            if (TryTransferMember(source: source, sourceSlot: slot, sourceName: transfer.SourceInstance, target: target!, targetName: targetName, actingPrincipal: memberPrincipal)) {
                landed++;
            }
        }

        // A freshly spawned destination that seated NOBODY (every member's join refused — a doc-specific rejection,
        // most likely) is worth cleaning up rather than leaking an empty one-shot instance. ReapIfEmpty already
        // refuses a RETAINED (persistent) name, so a freshly-STARTED persistent instance survives this exactly as
        // the reap-interaction rule requires — it is never torn down just because it raced its own first join.
        if (spawned && (landed == 0)) {
            ReapIfEmpty(name: targetName);
        }

        // A SOURCE that this transfer just emptied is reaped by the SAME rule as any other departure — a transfer
        // is "the last occupant left" exactly as much as a player.leave <slot> instance:<name> is, and ReapIfEmpty is already
        // the one place that fact is judged: a no-op for boot, for a RETAINED (persistent) name (a party emptying
        // 'town' in the docs' own example leaves it standing, by design), for an unknown name, or for an instance
        // that still holds an active entry. A fresh instance with nobody left in it is exactly the case this reaps —
        // never leaking a one-shot destination once its traveler(s) move on again.
        ReapIfEmpty(name: transfer.SourceInstance);
    }

    // The source's active local seats (0..LocalSeatCount-1), in slot order — a `party` transfer's member set,
    // snapshotted once before this transfer's first detach (a slot's own active flag is unaffected by another slot's
    // detach, so no separate copy is needed to keep the set stable across the loop).
    private static int[] ActiveLocalSeats(WorldServer server) {
        var members = new List<int>(capacity: WorldPopulation.LocalSeatCount);

        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            if (server.Population.IsActive(index: slot)) {
                members.Add(item: slot);
            }
        }

        return [.. members];
    }

    // How many local seats (0..LocalSeatCount-1) are free in a running instance — the destination-capacity term a
    // transfer's whole-party all-or-nothing pre-check reads before any member leaves its source.
    private static int CountFreeLocalSeats(WorldServer server) {
        var free = 0;

        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            if (!server.Population.IsActive(index: slot)) {
                free++;
            }
        }

        return free;
    }

    // A party member's TRAVELLING principal. A Seat-kind acting principal's own Drive claim covers ONLY its own body
    // everywhere, and the DESTINATION reseeds its grants from scratch (never inheriting the source's), so a `party`
    // member OTHER than the one that actually crossed can never be authorized under the crossing seat's identity — it
    // travels under ITS OWN Seat identity instead (the honest N-body reading of "the entering body's own principal"
    // the diegetic trigger threads, never a borrowed one). The crossing member itself, and every member under a
    // Console-kind acting principal (whose Drive/all wildcard already covers them all — the console
    // `world.transfer party` path), keep the ORIGINAL acting principal. Used for BOTH the pre-leave standing check
    // and the leave+join itself, so the two can never disagree on who a member travels as.
    private static WorldPrincipal MemberTravelPrincipal(in PendingTransfer transfer, int slot) =>
        (((transfer.ActingPrincipal.Kind == PrincipalKind.Seat) && (transfer.ActingPrincipal.Index != slot))
            ? WorldPrincipal.Seat(slot: slot)
            : transfer.ActingPrincipal);

    // LEAVE(source) then JOIN(target), synchronously, with no Server.Step of either instance run between them — the
    // atomic core both a single `body` transfer and each member of a `party` transfer share. Every refusal below
    // leaves BOTH instances exactly as they were: the source seat is detached only after every up-front check
    // passes, and a destination join refusal (the one path that can still fail after detach) reinstates the source
    // seat rather than losing the traveler. A party shares ONE destination across many calls of this method, so the
    // destination's resolved (possibly freshly minted) name is passed in rather than re-read per member.
    private bool TryTransferMember(WorldInstance source, int sourceSlot, string sourceName, WorldInstance target, string targetName, WorldPrincipal actingPrincipal) {
        if ((uint)sourceSlot >= WorldPopulation.LocalSeatCount) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (seat {(sourceSlot + 1)} out of range in '{sourceName}')]");

            return false;
        }

        if (!source.Server.Population.IsActive(index: sourceSlot)) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (seat {(sourceSlot + 1)} is not active in '{sourceName}')]");

            return false;
        }

        // The SAME Drive/body:<slot> gate ApplySession's own Leave case checks — bypassing player.leave <slot> instance:<name>
        // skips its park/reap TEARDOWN, never its authority.
        if (source.Server.Grants.Allows(principal: actingPrincipal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: sourceSlot)) is { IsAllowed: false } leaveVerdict) {
            Console.Error.WriteLine(value: $"[world.transfer: refused ({actingPrincipal.Describe()} cannot leave '{sourceName}' seat {(sourceSlot + 1)} — {leaveVerdict.DescribeDenial()})]");

            return false;
        }

        var targetSlot = FindFreeLocalSeat(server: target.Server, actingPrincipal: actingPrincipal);

        if (targetSlot < 0) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (no free local seat in '{targetName}')]");

            return false;
        }

        // LEAVE(source) — a clean, non-advancing, non-parking removal. Never player.leave <slot> instance:<name> / ReapIfEmpty /
        // ApplySession(Leave): those are destructive (park-with-grace still ADVANCES a parked body, and ReapIfEmpty
        // would retire the source out from under a transfer still in flight) — see
        // WorldPopulation.TryDetachSeatForTransfer.
        if (!source.Server.Population.TryDetachSeatForTransfer(slot: sourceSlot, profile: out var profile)) {
            Console.Error.WriteLine(value: $"[world.transfer: refused (seat {(sourceSlot + 1)} in '{sourceName}' has no body to transfer)]");

            return false;
        }

        // JOIN(target) — the target's OWN normal join path, so it assigns kit/appearance/grants from ITS OWN tables
        // (never anything carried from the source). SessionRequest.Join is name-keyed against the TARGET instance's
        // own owned-worlds catalog, which never holds an identity minted in the source's (a separate directory per
        // instance — OwnedWorldsDirectory), so this joins anonymous first; the SetSeatProfile call below is the one
        // join-side addition that threads the traveler's ALREADY-RESOLVED WorldIdentity through directly, bypassing
        // the by-name lookup that would otherwise silently drop it. The SAME acting principal drives both halves —
        // the target never sees a fresh, invented principal.
        var reply = target.Server.ApplySession(request: new SessionRequest.Join(Principal: actingPrincipal, Slot: targetSlot, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        if (!reply.Accepted) {
            // The target refused a join this method already slot-picked and authority-checked itself — reinstate
            // rather than strand the traveler in neither instance. A fresh ActivateSeat (not a pose restore — pose
            // was never part of what a transfer carries) is the same shape a first join into the source would have
            // taken.
            source.Server.Population.ActivateSeat(slot: sourceSlot, profile: profile);
            Console.Error.WriteLine(value: $"[world.transfer: refused ('{targetName}' rejected the join — {reply.Reason}) — '{sourceName}' seat {(sourceSlot + 1)} reinstated]");

            return false;
        }

        // DEPARTURE and ARRIVAL are now certain, so the CLIENT-side state that mirrors a seat catches up here — and
        // only here, AFTER the join's accept, so a refused-and-reinstated traveler is left exactly as it was (the
        // reinstate path really does leave both instances as they were). Every write below is presentation state:
        // the simulation is untouched by any of it, and nothing it writes feeds back into a tick.
        //
        // Scoped to the BOOT instance on each side independently, because that is the only instance a local client
        // mirrors (see this type's own "what is flat and what is not" remark) — a transfer between two non-boot
        // instances legitimately touches neither, and an unscoped write would clear or fill a boot seat belonging to
        // somebody who never moved.
        if (string.Equals(a: sourceName, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
            // The seat's input-layer held state — destination-embodies doctrine: a traveler arrives in a new world in
            // a neutral stance, so a BindingEntryMode.Toggle latch (sprint held ON, say) does not ride through the
            // door. Unlike player.engage/disengage (input rerouting, not a stop — the latch survives those on
            // purpose, see PlayerCommandModule's own remarks), a transfer is a genuine departure.
            _ = m_router().ClearSlotHeld(slot: sourceSlot);
            // The roster's own seat-vacated fact — the SAME one player.leave emits, from a second producer, never a
            // transfer-shaped special case inside the roster (see PlayerRoster.VacateSeat). Without it the departed
            // slot keeps its participant: world.players still lists the traveler's identity, and player.join on that
            // slot refuses as "already joined" while the server has it free.
            _ = m_roster.VacateSeat(slot: sourceSlot);
        }

        if (profile is not null) {
            target.Server.Population.SetSeatProfile(slot: targetSlot, profile: profile);
        }

        // The mirror fact, for a traveler landing in the instance the client mirrors: without it an arrival is
        // invisible to every roster-gated read and the seat cannot be driven from here at all.
        if (string.Equals(a: targetName, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
            _ = m_roster.OccupySeat(slot: targetSlot, profile: profile);
        }

        // The accepted transfer echoes its full decision on STDOUT — departed source seat, arrived target seat, and
        // the arrival pose read from the target's OWN snapshot (PlayerWhere is 1-based, hence targetSlot + 1) — so a
        // caller reads the outcome here rather than inferring it from a later world.instance.seats.
        var arrival = target.Server.Answer(query: new WorldQuery.PlayerWhere(Index: (targetSlot + 1)));

        Console.Out.WriteLine(value: $"[world.transfer: '{sourceName}' seat {(sourceSlot + 1)} departed -> '{targetName}' seat {(targetSlot + 1)} arrived{((profile is not null) ? $" as {profile.Id}" : " (anonymous)")} — {arrival.Text}]");

        return true;
    }

    // The destination slot a transfer lands on. A Seat-kind acting principal is bound to ITS OWN slot number
    // EVERYWHERE — WorldServer.ApplySession's Join case reads a seat's own Drive/body:slot grant (seeded once, at
    // every instance's construction, identically) as "this principal legitimately IS this seat", so a Seat(3)
    // principal can never join slot 0 under its own claim no matter how empty the destination is. A same-process
    // transfer therefore prefers the traveler's OWN slot number when it is free there — the one placement its
    // identity actually carries authority over — before falling back to the lowest free slot, which remains the
    // right (and only meaningful) rule for a Console-kind principal, whose Drive/all wildcard carries no fixed slot
    // identity to prefer. Discovered running a portal crossing under a non-seat-1 identity: every crossing used to
    // land on the lowest free slot regardless of who was crossing, so only a traveler whose own index already
    // happened to be 0 (or matched wherever they'd land) could ever actually complete a same-process transfer.
    private static int FindFreeLocalSeat(WorldServer server, WorldPrincipal actingPrincipal) {
        if ((actingPrincipal.Kind == PrincipalKind.Seat) && ((uint)actingPrincipal.Index < WorldPopulation.LocalSeatCount) && !server.Population.IsActive(index: actingPrincipal.Index)) {
            return actingPrincipal.Index;
        }

        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            if (!server.Population.IsActive(index: slot)) {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Disposes every instance this host owns. The boot instance's own graph belongs to the container and
    /// is untouched.</summary>
    public void Dispose() {
        foreach (var instance in m_instances.Values) {
            instance.Dispose();
        }

        m_instances.Clear();
    }

    // The directory every non-boot instance's store hangs under, separator-terminated so a prefix test is a containment
    // test rather than a sibling-name test ("…/instances-other" must not read as inside "…/instances").
    private string InstancesRoot() => (Path.GetFullPath(path: Path.Combine(path1: m_stateRoot, path2: "instances")).TrimEnd(trimChar: Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

    // Mirrors WorldDefinitionLoader.TryResolve's explicit-path handling, plus a shipped-asset fallback so a console
    // verb can name "Assets/worlds/jump.world.json" regardless of the process's current directory (the boot path
    // needs the fallback only for its OWN default document; a named instance is always explicit, so it needs both).
    // A THIRD probe under the shipped worlds directory itself (WorldDefinitionLoader.DefaultRelativePath's own
    // "Assets/worlds/" convention) is what lets a portal facet's destination resolve a `references` row authored as
    // a BARE shipped-world filename ("dive.world.json", exactly how play.world.json's own references section spells
    // it) — the SAME bare spelling a console world.transfer caller would otherwise also have to know to prefix by
    // hand. A rooted or already-relative-enough path still resolves at the first two probes; this one only ever
    // fires for a bare filename neither of those found.
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
