using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private sealed record WorldReleaseContext(JsonNode Outputs, Guid Owner, string Group, string ResourceGroup,
        WorldReleaseGroupStore Groups, WorldReleaseArchive Archive, WorldReleaseDeploymentStore Deployments, WorldAuthorityBlobStore Authority,
        IObjectBlobStore Blobs, WorldReleaseFixtureArchive Fixtures);

    private static async Task<T> WithManagedWorldReleaseAsync<T>(Func<WorldReleaseContext, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken, string? resourceGroup = null) {
        var outputs = Outputs();
        var configuration = Value(
            key: "worldSiloConfiguration",
            outputs: outputs
        );
        var owner = Guid.Parse(input: Text(value: Value(
            key: "worldSiloOwner",
            outputs: outputs
        )));
        var endpoint = Text(value: Value(
            key: "worldSiloStorageEndpoint",
            outputs: outputs
        ));
        var group = Text(value: configuration["name"]);

        return await WithWorldReleaseControllerAsync(
            endpoint,
            owner,
            group,
            async token => {
                var target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: endpoint);
                var services = new ServiceCollection();

                Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
                using var provider = services.BuildServiceProvider();
                var blobs = provider.GetRequiredService<IObjectBlobStore>();
                var context = new WorldReleaseContext(
                    outputs,
                    owner,
                    group,
                    (resourceGroup ?? Text(value: Value(
                        key: "deploymentResourceGroupName",
                        outputs: outputs
                    ))),
                    new(
                        owner: owner,
                        store: blobs,
                        target: target
                    ),
                    new(
                        blobs,
                        target,
                        owner
                    ),
                    new(
                        blobs,
                        target,
                        owner,
                        new AzureWorldReleaseSecretVersions(
                            Text(value: Value(
                                key: "deploymentKeyVaultName",
                                outputs: outputs
                            )),
                            Text(value: configuration["releaseStateSecretName"])
                        )
                    ),
                    new(
                        store: blobs,
                        target: target
                    ),
                    blobs,
                    new(
                        owner: owner,
                        store: blobs,
                        target: target
                    )
                );

                return await action(
                    context,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
            },
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    private static async Task<AzureWorldReleaseDeployment> LoadWorldReleaseDeploymentAsync(WorldReleaseContext context, string identity, CancellationToken token) {
        var manifest = (await context.Archive.LoadAsync(
            cancellationToken: token,
            releaseIdentity: identity
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: "release manifest is missing from retention"));

        await context.Archive.VerifyAsync(
            cancellationToken: token,
            manifest: manifest
        ).ConfigureAwait(continueOnCapturedContext: false);
        var retained = (await context.Deployments.LoadAsync(
            manifest,
            context.Group,
            token
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidDataException(message: "release deployment configuration is missing from retention"));

        if (retained.Parameters["configuration"]?["name"]?.GetValue<string>() != context.Group) {
            throw new InvalidDataException(message: "retained deployment parameters belong to a different worker group");
        }
        return new(
            Configuration: retained,
            Manifest: manifest
        );
    }
}
