namespace Puck.Machines.Post;

/// <summary>Diagnostic option lookup over the same parser used by the battery entry points.</summary>
public static class CommandLineArguments {
    /// <summary>Checks that each supplied option in a known set has a following nonblank value.</summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="names">The value-taking options, matched case-insensitively.</param>
    /// <param name="error">A diagnostic for a missing value, or an empty string on success.</param>
    /// <returns>Whether every supplied option has a value.</returns>
    public static bool TryValidateValues(string[] args, ReadOnlySpan<string> names, out string error) =>
        new PostCommandLine(args: args, values: names.ToArray(), requiredValues: names.ToArray()).TryValidateValues(error: out error);
    /// <summary>Returns the first supplied value for one diagnostic option.</summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="name">The option name, matched case-insensitively.</param>
    /// <returns>The first value verbatim, or null.</returns>
    public static string? Value(string[] args, string name) =>
        new PostCommandLine(args: args, values: [name]).Value(name: name);
}
