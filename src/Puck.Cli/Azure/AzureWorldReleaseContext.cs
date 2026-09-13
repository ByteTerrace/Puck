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
        var configuration = Value(outputs, "worldSiloConfiguration");
        var owner = Guid.Parse(Text(Value(outputs, "worldSiloOwner")));
        var endpoint = Text(Value(outputs, "worldSiloStorageEndpoint"));
        var group = Text(configuration["name"]);
        return await WithWorldReleaseControllerAsync(endpoint, owner, group, async token => {
            var target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(endpoint);
            var services = new ServiceCollection();
            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
            using var provider = services.BuildServiceProvider();
            var blobs = provider.GetRequiredService<IObjectBlobStore>();
            var context = new WorldReleaseContext(outputs, owner, group, resourceGroup ?? Text(Value(outputs, "deploymentResourceGroupName")),
                new(blobs, target, owner), new(blobs, target, owner),
                new(blobs, target, owner, new AzureWorldReleaseSecretVersions(Text(Value(outputs, "deploymentKeyVaultName")), Text(configuration["releaseStateSecretName"]))),
                new(blobs, target), blobs, new(blobs, target, owner));
            return await action(context, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AzureWorldReleaseDeployment> LoadWorldReleaseDeploymentAsync(WorldReleaseContext context, string identity, CancellationToken token) {
        var manifest = await context.Archive.LoadAsync(identity, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("release manifest is missing from retention");
        await context.Archive.VerifyAsync(manifest, token).ConfigureAwait(false);
        var retained = await context.Deployments.LoadAsync(manifest, context.Group, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("release deployment configuration is missing from retention");
        if (retained.Parameters["configuration"]?["name"]?.GetValue<string>() != context.Group) {
            throw new InvalidDataException("retained deployment parameters belong to a different worker group");
        }
        return new(manifest, retained);
    }
}
