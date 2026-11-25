using Puck.Assets;

namespace Puck.Launcher.Release;

/// <summary>Fetches a channel tree's content-addressed objects through an <see cref="IReleaseSource"/>, which is trusted
/// for transport only: every object is checked against the pin and length that named it before a caller keeps it. The
/// installer's stager and <c>puck world caches fetch</c> both take objects this way, many at a time.</summary>
public static class ReleaseObjectFetch {
    /// <summary>How many objects a caller fetches at once. A channel's objects are small immutable files served from an
    /// edge cache (a player build's mesh store alone is thousands of them), so one at a time spends the fetch on round
    /// trips; a bounded handful keeps the connection pool and the memory held by objects in flight small.</summary>
    public const int Parallelism = 16;

    /// <summary>Fetches one object and checks it against its pin and length.</summary>
    /// <param name="source">The source to fetch through.</param>
    /// <param name="pin">The object's content pin.</param>
    /// <param name="length">The object's exact length.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The object's bytes, or <see langword="null"/> with why the source could not supply them: it lacks the
    /// object, or the bytes it sent do not match the pin.</returns>
    public static async Task<(byte[]? Content, string? Refusal)> TryFetchAsync(IReleaseSource source, ContentPin pin, long length, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: source);
        using var buffer = new MemoryStream();

        if (!await source.TryGetFileAsync(cancellationToken: cancellationToken, destination: buffer, length: length, pin: pin).ConfigureAwait(continueOnCapturedContext: false)) {
            return (null, "could not be fetched");
        }

        var content = buffer.ToArray();
        var actual = ContentPin.Compute(content: content);

        return ((content.LongLength == length) && (actual == pin)) ? (content, null) : (null, $"was fetched as {content.LongLength} bytes with hash {actual}, not {length} bytes with hash {pin}");
    }
}
