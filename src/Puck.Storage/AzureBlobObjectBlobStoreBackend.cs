using System.Collections.Concurrent;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Puck.Storage;

/// <summary>
/// The Azure Blob backend. Its version token is the blob ETag — the download ETag on a read, and a
/// <c>BlobRequestConditions.IfMatch</c> on a conditional write, catching a 412 as a precondition
/// failure. This is the true optimistic-concurrency path; the local backend implements the same seam best-effort.
/// <see cref="ListAsync"/> answers the discovery half of the contract — the caller's own
/// listed keys, in the same object-relative key space a read/write address carries; an edge-shaped target routes List
/// to <see cref="AzureBlobObjectStorageTarget.DirectEndpoint"/> instead of the edge (see that property's remarks —
/// the edge cannot serve List at all), addressing the account's stored layout rather than the edge's view of it (see
/// <see cref="GetContainerProjection"/>), and refusing by name when none is authored. <see cref="IDisposable"/> because it
/// owns the lazily-created credential, which is also the credential a whole session's cloud access rides on: one
/// <c>DefaultAzureCredential</c> covers both deployment shapes without an app registration — a player's machine signs
/// in ambiently (developer tooling, the OS broker, a shared token cache) and a hosted server runs as a user-assigned
/// managed identity.
/// </summary>
internal sealed class AzureBlobObjectBlobStoreBackend : IObjectBlobStoreBackend, IDisposable {
    private static readonly BlobClientOptions BlobClientOptionsField = new(version: BlobClientOptions.ServiceVersion.V2025_11_05);

    // NOTE: initializer order is load-bearing; do not alphabetize.
    private readonly ConcurrentDictionary<string, BlobServiceClient> m_blobServiceClients = new(comparer: StringComparer.Ordinal);
    private readonly Lazy<DefaultAzureCredential> m_defaultAzureCredential = new(valueFactory: static () => new DefaultAzureCredential());

    private BlobClient GetBlobClient(AzureBlobObjectStorageTarget target, ObjectBlobAddress address) {
        return GetBlobClients(
            address: address,
            target: target
        ).BlobClient;
    }
    // The address projection (see AzureBlobObjectStorageTarget.EdgeNamespace): through the edge the container is the
    // namespace and the object id leads the blob name; raw, the object id is the container. Reads/writes never list,
    // so this always takes the ordinary (possibly edge) connection and the projection that matches it.
    private (BlobContainerClient ContainerClient, BlobClient BlobClient) GetBlobClients(
        AzureBlobObjectStorageTarget target,
        ObjectBlobAddress address
    ) {
        var (containerClient, keyPrefixRoot) = GetContainerProjection(
            forList: false,
            objectId: address.ObjectId,
            target: target
        );
        var key = ObjectBlobAddressPath.GetNormalizedKey(address: address);

        return (containerClient, containerClient.GetBlobClient(blobName: $"{keyPrefixRoot}{key}"));
    }
    // The same address projection, scoped to an object rather than one key: KeyPrefixRoot is the blob-name segment a
    // key rides behind — the prefix a list operation searches under, and the segment it strips back off each result's
    // name to recover the object-relative key. forList selects BOTH the connection and the projection, because an
    // edge-shaped target's two routes see different layouts of the same blob:
    //
    //   raw            — container {objectId},     blob {key}                    (one layout, every operation)
    //   edge, via edge — container {namespace},    blob {objectId}/{key}
    //   edge, direct   — container {objectId},     blob {namespace}/{key}
    //
    // The third row is the account's real layout: the edge rewrite maps /{namespace}/{container}/{rest} to container
    // {container}, blob {namespace}/{rest}, so what the SDK addresses as container {namespace}, blob {objectId}/{key}
    // is stored as container {objectId}, blob {namespace}/{key}. LIST can never be served through the edge (see
    // AzureBlobObjectStorageTarget.DirectEndpoint), so it goes direct to the account and must address that stored
    // layout — addressing the edge's view of it would ask for a container the account does not have, and one the
    // per-user access policy could not grant anyway (it reaches the caller's own {objectId} container, and only the
    // {namespace}/ paths inside it). Both edge projections strip KeyPrefixRoot back off a listed name, so either
    // route yields the same object-relative key space and a caller cannot tell them apart.
    private (BlobContainerClient ContainerClient, string KeyPrefixRoot) GetContainerProjection(
        AzureBlobObjectStorageTarget target,
        Guid objectId,
        bool forList
    ) {
        var root = ObjectBlobAddressPath.GetRoot(objectId: objectId);

        if (target.EdgeNamespace is not { Length: > 0 } edgeNamespace) {
            return (GetServiceClient(target: target).GetBlobContainerClient(blobContainerName: root), string.Empty);
        }

        return (forList
            ? (GetListServiceClient(target: target).GetBlobContainerClient(blobContainerName: root), $"{edgeNamespace}/")
            : (GetServiceClient(target: target).GetBlobContainerClient(blobContainerName: edgeNamespace), $"{root}/")
        );
    }
    // LIST can never be served through the platform edge, under any circumstance: the edge's path rewrite has no
    // segment for a query-string-only List Blobs/Containers request to occupy, so it 404s unconditionally before
    // reaching blob storage (see AzureBlobObjectStorageTarget.DirectEndpoint). An edge-shaped target therefore never
    // sends LIST through its own ServiceUri: it resolves against DirectEndpoint instead — parsed exactly like the
    // target's own connection string/service URI — or refuses by name, with zero network I/O, when none is
    // authored; that refusal is the point (never a silent empty result). A raw-shaped target (EdgeNamespace null) is
    // already a direct account connection, so it resolves exactly like GetServiceClient.
    private BlobServiceClient GetListServiceClient(AzureBlobObjectStorageTarget target) {
        if (target.EdgeNamespace is not { Length: > 0 } edgeNamespace) {
            return GetServiceClient(target: target);
        }

        if (target.DirectEndpoint is not { Length: > 0 } directEndpoint) {
            throw new InvalidOperationException(message: $"the target is edge-shaped (EdgeNamespace '{edgeNamespace}') and carries no DirectEndpoint — the platform edge cannot serve a container list (its path rewrite has no segment for a query-string-only list request), so discovery refuses rather than asking it; author a direct-to-account endpoint to enable it.");
        }

        var direct = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: directEndpoint);

