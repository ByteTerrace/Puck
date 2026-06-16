using System.Collections.Concurrent;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Puck.Storage;

internal sealed class AzureBlobObjectBlobStoreBackend : IObjectBlobStoreBackend {
    private static readonly BlobClientOptions BlobClientOptionsField = new(BlobClientOptions.ServiceVersion.V2025_11_05);

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
    private readonly Lazy<DefaultAzureCredential> m_defaultAzureCredential = new(static () => new DefaultAzureCredential());

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
        var containerClient = serviceClient.GetBlobContainerClient(root);

        return (containerClient, containerClient.GetBlobClient(blobName));
    }
    private BlobServiceClient GetServiceClient(AzureBlobObjectStorageTarget target) {
        if (target.ConnectionString is { Length: > 0 } connectionString) {
            return m_blobServiceClients.GetOrAdd(
                $"connection-string:{connectionString}",
                _ => new BlobServiceClient(
                    connectionString,
                    BlobClientOptionsField
                )
            );
        }

        if (target.ServiceUri is not null) {
            return m_blobServiceClients.GetOrAdd(
                $"service-uri:{target.ServiceUri.AbsoluteUri}",
                _ => new BlobServiceClient(
                    target.ServiceUri,
                    m_defaultAzureCredential.Value,
                    BlobClientOptionsField
                )
            );
        }

        throw new ArgumentException(
            message: "The Azure Blob target must provide either a connection string or a service URI.",
            paramName: nameof(target)
        );
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        CancellationToken cancellationToken = default
    ) {
        var blobClient = GetBlobClient(
            address: address,
            target: GetTarget(target: target)
        );

        try {
            var download = await blobClient.DownloadContentAsync(cancellationToken);

            return download.Value.Content.ToArray();
        } catch (RequestFailedException ex) when ((ex.Status == 404)) {
            return null;
        }
    }
    public bool Supports(ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(target);

        return (target is AzureBlobObjectStorageTarget);
    }
    public async ValueTask<bool> WriteAsync(
        ObjectStorageTarget target,
        ObjectBlobAddress address,
        ReadOnlyMemory<byte> content,
        ObjectBlobWriteMode mode,
        CancellationToken cancellationToken = default
    ) {
        var (containerClient, blobClient) = GetBlobClients(
            address: address,
            target: GetTarget(target: target)
        );
        await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var blobContent = new BinaryData(content.ToArray());

        if (mode == ObjectBlobWriteMode.Overwrite) {
            await blobClient.UploadAsync(
                blobContent,
                overwrite: true,
                cancellationToken
            );
            return true;
        }

        if (mode != ObjectBlobWriteMode.CreateOnly) {
            throw new ArgumentOutOfRangeException(
                actualValue: mode,
                message: "Unsupported object blob write mode.",
                paramName: nameof(mode)
            );
        }

        try {
            await blobClient.UploadAsync(
                blobContent,
                new BlobUploadOptions {
                    Conditions = new BlobRequestConditions {
                        IfNoneMatch = ETag.All,
                    },
                },
                cancellationToken
            );
            return true;
        } catch (RequestFailedException ex) when ((ex.Status is 409 or 412)) {
            return false;
        }
    }
}
