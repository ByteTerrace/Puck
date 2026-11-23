using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    private static readonly AsyncLocal<WorldReleaseControllerLease?> WorldReleaseController = new();

    private static async Task<T> WithWorldReleaseControllerAsync<T>(string endpoint, Guid owner, string group,
        Func<CancellationToken, Task<T>> action, TimeProvider clock, CancellationToken cancellationToken) {
        if (WorldReleaseController.Value is not null) { throw new InvalidOperationException(message: "a release controller is already active in this operation"); }
        var lease = await AcquireWorldReleaseLeaseAsync(
            cancellationToken: cancellationToken,
            clock: clock,
            endpoint: endpoint,
            group: group,
            owner: owner
        ).ConfigureAwait(continueOnCapturedContext: false);

        try {
            WorldReleaseController.Value = lease;
            return await lease.RunAsync(action: action).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            WorldReleaseController.Value = null;
            await lease.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            if (lease.ReleaseFailure is { } error) {
                Console.Error.WriteLine(value: $"Release controller lease could not be released; it expires within sixty seconds: {error.Message}");
            }
        }
    }

    public sealed class AzureWorldReleaseLeaseBackend(BlobClient blob) : IWorldReleaseControllerLeaseBackend {
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
        // Storage answers a renewal of a lease that is not this ID's, or of no lease, with 409 (LeaseIdMismatchWithLeaseOperation,
        // LeaseNotPresentWithLeaseOperation) or 412 (LeaseLost), and of a deleted blob with 404: each says the lease is gone.
        public async Task RenewAsync(CancellationToken cancellationToken) {
            try {
                await m_lease.RenewAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } catch (RequestFailedException error) when (error.Status is 404 or 409 or 412) {
                throw new WorldReleaseControllerLeaseLostException(
                    innerException: error,
                    message: $"storage answered {error.Status} {error.ErrorCode}"
                );
            }
        }
    }

    private static Task<WorldReleaseControllerLease> AcquireWorldReleaseLeaseAsync(string endpoint, Guid owner, string group, TimeProvider clock, CancellationToken cancellationToken) {
        _ = SafeName.Parse(candidate: group);
        var options = new BlobClientOptions();

        // The controller lease decides every retry and bounds every call on its clock (WorldReleaseControllerLease), so
        // the SDK neither retries nor times out on its own.
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = Timeout.InfiniteTimeSpan;
        var blob = new BlobClient(
            blobUri: new Uri(uriString: $"{endpoint.TrimEnd(trimChar: '/')}/{owner:D}/{WorldOwnedWorldSync.HostedPrivateNamespace}/release-groups/{group}.controller"),
            credential: new DefaultAzureCredential(),
            options: options
        );

        return WorldReleaseControllerLease.AcquireAsync(
            backend: new AzureWorldReleaseLeaseBackend(blob: blob),
            cancellationToken: cancellationToken,
            clock: clock
        );
    }
}
