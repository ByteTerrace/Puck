namespace Puck.Launcher.Release;

// Both transports bound bytes before allocation, without trusting an HTTP Content-Length. The attestation signs the
// manifest's content pin, never its inventory, so the cap bounds the inventory: a player release lists every file of its
// prewarmed caches (a backend's mesh store alone is thousands of entries) at about two hundred bytes each.
/// <summary>The byte bound every release transport reads a manifest or a file under.</summary>
public static class ReleaseSourceReader {
    /// <summary>The largest manifest, in bytes, a transport reads.</summary>
    public const int MaximumManifestBytes = 8 * 1024 * 1024;

    /// <summary>Copies a stream of untrusted length, refusing one longer than a bound before writing past it.</summary>
    /// <param name="source">The stream to read.</param>
    /// <param name="destination">The stream the bytes are written to.</param>
    /// <param name="maximumBytes">The largest number of bytes accepted.</param>
    /// <param name="cancellationToken">The token that cancels the copy.</param>
    /// <returns>The number of bytes copied, or <see langword="null"/> when the source holds more than
    /// <paramref name="maximumBytes"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is negative.</exception>
    public static async Task<long?> CopyBoundedAsync(Stream source, Stream destination, long maximumBytes, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: maximumBytes);
        var buffer = new byte[81920];
        long total = 0;

        while (true) {
            // Read one byte past the limit to distinguish an exact-length body from a truncated prefix.
            var remaining = maximumBytes - total;
            var count = (remaining < buffer.Length) ? (int)remaining + 1 : buffer.Length;
            var read = await source.ReadAsync(buffer: buffer.AsMemory(start: 0, length: count), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (read == 0) {
                return total;
            }
            if (read > remaining) {
                return null;
            }

            await destination.WriteAsync(buffer: buffer.AsMemory(start: 0, length: read), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            total += read;
        }
    }
}
