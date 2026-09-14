using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static readonly AsyncLocal<WorldReleaseControllerLease?> WorldReleaseController = new();

    private static async Task<T> WithWorldReleaseControllerAsync<T>(string endpoint, Guid owner, string group,
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) {
        if (WorldReleaseController.Value is not null) { throw new InvalidOperationException(message: "a release controller is already active in this operation"); }
        var lease = await AcquireWorldReleaseLeaseAsync(
            cancellationToken: cancellationToken,
            endpoint: endpoint,
            group: group,
            owner: owner
        ).ConfigureAwait(continueOnCapturedContext: false);

        try {
            WorldReleaseController.Value = lease;
            return await action(lease.Token).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            WorldReleaseController.Value = null;
            await lease.DisposeAsync().ConfigureAwait(false);
            if (lease.ReleaseFailure is { } error) {
                Console.Error.WriteLine(value: $"Release controller lease could not be released; it expires within sixty seconds: {error.Message}");
            }
        }
    }

    internal sealed class AzureWorldReleaseLeaseBackend(BlobClient blob) : IWorldReleaseControllerLeaseBackend {
        private readonly BlobLeaseClient m_lease = blob.GetBlobLeaseClient(leaseId: Guid.NewGuid().ToString(format: "D"));

        public async Task AcquireAsync(CancellationToken cancellationToken) {
            await blob.GetParentBlobContainerClient().CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            try {
                await blob.UploadAsync(
                    BinaryData.FromString(data: "puck.world.release-controller.v1"),
                    overwrite: false,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (RequestFailedException error) when ((error.Status == 409)) { }
            try {
                await m_lease.AcquireAsync(
                    TimeSpan.FromSeconds(seconds: 60),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (RequestFailedException error) when ((error.Status == 409)) {
                throw new InvalidOperationException(
                    innerException: error,
                    message: "another controller owns this deployment group; inspect status and retry after it finishes or its lease expires"
                );
            }
        }
        public async Task ReleaseAsync(CancellationToken cancellationToken) =>
            await m_lease.ReleaseAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        public async Task RenewAsync(CancellationToken cancellationToken) =>
            await m_lease.RenewAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static Task<WorldReleaseControllerLease> AcquireWorldReleaseLeaseAsync(string endpoint, Guid owner, string group, CancellationToken cancellationToken) {
        _ = SafeName.Parse(candidate: group);
        var options = new BlobClientOptions();

        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(seconds: 10);
        var blob = new BlobClient(
            blobUri: new Uri(uriString: $"{endpoint.TrimEnd(trimChar: '/')}/{owner:D}/{WorldOwnedWorldSync.HostedPrivateNamespace}/release-groups/{group}.controller"),
            credential: new DefaultAzureCredential(),
            options: options
        );

        return WorldReleaseControllerLease.AcquireAsync(
            backend: new AzureWorldReleaseLeaseBackend(blob: blob),
            cancellationToken: cancellationToken
        );
    }
}
