using System.Text.RegularExpressions;
using System.Xml.Linq;
using Puck.Cli.Gate;

namespace Puck.Cli.Locks;

/// <summary>What <see cref="LockFiles.Check"/> found: its exit code, one report line per offending project, and the
/// restore's own output for the log.</summary>
/// <param name="ExitCode"><see cref="CliExit.Success"/> when every lock file matched its project, otherwise
/// <see cref="CliExit.Failed"/>.</param>
/// <param name="Report">One line per project whose lock file drifted, was missing or failed to restore, by its
/// repository-relative path; empty when the check passed.</param>
/// <param name="RestoreOutput">Everything the locked restore wrote.</param>
public sealed record LockCheck(int ExitCode, IReadOnlyList<string> Report, string RestoreOutput);
/// <summary>The solution's <c>packages.lock.json</c> files against the projects that own them. Every restore in the tree
/// is locked (<c>Directory.Build.props</c> sets <c>RestoreLockedMode</c>), so a lock file that no longer matches its
/// project fails the build that drifted it. <see cref="Check"/> reports each such project by name and writes nothing,
/// which is how <c>puck locks --check</c> and the gate's first step run it, before anything builds; <see cref="Record"/>
/// is the one explicit way a lock file changes.</summary>
public static partial class LockFiles {
    /// <summary>The solution every lock file belongs to.</summary>
    public const string Solution = "Puck.slnx";
    /// <summary>The name NuGet gives a project's lock file, beside the project.</summary>
    public const string FileName = "packages.lock.json";

    // NuGet's advice in every locked-mode refusal names MSBuild switches; the report names the verb instead.
    private const string NuGetAdvice = "The packages lock file is inconsistent";
    private const string RecordAdvice = "run `puck locks` to re-record lock files after a reference change, or `puck locks --update` to take newer packages.";

    /// <summary>The locked restore <see cref="Check"/> runs: it refuses to change any lock file, so drift fails it.
    /// <c>--force</c> skips NuGet's no-op shortcut, which trusts a project whose own inputs have not changed since its
    /// last restore and so never reads a lock file edited (or merged) on its own; it costs about what a no-op restore
    /// of the solution does.</summary>
    public static IReadOnlyList<string> CheckArguments { get; } = ["restore", Solution, "--locked-mode", "--force", CliOptions.NoNodeReuse, "-nologo", "-v", "q"];

    // MSBuild's error format: "<file> : error <code>: <message> [<solution>]".
    [GeneratedRegex(pattern: @"^\s*(?<file>.+?)\s*:\s+error\s+(?<code>NU\d{4})\s*:\s*(?<message>.*?)(?:\s+\[[^\]]*\])?\s*$", options: RegexOptions.CultureInvariant)]
    private static partial Regex RestoreError();
    private static string LockPath(string project) => Path.Combine(path1: Path.GetDirectoryName(path: project)!, path2: FileName);
    private static string Reason(string message) {
        var advice = message.IndexOf(comparisonType: StringComparison.Ordinal, value: NuGetAdvice);

        return ((advice > 0) ? message[..advice] : message).Trim();
    }

