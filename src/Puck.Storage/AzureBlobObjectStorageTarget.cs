namespace Puck.Storage;

/// <summary>
/// The Azure Blob storage target: a service URI (credentialed via <c>DefaultAzureCredential</c>) or a connection
/// string (a dev/Azurite emulator, or an account whose keys the caller holds). <see cref="EdgeNamespace"/> selects
/// how an address projects into the store's container/name space — see the property for the two shapes.
/// </summary>
public sealed record AzureBlobObjectStorageTarget : ObjectStorageTarget {
    public static AzureBlobObjectStorageTarget FromConnectionStringOrServiceUri(string value) {
        if (string.IsNullOrWhiteSpace(value: value)) {
            throw new ArgumentException(
                message: "The Azure Blob target value must not be empty.",
                paramName: nameof(value)
            );
        }

        return (Uri.TryCreate(
            result: out var serviceUri,
            uriKind: UriKind.Absolute,
            uriString: value
        )
            ? new AzureBlobObjectStorageTarget(serviceUri: serviceUri)
            : new AzureBlobObjectStorageTarget(connectionString: value));
    }

    public string? ConnectionString { get; }
    public Uri? ServiceUri { get; }

    /// <summary>
    /// The platform edge namespace this target speaks through, or <see langword="null"/> for the raw account shape.
    /// The platform fronts one storage account with an edge that rewrites <c>/{namespace}/{container}/{key}</c> to
    /// container <c>{container}</c>, blob <c>{namespace}/{key}</c> — so through the edge the SDK addresses container
    /// <c>{namespace}</c> and blob <c>{objectId}/{key}</c>, and containers are platform-managed (created at
    /// onboarding, never by this client). <see langword="null"/> addresses container <c>{objectId}</c> and blob
    /// <c>{key}</c> directly and creates containers on demand — the dev/emulator shape. A direct-to-account
    /// connection for container LIST is wired — see <see cref="DirectEndpoint"/>, required by construction rather
    /// than routing a list through this edge (the edge cannot serve one at all; see that property's remarks). A
    /// direct-to-account connection for reads/writes more broadly is sanctioned but routes through the partitioner's
    /// companion mapping table, which is not wired here yet; when it is, the projection it needs becomes a third
    /// authored value of this property's type, not a new mechanism.
    /// </summary>
    public string? EdgeNamespace { get; init; }

    /// <summary>
    /// The direct-to-account connection <see cref="AzureBlobObjectBlobStoreBackend.ListAsync"/> uses when this
    /// target is edge-shaped (<see cref="EdgeNamespace"/> is non-null) — a service URI or a connection string,
    /// resolved exactly like <see cref="ServiceUri"/>/<see cref="ConnectionString"/>. The edge's
    /// <c>/{namespace}/{container}/{rest}</c> path rewrite has no segment for a query-string-only List
    /// Blobs/Containers request to occupy, so a list sent through the edge 404s unconditionally, for every prefix,
    /// before ever reaching blob storage — LIST can never be served through the edge, by construction, not by a
    /// fixable bug (verified live against the platform edge, 2026-08-05). An edge-shaped target therefore never
    /// sends LIST through its <see cref="ServiceUri"/>: it resolves against this property instead, or
    /// <see cref="AzureBlobObjectBlobStoreBackend.ListAsync"/> refuses BY NAME — never a silent empty result — when
    /// this is <see langword="null"/>. A raw-shaped target (<see cref="EdgeNamespace"/> null) never consults this
    /// property; it is already a direct account connection and lists exactly like it reads and writes.
    /// <para>Going direct means addressing what the account actually STORES, which is not what the edge shows: the
    /// rewrite lands an edge-shaped address in container <c>{objectId}</c>, blob <c>{namespace}/{key}</c>, so the
    /// list enumerates the object's own container beneath a <c>{namespace}/</c> prefix — the only shape the
    /// per-user access policy grants, since it reaches the caller's own container and only the namespace paths
    /// inside it. Listing the edge's view instead (a container named for the namespace) asks for something no
    /// account layout has. Listed names are translated back, so both routes hand the caller the same
    /// object-relative keys.</para>
    /// </summary>
    public string? DirectEndpoint { get; init; }

    public AzureBlobObjectStorageTarget(Uri serviceUri) {
        ArgumentNullException.ThrowIfNull(serviceUri);

        if (!serviceUri.IsAbsoluteUri) {
            throw new ArgumentException(
                message: "The Azure Blob service URI must be absolute.",
                paramName: nameof(serviceUri)
            );
        }

        ServiceUri = serviceUri;
    }
    public AzureBlobObjectStorageTarget(string connectionString) {
        if (string.IsNullOrWhiteSpace(value: connectionString)) {
            throw new ArgumentException(
                message: "The Azure Blob connection string must not be empty.",
                paramName: nameof(connectionString)
            );
        }

        ConnectionString = connectionString;
    }
}
