namespace Puck.Abstractions;

/// <summary>
/// A write-only stream that shows every byte written to a digest (<see cref="Append"/>) and, when it has one, passes the
/// bytes on to an inner stream, so a writer digests what it produces without an entry-sized staging buffer. The inner
/// stream stays open and owned by the caller.
/// </summary>
/// <param name="inner">The stream the bytes are also written to, or <see langword="null"/> to digest them and keep none.</param>
public abstract class DigestingStream(Stream? inner = null) : Stream {
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
        Append(bytes: buffer);
    }

    /// <summary>Folds bytes into the digest.</summary>
    /// <param name="bytes">The bytes written, borrowed for the call.</param>
    protected abstract void Append(ReadOnlySpan<byte> bytes);
}
