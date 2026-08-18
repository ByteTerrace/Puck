namespace Puck.Launcher.Release;

/// <summary>
/// Orders a <see cref="ReleaseManifest.Version"/> string by dot-separated numeric precedence over the core
/// <c>MAJOR.MINOR.PATCH</c> segments (a leading <c>v</c> stripped, build metadata after <c>+</c> ignored, a
/// pre-release suffix after <c>-</c> compared ordinally with a bare release always sorting after any pre-release of
/// the same core version) — practical semantic-version ordering, not the complete SemVer 2.0 precedence table (no
/// dotted pre-release identifier comparison, no numeric-vs-alphanumeric pre-release distinction).
/// </summary>
public static class ReleaseVersion {
    /// <summary>Compares two version strings.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>Negative when <paramref name="left"/> precedes <paramref name="right"/>, zero when they carry equal
    /// precedence, positive when <paramref name="left"/> follows <paramref name="right"/>.</returns>
    public static int Compare(string left, string right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        var (leftCore, leftPreRelease) = Split(version: left);
        var (rightCore, rightPreRelease) = Split(version: right);
        var coreComparison = CompareCore(left: leftCore, right: rightCore);

        if (coreComparison != 0) {
            return coreComparison;
        }

        // Same core version: a bare release outranks any pre-release of it, and two pre-releases compare ordinally.
        if ((leftPreRelease is null) && (rightPreRelease is null)) {
            return 0;
        }

        if (leftPreRelease is null) {
            return 1;
        }

        if (rightPreRelease is null) {
            return -1;
        }

        return string.CompareOrdinal(strA: leftPreRelease, strB: rightPreRelease);
    }
    /// <summary>Whether <paramref name="left"/> carries strictly greater precedence than <paramref name="right"/>.</summary>
    public static bool IsStrictlyGreaterThan(string left, string right) => (Compare(left: left, right: right) > 0);

    private static int CompareCore(IReadOnlyList<ulong> left, IReadOnlyList<ulong> right) {
        var length = Math.Max(val1: left.Count, val2: right.Count);

        for (var index = 0; (index < length); index++) {
            var leftSegment = ((index < left.Count) ? left[index] : 0UL);
            var rightSegment = ((index < right.Count) ? right[index] : 0UL);
            var comparison = leftSegment.CompareTo(value: rightSegment);

            if (comparison != 0) {
                return comparison;
            }
        }

        return 0;
    }
    private static (IReadOnlyList<ulong> Core, string? PreRelease) Split(string version) {
        var withoutBuildMetadata = version.Split(separator: '+', count: 2)[0];
        var trimmed = (withoutBuildMetadata.StartsWith(value: 'v') ? withoutBuildMetadata[1..] : withoutBuildMetadata);
        var dashIndex = trimmed.IndexOf(value: '-');
        var corePart = ((dashIndex < 0) ? trimmed : trimmed[..dashIndex]);
        var preRelease = ((dashIndex < 0) ? null : trimmed[(dashIndex + 1)..]);
        var core = corePart
            .Split(separator: '.')
            .Select(selector: segment => (ulong.TryParse(result: out var value, s: segment) ? value : 0UL))
            .ToArray();

        return (core, preRelease);
    }
}