    /// <summary>The restore <see cref="Record"/> runs: unlocked, so a lock file that no longer matches its project is
    /// re-resolved and rewritten; with <paramref name="update"/>, every floating version is re-evaluated as well.</summary>
    /// <param name="update">Whether to take the newest packages every floating range admits.</param>
    /// <returns>The <c>dotnet</c> arguments.</returns>
    public static IReadOnlyList<string> RecordArguments(bool update) => [
        "restore", Solution, "-p:RestoreLockedMode=false", .. (update ? (string[])["--force-evaluate"] : []), CliOptions.NoNodeReuse, "-nologo", "-v", "q",
    ];
    /// <summary>Every project <see cref="Solution"/> lists, as full paths.</summary>
    /// <param name="repositoryRoot">The checkout.</param>
    /// <returns>The projects, in the solution's order.</returns>
    public static IReadOnlyList<string> Projects(string repositoryRoot) => [.. XDocument.Load(uri: Path.Combine(path1: repositoryRoot, path2: Solution))
        .Descendants(name: "Project")
        .Select(selector: static project => project.Attribute(name: "Path")?.Value)
        .OfType<string>()
        .Select(selector: path => Path.GetFullPath(path: Path.Combine(path1: repositoryRoot, path2: path)))];
    /// <summary>Runs the locked restore and reports, by repository-relative path, every project whose lock file no
    /// longer matches it, every project the restore would have given a first lock file, and any other restore error.
    /// Writes no lock file: a locked restore refuses to change one, and a lock file it creates for a project that had
    /// none is removed again and reported.</summary>
    /// <param name="repositoryRoot">The checkout.</param>
    /// <param name="restore">Runs <c>dotnet</c> with the given arguments in the checkout.</param>
    /// <returns>The verdict.</returns>
    public static LockCheck Check(string repositoryRoot, Func<IReadOnlyList<string>, GateStepResult> restore) {
        var projects = Projects(repositoryRoot: repositoryRoot);
        var unlocked = projects.Where(predicate: static project => !File.Exists(path: LockPath(project: project))).ToArray();
        var result = restore(arg: CheckArguments);
        var report = new List<string>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var line in result.Output.Split(separator: '\n')) {
            var match = RestoreError().Match(input: line.TrimEnd(trimChar: '\r'));

            if (!match.Success) { continue; }
            var project = CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: Path.Combine(path1: repositoryRoot, path2: match.Groups["file"].Value)), relativeTo: repositoryRoot);
            var entry = $"{project}: {match.Groups["code"].Value} {Reason(message: match.Groups["message"].Value)}";

            if (seen.Add(item: entry)) { report.Add(item: entry); }
        }
        foreach (var project in unlocked) {
            var lockFile = LockPath(project: project);

            if (!File.Exists(path: lockFile)) { continue; }
            File.Delete(path: lockFile);
            report.Add(item: $"{CliPaths.ToDisplay(fullPath: project, relativeTo: repositoryRoot)}: has no {FileName}");
        }
        if ((result.ExitCode != 0) && (report.Count == 0)) {
            report.Add(item: $"{Solution}: the locked restore failed (exit {result.ExitCode}) without naming a project; see the log");
        }
        if (report.Count > 0) { report.Add(item: RecordAdvice); }

        return new LockCheck(ExitCode: ((report.Count == 0) ? CliExit.Success : CliExit.Failed), Report: report, RestoreOutput: result.Output);
    }
    /// <summary>Restores unlocked and reports, by repository-relative path, every lock file the restore rewrote or
    /// created: after a reference change, only the projects whose references changed; with <paramref name="update"/>,
    /// every project a newer package reaches.</summary>
    /// <param name="repositoryRoot">The checkout.</param>
    /// <param name="update">Whether to re-evaluate every floating version.</param>
    /// <param name="restore">Runs <c>dotnet</c> with the given arguments in the checkout.</param>
    /// <param name="changed">The lock files the restore rewrote or created, when it succeeded.</param>
    /// <returns>The restore's result.</returns>
    public static GateStepResult Record(string repositoryRoot, bool update, Func<IReadOnlyList<string>, GateStepResult> restore, out IReadOnlyList<string> changed) {
        var locks = Projects(repositoryRoot: repositoryRoot).Select(selector: LockPath).ToArray();
        var before = locks.ToDictionary(elementSelector: static path => (File.Exists(path: path) ? File.ReadAllBytes(path: path) : null), keySelector: static path => path, comparer: StringComparer.Ordinal);
        var result = restore(arg: RecordArguments(update: update));

        changed = [.. locks
            .Where(predicate: path => (File.Exists(path: path) && ((before[path] is not { } previous) || !previous.AsSpan().SequenceEqual(other: File.ReadAllBytes(path: path)))))
            .Select(selector: path => CliPaths.ToDisplay(fullPath: path, relativeTo: repositoryRoot))];
        return result;
    }
}
