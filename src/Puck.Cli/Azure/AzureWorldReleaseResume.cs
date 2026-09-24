using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    /// <summary>Resumes only the durable pending operation, using its retained image, template, and secret versions.</summary>
    internal static Task<WorldReleaseRunResult> ResumeWorldReleaseAsync(TimeProvider clock, CancellationToken cancellationToken) =>
        WithManagedWorldReleaseAsync(
            action: ResumeWorldReleaseCoreAsync,
            cancellationToken: cancellationToken,
            clock: clock
        );

    private static async Task<WorldReleaseRunResult> ResumeWorldReleaseCoreAsync(WorldReleaseContext context, CancellationToken token) {
        var state = (await context.Groups.LoadAsync(
            context.Group,
            token
        ).ConfigureAwait(continueOnCapturedContext: false)
            ?? throw new InvalidOperationException(message: "no managed deployment group exists"));

        if (state.Record.PendingOperationId is null) {
            return new(
                CandidatePrivate: false,
                Completed: true,
                Detail: "no pending release operation",
                Snapshot: state
            );
        }
        var candidate = await LoadWorldReleaseDeploymentAsync(
            context: context,
            identity: state.Record.PendingTargetRelease!,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var source = ((state.Record.PendingSourceRelease is { } sourceIdentity)
            ? await LoadWorldReleaseDeploymentAsync(
                context: context,
                identity: sourceIdentity,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false)
            : null
        );
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-release-resume-");

        try {
            var runtime = new AzureWorldReleaseRuntime(
                context.ResourceGroup,
                context.Group,
                temporary.FullName,
                Text(value: Value(
                    context.Outputs,
                    "worldSiloStorageEndpoint"
                )),
                Text(value: Value(
                    context.Outputs,
                    "worldSiloClientId"
                )),
                context.Groups,
                context.Authority,
                context.Archive,
                new WorldReleaseRestore(
                    context.Blobs,
                    Puck.Storage.AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: Text(value: Value(
                        context.Outputs,
                        "worldSiloStorageEndpoint"
                    ))),
                    context.Owner
                ),
                source,
                candidate,
                ct => InitializeWorldReleaseBootstrapAsync(
                    candidate.Manifest,
                    context.Archive,
                    context.Authority,
                    context.Owner,
                    ct
                ),
                context.Clock
            );

            return await new WorldReleaseCoordinator(groups: context.Groups).ResumeAsync(
                state,
                candidate.Manifest,
                runtime,
                token
            ).ConfigureAwait(continueOnCapturedContext: false);
        } finally { temporary.Delete(recursive: true); }
    }

    internal static async Task InitializeWorldReleaseBootstrapAsync(WorldReleaseManifest manifest, WorldReleaseArchive archive,
        WorldAuthorityBlobStore authority, Guid owner, CancellationToken cancellationToken) {
        _ = CliWorldVocabulary.EnsureInstalled();
        var definitions = new List<(WorldAuthorityIdentity Identity, WorldDefinition Definition)>();

        foreach (var row in manifest.DefinitionFiles) {
            var parts = row.Key.Split('/');

            if (
                (parts.Length != 2) ||
                (parts[0] != owner.ToString(format: "D"))
            ) { throw new InvalidDataException(message: "bootstrap inventory does not belong to the configured owner"); }
            var identity = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: parts[1])
            );
            var bytes = await archive.ReadFileAsync(
                manifest,
                row.Value,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!WorldDefinitionFileSource.TryParseComposed(
                System.Text.Encoding.UTF8.GetString(bytes: bytes.Span),
                row.Value,
                null,
                false,
                out var definition,
                out var reason
            )) { throw new InvalidDataException(message: reason); }
            var root = await authority.LoadRootAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (root is { } existing) &&
                ((existing.Root.FenceToken != Guid.Empty) || (existing.Root.CheckpointHash is not null) ||
                (existing.Root.JournalHash is not null) || (existing.Root.JournalEntryCount != 0) || (existing.Root.ReceiptHash is not null) || (existing.Root.ReceiptIndexHash is not null))
            ) {
                throw new InvalidDataException(message: "bootstrap cannot replace existing authoritative gameplay state");
            }
            var current = await authority.LoadPublishedDefinitionBytesAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (current is { } publishedBytes) &&
                !publishedBytes.Span.SequenceEqual(other: bytes.Span)
            ) {
                throw new InvalidDataException(message: "bootstrap cannot replace an existing different world definition");
            }
            definitions.Add(item: (identity, definition!));
        }
        foreach (var row in definitions) {
            var published = await authority.PublishDefinitionAsync(
                row.Identity,
                row.Definition,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!published.Ok) { throw new IOException(message: $"bootstrap definition publication refused: {published.Detail}"); }
        }
    }
}
