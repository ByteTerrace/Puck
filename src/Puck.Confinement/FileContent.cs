namespace Puck.Confinement;

/// <summary>Bounded-memory byte comparisons of files, including files published by atomic replacement, and reads of
/// an opened stream held to a byte ceiling.</summary>
public static class FileContent {
    private const int GrowthBytes = 4096;

    private static FileStream Open(string path) => new(access: FileAccess.Read, bufferSize: 4096, mode: FileMode.Open,
        options: FileOptions.SequentialScan, path: path, share: FileShare.Read | FileShare.Delete);
    // The stream's remaining length only sizes the first buffer: the reads themselves decide how many bytes there are,
    // so a file that grows or shrinks after the length was taken can neither exceed the ceiling nor end short of its end.
    private static byte[] FirstBuffer(Stream stream, int maximumBytes) {
        ArgumentNullException.ThrowIfNull(argument: stream);
        ArgumentOutOfRangeException.ThrowIfNegative(value: maximumBytes);

        return new byte[(stream.CanSeek ? ((int)Math.Clamp(value: (stream.Length - stream.Position), min: 0L, max: maximumBytes)) : Math.Min(val1: maximumBytes, val2: GrowthBytes))];
    }
    // A full buffer read one byte past itself: grows it to hold that byte, or answers false when it already holds the ceiling.
    private static bool Grow(ref byte[] buffer, int maximumBytes, byte next) {
        var count = buffer.Length;

        if (count >= maximumBytes) { return false; }
        Array.Resize(array: ref buffer, newSize: ((int)Math.Min(val1: maximumBytes, val2: Math.Max(val1: (count * 2L), val2: GrowthBytes))));
        buffer[count] = next;
        return true;
    }
    private static byte[] Trim(byte[] buffer, int count) => ((count == buffer.Length) ? buffer : buffer.AsSpan(length: count, start: 0).ToArray());

    /// <summary>Decodes a file's whole content as text exactly as <see cref="File.ReadAllText(string, System.Text.Encoding)"/>
    /// decodes the file: a byte-order mark selects the encoding and is not part of the text, and
    /// <paramref name="encoding"/> is assumed otherwise.</summary>
    /// <param name="content">The file's bytes, whole.</param>
    /// <param name="encoding">The encoding assumed when the content carries no byte-order mark; UTF-8 when
    /// <see langword="null"/>.</param>
    /// <returns>The decoded text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static string Text(byte[] content, System.Text.Encoding? encoding = null) {
        ArgumentNullException.ThrowIfNull(argument: content);

        using var reader = new StreamReader(
            detectEncodingFromByteOrderMarks: true,
            encoding: (encoding ?? System.Text.Encoding.UTF8),
            stream: new MemoryStream(buffer: content)
        );

