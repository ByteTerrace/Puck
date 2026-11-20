namespace Puck.Launcher.Release;

// Both transports bound bytes before allocation, without trusting an HTTP Content-Length. The attestation signs the
// manifest's content pin, never its inventory, so the cap bounds the inventory: a player release lists every file of its
// prewarmed caches (a backend's mesh store alone is thousands of entries) at about two hundred bytes each.
internal static class ReleaseSourceReader {
    public const int MaximumManifestBytes = 8 * 1024 * 1024;

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
