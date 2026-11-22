using Puck.Abstractions;
using Puck.Assets;

namespace Puck.World;

/// <summary>One immutable observable row: the cells a recipient may read and the row's observation, as
/// <see cref="WorldDisclosureImages"/> reads them from the store under one disclosure mask. Vector payloads are privately
/// copied.</summary>
public sealed class WorldRecipientImage {
    internal WorldRecipientImage(Sha256Digest digest, StateCell[] cells, WorldObservedRow? observation, int bytes) {
        Digest = digest; Cells = Array.AsReadOnly(cells); Observation = observation; Bytes = bytes;
    }
    // Spelled on first read: most images are compared by digest alone. A pin is one reference, so a racing first read
    // writes it whole and every writer writes the same text.
    private ContentPin m_identity;
    /// <summary>Content identity, including counts, placeholder order, traits and observations.</summary>
    public ContentPin Identity {
        get {
            if (m_identity == default) {
                Span<byte> digest = stackalloc byte[Sha256Digest.SizeInBytes];

                Digest.CopyTo(destination: digest);
                m_identity = ContentPin.FromDigest(digest: digest);
            }
            return m_identity;
        }
    }
    /// <summary>Readable cells in recipient order.</summary>
    public IReadOnlyList<StateCell> Cells { get; }
    /// <summary>The presentation image, or null when the row itself is withheld.</summary>
    public WorldObservedRow? Observation { get; }
    internal int Bytes { get; }
    // The SHA-256 digest of the canonical bytes written by the pass that built the image, which Identity spells.
    internal Sha256Digest Digest { get; }
}

// The keys a row held, in order, when a stream last read the row whole: all a delivery cursor keeps of such a row, to
// tell which cells the stream read if the row later withholds. One list serves every stream that read the row.
internal sealed class WorldRowMembers(CellName[] keys) {
    public static WorldRowMembers Empty { get; } = new(keys: []);
    public ReadOnlySpan<CellName> Keys => keys;
}