        return reader.ReadToEnd();
    }
    /// <summary>Returns a file's whole content as the UTF-8 bytes of the text <see cref="Text"/> decodes from it, with
    /// no byte-order mark: well-formed UTF-8 is the content itself, past a UTF-8 mark, and anything else (another
    /// encoding's mark, or bytes that are not well-formed UTF-8) is decoded by <see cref="Text"/> and encoded again. A
    /// reader of UTF-8 (a JSON reader) reads the result as it would read the decoded text, without the text.</summary>
    /// <param name="content">The file's bytes, whole.</param>
    /// <returns>The UTF-8 text bytes: a slice of <paramref name="content"/> when it is well-formed UTF-8, otherwise a
    /// new array.</returns>
    public static ReadOnlyMemory<byte> Utf8(ReadOnlyMemory<byte> content) {
        var text = (content.Span.StartsWith(value: System.Text.Encoding.UTF8.Preamble) ? content[System.Text.Encoding.UTF8.Preamble.Length..] : content);

        return (System.Text.Unicode.Utf8.IsValid(value: text.Span)
            ? text
            : System.Text.Encoding.UTF8.GetBytes(s: Text(content: content.ToArray())));
    }

    /// <summary>Reads a stream from its position to its end, refusing to hold more than <paramref name="maximumBytes"/>.
    /// The stream is opened once by the caller and read until end of stream; its length is never trusted as the
    /// answer, so no byte past the ceiling is ever allocated and no file that grew after its length was taken returns a
    /// prefix of itself. A same-length rewrite during the read is not detected.</summary>
    /// <param name="stream">The readable stream, positioned where the content begins; the caller owns it.</param>
    /// <param name="maximumBytes">The ceiling, in bytes, the content may reach.</param>
    /// <returns>Every byte up to end of stream, or <see langword="null"/> when the stream holds more than
    /// <paramref name="maximumBytes"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is negative.</exception>
    /// <exception cref="IOException">The stream could not be read.</exception>
    public static byte[]? ReadAtMost(Stream stream, int maximumBytes) {
        var buffer = FirstBuffer(maximumBytes: maximumBytes, stream: stream);
        var count = 0;
        Span<byte> probe = stackalloc byte[1];

        while (true) {
            if (count < buffer.Length) {
                var read = stream.Read(buffer: buffer.AsSpan(start: count));

                if (read == 0) { return Trim(buffer: buffer, count: count); }
                count += read;
            } else if (stream.Read(buffer: probe) == 0) {
                return buffer;
            } else if (Grow(buffer: ref buffer, maximumBytes: maximumBytes, next: probe[0])) {
                count++;
            } else {
                return null;
            }
        }
    }
    /// <summary>Reads a stream from its position to its end asynchronously, refusing to hold more than
    /// <paramref name="maximumBytes"/>, with the contract of <see cref="ReadAtMost"/>.</summary>
    /// <param name="stream">The readable stream, positioned where the content begins; the caller owns it.</param>
    /// <param name="maximumBytes">The ceiling, in bytes, the content may reach.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>Every byte up to end of stream, or <see langword="null"/> when the stream holds more than
    /// <paramref name="maximumBytes"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is negative.</exception>
    /// <exception cref="IOException">The stream could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async ValueTask<byte[]?> ReadAtMostAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken = default) {
        var buffer = FirstBuffer(maximumBytes: maximumBytes, stream: stream);
        var count = 0;
        var probe = new byte[1];

        while (true) {
            if (count < buffer.Length) {
                var read = await stream.ReadAsync(buffer: buffer.AsMemory(start: count), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                if (read == 0) { return Trim(buffer: buffer, count: count); }
                count += read;
            } else if (await stream.ReadAsync(buffer: probe, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false) == 0) {
                return buffer;
            } else if (Grow(buffer: ref buffer, maximumBytes: maximumBytes, next: probe[0])) {
                count++;
            } else {
                return null;
            }
        }
    }

    /// <summary>Compares a file to a byte span without allocating the file's length. A missing file differs.</summary>
    /// <param name="path">The file.</param>
    /// <param name="expected">The expected bytes.</param>
    /// <returns>Whether every byte agrees.</returns>
    public static bool Equals(string path, ReadOnlySpan<byte> expected) {
        try {
            using var stream = Open(path: path);

            if (stream.Length != expected.Length) { return false; }
            Span<byte> buffer = stackalloc byte[4096];
            var offset = 0;

            while (offset < expected.Length) {
                var read = stream.Read(buffer: buffer[..Math.Min(val1: buffer.Length, val2: (expected.Length - offset))]);

                if ((read == 0) || !buffer[..read].SequenceEqual(other: expected.Slice(length: read, start: offset))) { return false; }
                offset += read;
            }
            return (stream.ReadByte() == -1);
        } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
    }
    /// <summary>Compares two files without allocating either file's length.</summary>
    /// <param name="left">One file.</param>
    /// <param name="right">The other file.</param>
    /// <returns>Whether every byte agrees. Missing files propagate their read error.</returns>
    public static bool Equals(string left, string right) {
        using var first = Open(path: left);
        using var second = Open(path: right);

        if (first.Length != second.Length) { return false; }
        Span<byte> a = stackalloc byte[4096];
        Span<byte> b = stackalloc byte[4096];

        while (true) {
            var count = first.Read(buffer: a);

            if (count == 0) { return (second.ReadByte() == -1); }
            second.ReadExactly(buffer: b[..count]);
            if (!a[..count].SequenceEqual(other: b[..count])) { return false; }
        }
    }
}
