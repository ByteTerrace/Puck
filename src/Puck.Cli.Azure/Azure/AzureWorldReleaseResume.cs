using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
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
        // Staging holds nothing a later look needs, so it is deleted however the step ends.
        using var temporary = RunDirectory.Create(
            keepOnFailure: false,
            prefix: "puck-release-resume-"
        );
        var (candidate, runtime) = await CreateWorldReleaseRuntimeAsync(
            context: context,
            record: state.Record,
            temporary: temporary.Path,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);

        return await new WorldReleaseCoordinator(groups: context.Groups).ResumeAsync(
            state,
            candidate,
            runtime,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    // Ends the group's unfinished operation of another release (WorldReleaseCoordinator.SupersedeAsync) and returns the
    // group state that holds no unfinished operation, or refuses with the reason it could not end.
    private static async Task<WorldReleaseGroupSnapshot> SupersedeWorldReleaseAsync(WorldReleaseContext context,
        WorldReleaseGroupSnapshot state, string successor, CancellationToken token) {
        using var temporary = RunDirectory.Create(
            keepOnFailure: false,
            prefix: "puck-release-supersede-"
        );
        var (candidate, runtime) = await CreateWorldReleaseRuntimeAsync(
            context: context,
            record: state.Record,
            temporary: temporary.Path,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var result = await new WorldReleaseCoordinator(groups: context.Groups).SupersedeAsync(
            state,
            successor,
            candidate,
            await runtime.FindCapturedRootsAsync(
                cancellationToken: token,
                operationId: state.Record.PendingOperationId!.Value
            ).ConfigureAwait(continueOnCapturedContext: false),
            runtime,
            token
        ).ConfigureAwait(continueOnCapturedContext: false);

        Console.WriteLine(value: $"Superseded operation {state.Record.PendingOperationId:D}: {result.Detail}");
        return ((result.Snapshot is { Record.HasUnfinishedOperation: false } ended)
            ? ended
            : throw new InvalidOperationException(message: $"the unfinished operation {state.Record.PendingOperationId:D} could not be superseded: {result.Detail}"));
    }
    // The Azure runtime driver for the record's pending operation, over its retained source and target deployments.
    private static async Task<(WorldReleaseManifest Candidate, AzureWorldReleaseRuntime Runtime)> CreateWorldReleaseRuntimeAsync(
        WorldReleaseContext context, WorldReleaseGroupRecord record, string temporary, CancellationToken token) {
        var candidate = await LoadWorldReleaseDeploymentAsync(
            context: context,
            identity: record.PendingTargetRelease!,
            token: token
        ).ConfigureAwait(continueOnCapturedContext: false);
        var source = ((record.PendingSourceRelease is { } sourceIdentity)
            ? await LoadWorldReleaseDeploymentAsync(
                context: context,
                identity: sourceIdentity,
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false)
            : null
        );
        var endpoint = Text(value: Value(
            context.Outputs,
            "worldSiloStorageEndpoint"
        ));

        return (candidate.Manifest, new AzureWorldReleaseRuntime(
            context.ResourceGroup,
            context.Group,
            temporary,
            endpoint,
            Text(value: Value(
                context.Outputs,
                "worldSiloClientId"
            )),
            context.Groups,
            context.Authority,
            context.Archive,
            new WorldReleaseRestore(
                context.Blobs,
                Puck.Storage.Azure.AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: endpoint),
                context.Owner,
                context.Clock,
                LocalWorldsDirectory
            ),
            source,
            candidate,
            ct => InitializeWorldReleaseBootstrapAsync(
                candidate.Manifest,
                context.Archive,
                context.Authority,
                context.Owner,
                LocalWorldsDirectory,
                record.History,
                ct
            ),
            context.Clock
        ));
    }

    /// <summary>Publishes each definition of a first release, as the exact bytes the archive retains, through an unowned
    /// authority root, after proving it admits beside the asset layout it runs in. A retry compares exact published bytes.
    /// It refuses any existing gameplay state, and a different published definition unless an earlier bootstrap of the
    /// same deployment group published it and recovered without admitting anyone.</summary>
    /// <param name="manifest">The release whose definitions are published.</param>
    /// <param name="archive">The archive retaining the release's files.</param>
    /// <param name="authority">The authority store the definitions are published into.</param>
    /// <param name="owner">The deployment's owner; every definition must belong to it.</param>
    /// <param name="documentDirectory">The local copy of the asset layout the deployed silo resolves the documents'
    /// relative paths beside (<see cref="LocalWorldsDirectory"/>), or <see langword="null"/> for documents that author
    /// none.</param>
    /// <param name="history">The deployment group's durable history. A bootstrap entry (one with no source release)
    /// names the recovery roots it captured, and a published definition equal to one of them is the group's own.</param>
    /// <param name="cancellationToken">The token that cancels storage reads and writes.</param>
    /// <exception cref="InvalidDataException">A definition belongs to another owner, does not admit, or would replace
    /// existing state or a different published definition that no earlier bootstrap of the group published.</exception>
    /// <exception cref="IOException">The authority store refuses a publication.</exception>
    public static async Task InitializeWorldReleaseBootstrapAsync(WorldReleaseManifest manifest, WorldReleaseArchive archive,
        WorldAuthorityBlobStore authority, Guid owner, string? documentDirectory, IReadOnlyList<WorldReleaseGroupHistoryEntry> history,
        CancellationToken cancellationToken) {
        _ = CliWorldVocabulary.EnsureInstalled();
        var definitions = new List<(WorldAuthorityIdentity Identity, ReadOnlyMemory<byte> Bytes)>();

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

            if (!WorldDefinitionLoader.TryReadPublishable(
                definition: out _,
                documentDirectory: documentDirectory,
                json: System.Text.Encoding.UTF8.GetString(bytes: bytes.Span),
                reason: out var reason,
                sourceName: row.Value
            )) { throw new InvalidDataException(message: reason); }
            var root = await authority.LoadRootAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (root is { Root.HoldsGameplay: true }) {
                throw new InvalidDataException(message: "bootstrap cannot replace existing authoritative gameplay state");
            }
            var current = await authority.LoadPublishedDefinitionBytesAsync(
                cancellationToken: cancellationToken,
                identity: identity
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (current is { } publishedBytes) &&
                !publishedBytes.Span.SequenceEqual(other: bytes.Span) &&
                !await PublishedByRecoveredBootstrapAsync(
                    authority: authority,
                    cancellationToken: cancellationToken,
                    definitionHash: root?.Root.DefinitionHash,
                    history: history,
                    identity: identity
                ).ConfigureAwait(continueOnCapturedContext: false)
            ) {
                throw new InvalidDataException(message: $"bootstrap cannot replace '{identity.World}''s existing different world definition, which no earlier bootstrap of this group published");
            }
            definitions.Add(item: (identity, bytes));
        }
        foreach (var row in definitions) {
            var published = await authority.PublishDefinitionBytesAsync(
                row.Identity,
                row.Bytes,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!published.Ok) { throw new IOException(message: $"bootstrap definition publication refused: {published.Detail}"); }
        }
    }
    // A bootstrap publishes its definitions before it captures their recovery roots, so a recovered bootstrap's root for
    // this identity names the definition it published. Recovery admitted no one, so that definition holds nothing to keep.
    private static async Task<bool> PublishedByRecoveredBootstrapAsync(WorldAuthorityBlobStore authority, WorldAuthorityIdentity identity,
        string? definitionHash, IReadOnlyList<WorldReleaseGroupHistoryEntry> history, CancellationToken cancellationToken) {
        if (definitionHash is null) { return false; }
        foreach (var entry in history) {
            if (
                (entry.SourceRelease is not null) ||
                !entry.RecoveryRoots.TryGetValue(
                    key: identity.RootKey,
                    value: out var pin
                )
            ) { continue; }
            var saved = await authority.LoadRecoveryRootAsync(
                identity,
                pin,
                entry.OperationId,
                cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (string.Equals(
                a: saved?.Root.DefinitionHash,
                b: definitionHash,
                comparisonType: StringComparison.Ordinal
            )) { return true; }
        }
        return false;
    }
}
