using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Resumes only the durable pending operation, using its retained image, template, and secret versions.</summary>
    internal static Task<WorldReleaseRunResult> ResumeWorldReleaseAsync(CancellationToken cancellationToken) =>
        WithManagedWorldReleaseAsync(ResumeWorldReleaseCoreAsync, cancellationToken);

    private static async Task<WorldReleaseRunResult> ResumeWorldReleaseCoreAsync(WorldReleaseContext context, CancellationToken token) {
        var state = await context.Groups.LoadAsync(context.Group, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("no managed deployment group exists");
        if (state.Record.PendingOperationId is null) { return new(true, false, "no pending release operation", state); }
        var candidate = await LoadWorldReleaseDeploymentAsync(context, state.Record.PendingTargetRelease!, token).ConfigureAwait(false);
        var source = state.Record.PendingSourceRelease is { } sourceIdentity
            ? await LoadWorldReleaseDeploymentAsync(context, sourceIdentity, token).ConfigureAwait(false) : null;
        var temporary = Directory.CreateTempSubdirectory("puck-release-resume-");
        try {
            var runtime = new AzureWorldReleaseRuntime(context.ResourceGroup, context.Group, temporary.FullName,
                Text(Value(context.Outputs, "worldSiloStorageEndpoint")), Text(Value(context.Outputs, "worldSiloClientId")),
                context.Groups, context.Authority, source, candidate,
                ct => InitializeWorldReleaseBootstrapAsync(candidate.Manifest, context.Archive, context.Authority, context.Owner, ct));
            return await new WorldReleaseCoordinator(context.Groups).ResumeAsync(state, candidate.Manifest, runtime, token).ConfigureAwait(false);
        } finally { temporary.Delete(recursive: true); }
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
