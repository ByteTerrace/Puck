using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Puck.Cli;

/// <summary>Builds projects the CLI launches, restoring their closure and keeping their captured output in the
/// caller's log directory. Each project has its own log; a failed build quotes its first errors and names that log.</summary>
internal static partial class CliProjectBuild {
    /// <summary>The file that keeps a project's complete build output.</summary>
    /// <param name="project">The project file to build.</param>
    /// <returns>The project name followed by <c>.build.log</c>.</returns>
    public static string LogName(string project) => $"{Path.GetFileNameWithoutExtension(path: project)}.build.log";

    // How many lines a failed build's refusal quotes; the log keeps the rest.
    private const int QuotedLines = 5;

    // An error at the start or after an origin; the origin cannot contain an earlier diagnostic severity, so a
    // warning whose message mentions `error:` never becomes an error line.
    [GeneratedRegex(pattern: @"^(?:(?:(?!\b(?:warning|error)(?: [A-Za-z]+[0-9]+)?\s*:).)+:\s*)?error(?: [A-Za-z]+[0-9]+)?\s*:")]
    private static partial Regex ErrorLine();

    /// <summary>The build command without its output directory, which the World source-state key covers.</summary>
    /// <param name="project">The project file to build.</param>
    /// <returns>The arguments shared by every build.</returns>
    public static string[] Arguments(string project) => ["build", "--disable-build-servers", project, "-c", "Release", "--nologo", "-p:NuGetAudit=false"];

    // A failed build's refusal: why it failed, its first error lines (or its last lines when none is an error), and
    // where its whole output is kept.
    private static string Explain(string why, CliProcessResult build, string? log, string logFailure) {
        var lines = OutputLines(build: build).ToArray();
        var quoted = lines
            .Where(predicate: static line => ErrorLine().IsMatch(input: line))
            .Distinct(comparer: StringComparer.Ordinal)
            .Take(count: QuotedLines)
            .ToArray();
        var text = new StringBuilder(value: why);

        if (quoted.Length != 0) {
            _ = text.Append(value: " First errors:");
        } else {
            quoted = [.. lines.TakeLast(count: QuotedLines)];

            if (quoted.Length != 0) {
                _ = text.Append(value: " It reported no error line; its last lines:");
            }
        }

        foreach (var line in quoted) {
            _ = text.AppendLine().Append(value: "  ").Append(value: line);
        }

        _ = text.AppendLine().Append(value: ((log is null)
            ? $"Its output could not be kept: {logFailure}"
            : $"Its whole output is in {CliPaths.ToDisplay(fullPath: log)}."
        ));

        return text.ToString();
    }
    // Keeps the build's whole output in the log directory: the log's path, or null with the reason it could not be kept.
    private static string? KeepLog(CliProcessResult build, string project, string logDirectory, out string reason) {
        var path = Path.GetFullPath(path: Path.Combine(
            path1: logDirectory,
            path2: LogName(project: project)
        ));
        var text = new StringBuilder(value: build.Stdout);

        if (build.Stderr.Length != 0) {
            _ = text.AppendLine().AppendLine(value: "--- stderr ---").Append(value: build.Stderr);
        }

        try {
            _ = Directory.CreateDirectory(path: logDirectory);
            File.WriteAllText(
                contents: text.ToString(),
                path: path
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return null;
        }

        reason = string.Empty;

        return path;
    }
    // The build's output lines, standard output first, each without its MSBuild node prefix (`3>`), blank lines dropped.
    private static IEnumerable<string> OutputLines(CliProcessResult build) =>
        $"{build.Stdout}\n{build.Stderr}"
            .ReplaceLineEndings(replacementText: "\n")
            .Split(separator: '\n')
            .Select(selector: static line => line.Trim())
            .Select(selector: static line => ((line.TrimStart(trimChars: ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9']) is ['>', .. var rest])
                ? rest.Trim()
                : line
            ))
            .Where(predicate: static line => (line.Length != 0));

    /// <summary>Builds <paramref name="project"/> in Release, into
    /// <paramref name="outputDirectory"/>, restoring its closure first.</summary>
    /// <param name="repositoryRoot">The directory the build runs from.</param>
    /// <param name="project">The project to build, relative to <paramref name="repositoryRoot"/> or absolute.</param>
    /// <param name="outputDirectory">The directory every project in the closure writes into.</param>
    /// <param name="logDirectory">The caller's log directory, created only when output is kept.</param>
    /// <param name="timeout">How long the build may run.</param>
    /// <param name="build">The captured build process, or <see langword="null"/> when it could not start.</param>
    /// <param name="error">Why the build failed, its first errors and log path, or empty on success.</param>
    /// <param name="artifactName">The required artifact inside the output directory, or null to check only the build.</param>
    /// <param name="runner">A captured process runner, or null to launch dotnet.</param>
    /// <returns><see langword="true"/> when the build exited 0 inside its budget.</returns>
    public static bool TryBuild(string repositoryRoot, string project, string outputDirectory, string logDirectory, TimeSpan timeout, out CliProcessResult? build, out string error, string? artifactName = null, Func<IReadOnlyList<string>, TimeSpan, CliProcessResult>? runner = null) {
        build = null;

        var name = Path.GetFileNameWithoutExtension(path: project);
        List<string> arguments = [.. Arguments(project: project), "--output", outputDirectory];

        try {
            build = ((runner is { }) ? runner(arguments, timeout) : CliProcess.RunCaptured(
                arguments: arguments,
                fileName: "dotnet",
                input: string.Empty,
                timeout: timeout,
                workingDirectory: repositoryRoot
            ));
        } catch (Exception exception) when ((exception is InvalidOperationException or Win32Exception)) {
            error = $"could not start the {name} build: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        var log = KeepLog(build: build, logDirectory: logDirectory, project: project, reason: out var logFailure);

        if (build.TimedOut) {
            error = $"the {name} build exceeded its {timeout.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}-second budget.";
        } else if (build.ExitCode != 0) {
            error = $"the {name} build exited {build.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}.";
        } else if ((artifactName is { }) && !File.Exists(path: Path.Combine(path1: outputDirectory, path2: artifactName))) {
            error = $"the {name} build exited 0 but did not produce {artifactName}.";
        } else {
            error = string.Empty;

            return true;
        }

        error = Explain(build: build, log: log, logFailure: logFailure, why: error);

        return false;
    }
}
