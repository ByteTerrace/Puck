using System.Security.Cryptography;

namespace Puck.Abstractions;

/// <summary>
/// A write-only stream that feeds every byte written into a running SHA-256 and, when it has one, passes the bytes on to
/// an inner stream, so a writer hashes what it produces without an entry-sized staging buffer. The inner stream stays
/// open and owned by the caller.
/// </summary>
/// <param name="inner">The stream the bytes are also written to, or <see langword="null"/> to hash them and keep none.</param>
public sealed class Sha256Stream(Stream? inner = null) : DigestingStream(inner: inner) {
    private readonly IncrementalHash m_hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

    /// <summary>Gets the SHA-256 of every byte written so far and starts a new digest.</summary>
    /// <returns>The 32-byte digest.</returns>
    public byte[] TakeHash() => m_hash.GetHashAndReset();
    /// <inheritdoc/>
    protected override void Append(ReadOnlySpan<byte> bytes) => m_hash.AppendData(data: bytes);
    /// <inheritdoc/>
    protected override void Dispose(bool disposing) {
        if (disposing) { m_hash.Dispose(); }
        base.Dispose(disposing: disposing);
    }
}
