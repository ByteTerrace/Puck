using System.Text.Json;

namespace Puck.World;

/// <summary>One world row's federation signing material — a P-256 PKCS#8 private-key file, host state, never
/// published inside a document. One key file per world row: a silo is a placement for many independently signed
/// world authorities, never a shared signing namespace, so two rows naming the same file recreate the single-key
/// shape this design refuses — the validator names that collision.</summary>
/// <param name="KeyFile">The path to the PKCS#8 private-key file this row signs its outbound federation claims and
/// its own door's admission challenges with.</param>
public sealed record WorldSiloFederation(string KeyFile) {
    /// <summary>Optional installed connection-identity provider, layered over this world's own federation credentials.</summary>
    public WorldSiloExtension? Authentication { get; init; }
}
/// <summary>One world this silo activates. Its own <c>host.authority</c> (read from its definition after load) is
/// the subject its federation key signs as — never this silo's own identity.</summary>
/// <param name="Owner">The owning identity's stable UUID.</param>
/// <param name="World">The world id under that owner's container — the grain key's own extension.</param>
/// <param name="Federation">This row's own signing key.</param>
/// <param name="Pinned">Whether this row activates at silo start and never idle-deactivates.</param>
/// <param name="Extensions">The path of this row's host-approved extension configuration
/// (<c>puck.world.extensions.v1</c>) — the same document a local World reads through <c>--extensions-config-file</c> —
/// or <see langword="null"/> when the row runs no configured operations, connections, observations, embeddings, or
/// participants. Host state, never published inside a document; no two rows share one file.</param>
public sealed record WorldSiloWorldRow(
    Guid Owner,
    SafeName World,
    WorldSiloFederation Federation,
    bool Pinned = false,
    string? Extensions = null
);
/// <summary>The declared door budget — a silo-document field, never a hard-coded platform constant: the document
/// declares its own limit, and a deployment lane writes a platform-derived value into it when one exists.</summary>
/// <param name="Budget">The maximum number of <see cref="WorldSiloWorldRow.Pinned"/> rows this silo may activate at
/// start.</param>
public sealed record WorldSiloDoors(int Budget);
/// <summary>Selects an installed host extension. Only that extension interprets its settings.</summary>
/// <param name="Type">The case-sensitive key in the composition root's extension registry.</param>
/// <param name="Settings">An object validated by the selected extension before host startup.</param>
public sealed record WorldSiloExtension(string Type, JsonElement Settings);
/// <summary>Orleans cluster membership for this silo.</summary>
/// <param name="Kind">The membership implementation selected at composition; this distribution supports Localhost.</param>
public sealed record WorldSiloClustering(string Kind);
/// <summary>Optional durable release gate for an officially managed silo. The owner is also the private-store object id.</summary>
/// <param name="Group">The deployment-group key in the private release store.</param>
/// <param name="Owner">The deployment-group owner's stable container UUID.</param>
/// <param name="ExpectedRelease">The exact immutable release identity this process may publish.</param>
public sealed record WorldSiloReleaseManagement(string Group, Guid Owner, string ExpectedRelease) {
    /// <summary>Enforces a closed authoritative inventory for intentional rewind. Remote federation and
    /// outbound authority claims are refused; ordinary authenticated player connections remain available.</summary>
    public bool ClosedGroupRewind { get; init; }
}
/// <summary>Deployment-owned lifecycle observation and local health configuration.</summary>
/// <param name="ShutdownSeconds">Maximum time, on the silo's clock, allowed for operator or deployment retirement, a pinned reload, or a release control request.</param>
/// <param name="HealthPort">HTTP health port; retirement requests are accepted only from loopback.</param>
/// <param name="Observer">Optional installed extension supplying host retirement deadlines; absent means no external observation.</param>
/// <param name="ProgressTimeoutSeconds">Maximum age, on the silo's clock, of a completed simulation step before liveness fails; also bounds each health request.</param>
/// <param name="CheckpointTimeoutSeconds">Maximum age, on the silo's clock, of a successful checkpoint before readiness fails.</param>
/// <param name="JournalTimeoutSeconds">Maximum interval, on the silo's clock, without journal progress while appends are pending.</param>
/// <param name="JournalBacklogLimit">Maximum outstanding journal appends admitted as healthy.</param>
public sealed record WorldSiloLifecycle(
    int ShutdownSeconds,
    int HealthPort,
    WorldSiloExtension? Observer = null,
    int ProgressTimeoutSeconds = 30,
    int CheckpointTimeoutSeconds = 180,
    int JournalTimeoutSeconds = 30,
    int JournalBacklogLimit = 1024
);
/// <summary>
/// The silo document (<c>puck.silo.configuration.v1</c>) — durable configuration for one <c>Puck.World.Silo</c> process: which
/// worlds it may activate, its declared door budget, where its checkpoints/journals/definitions live, its own
/// state directory, and its clustering membership. Loaded once at silo start (<c>--silo &lt;path&gt;</c>); nothing in
/// it is simulation state. See <see cref="WorldSiloDefinitionValidator"/> for the checks a document must pass before
/// the silo composes against it.
/// </summary>
/// <param name="Worlds">Every world this silo may activate, keyed by <see cref="WorldSiloWorldRow.World"/>.</param>
/// <param name="Doors">The declared door budget.</param>
/// <param name="Store">The checkpoint/journal/definition backend.</param>
/// <param name="StateDir">The root every activated row's container-ephemeral owned-world store resolves its own
/// directory under — the <c>--state-dir</c> counterpart.</param>
/// <param name="Clustering">Orleans cluster membership.</param>
/// <param name="Lifecycle">Optional deployment-owned host lifecycle adapter.</param>
/// <param name="Release">Optional managed release identity and private deployment-group admission binding.</param>
public sealed record WorldSiloDefinition(
    IReadOnlyList<WorldSiloWorldRow> Worlds,
    WorldSiloDoors Doors,
    WorldSiloExtension Store,
    string StateDir,
    WorldSiloClustering Clustering,
    WorldSiloLifecycle? Lifecycle = null,
    WorldSiloReleaseManagement? Release = null
) {
    /// <summary>The document schema tag every well-formed <c>puck.silo.configuration.v1</c> document carries.</summary>
    public const string SchemaVersion = "puck.silo.configuration.v1";

    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;
}
