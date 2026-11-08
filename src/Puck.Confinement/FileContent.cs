namespace Puck.Confinement;

/// <summary>Bounded-memory byte comparisons of files, including files published by atomic replacement.</summary>
public static class FileContent {
    private static FileStream Open(string path) => new(access: FileAccess.Read, bufferSize: 4096, mode: FileMode.Open,
        options: FileOptions.SequentialScan, path: path, share: FileShare.Read | FileShare.Delete);

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
