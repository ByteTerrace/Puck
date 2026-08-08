using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// One running copy of a world's simulation in this process — an <i>instance</i> in the plan of record's own words
/// (docs/world-model.md, "The words"): a world is the only first-class noun, and an instance is a running copy of
/// one. Every instance this process runs is an entry of this type, INCLUDING the world the process booted with, so
/// the read-back surface carries one kind of row rather than a privileged world plus a lesser class beside it.
/// </summary>
/// <remarks><para>What still separates the boot instance from the rest is wiring, not kind, and it is named where it
/// bites: the boot instance is the only one the container gives a client, seats, an editor, a replay tape, a socket
/// door, a machine host with real screens, and the mutating console verbs. Every other instance holds a fresh
/// <see cref="Server.WorldServer"/>, <see cref="WorldPopulation"/>, <see cref="WorldOwnedWorlds"/> and an EMPTY
/// <see cref="WorldMachineHost"/> — reachable only through <see cref="WorldInstanceCommandModule"/>'s verbs, sharing
/// none of the boot instance's singletons. De-globalising the rest is an arc, not a rename; see
/// <see cref="WorldInstanceHost"/>'s remarks for the standing list.</para>
/// <para>Not thread-safe, and it does not need to be: every reader and the one writer
/// (<see cref="WorldInstanceHost.StepInstancesBesideBoot"/>) run on the launcher's single fixed-step thread, and the
/// verbs that start or retire an instance route <c>Simulation</c>, which applies on that same thread at a tick
/// boundary.</para></remarks>
internal sealed class WorldInstance : IDisposable {
    private readonly Func<string> m_origin;

    /// <summary>Initializes one running instance's held graph.</summary>
    /// <param name="name">The console-facing instance name, unique among running instances.</param>
    /// <param name="origin">Reads the document path this instance currently answers for. A delegate rather than a
    /// stored string because the boot instance's origin MOVES (a <c>world.load</c> retargets
    /// <c>WorldDefinitionSource.SourcePath</c>), and a second copy of a value that moves is a second copy that goes
    /// stale.</param>
    /// <param name="server">This instance's own authoritative server.</param>
    /// <param name="ownedMachines">The machine host this instance OWNS and must dispose, or <see langword="null"/>
    /// when the container owns it (the boot instance) — disposal follows ownership, never presence.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldInstance(string name, Func<string> origin, WorldServer server, WorldMachineHost? ownedMachines) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentNullException.ThrowIfNull(argument: origin);
        ArgumentNullException.ThrowIfNull(argument: server);

        m_origin = origin;
        Name = name;
        Server = server;
        OwnedMachines = ownedMachines;
    }

    /// <summary>This instance's own authoritative server — a distinct object graph per instance.</summary>
    public WorldServer Server { get; }

    /// <summary>Per-(portal placement id, face name, local seat) EDGE state for <see cref="WorldInstanceHost"/>'s
    /// diegetic portal-entry scan — <see langword="true"/> while that seat's body sits inside that face's enterable
    /// volume as of the most recent scan, so a body standing inside never re-fires (a fresh <see langword="true"/> is
    /// what fires a transfer; see <see cref="WorldInstanceHost.ScanPortalTriggers"/>). Scoped to THIS instance
    /// object — a name reused after <see cref="WorldInstanceHost.TryStop"/> starts a brand-new instance with a
    /// brand-new (empty) map rather than inheriting a departed instance's occupancy, exactly like every other
    /// per-instance table here.</summary>
    public Dictionary<(string PlacementId, string Face, int Seat), bool> PortalOccupancy { get; } = new();

    /// <summary>The console-facing instance name.</summary>
    public string Name { get; }

    /// <summary>The machine host this instance owns and disposes, or <see langword="null"/> when the container
    /// owns it.</summary>
    public WorldMachineHost? OwnedMachines { get; }

    /// <summary>The number of fixed ticks this instance has completed since IT started — derived from its own server
    /// (<see cref="Server.WorldServer.NextInputTick"/>, whose one writer is that server's <c>Step</c>) rather than
    /// counted a second time here, so the step cursor and the read-back can never disagree. An instance started
    /// mid-session counts from its own zero; there is no shared absolute tick, and the plan of record says why there
    /// cannot be one until worlds agree on a rate ("Authored tick rate per world").</summary>
    public ulong CompletedTicks => (Server.NextInputTick - 1UL);

    /// <summary>The document path this instance currently answers for.</summary>
    public string SourcePath => m_origin();

    /// <summary>Disposes what this instance owns. A no-op for the boot instance, whose machine host belongs to the
    /// container and outlives any retirement of the entry.</summary>
    public void Dispose() => OwnedMachines?.Dispose();
}
