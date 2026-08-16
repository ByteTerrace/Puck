namespace Puck.GamingBricks.Post;

public static class CommandLineArguments {
    /// <summary>Returns the value following the first occurrence of <paramref name="name"/> in <paramref name="args"/>,
    /// or <see langword="null"/> when the flag is absent.</summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="name">The flag to look up (matched case-insensitively).</param>
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
