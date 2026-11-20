using System.Security.Cryptography;

namespace Puck.Abstractions;

/// <summary>
/// A write-only stream that feeds every byte written into a running SHA-256 and, when it has one, passes the bytes on to
/// an inner stream, so a writer hashes what it produces without an entry-sized staging buffer. The inner stream stays
/// open and owned by the caller.
/// </summary>
/// <param name="inner">The stream the bytes are also written to, or <see langword="null"/> to hash them and keep none.</param>
public sealed class Sha256Stream(Stream? inner = null) : Stream {
    private readonly IncrementalHash m_hash = IncrementalHash.CreateHash(hashAlgorithm: HashAlgorithmName.SHA256);

    /// <inheritdoc/>
    public override bool CanRead => false;
    /// <inheritdoc/>
    public override bool CanSeek => false;
    /// <inheritdoc/>
    public override bool CanWrite => true;
    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();
    /// <inheritdoc/>
    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Gets the SHA-256 of every byte written so far and starts a new digest.</summary>
    /// <returns>The 32-byte digest.</returns>
    public byte[] TakeHash() => m_hash.GetHashAndReset();
    /// <inheritdoc/>
    public override void Flush() => inner?.Flush();
    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer: buffer.AsSpan(length: count, start: offset));
    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer) {
        inner?.Write(buffer: buffer);
        m_hash.AppendData(data: buffer);
    }
    /// <inheritdoc/>
    protected override void Dispose(bool disposing) {
        if (disposing) { m_hash.Dispose(); }
        base.Dispose(disposing: disposing);
    }
}
