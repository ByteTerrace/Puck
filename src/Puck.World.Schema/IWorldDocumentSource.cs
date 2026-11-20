using Puck.Assets;

namespace Puck.World;

/// <summary>One document a composition read: its resolved path, the SHA-256 digest of the bytes read there, and those
/// bytes while something must still prove the image by them (its own document's, and every document's when the image
/// carries no input facts).</summary>
public sealed class WorldComposedLink {
    private byte[]? m_digest;

    /// <summary>Initializes a new instance of the <see cref="WorldComposedLink"/> class for bytes just read.</summary>
    /// <param name="path">The document's resolved path.</param>
    /// <param name="bytes">The bytes read there, whole; their digest is computed when first asked for.</param>
    public WorldComposedLink(string path, byte[] bytes) {
        Path = path;
        Bytes = bytes;
    }
    /// <summary>Initializes a new instance of the <see cref="WorldComposedLink"/> class from a digest already known.</summary>
    /// <param name="path">The document's resolved path.</param>
    /// <param name="digest">The SHA-256 digest of the bytes read there, 32 bytes.</param>
    /// <param name="bytes">The bytes, or <see langword="null"/> when the image no longer holds them.</param>
    public WorldComposedLink(string path, byte[] digest, byte[]? bytes) {
        Path = path;
        m_digest = digest;
        Bytes = bytes;
    }

    /// <summary>Gets the document's resolved path.</summary>
    public string Path { get; }
    /// <summary>Gets the bytes read, or <see langword="null"/> when the image keeps only their digest.</summary>
    public byte[]? Bytes { get; }
    /// <summary>Gets the SHA-256 digest of the bytes read, 32 bytes.</summary>
    public byte[] Digest => (m_digest ??= System.Security.Cryptography.SHA256.HashData(source: Bytes!));

    // The same read without its bytes, for an image whose input facts prove it.
    internal WorldComposedLink WithoutBytes() => ((Bytes is null) ? this : new WorldComposedLink(bytes: null, digest: Digest, path: Path));
}
/// <summary>A composed image and the filesystem facts that justify it. The original chain preserves content hashes.</summary>
public sealed record WorldComposedDocument(IReadOnlyList<WorldComposedLink> Chain, byte[] ComposedJson, int Reach, IReadOnlyList<CompileInput>? Inputs) {
    // Effective scalar provenance is process-local. Older/persisted images without it still serve ordinary reads;
    // a provenance read recomposes once and holds a new image. No mutable JSON node is shared with a caller.
    internal IReadOnlyDictionary<string, WorldDocumentOrigins.Origin>? Origins { get; init; }
    // The layers a composite image composed from, process-local like Origins: a later composition of the same document
    // after one layer changed recomposes only what that layer writes (WorldDefinitionFileSource.Layers). A persisted
    // image carries none and its next recomposition merges whole.
    internal WorldDefinitionFileSource.ComposedLayers? Layers { get; init; }
}
/// <summary>The persistent composition entries of the compile cache.</summary>
public interface IWorldCompositionStore {
    /// <summary>Reads an image for this source, path and catalog identity.</summary>
    WorldComposedDocument? ReadComposition(string key);
    /// <summary>Stores an image under this source, path and catalog identity.</summary>
    void WriteComposition(string key, WorldComposedDocument image);
}
/// <summary>Resolves and reads one document's raw bytes by name — the byte-level seam basis-chain composition walks
/// over, implemented once for a directory (the local load path) and once for a flat cloud namespace
/// (<c>Puck.World.Server</c>'s storage tier). Lower-level than <see cref="IWorldNeighbourResolver"/>: this returns
/// raw bytes for one named document, never a parsed or composed one, and is what
/// <see cref="WorldDefinitionFileSource.TryComposeChain"/> calls once per chain link.</summary>
public interface IWorldDocumentSource {
    /// <summary>Gets the persistent store, when this source records every filesystem dependency.</summary>
    IWorldCompositionStore? Compositions => null;

    /// <summary>Records the inputs of an already-read root, returning whether they justify its bytes.</summary>
    bool RecordInputs(string resolvedName, byte[] content) => false;
    /// <summary>Resolves <paramref name="name"/> (a <see cref="WorldDocumentBasis.BasisMemberName"/> value, authored
    /// verbatim) against <paramref name="referrerName"/> and reads its bytes.</summary>
    /// <param name="name">The authored basis reference, exactly as the document spells it.</param>
    /// <param name="referrerName">The referring document's own resolved name (from its own prior
    /// <see cref="TryRead"/> call's <paramref name="resolvedName"/> out-param, or the caller's root name for the
    /// first hop) — a directory-relative source resolves <paramref name="name"/> beside it; a flat-namespace source
    /// ignores it.</param>
    /// <param name="resolvedName">The canonical identity of the resolved document — used for cycle/depth accounting
    /// and in refusal messages. Two different <paramref name="name"/> spellings that address the same underlying
    /// document must resolve to the same <paramref name="resolvedName"/> (this is what makes cycle detection
    /// correct); a source that cannot make that guarantee must refuse instead of guessing.</param>
    /// <param name="content">The document's raw bytes on success; <see langword="null"/> on a miss.</param>
    /// <param name="reason">The one-line, named miss reason (not found, no permission, transport error, escapes the
    /// source's addressable space) — never empty on a <see langword="false"/> return.</param>
    /// <returns><see langword="true"/> when the document was read.</returns>
    bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason);

    /// <summary>Gets whether this source resolves each name to a file beside its referrer, so a composition over it
    /// can be held and reused by path and proved still standing through <see cref="StillReads"/>. A source naming
    /// documents in a space of its own answers <see langword="false"/> and is never cached.</summary>
    bool ResolvesFiles => false;

    /// <summary>Returns whether reading <paramref name="resolvedName"/> now would yield <paramref name="content"/>
    /// again: the proof a held composition still stands. The default compares the file's current bytes.</summary>
    /// <param name="resolvedName">A name this source resolved earlier.</param>
    /// <param name="content">The bytes that read produced.</param>
    /// <returns><see langword="true"/> when the same read would produce the same bytes.</returns>
    bool StillReads(string resolvedName, byte[] content) {
        try {
            return File.ReadAllBytes(path: resolvedName).AsSpan().SequenceEqual(other: content);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return false;
        }
    }
}
