using System.Text;

namespace Puck.Cli;

/// <summary>
/// The write-or-check step of a verb that generates a checked-in file from the code: it writes the text, or under
/// <c>--check</c> compares it against the file and names the first line that differs. Line endings never count as a
/// difference.
/// </summary>
internal static class CliGeneratedFile {
    /// <summary>Writes a generated file, or compares the checked-in file against the generated text.</summary>
    /// <param name="check">Whether to compare rather than write.</param>
    /// <param name="detail">The text appended to the written and matched lines, such as <c> (12 sites)</c>, or empty.</param>
    /// <param name="relativePath">The file's repository-relative path, with forward slashes.</param>
    /// <param name="repositoryRoot">The repository root the path resolves against.</param>
    /// <param name="source">What the text is generated from, as the messages name it, such as <c>the model</c>.</param>
    /// <param name="text">The generated text, with LF line endings.</param>
    /// <param name="verb">The verb as it is typed after <c>puck</c>, which prefixes every line.</param>
    /// <returns><see langword="true"/> when the file was written or matched; <see langword="false"/> when a check found
    /// the file missing or different.</returns>
    public static bool WriteOrCheck(bool check, string detail, string relativePath, string repositoryRoot, string source, string text, string verb) {
        var path = Path.Combine(
            path1: repositoryRoot,
            path2: relativePath
        );

        if (!check) {
            File.WriteAllText(
                contents: text,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                path: path
            );
            Console.Out.WriteLine(value: $"{verb}: wrote {relativePath}{detail}.");

            return true;
        }

        if (!File.Exists(path: path)) {
            Console.Error.WriteLine(value: $"{verb}: {relativePath} is missing; run `puck {verb}` to write it.");

            return false;
        }

        var onDisk = File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n");

        if (string.Equals(
            a: onDisk,
            b: text,
            comparisonType: StringComparison.Ordinal
        )) {
            Console.Out.WriteLine(value: $"{verb}: {relativePath} matches {source}{detail}.");

            return true;
        }

        var expected = text.Split(separator: '\n');
        var actual = onDisk.Split(separator: '\n');
        var line = 0;

        while (
            (line < expected.Length) &&
            (line < actual.Length) &&
            string.Equals(
            a: expected[line],
            b: actual[line],
            comparisonType: StringComparison.Ordinal
        )
        ) {
            line++;
        }

        Console.Error.WriteLine(value: $"{verb}: {relativePath} disagrees with {source} at line {(line + 1)}; run `puck {verb}` to rewrite it.");
        Console.Error.WriteLine(value: $"  on disk:   {((line < actual.Length)
            ? actual[line]
            : "(end of file)")}");
        Console.Error.WriteLine(value: $"  generated: {((line < expected.Length)
            ? expected[line]
            : "(end of file)")}");

        return false;
    }
}
