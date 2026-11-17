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

    /// <summary>The restore <see cref="Record"/> runs: unlocked, and evaluating the whole graph afresh
    /// (<c>--force-evaluate</c>), so every lock file lists exactly the current project closure, including a project
    /// entry a removed reference left behind in a lock file NuGet would otherwise judge current. The fresh evaluation also
    /// resolves every floating version to the newest package its range admits; <see cref="Record"/> takes the versions a
    /// lock file already pinned back unless it is asked to update. Warnings stay warnings in this restore (a newest
    /// package can break another's constraint, NU1608), since the pins restore the versions that satisfy them and the
    /// locked restore that judges the result keeps warnings as errors.</summary>
    public static IReadOnlyList<string> RecordArguments { get; } = ["restore", Solution, "-p:RestoreLockedMode=false", "-p:TreatWarningsAsErrors=false", "--force-evaluate", CliOptions.NoNodeReuse, "-nologo", "-v", "q"];
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
    /// created: every lock file lists the current project closure, and without <paramref name="update"/> each keeps the
    /// package versions it pinned (<see cref="LockPins.Hold"/>), so a reference change moves only the entries it
    /// touches; with <paramref name="update"/>, every project a newer package reaches. The pinned lock files are
    /// checked by a locked restore (<see cref="CheckArguments"/>), the one judge of whether a lock file is consistent.
    /// When it refuses them (a new reference needs a newer package than a pin), the restore's own lock files stand and
    /// <paramref name="advanced"/> names each package that advanced.</summary>
    /// <param name="repositoryRoot">The checkout.</param>
    /// <param name="update">Whether to take the newest package every floating version admits.</param>
    /// <param name="restore">Runs <c>dotnet</c> with the given arguments in the checkout.</param>
    /// <param name="changed">The lock files the restore rewrote or created, when it succeeded.</param>
    /// <param name="advanced">The packages whose pinned version the references forced forward, empty unless the locked
    /// restore refused the pinned lock files.</param>
    /// <returns>The restore's result.</returns>
    public static GateStepResult Record(string repositoryRoot, bool update, Func<IReadOnlyList<string>, GateStepResult> restore, out IReadOnlyList<string> changed, out IReadOnlyList<string> advanced) {
        var locks = Projects(repositoryRoot: repositoryRoot).Select(selector: LockPath).ToArray();
        var before = locks.ToDictionary(elementSelector: static path => (File.Exists(path: path) ? File.ReadAllBytes(path: path) : null), keySelector: static path => path, comparer: StringComparer.Ordinal);
        var result = restore(arg: RecordArguments);

        advanced = [];
        if (result.ExitCode != 0) {
            // A failed restore can have rewritten some lock files and not others; none is known to be current.
            foreach (var (path, bytes) in before) {
                if (bytes is not null) { Puck.Confinement.AtomicFile.WriteAllBytes(bytes: bytes, path: path); }
                else if (File.Exists(path: path)) { File.Delete(path: path); }
            }
        } else {
            if (!update) { advanced = HoldPins(before: before, restore: restore); }
            // The restore spells line endings its own way; a lock file that differs from before only there is unchanged.
            foreach (var (path, bytes) in before) {
                if ((bytes is not null) && File.Exists(path: path) && !Puck.Confinement.FileContent.Equals(expected: bytes, path: path)
                    && string.Equals(a: File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n"), b: System.Text.Encoding.UTF8.GetString(bytes: bytes).ReplaceLineEndings(replacementText: "\n"), comparisonType: StringComparison.Ordinal)) {
                    Puck.Confinement.AtomicFile.WriteAllBytes(bytes: bytes, path: path);
                }
            }
        }
        changed = [.. locks
            .Where(predicate: path => (File.Exists(path: path) && ((before[path] is not { } previous) || !Puck.Confinement.FileContent.Equals(expected: previous, path: path))))
            .Select(selector: path => CliPaths.ToDisplay(fullPath: path, relativeTo: repositoryRoot))];
        return result;
    }

    // Writes each rewritten lock file back at the versions its previous text pinned, and a package it did not list at the
    // lowest version any lock file pins (a project's compile-time reference never outruns the assembly a dependent
    // deploys), then has the locked restore judge the set. A refusal puts every lock file back as the restore wrote it and
    // holds only the versions each file's own previous text pinned; a second refusal puts them back again and returns the
    // packages that advanced.
    private static List<string> HoldPins(Dictionary<string, byte[]?> before, Func<IReadOnlyList<string>, GateStepResult> restore) {
        var solution = LockPins.Collect(texts: before.Values.OfType<byte[]>().Select(selector: static bytes => System.Text.Encoding.UTF8.GetString(bytes: bytes)));
        var advanced = new List<string>();

        foreach (var pins in ((IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject>?[])[solution, null])) {
            var restored = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

            advanced = [];
            foreach (var (path, bytes) in before) {
                if ((bytes is null) || !File.Exists(path: path)) { continue; }

                var current = File.ReadAllText(path: path);
                var previous = System.Text.Encoding.UTF8.GetString(bytes: bytes);

                if (LockPins.Hold(current: current, packages: advanced, previous: previous, solution: pins) is not { } held) { continue; }
                restored[path] = current;
                if (held == previous) { Puck.Confinement.AtomicFile.WriteAllBytes(bytes: bytes, path: path); }
                else { Puck.Confinement.AtomicFile.WriteAllText(contents: held, path: path); }
            }
            if ((restored.Count == 0) || (restore(arg: CheckArguments).ExitCode == 0)) { return []; }
            foreach (var (path, text) in restored) { Puck.Confinement.AtomicFile.WriteAllText(contents: text, path: path); }
        }

        return advanced;
    }
}