        return GetServiceClient(
            connectionString: direct.ConnectionString,
            serviceUri: direct.ServiceUri,
            description: "DirectEndpoint"
        );
    }
    private BlobServiceClient GetServiceClient(AzureBlobObjectStorageTarget target) {
        return GetServiceClient(
            connectionString: target.ConnectionString,
            serviceUri: target.ServiceUri,
            description: "target"
        );
    }
    private BlobServiceClient GetServiceClient(string? connectionString, Uri? serviceUri, string description) {
        if (connectionString is { Length: > 0 }) {
            return m_blobServiceClients.GetOrAdd(
                key: $"connection-string:{connectionString}",
                valueFactory: _ => new BlobServiceClient(
                    connectionString: connectionString,
                    options: BlobClientOptionsField
                )
            );
        }

        if (serviceUri is not null) {
            return m_blobServiceClients.GetOrAdd(
                key: $"service-uri:{serviceUri.AbsoluteUri}",
                valueFactory: _ => new BlobServiceClient(
                    credential: m_defaultAzureCredential.Value,
                    options: BlobClientOptionsField,
                    serviceUri: serviceUri
                )
            );
        }

        throw new ArgumentException(
            message: $"The Azure Blob {description} must provide either a connection string or a service URI.",
            paramName: description
        );
    }

    public void Dispose() {
        // The service clients hold no unmanaged handles; the credential can (managed-identity/broker token pipes), so
        // dispose it when it was ever materialized. Guarded on IDisposable so a credential type that is not disposable
        // (the common case) is a harmless no-op.
        if (
            m_defaultAzureCredential.IsValueCreated &&
            (m_defaultAzureCredential.Value is IDisposable disposable)
        ) {
            disposable.Dispose();
        }

        m_blobServiceClients.Clear();
    }
    public async ValueTask<IReadOnlyList<string>> ListAsync(
        ObjectStorageTarget target,
        Guid objectId,
        string keyPrefix,
        CancellationToken cancellationToken = default
    ) {
        var azureTarget = ObjectStorageTarget.Require<AzureBlobObjectStorageTarget>(
            description: "an Azure Blob target",
            target: target
        );

        var (containerClient, keyPrefixRoot) = GetContainerProjection(
            forList: true,
            objectId: objectId,
            target: azureTarget
        );
        var blobPrefix = $"{keyPrefixRoot}{ObjectBlobAddressPath.GetNormalizedPrefix(keyPrefix: keyPrefix)}";
        var keys = new List<string>();

        try {
            await foreach (var item in containerClient.GetBlobsAsync(
                cancellationToken: cancellationToken,
                prefix: blobPrefix,
                states: BlobStates.None,
                traits: BlobTraits.None
            )) {
                // keyPrefixRoot is the segment the account's stored layout puts in front of a key (the edge namespace
                // going direct, nothing raw); strip it back off so every listed key is object-relative, the same shape
                // a read/write address carries — a discovered key addresses a read without translation.
                keys.Add(item: ((keyPrefixRoot.Length > 0)
                    ? item.Name[keyPrefixRoot.Length..]
                    : item.Name));
            }
        } catch (RequestFailedException ex) when (((ex.Status == 404) && (azureTarget.EdgeNamespace is not { Length: > 0 }))) {
            // Only the raw/dev-emulator shape self-manages containers (WriteAsync creates one on demand), so only
            // THERE does a 404 legitimately mean "nothing written yet." An edge-shaped target's per-object container
            // is platform-managed — created at onboarding, never by this client, so never legitimately absent — and
            // by the time control reaches here it has already resolved DirectEndpoint rather than the edge
            // (GetListServiceClient refuses by name before ever sending a request otherwise), so a 404 through that
            // direct connection is a genuine anomaly (a misconfigured endpoint, or an object that was never
            // onboarded) and must propagate, not read as an empty prefix.
            return [];
        }

        return keys;
    }
    public async ValueTask<ObjectBlobContent?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    ) {
        var blobClient = GetBlobClient(
            address: address,
            target: ObjectStorageTarget.Require<AzureBlobObjectStorageTarget>(
                description: "an Azure Blob target",
                target: target
            )
        );

        try {
            var download = await blobClient.DownloadContentAsync(cancellationToken: cancellationToken);

            return new ObjectBlobContent(
                Content: download.Value.Content.ToArray(),
                VersionToken: download.Value.Details.ETag.ToString()
            );
        } catch (RequestFailedException ex) when ((ex.Status == 404)) {
            return null;
        }
    }
    public bool Supports(ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(target);

        return (target is AzureBlobObjectStorageTarget);
    }
    public async ValueTask<ObjectBlobWriteResult> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        string? ifMatchVersion = null,
        CancellationToken cancellationToken = default
    ) {
        var azureTarget = ObjectStorageTarget.Require<AzureBlobObjectStorageTarget>(
            description: "an Azure Blob target",
            target: target
        );

        var (containerClient, blobClient) = GetBlobClients(
            address: address,
            target: azureTarget
        );

        // Through the edge, containers are platform-managed (created at onboarding) and creating one is not this
        // client's to do; raw targets self-manage.
        if (azureTarget.EdgeNamespace is not { Length: > 0 }) {
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        var blobContent = new BinaryData(data: content.ToArray());
        // Through the edge the platform authorizes a read against the CanRead index tag STORED on the blob, so an
        // untagged write SUCCEEDS and the blob can then never be read back. The keys are case-sensitive. Raw targets
        // carry no such policy and stay untagged, which is why this rides EdgeNamespace rather than a knob of its own.
        var tags = ((azureTarget.EdgeNamespace is { Length: > 0 })
            ? new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                { "CanRead", "Enabled" },
                { "CanWrite", "Enabled" },
            }
            : null
        );

        if (mode == ObjectBlobWriteMode.Overwrite) {
            // An if-match guards the overwrite (optimistic concurrency); its absence keeps the unconditional overwrite.
            var conditions = ((ifMatchVersion is not null)
                ? new BlobRequestConditions { IfMatch = new ETag(etag: ifMatchVersion) }
                : null
            );

            try {
                var response = await blobClient.UploadAsync(
                    blobContent,
                    new BlobUploadOptions { Conditions = conditions, Tags = tags },
                    cancellationToken
                );

                return new ObjectBlobWriteResult(
                    Succeeded: true,
                    PreconditionFailed: false,
                    VersionToken: response.Value.ETag.ToString()
                );
            } catch (RequestFailedException ex) when ((ex.Status == 412)) {
                return new ObjectBlobWriteResult(
                    PreconditionFailed: true,
                    Succeeded: false,
                    VersionToken: null
                );
            }
        }

        if (mode != ObjectBlobWriteMode.CreateOnly) {
            throw new ArgumentOutOfRangeException(
                actualValue: mode,
                message: "Unsupported object blob write mode.",
                paramName: nameof(mode)
            );
        }

        try {
            var response = await blobClient.UploadAsync(
                blobContent,
                new BlobUploadOptions {
                    Conditions = new BlobRequestConditions {
                        IfNoneMatch = ETag.All,
                    },
                    Tags = tags,
                },
                cancellationToken
            );

            return new ObjectBlobWriteResult(
                Succeeded: true,
                PreconditionFailed: false,
                VersionToken: response.Value.ETag.ToString()
            );
        } catch (RequestFailedException ex) when ((ex.Status is 409 or 412)) {
            // The blob already existed — a create-only loss, not an if-match precondition failure.
            return new ObjectBlobWriteResult(
                PreconditionFailed: false,
                Succeeded: false,
                VersionToken: null
            );
        }
    }
}
