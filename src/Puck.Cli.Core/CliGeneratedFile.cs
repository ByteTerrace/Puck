using System.Text;

namespace Puck.Cli;

/// <summary>One file a verb generates: its repository-relative path with forward slashes, and its text with LF line
/// endings.</summary>
/// <param name="RelativePath">The file's repository-relative path, with forward slashes.</param>
/// <param name="Text">The generated text, with LF line endings.</param>
public readonly record struct GeneratedFile(string RelativePath, string Text);

/// <summary>
/// The write-or-check step of a verb that generates checked-in files from the code: it writes the text, or under
/// <c>--check</c> compares it against the file and names the first line that differs. Line endings never count as a
/// difference. A check also holds every file that matches to what the git index holds: a file that matches only in the
/// working tree is drift the change has not staged, since a build or an earlier run may have written it.
/// </summary>
public static class CliGeneratedFile {
    /// <summary>Writes a generated file, or compares the checked-in file against the generated text.</summary>
    /// <param name="check">Whether to compare rather than write.</param>
    /// <param name="detail">The text appended to the written and matched lines, such as <c> (12 sites)</c>, or empty.</param>
    /// <param name="relativePath">The file's repository-relative path, with forward slashes.</param>
    /// <param name="repositoryRoot">The repository root the path resolves against.</param>
    /// <param name="source">What the text is generated from, as the messages name it, such as <c>the model</c>.</param>
    /// <param name="text">The generated text, with LF line endings.</param>
    /// <param name="verb">The verb as it is typed after <c>puck</c>, which prefixes every line.</param>
    /// <returns><see langword="true"/> when the file was written or matched; <see langword="false"/> when a check found
    /// the file missing, different or unstaged.</returns>
    public static bool WriteOrCheck(bool check, string detail, string relativePath, string repositoryRoot, string source, string text, string verb) =>
        WriteOrCheckAll(
            check: check,
            detail: detail,
            eachFile: true,
            files: [new GeneratedFile(RelativePath: relativePath, Text: text)],
            repositoryRoot: repositoryRoot,
            source: source,
            verb: verb
        );
    /// <summary>Writes every generated file, or compares each checked-in file against its generated text, then holds the
    /// matching files to the git index with one git query.</summary>
    /// <param name="check">Whether to compare rather than write.</param>
    /// <param name="detail">The text appended to each written and matched line, or empty.</param>
    /// <param name="eachFile">Whether to print a line for each file written or matched; a mismatch always prints.</param>
    /// <param name="files">The files, each with its path and generated text.</param>
    /// <param name="repositoryRoot">The repository root the paths resolve against.</param>
    /// <param name="source">What the text is generated from, as the messages name it.</param>
    /// <param name="verb">The verb as it is typed after <c>puck</c>, which prefixes every line.</param>
    /// <returns><see langword="true"/> when every file was written or matched and staged; otherwise
    /// <see langword="false"/>.</returns>
    public static bool WriteOrCheckAll(bool check, string detail, bool eachFile, IReadOnlyList<GeneratedFile> files, string repositoryRoot, string source, string verb) {
        var ok = true;
        var matched = new List<string>();

        foreach (var (relativePath, text) in files) {
            var path = Path.Combine(
                path1: repositoryRoot,
                path2: relativePath
            );

            if (!check) {
                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
                File.WriteAllText(
                    contents: text,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    path: path
                );

                if (eachFile) {
                    Console.Out.WriteLine(value: $"{verb}: wrote {relativePath}{detail}.");
                }

                continue;
            }

            if (!File.Exists(path: path)) {
                Console.Error.WriteLine(value: $"{verb}: {relativePath} is missing; run `puck {verb}` to write it.");
                ok = false;

                continue;
            }

            var onDisk = File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n");

            if (string.Equals(
                a: onDisk,
                b: text,
                comparisonType: StringComparison.Ordinal
            )) {
                if (eachFile) {
                    Console.Out.WriteLine(value: $"{verb}: {relativePath} matches {source}{detail}.");
                }

                matched.Add(item: relativePath);

                continue;
            }

            ok = false;
            ReportDifference(
                actualText: onDisk,
                expectedText: text,
                relativePath: relativePath,
                source: source,
                verb: verb
            );
        }

        if (check) {
            foreach (var path in Unstaged(paths: matched, repositoryRoot: repositoryRoot)) {
                Console.Error.WriteLine(value: $"{verb}: {path} matches {source} only in the working tree, and its staged copy differs or is missing; a build or `puck {verb}` wrote it, so stage and commit it.");
                ok = false;
            }
        }

        return ok;
    }

    private static void ReportDifference(string relativePath, string source, string verb, string actualText, string expectedText) {
        var expected = expectedText.Split(separator: '\n');
        var actual = actualText.Split(separator: '\n');
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
    }
    // The paths whose working-tree text differs from or is absent from the index, when the root is a git work tree's top.
    private static IReadOnlyList<string> Unstaged(string repositoryRoot, IReadOnlyList<string> paths) {
        if (paths.Count == 0) {
            return [];
        }

        var top = CliGit.Run(repositoryRoot, "rev-parse", "--show-toplevel");

        if ((top.ExitCode != 0) || !string.Equals(
            a: Path.GetFullPath(path: top.Stdout.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b: Path.GetFullPath(path: repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            return [];
        }

        var staged = CliGit.Run(repositoryRoot, ["ls-files", "--cached", "-z", "--", .. paths]);
        var differing = CliGit.Run(repositoryRoot, ["diff", "--name-only", "-z", "--no-ext-diff", "--no-textconv", "--", .. paths]);

        if ((staged.ExitCode != 0) || (differing.ExitCode != 0)) {
            throw new InvalidOperationException(message: $"Cannot check generated files against the git index: {staged.Stderr}{differing.Stderr}");
        }

        var indexed = staged.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0').ToHashSet(comparer: StringComparer.Ordinal);

        return [.. paths.Where(predicate: path => !indexed.Contains(item: path))
            .Concat(second: differing.Stdout.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\0'))
            .Distinct(comparer: StringComparer.Ordinal)];
    }
}
