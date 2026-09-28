using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.Networking;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Where a row's document came from, and how its <c>references[].document</c> locators resolve.
/// <see cref="Identity"/> is what the host compares to find a running row by origin; <see cref="TryLoad"/> is the
/// one loader entry (compose basis, strict parse, draw, validate).</summary>
public abstract class WorldDocumentOrigin {
    /// <summary>The origin's own comparable identity — a file arm's canonical full path, a hosted arm's
    /// <c>owner/{oid}/{world}</c>.</summary>
    public abstract string Identity { get; }
    /// <summary>The neighbour resolver the whole-document validator proves this origin's adjacencies against.</summary>
    public abstract IWorldNeighbourResolver Neighbours { get; }

    /// <summary>Loads the definition this origin names.</summary>
    public abstract bool TryLoad(string instanceIdentity, out WorldDefinition? definition, out string reason);
    /// <summary>Resolves a <c>references[].document</c> locator authored relative to this origin into the
    /// neighbour's own origin.</summary>
    public abstract bool TryResolveReference(string document, out WorldDocumentOrigin? sibling, out string reason);
}
/// <summary>A row loaded from a file on disk — a canonical full path, whose neighbour references resolve beside it
/// (<see cref="WorldDocumentPaths"/>).</summary>
public sealed class WorldFileOrigin : WorldDocumentOrigin {
    private readonly IMachineValidationCatalog? m_catalog;
    private readonly string m_catalogFingerprint;

    /// <summary>Initializes the origin over an already-resolved canonical path.</summary>
    /// <param name="resolvedPath">The canonical full path this row's document was loaded from.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for this host's selected machine catalog.</param>
    /// <param name="catalog">The selected host machine catalog, or null for structural-only callers.</param>
    public WorldFileOrigin(string resolvedPath, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: resolvedPath);

        Identity = resolvedPath;
        m_catalogFingerprint = catalogFingerprint;
        m_catalog = catalog;
    }

    /// <inheritdoc/>
    public override string Identity { get; }
    /// <inheritdoc/>
    public override IWorldNeighbourResolver Neighbours => new WorldFileNeighbourResolver(
        baseDirectory: () => WorldDocumentPaths.DirectoryOf(documentPath: Identity),
        catalogFingerprint: m_catalogFingerprint,
        catalog: m_catalog
    );

    /// <summary>Whether two resolved paths name the same file, comparing case-insensitively on a platform whose file
    /// names are case-insensitive.</summary>
    public static bool IdentityEquals(string left, string right) => string.Equals(
        a: left,
        b: right,
        comparisonType: PuckPaths.Comparison
    );
    /// <inheritdoc/>
    public override bool TryLoad(string instanceIdentity, out WorldDefinition? definition, out string reason) =>
        WorldDefinitionLoader.TryLoadFile(
            definition: out definition,
            instanceIdentity: instanceIdentity,
            neighbours: Neighbours,
            path: Identity,
            reason: out reason,
            catalogFingerprint: m_catalogFingerprint,
            catalog: m_catalog
        );
    /// <summary>Reads this file as a release publishes it: composed as <see cref="TryLoad"/> composes it, then read by
    /// <see cref="WorldDefinitionLoader.TryReadPublishable"/>, which proves a drawn copy admits and returns the parsed
    /// document undrawn, so a published definition never carries one instance's drawn cells.</summary>
    /// <param name="definition">The undrawn definition, or <see langword="null"/> on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns>Whether the file composed and a drawn copy of it admitted.</returns>
    public bool TryReadPublishable([System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason) {
        definition = null;

        return (WorldDefinitionFileSource.TryComposeDocumentTree(
            catalog: m_catalog,
            catalogFingerprint: m_catalogFingerprint,
            path: Identity,
            reason: out reason,
            tree: out var tree
        ) && WorldDefinitionLoader.TryReadPublishable(
            catalog: m_catalog,
            definition: out definition,
            documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: Identity),
            json: tree!.ToJsonString(),
            reason: out reason,
            sourceName: Identity
        ));
    }
    /// <summary>Resolves a path exactly like <c>--world</c>: rooted, or relative to the current directory, and naming
    /// a file that exists. A path a document authors never reaches here relative; it is resolved beside that document
    /// first (<see cref="WorldDocumentPaths"/>).</summary>
    public static bool TryResolveCanonicalPath(string path, out string resolved) {
        try {
            var direct = Path.GetFullPath(path: path);

            if (File.Exists(path: direct)) {
                resolved = direct;

                return true;
            }
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            // A path the OS cannot even form is a path with no file at it — the caller refuses by name either way.
        }

        resolved = string.Empty;

        return false;
    }
    /// <inheritdoc/>
    public override bool TryResolveReference(string document, out WorldDocumentOrigin? sibling, out string reason) {
        if (!WorldDocumentName.TryValidate(
            name: document,
            reason: out reason
        )) {
            sibling = null;

            return false;
        }

        if (
            !WorldDocumentPaths.TryResolve(
            documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: Identity),
            path: WorldDocumentName.DocumentFile(name: document),
            reason: out reason,
            resolved: out var besideSource
        ) ||
            !TryResolveCanonicalPath(
            path: besideSource,
            resolved: out var canonical
        )
        ) {
            sibling = null;
            reason = $"no world document at '{document}' beside {Identity}";

            return false;
        }

        sibling = new WorldFileOrigin(
            catalog: m_catalog,
            catalogFingerprint: m_catalogFingerprint,
            resolvedPath: canonical
        );
        reason = string.Empty;

        return true;
    }
}
/// <summary>A row loaded from cloud storage under an owner identity's own container — the composed definition its
/// authority root names (<see cref="WorldAuthorityBlobStore.RootAddress"/>), resolved through
/// <see cref="WorldStorageNeighbourResolver"/>'s hosted arm. A hosted document is always stored already composed
/// (basis folded), so <see cref="TryLoad"/> never resolves a chain, and it has no directory: a relative path it
/// authors is refused by name (<see cref="WorldDocumentPaths"/>).</summary>
public sealed class WorldHostedOrigin : WorldDocumentOrigin {
    private readonly TimeProvider m_clock;
    private readonly Guid m_owner;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly SafeName m_world;

