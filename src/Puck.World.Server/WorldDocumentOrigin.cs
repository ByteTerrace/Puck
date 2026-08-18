using Puck.Storage;

namespace Puck.World.Server;

/// <summary>Where a row's document came from, and how its <c>references[].document</c> locators resolve.
/// <see cref="Identity"/> is what the host compares to find a running row by origin; <see cref="TryLoad"/> is the
/// one loader entry (compose basis, strict parse, draw, validate).</summary>
public abstract class WorldDocumentOrigin {
    /// <summary>The origin's own comparable identity — a file arm's canonical full path, a hosted arm's
    /// <c>owner/{oid}/{world}</c>.</summary>
    public abstract string Identity { get; }

    /// <summary>Resolves a <c>references[].document</c> locator authored relative to this origin into the
    /// neighbour's own origin.</summary>
    public abstract bool TryResolveReference(string document, out WorldDocumentOrigin? sibling, out string reason);
    /// <summary>Loads the definition this origin names.</summary>
    public abstract bool TryLoad(string instanceIdentity, out WorldDefinition? definition, out string reason);

    /// <summary>The neighbour resolver the whole-document validator proves this origin's adjacencies against.</summary>
    public abstract IWorldNeighbourResolver Neighbours { get; }
}
/// <summary>A row loaded from a file on disk — a canonical full path plus the sibling-resolution probes
/// (rooted/relative-to-source/base-directory/shipped-worlds) every desktop boot and instance start already uses.</summary>
public sealed class WorldFileOrigin : WorldDocumentOrigin {
    private static readonly StringComparison PathComparison = (OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal
    );

    /// <summary>Initializes the origin over an already-resolved canonical path.</summary>
    /// <param name="resolvedPath">The canonical full path this row's document was loaded from.</param>
    public WorldFileOrigin(string resolvedPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: resolvedPath);

        Identity = resolvedPath;
    }

    /// <inheritdoc/>
    public override string Identity { get; }
    /// <inheritdoc/>
    public override IWorldNeighbourResolver Neighbours => new WorldFileNeighbourResolver(baseDirectory: () => ((Path.GetDirectoryName(path: Identity) is { Length: > 0 } directory)
        ? directory
        : AppContext.BaseDirectory));

    /// <inheritdoc/>
    public override bool TryLoad(string instanceIdentity, out WorldDefinition? definition, out string reason) =>
        WorldDefinitionLoader.TryLoadFile(
            definition: out definition,
            instanceIdentity: instanceIdentity,
            neighbours: Neighbours,
            path: Identity,
            reason: out reason
        );
    /// <inheritdoc/>
    public override bool TryResolveReference(string document, out WorldDocumentOrigin? sibling, out string reason) {
        var resolvedDocument = document;

        if (!Path.IsPathRooted(path: document)) {
            try {
                if (Path.GetDirectoryName(path: Identity) is { Length: > 0 } sourceDirectory) {
                    var besideSource = Path.GetFullPath(path: Path.Combine(
                        path1: sourceDirectory,
                        path2: document
                    ));

                    if (File.Exists(path: besideSource)) {
                        resolvedDocument = besideSource;
                    }
                }
            } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
                // The canonicalization probe below owns the eventual by-name refusal for an unformable locator.
            }
        }

        if (!TryResolveCanonicalPath(
            path: resolvedDocument,
            resolved: out var canonical
        )) {
            sibling = null;
            reason = $"no world document at '{document}', either as given or under {AppContext.BaseDirectory}";

            return false;
        }

        sibling = new WorldFileOrigin(resolvedPath: canonical);
        reason = string.Empty;

        return true;
    }
    /// <summary>Resolves a path exactly like <c>--world</c>: tried directly (rooted, or relative to the current
    /// directory), then relative to <see cref="AppContext.BaseDirectory"/>, then under the shipped worlds
    /// directory — so a bare shipped-asset file name resolves regardless of the process's launch directory or
    /// which document referenced it.</summary>
    public static bool TryResolveCanonicalPath(string path, out string resolved) {
        try {
            var direct = Path.GetFullPath(path: path);

            if (File.Exists(path: direct)) {
                resolved = direct;

                return true;
            }

            var fallback = Path.GetFullPath(path: Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: path
            ));

            if (File.Exists(path: fallback)) {
                resolved = fallback;

                return true;
            }

            var shippedWorlds = Path.GetFullPath(path: Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: "Assets",
                path3: "worlds",
                path4: path
            ));

            if (File.Exists(path: shippedWorlds)) {
                resolved = shippedWorlds;

                return true;
            }
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            // A path the OS cannot even form is a path with no file at it — the caller refuses by name either way.
        }

        resolved = string.Empty;

        return false;
    }
    /// <summary>Whether two resolved paths name the same file, comparing case-insensitively on a platform whose file
    /// names are case-insensitive.</summary>
    public static bool IdentityEquals(string left, string right) => string.Equals(
        a: left,
        b: right,
        comparisonType: PathComparison
    );
}
/// <summary>A row loaded from cloud storage under an owner identity's own container — the composed
/// <c>definition.json</c> a silo publishes, addressed and resolved through <see cref="WorldOwnedWorldSync.HostedAddressFor"/>
/// and <see cref="WorldStorageNeighbourResolver"/>'s hosted arm. A hosted document is always stored already composed
/// (basis folded), so <see cref="TryLoad"/> never resolves a chain.</summary>
public sealed class WorldHostedOrigin : WorldDocumentOrigin {
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(seconds: 15);

