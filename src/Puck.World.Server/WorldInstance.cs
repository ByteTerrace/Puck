using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// One running copy of a world's simulation in this process — an <i>instance</i> in the plan of record's own words
/// (docs/vision.md, "The words"): a world is the only first-class noun, and an instance is a running copy of
/// one. Every instance a host runs is an entry of this type, including a desktop's boot world, so the read-back
/// surface carries one kind of row rather than a privileged world plus a lesser class beside it.
/// </summary>
/// <remarks>Not thread-safe, and it does not need to be: every reader and the one writer
/// (<see cref="WorldInstanceHost.StepInstances"/>) run on the host's single fixed-step thread, and the verbs that
/// start or retire an instance route <c>Simulation</c>, which applies on that same thread at a tick boundary.</remarks>
public sealed class WorldInstance : IDisposable {
    private readonly Func<string> m_origin;
    private readonly IDisposable? m_ownedAdjacencies;
    private readonly WorldPeerNetwork? m_ownedNetwork;

    /// <summary>Initializes one running instance's held graph.</summary>
    /// <param name="name">The console-facing instance name, unique among running instances.</param>
    /// <param name="origin">Reads the document path this instance currently answers for. A delegate rather than a
    /// stored string because a desktop's boot origin moves (a <c>world.load</c> retargets
    /// <c>WorldDefinitionSource.SourcePath</c>), and a second copy of a value that moves is a second copy that goes
    /// stale.</param>
    /// <param name="server">This instance's own authoritative server.</param>
    /// <param name="ownedMachines">The machine host this instance owns and must dispose, or <see langword="null"/>
    /// when the container owns it (a desktop's boot instance) — disposal follows ownership, never presence.</param>
    /// <param name="link">This instance's own transport — the same two-line <c>LoopbackTransport</c> pattern a
    /// desktop's composition root wires for its boot instance, now held uniformly on every row:
    /// <see cref="WorldInstanceHost.TryGetLink"/> is the one local-authority submission door.</param>
    /// <param name="federation">This row's own federation signing identity.</param>
    /// <param name="documentOrigin">This row's document origin — where its <c>references[].document</c> locators
    /// resolve against.</param>
    /// <param name="ownedAdjacencies">The per-instance adjacency resolver this row owns, or null for boot wiring.</param>
    /// <param name="ownedNetwork">The peer network this row owns, or null when shared and externally owned.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldInstance(string name, Func<string> origin, WorldServer server, WorldMachineHost? ownedMachines, IServerLink link, WorldFederationIdentity federation, WorldDocumentOrigin documentOrigin, IDisposable? ownedAdjacencies = null, WorldPeerNetwork? ownedNetwork = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentNullException.ThrowIfNull(argument: origin);
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: documentOrigin);

        m_origin = origin;
        Name = name;
        Server = server;
        OwnedMachines = ownedMachines;
        Link = link;
        Federation = federation;
        Origin = documentOrigin;
        m_ownedAdjacencies = ownedAdjacencies;
        m_ownedNetwork = ownedNetwork;
    }

    /// <summary>The number of fixed ticks this instance has completed since it started — derived from its own server
    /// (<see cref="Server.WorldServer.NextInputTick"/>, whose one writer is that server's <c>Step</c>) rather than
    /// counted a second time here, so the step cursor and the read-back can never disagree. An instance started
    /// mid-session counts from its own zero; there is no shared absolute tick, and every instance advances on its own
    /// authored rate (see <see cref="WorldInstanceHost.StepInstances"/>), so this count is never comparable across
    /// instances running different rates — only each instance's own trajectory means anything.</summary>
    public ulong CompletedTicks => (Server.NextInputTick - 1UL);
    /// <summary>This row's per-activation socket door, or <see langword="null"/> for a row nothing outside its own
    /// process reaches directly (a desktop's non-boot local instance).</summary>
    public WorldPeerHost? Door { get; set; }
    /// <summary>This instance's own exact engine-tick elapsed clock, accumulated additively one step width at a
    /// time rather than re-derived as <c>(tick + 1) * stepWidth</c> — the identical
    /// discontinuity <c>Puck.Launcher.FixedStepPump</c>'s own <c>m_elapsedTicks</c> field exists to avoid: a product
    /// re-derivation is only valid while <c>stepWidth</c> has been constant for every tick it counts, which a rate
    /// change this instance's own document authors (a <c>world.load</c>/<c>.reset</c>/<c>.reload</c> swap) breaks
    /// the instant it fires. <see cref="WorldInstanceHost.StepInstances"/> is this field's one writer, adding the
    /// actual step width just taken after every <c>Server.Step</c> call, so the value stays monotonic and
    /// rate-change-safe across the instance's whole lifetime, not merely within one master call's catch-up
    /// batch.</summary>
    public ulong ElapsedEngineTicks { get; set; }
    /// <summary>This row's own federation signing identity — the authenticator and claim subject its outbound
    /// reserve/commit/acknowledge/status calls and its own door authenticate under.</summary>
    public WorldFederationIdentity Federation { get; set; }
    /// <summary>The live pause lever for this instance's own schedule — operational state only: never
    /// document-authored, never journaled, and untouched by any view (the world.rate command module is its only
    /// writer). An authored <c>simulation.rateHz</c> of 0 is the durable stop; this is the live one, and the two are
    /// independent — pausing never touches the authored rate, and resuming restores the exact schedule
    /// <see cref="ScheduleAccumulatorTicks"/> already holds (no skew). Default <see langword="false"/>: an instance
    /// runs the instant it is admitted, exactly as before this lever existed.</summary>
    public bool IsPaused { get; set; }
    /// <summary>Whether this row was admitted from a checkpoint restore and is held pending its adjacency mirrors —
    /// distinct from <see cref="IsPaused"/>, which is an operator lever a script can flip on a row that is otherwise
    /// stepping normally. A held row's door is not started (readiness is a promise to step) and
    /// <see cref="WorldInstanceHost.StepInstances"/> skips it entirely rather than banking ticks it will never spend.
    /// Set by the admitting caller at restore time; cleared by <see cref="WorldInstanceHost"/> the first master
    /// boundary at which every adjacency handle is either primed or unavailable by name.</summary>
    public bool AwaitingMirrors { get; set; }
    /// <summary>This instance's own transport — see this type's constructor remarks.</summary>
    public IServerLink Link { get; }
    /// <summary>The console-facing instance name.</summary>
    public string Name { get; }
    /// <summary>This row's own next transfer id counter — advances by exactly one per transfer this row
    /// SOURCES, a pure function of enqueue order, never wall-clock, RNG, or tick-of-entry.</summary>
    public ulong NextTransferId { get; set; }
    /// <summary>This row's own document origin.</summary>
    public WorldDocumentOrigin Origin { get; set; }
    /// <summary>The machine host this instance owns and disposes, or <see langword="null"/> when the container
    /// owns it.</summary>
    public WorldMachineHost? OwnedMachines { get; }

    /// <summary>This instance's portal-crossing edge state, read and written by <see cref="WorldInstanceHost"/>'s
    /// diegetic boundary scan. Scoped to this instance object — a name reused after
    /// <see cref="WorldInstanceHost.TryStop"/> starts a brand-new instance with a brand-new latch rather than
    /// inheriting a departed instance's occupancy, exactly like every other per-instance table here.</summary>
    public WorldPortalOccupancy PortalOccupancy { get; } = new();
    /// <summary>Called with this row's own completed tick count after every step — a console wait gate's tick clock
    /// on the desktop, a no-op for a row nothing is waiting on.</summary>
    public Action<ulong> PublishTick { get; set; } = static _ => { };

    /// <summary>Engine ticks banked toward this instance's own next step against the host's master timeline (see
    /// <see cref="WorldInstanceHost.StepInstances"/>) — this instance's own per-instance accumulator, so an
    /// instance running faster than the master cadence steps more than once per master tick and one running slower
    /// steps less than once. Held exactly where it is while <see cref="IsPaused"/> (or while the authored rate is
    /// the durable stop, 0) — nothing is banked toward a step that will not happen, and resuming continues on the
    /// identical schedule the instance would have kept had it never paused.</summary>
    public ulong ScheduleAccumulatorTicks { get; set; }
    /// <summary>This instance's own authoritative server — a distinct object graph per instance.</summary>
    public WorldServer Server { get; }
    /// <summary>The document path this instance currently answers for.</summary>
    public string SourcePath => m_origin();
    /// <summary>This row's own replay tape, or <see langword="null"/> for a row nothing records (every row but a
    /// desktop's boot instance today).</summary>
    public WorldReplayTape? Tape { get; set; }

    /// <summary>Disposes what this instance owns. A no-op for a boot instance, whose machine host belongs to the
    /// container and outlives any retirement of the entry.</summary>
    public void Dispose() {
        m_ownedAdjacencies?.Dispose();
        OwnedMachines?.Dispose();
        Door?.Dispose();
        m_ownedNetwork?.Dispose();
    }
}
