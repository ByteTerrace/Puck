using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>An <see cref="IAuthenticator"/> that never authenticates — test furniture for a row whose federation
/// identity is constructed but never exercised (no remote peer call crosses this suite's in-process host).</summary>
internal sealed class InertAuthenticator : IAuthenticator {
    public int ChallengeBytes => 0;
    public bool IsConfigured => false;

    public byte[] NewChallenge() => [];
    public byte[] Prove(ReadOnlySpan<byte> challenge) => throw new NotSupportedException();
    public bool TryVerify(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof, out string? sourceAuthority) {
        sourceAuthority = null;

        return false;
    }
}
/// <summary>One admitted, non-boot <see cref="WorldInstance"/> row plus the resources its construction owns —
/// bundled so a two-row host law drives the row without hand-wiring the desktop's own boot-composition sequence
/// (<c>WorldBootComposition.AddWorldAuthoritativeCore</c>) for a row that carries no seats, no door, and no
/// tape.</summary>
internal sealed class HostRow : IDisposable {
    private readonly string m_stateDirectory;

    private HostRow(WorldInstance instance, WorldMachineHost machines, string stateDirectory) {
        Instance = instance;
        Machines = machines;
        m_stateDirectory = stateDirectory;
    }

    /// <summary>The admitted row.</summary>
    public WorldInstance Instance { get; }
    /// <summary>The row's own machine host — empty (no engines), owned by this fixture.</summary>
    public WorldMachineHost Machines { get; }
    /// <summary>The row's own server — <see cref="WorldInstance.Server"/>, surfaced for convenience.</summary>
    public WorldServer Server => Instance.Server;

    /// <summary>Builds one row named <paramref name="name"/>, over a fresh server/population/profile-catalog exactly
    /// like <see cref="Fixtures.FreshServer"/> builds a boot server — but wired as an ordinary (non-boot) instance:
    /// a <see cref="LoopbackTransport"/> of its own, an inert federation identity, and a <see cref="WorldFileOrigin"/>
    /// naming no real file (this suite never calls <see cref="WorldDocumentOrigin.TryLoad"/> — every row is built
    /// directly, never resolved by path).</summary>
    /// <param name="name">The row's console-facing instance name.</param>
    /// <param name="definition">The document to boot from, or <see langword="null"/> for <see cref="Fixtures.BuildDocument"/>.</param>
    public static HostRow Build(string name, WorldDefinition? definition = null) {
        var bytes = WorldDefinitionSerialization.Serialize(definition: (definition ?? Fixtures.BuildDocument()));
        var doc = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);
        var population = new WorldPopulation(definition: doc);
        var machines = new WorldMachineHost(screens: doc.Screens, engines: []);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: $"puck-host-row-tests-{name}-").FullName;
        var profiles = new WorldOwnedWorlds(template: doc, directory: stateDirectory, machineId: Guid.NewGuid());
        var server = new WorldServer(definition: doc, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines, instanceIdentity: name);
        var link = new LoopbackTransport(server: server);
        var instance = new WorldInstance(
            name: name,
            origin: () => name,
            server: server,
            ownedMachines: machines,
            link: link,
            federation: new WorldFederationIdentity(Authenticator: new InertAuthenticator(), Subject: server.AuthorityIdentity),
            documentOrigin: new WorldFileOrigin(resolvedPath: name)
        );

        return new HostRow(instance: instance, machines: machines, stateDirectory: stateDirectory);
    }
    /// <summary>Wraps an ALREADY-BUILT server (a checkpoint restore's own <see cref="WorldServer.FromCheckpoint"/>
    /// output) into a row this suite's host can admit and drive — the reciprocal of <see cref="Build"/> for the
    /// restore side of a round-trip law, where the server graph is the checkpoint's own reconstruction rather than a
    /// fresh boot.</summary>
    /// <param name="name">The row's console-facing instance name — must match the captured row's own name for a
    /// round-trip comparison to mean anything.</param>
    /// <param name="server">The restored server.</param>
    /// <param name="machines">The machine host the restored server was built with.</param>
    public static HostRow Wrap(string name, WorldServer server, WorldMachineHost machines) {
        var link = new LoopbackTransport(server: server);
        var instance = new WorldInstance(
            name: name,
            origin: () => name,
            server: server,
            ownedMachines: machines,
            link: link,
            federation: new WorldFederationIdentity(Authenticator: new InertAuthenticator(), Subject: server.AuthorityIdentity),
            documentOrigin: new WorldFileOrigin(resolvedPath: name)
        );

        return new HostRow(instance: instance, machines: machines, stateDirectory: string.Empty);
    }
    /// <inheritdoc/>
    public void Dispose() {
        Instance.Dispose();

        if (m_stateDirectory.Length == 0) {
            return;
        }

        try {
            Directory.Delete(path: m_stateDirectory, recursive: true);
        } catch (IOException) {
            // Best-effort scratch cleanup; a locked handle on a slow CI disk must never fail the test itself.
        }
    }
}
