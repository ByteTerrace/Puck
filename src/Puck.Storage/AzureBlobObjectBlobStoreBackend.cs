using System.Collections.Concurrent;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Puck.Storage;

/// <summary>
/// The Azure Blob backend. Its version token is the blob ETag — the download ETag on a read (§2.5.2, previously
/// discarded), and a <c>BlobRequestConditions.IfMatch</c> on a conditional write, catching a 412 as a precondition
/// failure. This is the true optimistic-concurrency path the cloud arc exercises; the same seam the local backend
/// implements best-effort. <see cref="IDisposable"/> because it owns the lazily-created credential.
/// </summary>
internal sealed class AzureBlobObjectBlobStoreBackend : IObjectBlobStoreBackend, IDisposable {
    private static readonly BlobClientOptions BlobClientOptionsField = new(version: BlobClientOptions.ServiceVersion.V2025_11_05);

    private static AzureBlobObjectStorageTarget GetTarget(ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(target);

        return ((target as AzureBlobObjectStorageTarget)
            ?? throw new ArgumentException(
                message: "The storage target must be an Azure Blob target.",
                paramName: nameof(target)
            ));
    }

    // NOTE: initializer order is load-bearing; do not alphabetize.
    private readonly ConcurrentDictionary<string, BlobServiceClient> m_blobServiceClients = new(comparer: StringComparer.Ordinal);
    private readonly Lazy<DefaultAzureCredential> m_defaultAzureCredential = new(valueFactory: static () => new DefaultAzureCredential());

    private BlobClient GetBlobClient(AzureBlobObjectStorageTarget target, ObjectBlobAddress address) {
        return GetBlobClients(
            address: address,
            target: target
        ).BlobClient;
    }
    private (BlobContainerClient ContainerClient, BlobClient BlobClient) GetBlobClients(
        AzureBlobObjectStorageTarget target,
        ObjectBlobAddress address
    ) {
        var root = ObjectBlobAddressPath.GetRoot(address: address);
        var blobName = ObjectBlobAddressPath.GetNormalizedKey(address: address);
        var serviceClient = GetServiceClient(target: target);
        var containerClient = serviceClient.GetBlobContainerClient(blobContainerName: root);

        return (containerClient, containerClient.GetBlobClient(blobName: blobName));
    }
    private BlobServiceClient GetServiceClient(AzureBlobObjectStorageTarget target) {
        if (target.ConnectionString is { Length: > 0 } connectionString) {
            return m_blobServiceClients.GetOrAdd(
                key: $"connection-string:{connectionString}",
                valueFactory: _ => new BlobServiceClient(
                    connectionString: connectionString,
                    options: BlobClientOptionsField
                )
            );
        }

        if (target.ServiceUri is not null) {
            return m_blobServiceClients.GetOrAdd(
                key: $"service-uri:{target.ServiceUri.AbsoluteUri}",
                valueFactory: _ => new BlobServiceClient(
                    credential: m_defaultAzureCredential.Value,
                    options: BlobClientOptionsField,
                    serviceUri: target.ServiceUri
                )
            );
        }

        throw new ArgumentException(
            message: "The Azure Blob target must provide either a connection string or a service URI.",
            paramName: nameof(target)
        );
    }

    public void Dispose() {
        // The service clients hold no unmanaged handles; the credential can (managed-identity/broker token pipes), so
        // dispose it when it was ever materialized. Guarded on IDisposable so a credential type that is not disposable
        // (the common case) is a harmless no-op.
        if (m_defaultAzureCredential.IsValueCreated && (m_defaultAzureCredential.Value is IDisposable disposable)) {
            disposable.Dispose();
        }

        m_blobServiceClients.Clear();
    }

    public async ValueTask<ObjectBlobContent?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    ) {
        var blobClient = GetBlobClient(
            address: address,
            target: GetTarget(target: target)
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
        var (containerClient, blobClient) = GetBlobClients(
            address: address,
            target: GetTarget(target: target)
        );
        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobContent = new BinaryData(data: content.ToArray());

        if (mode == ObjectBlobWriteMode.Overwrite) {
            // An if-match guards the overwrite (optimistic concurrency); its absence keeps the unconditional overwrite.
            var conditions = (ifMatchVersion is not null
                ? new BlobRequestConditions { IfMatch = new ETag(etag: ifMatchVersion) }
                : null);

            try {
                var response = await blobClient.UploadAsync(
                    blobContent,
                    new BlobUploadOptions { Conditions = conditions },
                    cancellationToken
                );

                return new ObjectBlobWriteResult(Succeeded: true, PreconditionFailed: false, VersionToken: response.Value.ETag.ToString());
            } catch (RequestFailedException ex) when ((ex.Status == 412)) {
                return new ObjectBlobWriteResult(Succeeded: false, PreconditionFailed: true, VersionToken: null);
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
                },
                cancellationToken
            );

            return new ObjectBlobWriteResult(Succeeded: true, PreconditionFailed: false, VersionToken: response.Value.ETag.ToString());
        } catch (RequestFailedException ex) when ((ex.Status is 409 or 412)) {
            // The blob already existed — a create-only loss, not an if-match precondition failure.
            return new ObjectBlobWriteResult(Succeeded: false, PreconditionFailed: false, VersionToken: null);
        }
    }
}
