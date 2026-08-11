using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// One running copy of a world's simulation in this process — an <i>instance</i> in the plan of record's own words
/// (docs/world-model.md, "The words"): a world is the only first-class noun, and an instance is a running copy of
/// one. Every instance this process runs is an entry of this type, including the world the process booted with, so
/// the read-back surface carries one kind of row rather than a privileged world plus a lesser class beside it.
/// </summary>
/// <remarks><para>What still separates the boot instance from the rest is wiring, not kind, and it is named where it
/// bites: the boot instance is the only one the container gives a client, seats, an editor, a replay tape, a socket
/// door, a machine host with real screens, and the mutating console verbs. Every other instance holds a fresh
/// <see cref="Server.WorldServer"/>, <see cref="WorldPopulation"/>, <see cref="WorldOwnedWorlds"/> and an empty
/// <see cref="WorldMachineHost"/> — reachable only through <see cref="WorldInstanceCommandModule"/>'s verbs, sharing
/// none of the boot instance's singletons. De-globalising the rest is an arc, not a rename; see
/// <see cref="WorldInstanceHost"/>'s remarks for the standing list.</para>
/// <para>Not thread-safe, and it does not need to be: every reader and the one writer
/// (<see cref="WorldInstanceHost.StepInstancesBesideBoot"/>) run on the launcher's single fixed-step thread, and the
/// verbs that start or retire an instance route <c>Simulation</c>, which applies on that same thread at a tick
/// boundary.</para></remarks>
internal sealed class WorldInstance : IDisposable {
    private readonly Func<string> m_origin;
    private readonly IDisposable? m_ownedAdjacencies;

