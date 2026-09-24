using Microsoft.Extensions.DependencyInjection;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Reads the managed world group selected by the existing production deployment outputs.</summary>
    internal static Task<WorldReleaseGroupSnapshot?> ReadWorldReleaseStatusAsync(CancellationToken cancellationToken) =>
        WithWorldReleaseStoreAsync(
            (groups, group, token) => groups.LoadAsync(
                cancellationToken: token,
                deploymentGroup: group
            ),
            TimeProvider.System,
            cancellationToken
        );
    /// <summary>Closes the current admitted rollback window with one guarded write, retaining recovery history. The
    /// controller lease that write holds runs on <paramref name="clock"/>.</summary>
    internal static Task<WorldReleaseGroupSnapshot> FinalizeWorldReleaseAsync(TimeProvider clock, CancellationToken cancellationToken) =>
        WithWorldReleaseStoreAsync(
            async (groups, group, token) => {
                var current = (await groups.LoadAsync(
                    cancellationToken: token,
                    deploymentGroup: group
                ).ConfigureAwait(continueOnCapturedContext: false)
                    ?? throw new InvalidOperationException(message: "no managed deployment group exists"));

                if (
                    (current.Record.ActiveRelease is not null) &&
                    (current.Record.PendingOperationId is null) &&
                    !current.Record.RollbackEligible &&
                    (current.Record.Admission == WorldReleaseAdmissionState.Open)
                ) {
                    return current;
                }
                var finalized = await groups.FinalizeAsync(
                    cancellationToken: token,
                    current: current
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!finalized.Ok) { throw new InvalidOperationException(message: finalized.Detail); }
                return finalized.Snapshot!.Value;
            },
            clock,
            cancellationToken,
            exclusive: true
        );

    private static async Task<T> WithWorldReleaseStoreAsync<T>(Func<WorldReleaseGroupStore, string, CancellationToken, Task<T>> action, TimeProvider clock,
        CancellationToken cancellationToken, bool exclusive = false) {
        var outputs = Outputs();
        var configuration = Value(
            key: "worldSiloConfiguration",
            outputs: outputs
        );
        var owner = Guid.Parse(input: Text(value: Value(
            key: "worldSiloOwner",
            outputs: outputs
        )));
        var target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: Text(value: Value(
            key: "worldSiloStorageEndpoint",
            outputs: outputs
        )));
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var provider = services.BuildServiceProvider();
        var groups = new WorldReleaseGroupStore(
            provider.GetRequiredService<IObjectBlobStore>(),
            target,
            owner
        );
        var group = Text(value: configuration["name"]);

        return (exclusive
            ? await WithWorldReleaseControllerAsync(
                Text(value: Value(
                    key: "worldSiloStorageEndpoint",
                    outputs: outputs
                )),
                owner,
                group,
                token => action(
                    groups,
                    group,
                    token
                ),
                clock,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)
            : await action(
                groups,
                group,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)
        );
    }
}
