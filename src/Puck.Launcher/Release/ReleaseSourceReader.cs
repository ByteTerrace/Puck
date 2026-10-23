namespace Puck.Launcher.Release;

// Both transports bound bytes before allocation. A manifest's attestation payload is itself bounded; this
// larger envelope cap leaves room for JSON/base64 and the two bindings without trusting an HTTP Content-Length.
internal static class ReleaseSourceReader {
    public const int MaximumManifestBytes = 1024 * 1024;

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
