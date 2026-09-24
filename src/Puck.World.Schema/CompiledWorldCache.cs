using System.Text;
using Puck.Abstractions;
using Puck.Assets;

namespace Puck.World;

/// <summary>
/// Where a boot finds and keeps compiled worlds (<see cref="CompiledWorld"/>). A boot looks first beside its document
/// (<see cref="CompiledWorld.Beside"/>, where <c>puck compile</c> and the build write one) and then in this cache's
/// directory, takes the first whose header is the boot's own, keeps each chunk that still holds, and derives the rest.
/// When anything was derived, the whole compiled world is written into the cache's directory, never beside the
/// document; a file there is named for the document's path, so each document has at most one. A chunk that does not
/// derive on boot (<see cref="ICompiledWorldChunk.DerivesOnBoot"/>) is kept when it holds and otherwise left out of the
/// boot and of what it writes. A compiled world that cannot be read or written costs the boot a derivation and nothing
/// else.
/// </summary>
public sealed class CompiledWorldCache {
    /// <summary>Initializes a new instance of the <see cref="CompiledWorldCache"/> class.</summary>
    /// <param name="directory">The directory compiled worlds are written into; created on the first write.</param>
    /// <param name="chunks">The derivations a compiled world stores, or <see langword="null"/> for
    /// <see cref="CompiledWorldChunks.Standard"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty or white space.</exception>
    public CompiledWorldCache(string directory, CompiledWorldChunks? chunks = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: directory);

        Chunks = (chunks ?? CompiledWorldChunks.Standard);
        Directory = Path.GetFullPath(path: directory);
    }

    /// <summary>Gets the derivations a compiled world stores.</summary>
    public CompiledWorldChunks Chunks { get; }
    /// <summary>Gets the directory compiled worlds are written into.</summary>
    public string Directory { get; }

    /// <summary>Returns the file this cache keeps the compiled world of the document at
    /// <paramref name="documentPath"/> in: the <c>sha256-64</c> hex of the document's full path, with
    /// <see cref="CompiledWorld.Extension"/>.</summary>
    /// <param name="documentPath">The document's path.</param>
    /// <returns>The file's full path.</returns>
    public string FileFor(string documentPath) => Path.Combine(
        path1: Directory,
        path2: (AssetContentHash.Compute(content: Encoding.UTF8.GetBytes(s: PuckPaths.Normalize(path: Path.GetFullPath(path: documentPath)))).Hex + CompiledWorld.Extension)
    );
    /// <summary>Starts one boot's use of this cache.</summary>
    /// <param name="documentPath">The document the boot loads: its <c>.puck</c> source or <c>.world.json</c> file.</param>
    /// <param name="catalogFingerprint">The composition fingerprint of the host's machine catalog.</param>
    /// <returns>The request the loader resolves the drawn definition through.</returns>
    public CompiledWorldRequest For(string documentPath, string catalogFingerprint) => new(
        cache: this,
        catalogFingerprint: catalogFingerprint,
        documentPath: documentPath
    );
}
/// <summary>What one boot's compiled world did: where it was read from, which chunks it kept, derived and left out, and
/// where it was written.</summary>
/// <param name="LoadedFrom">The compiled world whose header matched, or <see langword="null"/> when none did.</param>
/// <param name="Kept">The chunks taken from it, in registry order.</param>
/// <param name="Derived">The chunks derived fresh, in registry order.</param>
/// <param name="Deferred">The chunks no compiled world held that do not derive on boot, in registry order.</param>
/// <param name="WrittenTo">The file the compiled world was written into, or <see langword="null"/> when nothing was
/// derived or the write failed.</param>
/// <param name="WriteFailure">Why the write failed, or <see langword="null"/>.</param>
public sealed record CompiledWorldResolution(string? LoadedFrom, IReadOnlyList<ChunkCode> Kept, IReadOnlyList<ChunkCode> Derived, IReadOnlyList<ChunkCode> Deferred, string? WrittenTo, string? WriteFailure) {
    /// <summary>Gets whether the boot's definition came from a compiled world rather than a fresh draw.</summary>
    public bool DefinitionFromCompiledWorld => Kept.Contains(value: DefinitionChunk.Instance.Code);

    /// <summary>Returns the boot line that reports this resolution.</summary>
    /// <returns>A <c>[world] compiled world:</c> line.</returns>
    public string Describe() {
        var text = new StringBuilder(value: "[world] compiled world: ");

        _ = ((LoadedFrom is null)
            ? text.Append(value: "none for this build")
            : text.Append(value: LoadedFrom));

        if (Kept.Count > 0) {
            _ = text.Append(value: "; kept ").AppendJoin(separator: ' ', values: Kept);
        }

        if (Derived.Count > 0) {
            _ = text.Append(value: "; derived ").AppendJoin(separator: ' ', values: Derived);
        }

        if (Deferred.Count > 0) {
            _ = text.Append(value: "; deferred ").AppendJoin(separator: ' ', values: Deferred);
        }

        if (WrittenTo is not null) {
            _ = text.Append(value: "; wrote ").Append(value: WrittenTo);
        }

        if (WriteFailure is not null) {
            _ = text.Append(value: "; not written: ").Append(value: WriteFailure);
        }

        return text.ToString();
    }
}
/// <summary>One boot's use of a <see cref="CompiledWorldCache"/>: resolves the drawn definition for a document from
/// the compiled world that holds for it, deriving what does not hold, and records what happened in
/// <see cref="Resolution"/>.</summary>
public sealed class CompiledWorldRequest {
    private readonly CompiledWorldCache m_cache;

