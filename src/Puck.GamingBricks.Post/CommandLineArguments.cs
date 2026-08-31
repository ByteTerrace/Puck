namespace Puck.GamingBricks.Post;

public static class CommandLineArguments {
    /// <summary>Resolves a directory root: the CLI flag's value wins; else the environment variable, when it names an
    /// existing directory; else <paramref name="fallback"/>, when it names an existing directory; else
    /// <see langword="null"/> (the stages that need the root skip when it is absent).</summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="flag">The CLI flag naming the root explicitly.</param>
    /// <param name="variable">The environment variable naming the root.</param>
    /// <param name="fallback">An optional last-resort directory path.</param>
    public static string? ResolveDirectoryRoot(string[] args, string flag, string variable, string? fallback = null) {
        var explicitRoot = Value(
            args: args,
            name: flag
        );

        if (!string.IsNullOrEmpty(value: explicitRoot)) {
            return explicitRoot;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(variable: variable);

        if (
            !string.IsNullOrEmpty(value: fromEnvironment) &&
            Directory.Exists(path: fromEnvironment)
        ) {
            return fromEnvironment;
        }

        return (((fallback is not null) && Directory.Exists(path: fallback))
            ? fallback
            : null);
    }
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
