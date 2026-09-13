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
        if (WorldReleaseController.Value is not null) { throw new InvalidOperationException("a release controller is already active in this operation"); }
        var lease = await AcquireWorldReleaseLeaseAsync(endpoint, owner, group, cancellationToken).ConfigureAwait(false);
        try {
            WorldReleaseController.Value = lease;
            return await action(lease.Token).ConfigureAwait(false);
        } finally {
            WorldReleaseController.Value = null;
            await lease.DisposeAsync().ConfigureAwait(false);
            if (lease.ReleaseFailure is { } error) {
                Console.Error.WriteLine($"Release controller lease could not be released; it expires within sixty seconds: {error.Message}");
            }
        }
    }

    internal sealed class AzureWorldReleaseLeaseBackend(BlobClient blob) : IWorldReleaseControllerLeaseBackend {
        private readonly BlobLeaseClient m_lease = blob.GetBlobLeaseClient(Guid.NewGuid().ToString("D"));
        public async Task AcquireAsync(CancellationToken cancellationToken) {
            await blob.GetParentBlobContainerClient().CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            try { await blob.UploadAsync(BinaryData.FromString("puck.world.release-controller.v1"), overwrite: false, cancellationToken).ConfigureAwait(false); }
            catch (RequestFailedException error) when (error.Status == 409) { }
            try { await m_lease.AcquireAsync(TimeSpan.FromSeconds(60), cancellationToken: cancellationToken).ConfigureAwait(false); }
            catch (RequestFailedException error) when (error.Status == 409) {
                throw new InvalidOperationException("another controller owns this deployment group; inspect status and retry after it finishes or its lease expires", error);
            }
        }
        public async Task RenewAsync(CancellationToken cancellationToken) =>
            await m_lease.RenewAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        public async Task ReleaseAsync(CancellationToken cancellationToken) =>
            await m_lease.ReleaseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static Task<WorldReleaseControllerLease> AcquireWorldReleaseLeaseAsync(string endpoint, Guid owner, string group, CancellationToken cancellationToken) {
        _ = SafeName.Parse(group);
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        var blob = new BlobClient(new Uri($"{endpoint.TrimEnd('/')}/{owner:D}/{WorldOwnedWorldSync.HostedPrivateNamespace}/release-groups/{group}.controller"),
            new DefaultAzureCredential(), options);
        return WorldReleaseControllerLease.AcquireAsync(new AzureWorldReleaseLeaseBackend(blob), cancellationToken);
    }
}
