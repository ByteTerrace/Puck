namespace Puck.Launcher.Release;

/// <summary>
/// Orders a <see cref="ReleaseManifest.Version"/> string by dot-separated numeric precedence over the core
/// <c>MAJOR.MINOR.PATCH</c> segments (a leading <c>v</c> stripped, build metadata after <c>+</c> ignored, a
/// pre-release suffix after <c>-</c> compared ordinally with a bare release always sorting after any pre-release of
/// the same core version) — practical semantic-version ordering, not the complete SemVer 2.0 precedence table (no
/// dotted pre-release identifier comparison, no numeric-vs-alphanumeric pre-release distinction).
/// </summary>
public static class ReleaseVersion {
    private static int CompareCore(IReadOnlyList<ulong> left, IReadOnlyList<ulong> right) {
        var length = Math.Max(
            val1: left.Count,
            val2: right.Count
        );

        for (var index = 0; (index < length); index++) {
            var leftSegment = ((index < left.Count)
                ? left[index]
                : 0UL
            );
            var rightSegment = ((index < right.Count)
                ? right[index]
                : 0UL
            );
            var comparison = leftSegment.CompareTo(value: rightSegment);

            if (comparison != 0) {
                return comparison;
            }
        }

        return 0;
    }
    // Ordered is whether every core segment is a decimal number; the others read as zero.
    private static (IReadOnlyList<ulong> Core, string? PreRelease, bool Ordered) Split(string version) {
        var withoutBuildMetadata = version.Split(
            separator: '+',
            count: 2
        )[0];
        var trimmed = (withoutBuildMetadata.StartsWith(value: 'v')
            ? withoutBuildMetadata[1..]
            : withoutBuildMetadata
        );
        var dashIndex = trimmed.IndexOf(value: '-');
        var corePart = ((dashIndex < 0)
            ? trimmed
            : trimmed[..dashIndex]
        );
        var preRelease = ((dashIndex < 0)
            ? null
            : trimmed[(dashIndex + 1)..]
        );
        var ordered = true;
        var core = corePart
            .Split(separator: '.')
            .Select(selector: segment => {
                if (ulong.TryParse(result: out var value, s: segment)) { return value; }
                ordered = false;
                return 0UL;
            })
            .ToArray();

        return (core, preRelease, ordered);
    }

    /// <summary>Compares two version strings.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>Negative when <paramref name="left"/> precedes <paramref name="right"/>, zero when they carry equal
    /// precedence, positive when <paramref name="left"/> follows <paramref name="right"/>.</returns>
    public static int Compare(string left, string right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        var (leftCore, leftPreRelease, _) = Split(version: left);
        var (rightCore, rightPreRelease, _) = Split(version: right);
        var coreComparison = CompareCore(
            left: leftCore,
            right: rightCore
        );

        if (coreComparison != 0) {
            return coreComparison;
        }

        // Same core version: a bare release outranks any pre-release of it, and two pre-releases compare ordinally.
        if (
            (leftPreRelease is null) &&
            (rightPreRelease is null)
        ) {
            return 0;
        }

        if (leftPreRelease is null) {
            return 1;
        }

        if (rightPreRelease is null) {
            return -1;
        }

        return string.CompareOrdinal(
            strA: leftPreRelease,
            strB: rightPreRelease
        );
    }
    /// <summary>Compares two version strings when both have a defined order: every core segment a decimal number. A name
    /// such as <c>canary</c>, whose segments <see cref="Compare"/> would read as zero, has none, so the install's release
    /// record never retires a revocation of it or installs it over another version.</summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns>As <see cref="Compare"/>, or <see langword="null"/> when either core holds a segment that is not a number.</returns>
    public static int? TryCompare(string left, string right) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);
        return ((Split(version: left).Ordered && Split(version: right).Ordered) ? Compare(left: left, right: right) : null);
    }
    /// <summary>Whether <paramref name="left"/> carries strictly greater precedence than <paramref name="right"/>.</summary>
    public static bool IsStrictlyGreaterThan(string left, string right) => (Compare(
        left: left,
        right: right
    ) > 0);
}