    internal CompiledWorldRequest(CompiledWorldCache cache, string documentPath, string catalogFingerprint) {
        m_cache = cache;
        CatalogFingerprint = catalogFingerprint;
        DocumentPath = Path.GetFullPath(path: documentPath);
    }

    /// <summary>Gets the composition fingerprint of the host's machine catalog.</summary>
    public string CatalogFingerprint { get; }
    /// <summary>Gets the document the boot loads.</summary>
    public string DocumentPath { get; }
    /// <summary>Gets what the last <see cref="TryResolve"/> did, or <see langword="null"/> before it ran or after it
    /// refused.</summary>
    public CompiledWorldResolution? Resolution { get; private set; }

    private static bool TryRead(string path, out ReadOnlyMemory<byte> content) {
        content = default;

        try {
            if (!File.Exists(path: path)) {
                return false;
            }

            content = File.ReadAllBytes(path: path);
            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }

    /// <summary>Resolves the drawn definition of <paramref name="authored"/>, counting a compiled-world hit and each
    /// chunk derived through the <c>world.boot</c> work source.</summary>
    /// <param name="authored">The parsed, composed, undrawn definition.</param>
    /// <param name="sourceName">The origin echoed in refusals.</param>
    /// <param name="instanceIdentity">The instance identity the draws are seeded for.</param>
    /// <param name="drawn">The drawn, resolved definition, or <see langword="null"/> on refusal.</param>
    /// <param name="reason">A derivation's named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the drawn definition resolved.</returns>
    public bool TryResolve(WorldDefinition authored, string sourceName, string instanceIdentity,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? drawn, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: authored);
        drawn = null;
        Resolution = null;

        var header = CompiledWorld.HeaderFor(
            authored: authored,
            catalogFingerprint: CatalogFingerprint,
            instanceIdentity: instanceIdentity
        );
        var cached = m_cache.FileFor(documentPath: DocumentPath);
        string? loadedFrom = null;
        ChunkContainer? container = null;

        foreach (var candidate in ((string[])[CompiledWorld.Beside(documentPath: DocumentPath), cached])) {
            if (
                TryRead(
                content: out var content,
                path: candidate
            ) &&
                CompiledWorld.TryDecode(
                container: out var decoded,
                content: content,
                header: out var found,
                reason: out _
            ) &&
                (found == header)
            ) {
                container = decoded;
                loadedFrom = candidate;
                break;
            }
        }

        var context = new CompiledWorldContext(
            authored: authored,
            documentPath: DocumentPath,
            instanceIdentity: instanceIdentity,
            sourceName: sourceName
        );
        var chunks = new List<ContainerChunk>(capacity: m_cache.Chunks.Count);
        var kept = new List<ChunkCode>();
        var derived = new List<ChunkCode>();
        var deferred = new List<ChunkCode>();

        foreach (var chunk in m_cache.Chunks) {
            if (
                (container is not null) &&
                !chunk.DependsOn.Any(predicate: code => (derived.Contains(item: code) || deferred.Contains(item: code))) &&
                container.TryFind(
                chunk: out var stored,
                code: chunk.Code
            ) &&
                CompiledWorldChunks.Holds(
                chunk: chunk,
                context: context,
                stored: stored
            ) &&
                chunk.TryLoad(
                context: context,
                payload: stored.Payload,
                reason: out _
            )
            ) {
                chunks.Add(item: stored);
                kept.Add(item: chunk.Code);
                continue;
            }

            if (!chunk.DerivesOnBoot) {
                deferred.Add(item: chunk.Code);
                continue;
            }

            if (!m_cache.Chunks.TryDerive(
                chunk: chunk,
                context: context,
                derived: out var product,
                reason: out reason
            )) {
                return false;
            }

            chunks.Add(item: product);
            derived.Add(item: chunk.Code);
        }

        if (kept.Contains(item: DefinitionChunk.Instance.Code)) {
            WorldBootWork.Count(kind: WorldBootWork.CompiledHits);
        }

        string? writtenTo = null;
        string? writeFailure = null;

        if (derived.Count > 0) {
            try {
                _ = System.IO.Directory.CreateDirectory(path: m_cache.Directory);
                AtomicFile.WriteAllBytes(
                    bytes: CompiledWorld.Encode(
                        chunks: chunks,
                        header: header
                    ),
                    path: cached
                );
                writtenTo = cached;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                writeFailure = exception.Message.ReplaceLineEndings(replacementText: " ");
            }
        }

        Resolution = new CompiledWorldResolution(
            Deferred: deferred,
            Derived: derived,
            Kept: kept,
            LoadedFrom: loadedFrom,
            WriteFailure: writeFailure,
            WrittenTo: writtenTo
        );
        drawn = context.RequireDrawn();
        reason = string.Empty;
        return true;
    }
}
