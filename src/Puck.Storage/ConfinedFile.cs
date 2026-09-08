namespace Puck.Storage;

/// <summary>Bounded, no-follow reads of a file explicitly selected by the trusted host, such as deployment configuration.</summary>
public static class ConfinedFile {
    /// <summary>Reads one regular, singly linked file while retaining its directory capabilities.</summary>
    /// <param name="path">The exact host-selected path, never a path received from a world or extension caller.</param>
    /// <param name="maximumBytes">Positive byte ceiling checked before allocation.</param>
    /// <returns>The file's bytes.</returns>
    /// <exception cref="IOException">The file is missing, linked, oversized, or inaccessible.</exception>
    /// <exception cref="ArgumentException">The path or ceiling is invalid.</exception>
    public static byte[] ReadAllBytes(string path, int maximumBytes) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        _ = ObjectBlobAddressPath.GetKeySegments(new(Guid.Empty, name));
        using var directory = ConfinedDirectory.Open(Path.GetDirectoryName(full)!, create: false)
            ?? throw new FileNotFoundException("Host configuration directory does not exist.");
        using var stream = directory.OpenFile(name) ?? throw new FileNotFoundException("Host configuration file does not exist.");
        if (stream.Length > maximumBytes) { throw new IOException("Host configuration exceeds its byte budget."); }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
