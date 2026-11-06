namespace Puck.Cli;

/// <summary>Text helpers the verbs share.</summary>
public static class CliText {
    /// <summary>Splits process output into its non-empty lines, whatever line ending each carries.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The lines in order, without line endings and without blank lines.</returns>
    public static IReadOnlyList<string> Lines(string text) => [.. text.Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => (line.Length > 0))];
}
