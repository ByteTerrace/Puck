using Microsoft.Extensions.DependencyInjection;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Resumes only the durable pending operation, using its retained image, template, and secret versions.</summary>
    internal static async Task<WorldReleaseRunResult> ResumeWorldReleaseAsync(CancellationToken cancellationToken) {
        var outputs = Outputs();
        var configuration = Value(outputs, "worldSiloConfiguration");
        var owner = Guid.Parse(Text(Value(outputs, "worldSiloOwner")));
        var endpoint = Text(Value(outputs, "worldSiloStorageEndpoint"));
        var group = Text(configuration["name"]);
        var resourceGroup = Text(Value(outputs, "deploymentResourceGroupName"));
        return await WithWorldReleaseControllerAsync(endpoint, owner, group, async token => {
            var target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(endpoint);
            var services = new ServiceCollection();
            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
            using var provider = services.BuildServiceProvider();
            var blobs = provider.GetRequiredService<IObjectBlobStore>();
            var groups = new WorldReleaseGroupStore(blobs, target, owner);
            var state = await groups.LoadAsync(group, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("no managed deployment group exists");
            if (state.Record.PendingOperationId is null) { return new WorldReleaseRunResult(true, false, "no pending release operation", state); }
            var archive = new WorldReleaseArchive(blobs, target, owner);
            var deployments = new WorldReleaseDeploymentStore(blobs, target, owner,
                new AzureWorldReleaseSecretVersions(Text(Value(outputs, "deploymentKeyVaultName")), Text(configuration["releaseStateSecretName"])));
            var candidate = await LoadDeploymentAsync(state.Record.PendingTargetRelease!).ConfigureAwait(false);
            var source = state.Record.PendingSourceRelease is { } sourceIdentity ? await LoadDeploymentAsync(sourceIdentity).ConfigureAwait(false) : null;
            var authority = new WorldAuthorityBlobStore(blobs, target);
            var temporary = Directory.CreateTempSubdirectory("puck-release-resume-");
            try {
                var runtime = new AzureWorldReleaseRuntime(resourceGroup, group, temporary.FullName, groups, authority, source, candidate,
                    ct => InitializeWorldReleaseBootstrapAsync(candidate.Manifest, archive, authority, owner, ct));
                return await new WorldReleaseCoordinator(groups).ResumeAsync(state, candidate.Manifest, runtime, token).ConfigureAwait(false);
            } finally { temporary.Delete(recursive: true); }

            async Task<AzureWorldReleaseDeployment> LoadDeploymentAsync(string identity) {
                var manifest = await archive.LoadAsync(identity, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("pending release manifest is missing from retention");
                await archive.VerifyAsync(manifest, token).ConfigureAwait(false);
                var retained = await deployments.LoadAsync(manifest, group, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("pending release deployment configuration is missing from retention");
                if (retained.Parameters["configuration"]?["name"]?.GetValue<string>() != group) {
                    throw new InvalidDataException("retained deployment parameters belong to a different worker group");
                }
                return new(manifest, retained);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task InitializeWorldReleaseBootstrapAsync(WorldReleaseManifest manifest, WorldReleaseArchive archive,
        WorldAuthorityBlobStore authority, Guid owner, CancellationToken cancellationToken) {
        _ = CliWorldVocabulary.EnsureInstalled();
        var definitions = new List<(WorldAuthorityIdentity Identity, WorldDefinition Definition)>();
        foreach (var row in manifest.DefinitionFiles) {
            var parts = row.Key.Split('/');
            if (parts.Length != 2 || parts[0] != owner.ToString("D")) { throw new InvalidDataException("bootstrap inventory does not belong to the configured owner"); }
            var identity = new WorldAuthorityIdentity(owner, SafeName.Parse(parts[1]));
            var bytes = await archive.ReadFileAsync(manifest, row.Value, cancellationToken).ConfigureAwait(false);
            var definition = WorldDefinitionSerialization.Deserialize(bytes.ToArray());
            var root = await authority.LoadRootAsync(identity, cancellationToken).ConfigureAwait(false);
            if (root is { } existing && (existing.Root.FenceToken != Guid.Empty || existing.Root.CheckpointHash is not null ||
                existing.Root.JournalHash is not null || existing.Root.JournalEntryCount != 0 || existing.Root.ReceiptHash is not null || existing.Root.ReceiptIndexHash is not null)) {
                throw new InvalidDataException("bootstrap cannot replace existing authoritative gameplay state");
            }
            if (await authority.LoadLatestAsync(identity, cancellationToken).ConfigureAwait(false) is not null) {
                throw new InvalidDataException("bootstrap cannot adopt an existing legacy checkpoint");
            }
            var current = await authority.LoadDefinitionAsync(identity, cancellationToken).ConfigureAwait(false);
            if (current is not null && !WorldDefinitionSerialization.Serialize(current).AsSpan().SequenceEqual(bytes.Span)) {
                throw new InvalidDataException("bootstrap cannot replace an existing different world definition");
            }
            definitions.Add((identity, definition));
        }
        foreach (var row in definitions) {
            var published = await authority.PublishDefinitionAsync(row.Identity, row.Definition, cancellationToken).ConfigureAwait(false);
            if (!published.Ok) { throw new IOException($"bootstrap definition publication refused: {published.Detail}"); }
        }
    }
}
