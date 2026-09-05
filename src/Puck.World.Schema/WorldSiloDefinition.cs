using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Which backend a silo document's <see cref="WorldSiloStore"/> addresses.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSiloStoreKind>))]
public enum WorldSiloStoreKind {
    /// <summary>A local filesystem directory — local runs and the canary, never a deployment target.</summary>
    Directory,

    /// <summary>Azure Blob Storage, addressed under the silo's own identity container.</summary>
    Azure,
}
/// <summary>Which clustering provider a silo document's <see cref="WorldSiloClustering"/> selects.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSiloClusteringKind>))]
public enum WorldSiloClusteringKind {
    /// <summary>Single-process membership — every local run and the canary; no table is named or touched.</summary>
    Localhost,

    /// <summary>Azure Storage Table clustering.</summary>
    Table,
}
/// <summary>One world row's federation signing material — a P-256 PKCS#8 private-key file, host state, never
/// published inside a document. One key file per world row: a silo is a placement for many independently signed
/// world authorities, never a shared signing namespace, so two rows naming the same file recreate the single-key
/// shape this design refuses — the validator names that collision.</summary>
/// <param name="KeyFile">The path to the PKCS#8 private-key file this row signs its outbound federation claims and
/// its own door's admission challenges with.</param>
public sealed record WorldSiloFederation(string KeyFile);
/// <summary>One world this silo activates. Its own <c>host.authority</c> (read from its definition after load) is
/// the subject its federation key signs as — never this silo's own identity.</summary>
/// <param name="Owner">The owning identity's Entra oid.</param>
/// <param name="World">The world id under that owner's container — the grain key's own extension.</param>
/// <param name="Federation">This row's own signing key.</param>
/// <param name="Pinned">Whether this row activates at silo start and never idle-deactivates.</param>
public sealed record WorldSiloWorldRow(
    Guid Owner,
    SafeName World,
    WorldSiloFederation Federation,
    bool Pinned = false
);
/// <summary>The declared door budget — a silo-document field, never a hard-coded platform constant: the document
/// declares its own limit, and a deployment lane writes a platform-derived value into it when one exists.</summary>
/// <param name="Budget">The maximum number of <see cref="WorldSiloWorldRow.Pinned"/> rows this silo may activate at
/// start.</param>
public sealed record WorldSiloDoors(int Budget);
/// <summary>Where this silo's checkpoints, journals, and published definitions live.</summary>
/// <param name="Kind">Which backend.</param>
/// <param name="DirectoryPath">The root directory — required when <paramref name="Kind"/> is
/// <see cref="WorldSiloStoreKind.Directory"/>, otherwise absent.</param>
/// <param name="AccountUrl">The Azure Blob account endpoint (e.g. <c>https://bytrcstp001.blob.core.windows.net</c>)
/// — required when <paramref name="Kind"/> is <see cref="WorldSiloStoreKind.Azure"/>, otherwise absent.</param>
public sealed record WorldSiloStore(
    WorldSiloStoreKind Kind,
    string? DirectoryPath = null,
    string? AccountUrl = null
);
/// <summary>Orleans cluster membership for this silo.</summary>
/// <param name="Kind">Which provider.</param>
/// <param name="TableName">The Storage Table name — required when <paramref name="Kind"/> is
/// <see cref="WorldSiloClusteringKind.Table"/>, otherwise absent (a local run names no table).</param>
public sealed record WorldSiloClustering(
    WorldSiloClusteringKind Kind,
    string? TableName = null
);
/// <summary>
/// The silo document (<c>puck.silo.def.v1</c>) — durable configuration for one <c>Puck.World.Silo</c> process: which
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
public sealed record WorldSiloDefinition(
    IReadOnlyList<WorldSiloWorldRow> Worlds,
    WorldSiloDoors Doors,
    WorldSiloStore Store,
    string StateDir,
    WorldSiloClustering Clustering
) {
    /// <summary>The document schema tag every well-formed <c>puck.silo.def.v1</c> document carries.</summary>
    public const string SchemaVersion = "puck.silo.def.v1";

    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;
}
