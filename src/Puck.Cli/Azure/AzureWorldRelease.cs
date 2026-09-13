using Microsoft.Extensions.DependencyInjection;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Reads the managed world group selected by the existing production deployment outputs.</summary>
    internal static Task<WorldReleaseGroupSnapshot?> ReadWorldReleaseStatusAsync(CancellationToken cancellationToken) =>
        WithWorldReleaseStoreAsync((groups, group) => groups.LoadAsync(group, cancellationToken));

    /// <summary>Closes the current admitted rollback window with one guarded write, retaining recovery history.</summary>
    internal static Task<WorldReleaseGroupSnapshot> FinalizeWorldReleaseAsync(CancellationToken cancellationToken) =>
        WithWorldReleaseStoreAsync(async (groups, group) => {
            var current = await groups.LoadAsync(group, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("no managed deployment group exists");
            if (current.Record.ActiveRelease is not null && current.Record.PendingOperationId is null &&
                !current.Record.RollbackEligible && current.Record.Admission == WorldReleaseAdmissionState.Open) {
                return current;
            }
            var finalized = await groups.FinalizeAsync(current, cancellationToken).ConfigureAwait(false);
            if (!finalized.Ok) { throw new InvalidOperationException(finalized.Detail); }
            return finalized.Snapshot!.Value;
        });

    private static async Task<T> WithWorldReleaseStoreAsync<T>(Func<WorldReleaseGroupStore, string, Task<T>> action) {
        var outputs = Outputs();
        var configuration = Value(outputs, "worldSiloConfiguration");
        var owner = Guid.Parse(Text(Value(outputs, "worldSiloOwner")));
        var target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(Text(Value(outputs, "worldSiloStorageEndpoint")));
        var services = new ServiceCollection();
        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
        using var provider = services.BuildServiceProvider();
        var groups = new WorldReleaseGroupStore(provider.GetRequiredService<IObjectBlobStore>(), target, owner);
        return await action(groups, Text(configuration["name"])).ConfigureAwait(false);
    }
}
