namespace Puck.GamingBricks.Post;

/// <summary>Explicit argument lookup and value validation shared by the GamingBrick Post runners.</summary>
public static class CommandLineArguments {
    /// <summary>Checks that each supplied flag in a known set has a following non-option value.</summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="names">The value-taking flags to validate, matched case-insensitively.</param>
    /// <param name="error">A diagnostic for a missing value, or an empty string on success.</param>
    /// <returns>Whether every supplied flag has a value.</returns>
    public static bool TryValidateValues(string[] args, ReadOnlySpan<string> names, out string error) {
        for (var index = 0; index < args.Length; ++index) {
            foreach (var name in names) {
                if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase) &&
                    (index + 1 == args.Length || string.IsNullOrWhiteSpace(value: args[index + 1]) || args[index + 1].StartsWith(value: "--", comparisonType: StringComparison.Ordinal))) {
                    error = $"{name} requires a value.";
                    return false;
                }
            }
        }
        error = "";
        return true;
    }

    /// <summary>Returns the value following the first occurrence of <paramref name="name"/> in <paramref name="args"/>,
    /// or <see langword="null"/> when the flag or its following value is absent.</summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="name">The flag to look up (matched case-insensitively).</param>
    /// <returns>The following argument verbatim, or null. Use <see cref="TryValidateValues"/> to reject missing values.</returns>
    public static string? Value(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (string.Equals(
                a: args[index],
                b: name,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                return args[(index + 1)];
            }
        }

        return null;
    }
}