    /// <summary>Initializes one running instance's held graph.</summary>
    /// <param name="name">The console-facing instance name, unique among running instances.</param>
    /// <param name="origin">Reads the document path this instance currently answers for. A delegate rather than a
    /// stored string because the boot instance's origin moves (a <c>world.load</c> retargets
    /// <c>WorldDefinitionSource.SourcePath</c>), and a second copy of a value that moves is a second copy that goes
    /// stale.</param>
    /// <param name="server">This instance's own authoritative server.</param>
    /// <param name="ownedMachines">The machine host this instance owns and must dispose, or <see langword="null"/>
    /// when the container owns it (the boot instance) — disposal follows ownership, never presence.</param>
    /// <param name="link">This instance's own transport — the same two-line <see cref="LoopbackTransport"/> pattern
    /// <c>WorldBootComposition</c> wires for the boot instance, now held uniformly on every row (traveler-follow
    /// stage 1): <see cref="WorldInstanceHost.TryGetLink"/> is the one door a presentation-side consumer (an
    /// away-seat intent submission, an away view's mirror attach) resolves it through.</param>
    /// <param name="ownedAdjacencies">The per-instance adjacency resolver this row owns, or null for boot wiring.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldInstance(string name, Func<string> origin, WorldServer server, WorldMachineHost? ownedMachines, IServerLink link, IDisposable? ownedAdjacencies = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentNullException.ThrowIfNull(argument: origin);
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: link);

        m_origin = origin;
        Name = name;
        Server = server;
        OwnedMachines = ownedMachines;
        Link = link;
        m_ownedAdjacencies = ownedAdjacencies;
    }

    /// <summary>This instance's own authoritative server — a distinct object graph per instance.</summary>
    public WorldServer Server { get; }

    /// <summary>This instance's own transport — see this type's constructor remarks.</summary>
    public IServerLink Link { get; }

    /// <summary>This instance's portal-crossing edge state, read and written by
    /// <see cref="WorldInstanceHost"/>'s diegetic boundary scan (<see cref="WorldInstanceHost.ScanBootBoundaryTriggers"/>
    /// for the boot instance, <see cref="WorldInstanceHost.StepInstancesBesideBoot"/> for every other). Scoped to this
    /// instance object — a name reused after <see cref="WorldInstanceHost.TryStop"/> starts a brand-new instance with
    /// a brand-new latch rather than inheriting a departed instance's occupancy, exactly like every other
    /// per-instance table here.</summary>
    public WorldPortalOccupancy PortalOccupancy { get; } = new();

    /// <summary>The console-facing instance name.</summary>
    public string Name { get; }

    /// <summary>The machine host this instance owns and disposes, or <see langword="null"/> when the container
    /// owns it.</summary>
    public WorldMachineHost? OwnedMachines { get; }

    /// <summary>The number of fixed ticks this instance has completed since it started — derived from its own server
    /// (<see cref="Server.WorldServer.NextInputTick"/>, whose one writer is that server's <c>Step</c>) rather than
    /// counted a second time here, so the step cursor and the read-back can never disagree. An instance started
    /// mid-session counts from its own zero; there is no shared absolute tick, and every instance now advances on
    /// its own authored rate (see <see cref="WorldInstanceHost.StepInstancesBesideBoot"/>), so this count is never
    /// comparable across instances running different rates — only each instance's own trajectory means anything.</summary>
    public ulong CompletedTicks => (Server.NextInputTick - 1UL);

    /// <summary>The document path this instance currently answers for.</summary>
    public string SourcePath => m_origin();

    /// <summary>The live pause lever for this instance's own schedule — operational state only: never
    /// document-authored, never journaled, and untouched by any view (the world.rate command module is its only
    /// writer). An authored <c>simulation.rateHz</c> of 0 is the durable stop; this is the live one, and the two are
    /// independent — pausing never touches the authored rate, and resuming restores the exact schedule
    /// <see cref="ScheduleAccumulatorTicks"/> already holds (no skew). Default <see langword="false"/>: an instance
    /// runs the instant it is admitted, exactly as before this lever existed.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Engine ticks banked toward this instance's own next step against the host's master timeline (see
    /// <see cref="WorldInstanceHost.StepInstancesBesideBoot"/>) — this instance's own per-instance accumulator, so
    /// an instance running faster than the master cadence steps more than once per master tick and one running
    /// slower steps less than once. Held exactly where it is while <see cref="IsPaused"/> (or while the authored
    /// rate is the durable stop, 0) — nothing is banked toward a step that will not happen, and resuming continues
    /// on the identical schedule the instance would have kept had it never paused. The boot instance does not use
    /// this field: the master pump's own cadence tracks the boot world's own authored rate (falling back to
    /// <c>Puck.Launcher.LauncherHostLoop.DefaultUpdateRate</c> only while boot itself is stopped), so boot's own
    /// schedule matches the master timeline 1:1 by construction once its own <see cref="IsPaused"/>/rate-0 gate is
    /// considered — see <see cref="WorldInstanceHost.ShouldStepBoot"/>.</summary>
    public ulong ScheduleAccumulatorTicks { get; set; }

    /// <summary>This instance's own exact engine-tick elapsed clock, accumulated additively one step width at a
    /// time rather than re-derived as <c>(tick + 1) * stepWidth</c> — the identical
    /// discontinuity <c>Puck.Launcher.FixedStepPump</c>'s own <c>m_elapsedTicks</c> field exists to avoid (see that
    /// type's own remarks): a product re-derivation is only valid while <c>stepWidth</c> has been constant for every
    /// tick it counts, which a rate change this instance's own document authors (a <c>world.load</c>/<c>.reset</c>/
    /// <c>.reload</c> swap) breaks the instant it fires. <see cref="WorldInstanceHost.StepInstancesBesideBoot"/> is
    /// this field's one writer, adding the actual step width just taken after every <c>Server.Step</c> call, so the
    /// value stays monotonic and rate-change-safe across the instance's whole lifetime, not merely within one master
    /// call's catch-up batch.</summary>
    public ulong ElapsedEngineTicks { get; set; }

    /// <summary>Disposes what this instance owns. A no-op for the boot instance, whose machine host belongs to the
    /// container and outlives any retirement of the entry.</summary>
    public void Dispose() {
        m_ownedAdjacencies?.Dispose();
        OwnedMachines?.Dispose();
    }
}
