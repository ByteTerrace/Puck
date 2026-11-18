using System.Globalization;

namespace Puck.Cli;

/// <summary>A package or tag version the way NuGet, npm and Git tags all spell it: dot-separated numbers (up to any
/// count), an optional <c>-prerelease</c> suffix and ignored <c>+build</c> metadata. Ordering follows semantic
/// versioning: numbers first, a release above its own prereleases, prereleases by their dot-separated identifiers.
/// <see cref="Parse"/> refuses anything else, so a spec that is not a version (a URL, a range) never gets one.</summary>
public sealed class DependencyVersion : IComparable<DependencyVersion>, IEquatable<DependencyVersion> {
    private readonly long[] m_numbers;
    private readonly string[] m_prerelease;

    private DependencyVersion(long[] numbers, string prerelease, string text) {
        m_numbers = numbers;
        m_prerelease = ((prerelease.Length == 0) ? [] : prerelease.Split(separator: '.'));
        Text = text;
    }

    private static int ComparePrerelease(string[] left, string[] right) {
        // A release outranks every prerelease of the same numbers.
        if ((left.Length == 0) || (right.Length == 0)) { return right.Length.CompareTo(value: left.Length); }
        for (var index = 0; (index < Math.Min(val1: left.Length, val2: right.Length)); ++index) {
            var leftNumeric = long.TryParse(provider: CultureInfo.InvariantCulture, result: out var leftNumber, s: left[index], style: NumberStyles.None);
            var rightNumeric = long.TryParse(provider: CultureInfo.InvariantCulture, result: out var rightNumber, s: right[index], style: NumberStyles.None);
            var order = (leftNumeric && rightNumeric)
                ? leftNumber.CompareTo(value: rightNumber)
                : (leftNumeric ? -1 : (rightNumeric ? 1 : string.CompareOrdinal(strA: left[index], strB: right[index])));

            if (order != 0) { return order; }
        }

        return left.Length.CompareTo(value: right.Length);
    }

    /// <summary>The version as spelled, without a leading <c>v</c> or build metadata.</summary>
    public string Text { get; }
    /// <summary>The first number.</summary>
    public long Major => m_numbers[0];
    /// <summary>The second number, zero when the version has one number.</summary>
    public long Minor => ((m_numbers.Length > 1) ? m_numbers[1] : 0L);
    /// <summary>Whether this version has no prerelease suffix.</summary>
    public bool IsStable => (m_prerelease.Length == 0);

    /// <summary>Creates an empty set of version spellings, ordered by version, for the versions of one package.</summary>
    /// <returns>The set; it must only hold spellings <see cref="Parse"/> reads.</returns>
    public static SortedSet<string> NewSet() => new(comparer: Comparer<string>.Create(comparison: static (left, right) => Parse(text: left).CompareTo(other: Parse(text: right))));
    /// <summary>Reads a version, with an optional leading <c>v</c>.</summary>
    /// <param name="text">The spelling.</param>
    /// <param name="version">The version when <paramref name="text"/> is one.</param>
    /// <returns>Whether <paramref name="text"/> is a version.</returns>
    public static bool TryParse(string? text, out DependencyVersion version) {
        version = null!;
        if (string.IsNullOrWhiteSpace(value: text)) { return false; }

        var body = text.Trim().TrimStart(trimChar: 'v');
        var plus = body.IndexOf(value: '+');

        if (plus >= 0) { body = body[..plus]; }
        var dash = body.IndexOf(value: '-');
        var numbers = ((dash >= 0) ? body[..dash] : body);
        var prerelease = ((dash >= 0) ? body[(dash + 1)..] : string.Empty);

        if ((dash >= 0) && (prerelease.Length == 0)) { return false; }
        var parts = numbers.Split(separator: '.');
        var values = new long[parts.Length];

        for (var index = 0; (index < parts.Length); ++index) {
            if (!long.TryParse(provider: CultureInfo.InvariantCulture, result: out values[index], s: parts[index], style: NumberStyles.None)) { return false; }
        }
        version = new DependencyVersion(numbers: values, prerelease: prerelease, text: body);
        return true;
    }
    /// <summary>Reads a version that must be well formed.</summary>
    /// <param name="text">The spelling.</param>
    /// <returns>The version.</returns>
    public static DependencyVersion Parse(string text) =>
        (TryParse(text: text, version: out var version) ? version : throw new FormatException(message: $"'{text}' is not a version."));
    /// <summary>The highest of the stable versions among <paramref name="spellings"/> that satisfy <paramref name="admits"/>.</summary>
    /// <param name="spellings">The candidate spellings; one that is not a version is skipped.</param>
    /// <param name="admits">Which candidates count.</param>
    /// <returns>The highest, or <see langword="null"/> when none counts.</returns>
    public static DependencyVersion? HighestStable(IEnumerable<string> spellings, Func<DependencyVersion, bool>? admits = null) {
        DependencyVersion? best = null;

        foreach (var spelling in spellings) {
            if (!TryParse(text: spelling, version: out var candidate) || !candidate.IsStable || ((admits is not null) && !admits(arg: candidate))) { continue; }
            if ((best is null) || (candidate.CompareTo(other: best) > 0)) { best = candidate; }
        }

        return best;
    }
    /// <inheritdoc/>
    public int CompareTo(DependencyVersion? other) {
        if (other is null) { return 1; }
        for (var index = 0; (index < Math.Max(val1: m_numbers.Length, val2: other.m_numbers.Length)); ++index) {
            var left = ((index < m_numbers.Length) ? m_numbers[index] : 0L);
            var right = ((index < other.m_numbers.Length) ? other.m_numbers[index] : 0L);

            if (left != right) { return left.CompareTo(value: right); }
        }

        return ComparePrerelease(left: m_prerelease, right: other.m_prerelease);
    }
    /// <inheritdoc/>
    public bool Equals(DependencyVersion? other) => ((other is not null) && (CompareTo(other: other) == 0));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as DependencyVersion));
    /// <inheritdoc/>
    public override int GetHashCode() => Text.ToLowerInvariant().GetHashCode();
    /// <inheritdoc/>
    public override string ToString() => Text;
}