    private readonly Guid m_owner;
    private readonly IObjectBlobStore m_store;
    private readonly ObjectStorageTarget m_target;
    private readonly WorldSafeName m_world;

    /// <summary>Initializes the origin.</summary>
    /// <param name="owner">The owning identity's oid.</param>
    /// <param name="world">The world id under that owner's container.</param>
    /// <param name="store">The blob store.</param>
    /// <param name="target">The storage target (the identity's own hosted endpoint).</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="target"/> is <see langword="null"/>.</exception>
    public WorldHostedOrigin(Guid owner, WorldSafeName world, IObjectBlobStore store, ObjectStorageTarget target) {
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: target);

        m_owner = owner;
        m_store = store;
        m_target = target;
        m_world = world;
        Identity = $"owner/{owner:D}/{world.Value}";
    }

    /// <inheritdoc/>
    public override string Identity { get; }
    /// <inheritdoc/>
    public override IWorldNeighbourResolver Neighbours => new WorldStorageNeighbourResolver(
        containerId: m_owner,
        @namespace: WorldStorageNamespace.Hosted,
        store: m_store,
        target: m_target
    );

    /// <inheritdoc/>
    public override bool TryLoad(string instanceIdentity, out WorldDefinition? definition, out string reason) {
        definition = null;

        var address = WorldOwnedWorldSync.HostedAddressFor(
            containerId: m_owner,
            leaf: "definition.json",
            world: m_world
        );
        ObjectBlobContent? content;

        try {
            using var timeout = new CancellationTokenSource(delay: OperationTimeout);

            content = m_store.ReadAsync(
                address: address,
                cancellationToken: timeout.Token,
                target: m_target
            ).AsTask().GetAwaiter().GetResult();
        } catch (OperationCanceledException) {
            reason = $"timed out after {OperationTimeout.TotalSeconds:0}s reading '{address.Key}'";

            return false;
        } catch (Exception exception) {
            reason = $"transport error reading '{address.Key}' — {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (content is not { } found) {
            reason = $"no cloud copy at '{address.Key}'";

            return false;
        }

        return WorldDefinitionLoader.TryLoad(
            definition: out definition,
            instanceIdentity: instanceIdentity,
            neighbours: Neighbours,
            reason: out reason,
            sourceName: address.Key,
            utf8: found.Content
        );
    }
    /// <inheritdoc/>
    public override bool TryResolveReference(string document, out WorldDocumentOrigin? sibling, out string reason) {
        if (!document.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldOwnedWorldFileName.Suffix
        )) {
            sibling = null;
            reason = $"'{document}' is not a canonical owned-world file name ending in '{WorldOwnedWorldFileName.Suffix}'";

            return false;
        }

        var candidateId = document[..^WorldOwnedWorldFileName.Suffix.Length];

        if (
            !WorldSafeName.TryParse(
            candidate: candidateId,
            name: out var world,
            reason: out var nameReason
        ) ||
            !string.Equals(
            a: document,
            b: WorldOwnedWorldFileName.For(id: world),
            comparisonType: StringComparison.Ordinal
        )
        ) {
            sibling = null;
            reason = $"'{document}' is not a canonical owned-world file name — {nameReason}";

            return false;
        }

        sibling = new WorldHostedOrigin(
            owner: m_owner,
            store: m_store,
            target: m_target,
            world: world
        );
        reason = string.Empty;

        return true;
    }
}