    /// <summary>Initializes the origin.</summary>
    /// <param name="owner">The owning identity's oid.</param>
    /// <param name="world">The world id under that owner's container.</param>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The storage target (the identity's own hosted endpoint).</param>
    /// <param name="timeProvider">The host clock the definition read's <see cref="OperationTimeout"/> and every
    /// neighbour read run on; <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public WorldHostedOrigin(Guid owner, SafeName world, IObjectBlobStore store, ObjectStorageTarget target, TimeProvider? timeProvider = null) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);

        m_clock = (timeProvider ?? TimeProvider.System);
        m_owner = owner;
        m_store = store;
        m_target = target;
        m_world = world;
        Identity = $"owner/{owner:D}/{world.Value}";
    }

    /// <summary>Gets the bound on reading the hosted definition, on the host clock.</summary>
    public static TimeSpan OperationTimeout { get; } = TimeSpan.FromSeconds(seconds: 15);
    /// <inheritdoc/>
    public override string Identity { get; }
    /// <inheritdoc/>
    public override IWorldNeighbourResolver Neighbours => new WorldStorageNeighbourResolver(
        containerId: m_owner,
        @namespace: WorldStorageNamespace.Hosted,
        store: m_store,
        target: m_target,
        timeProvider: m_clock
    );

    /// <summary>Reads a hosted definition and its neighbours without blocking on storage I/O.</summary>
    /// <param name="instanceIdentity">The running instance's identity for boot draws.</param>
    /// <param name="cancellationToken">Cancels root and neighbour reads.</param>
    /// <returns>The fully validated document, or a named load refusal.</returns>
    public async ValueTask<(WorldDefinition? Definition, string Reason)> LoadAsync(string instanceIdentity, CancellationToken cancellationToken) {
        var address = WorldAuthorityBlobStore.RootAddress(identity: new WorldAuthorityIdentity(
            Owner: m_owner,
            World: m_world
        ));
        using var timeout = new OperationDeadline(
            caller: cancellationToken,
            timeout: OperationTimeout,
            timeProvider: m_clock
        );
        ObjectBlobContent? content;

        try {
            content = await WorldAuthorityRootReader.ReadDefinitionAsync(
                m_owner,
                m_world,
                m_store,
                m_target,
                timeout.Token
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } catch (Exception error) { return (null, $"could not read '{address.Key}' — {error.Message.ReplaceLineEndings(replacementText: " ")}"); }
        cancellationToken.ThrowIfCancellationRequested();
        if (content is not { } found) { return (null, $"no cloud copy at '{address.Key}'"); }
        var neighbours = new WorldStorageNeighbourResolver(
            containerId: m_owner,
            @namespace: WorldStorageNamespace.Hosted,
            store: m_store,
            target: m_target,
            timeProvider: m_clock
        );

        var loaded = await WorldDefinitionLoader.LoadAsync(
            found.Content,
            address.Key,
            instanceIdentity,
            neighbours.ResolveHostedAsync,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (loaded.Admission?.Definition, loaded.Reason);
    }
    /// <inheritdoc/>
    public override bool TryLoad(string instanceIdentity, out WorldDefinition? definition, out string reason) {
        (definition, reason) = LoadAsync(
            instanceIdentity,
            CancellationToken.None
        ).AsTask().GetAwaiter().GetResult();
        return (definition is not null);
    }
    /// <inheritdoc/>
    public override bool TryResolveReference(string document, out WorldDocumentOrigin? sibling, out string reason) {
        if (!WorldDocumentName.TryParseId(
            id: out var world,
            name: document,
            reason: out reason
        )) {
            sibling = null;

            return false;
        }

        sibling = new WorldHostedOrigin(
            owner: m_owner,
            store: m_store,
            target: m_target,
            timeProvider: m_clock,
            world: world
        );
        reason = string.Empty;

        return true;
    }
}
