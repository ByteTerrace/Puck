namespace Puck.World;

/// <summary>Resolves and reads one document's raw bytes by name — the byte-level seam basis-chain composition walks
/// over, implemented once for a directory (the local load path) and once for a flat cloud namespace
/// (<c>Puck.World.Server</c>'s storage tier). Lower-level than <see cref="IWorldNeighbourResolver"/>: this returns
/// raw bytes for one named document, never a parsed or composed one, and is what
/// <see cref="WorldDefinitionFileSource.TryComposeChain"/> calls once per chain link.</summary>
public interface IWorldDocumentSource {
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
}
