using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Puck.Cli.Locks;

namespace Puck.Cli.Deps;

/// <summary>The NuGet half of <c>puck deps update</c>. A project declares each package as a floating range
/// (<c>[11.*, )</c>, <c>[1.*,2.0)</c>), an exact pin (<c>[5.12.0]</c>) or a plain version. Ranges move through
/// <see cref="LockFiles.Record"/> with <c>update</c>, so the lock policy holds: every lock file lists the current project
/// closure, a locked restore judges the result, and the newest package each range admits is recorded. A range whose upper
/// bound excludes the newest stable package, and an exact pin, are held and reported; <c>--major</c> moves a bounded
/// range's bound, and an exact pin moves only by an edit of its project. A plain version (a floor, not a pin) and the
/// <c>#:package</c> directives of file-based apps, which have no lock file, move to the newest stable version of their
/// major.</summary>
public static partial class NuGetDependencies {
    // [1.*,2.0) and [10.*, ): a floating prefix and an optional exclusive upper bound.
    [GeneratedRegex(pattern: @"^\[(?<prefix>\d+(?:\.\d+)*)\.\*,\s*(?<upper>\d+(?:\.\d+)*)?\)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex FloatingRange();
    // [5.12.0]: an exact pin.
    [GeneratedRegex(pattern: @"^\[(?<version>[^,\]\[()*]+)\]$", options: RegexOptions.CultureInvariant)]
    private static partial Regex ExactPin();
    [GeneratedRegex(pattern: @"^#:package\s+(?<id>[^@\s]+)@(?<version>\S+)\s*$", options: RegexOptions.CultureInvariant)]
    private static partial Regex PackageDirective();

    private const string Ecosystem = DependencyEcosystems.NuGet;

    // Every package id with the versions of it each lock file resolves, from the lock files' text.
    private static SortedDictionary<string, SortedSet<string>> Resolved(IEnumerable<string> lockTexts) {
        var resolved = new SortedDictionary<string, SortedSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var text in lockTexts) {
            if ((JsonNode.Parse(json: text) is not JsonObject root) || (root["dependencies"] is not JsonObject frameworks)) { continue; }
            foreach (var (_, frameworkNode) in frameworks) {
                if (frameworkNode is not JsonObject packages) { continue; }
                foreach (var (id, node) in packages) {
                    if ((node is not JsonObject entry) || ((string?)entry["type"] is null or "Project") || ((string?)entry["resolved"] is not { } version)) { continue; }
                    if (!resolved.TryGetValue(key: id, value: out var versions)) { resolved[id] = (versions = new SortedSet<string>(comparer: Comparer<string>.Create(comparison: static (left, right) => DependencyVersion.Parse(text: left).CompareTo(other: DependencyVersion.Parse(text: right))))); }
                    versions.Add(item: version);
                }
            }
        }

        return resolved;
    }
    private static string Spell(SortedSet<string>? versions) => ((versions is null) ? "-" : string.Join(separator: ", ", values: versions));
    private static string[] LockPaths(string repositoryRoot) => [.. LockFiles.Projects(repositoryRoot: repositoryRoot).Select(selector: static project => Path.Combine(path1: Path.GetDirectoryName(path: project)!, path2: LockFiles.FileName))];
    // The version a floor or file-app pin moves to: the newest stable one of its major, or of any major under --major, and
    // only when it is newer. A newer stable version of another major is a hold, reported beside the move.
    internal static async Task<DependencyVersion?> FloorTargetAsync(DependencyContext context, string ecosystem, string id, DependencyVersion current, string holdReason) {
        var target = await context.Feed.LatestStableAsync(
            admits: candidate => (context.Major || (candidate.Major == current.Major)),
            cancellationToken: context.CancellationToken,
            id: id
        ).ConfigureAwait(continueOnCapturedContext: false);
        var moved = (((target is not null) && (target.CompareTo(other: current) > 0)) ? target : null);

        await HoldNewerMajorAsync(context: context, current: (moved ?? current), ecosystem: ecosystem, id: id, reason: holdReason).ConfigureAwait(continueOnCapturedContext: false);
        return moved;
    }
    private static async Task HoldNewerMajorAsync(DependencyContext context, string ecosystem, string id, DependencyVersion current, string reason) {
        var latest = await context.Feed.LatestStableAsync(admits: null, cancellationToken: context.CancellationToken, id: id).ConfigureAwait(continueOnCapturedContext: false);

        if ((latest is not null) && (latest.CompareTo(other: current) > 0)) {
            context.Report.Add(hold: new DependencyHold(Available: latest.Text, Current: current.Text, Ecosystem: ecosystem, Name: id, Reason: reason));
        }
    }
    private static async Task UpdateProjectDeclarationsAsync(DependencyContext context, IReadOnlyList<string> projects) {
        var declarations = new List<(string Project, string Id, string Version)>();

        foreach (var project in projects) {
            var document = XDocument.Load(uri: project);

            foreach (var reference in document.Descendants(name: "PackageReference")) {
                if ((reference.Attribute(name: "Include")?.Value is { } id) && (reference.Attribute(name: "Version")?.Value is { } version)) { declarations.Add(item: (project, id, version)); }
            }
        }
        var decided = new Dictionary<(string Id, string Version), string?>();

        foreach (var (id, version) in declarations.Select(selector: static declaration => (declaration.Id, declaration.Version)).Distinct().OrderBy(keySelector: static pair => pair.Id, comparer: StringComparer.OrdinalIgnoreCase)) {
            decided[(id, version)] = await DecideAsync(context: context, id: id, spec: version).ConfigureAwait(continueOnCapturedContext: false);
        }
        foreach (var project in projects) {
            var text = File.ReadAllText(path: project);
            var edited = text;

            foreach (var (id, version) in declarations.Where(predicate: declaration => (declaration.Project == project)).Select(selector: static declaration => (declaration.Id, declaration.Version)).Distinct()) {
                if (decided[(id, version)] is not { } replacement) { continue; }
                var pattern = $"(?<head><PackageReference\\s+Include=\"{Regex.Escape(str: id)}\"\\s+Version=\")(?:{Regex.Escape(str: version)})(?<tail>\")";

                if (!Regex.IsMatch(input: edited, options: RegexOptions.CultureInvariant, pattern: pattern)) { throw new InvalidOperationException(message: $"{project}: PackageReference {id} {version} is not written as Include then Version; write it so and rerun."); }
                edited = Regex.Replace(evaluator: match => $"{match.Groups["head"].Value}{replacement}{match.Groups["tail"].Value}", input: edited, options: RegexOptions.CultureInvariant, pattern: pattern);
            }
            if (edited != text) { context.Files.Write(path: project, text: edited); }
        }
    }
    // The replacement for one declared version, or null to leave it; holds are reported as they are found.
    private static async Task<string?> DecideAsync(DependencyContext context, string id, string spec) {
        if (FloatingRange().Match(input: spec) is { Success: true } floating) {
            // An open range already floats to the newest package; only a bounded one can sit below it.
            if (!floating.Groups["upper"].Success || !DependencyVersion.TryParse(text: floating.Groups["upper"].Value, version: out var upper)) { return null; }
            var latest = await context.Feed.LatestStableAsync(admits: null, cancellationToken: context.CancellationToken, id: id).ConfigureAwait(continueOnCapturedContext: false);

            if ((latest is null) || (latest.CompareTo(other: upper) < 0)) { return null; }
            if (context.Major && !floating.Groups["prefix"].Value.Contains(value: '.')) { return string.Create(provider: CultureInfo.InvariantCulture, handler: $"[{latest.Major}.*,{latest.Major + 1}.0)"); }
            context.Report.Add(hold: new DependencyHold(Available: latest.Text, Current: spec, Ecosystem: Ecosystem, Name: id, Reason: $"the range excludes {upper.Text} and above"));
            return null;
        }
        if (ExactPin().Match(input: spec) is { Success: true } pin) {
            if (DependencyVersion.TryParse(text: pin.Groups["version"].Value, version: out var pinned)) {
                await HoldNewerMajorAsync(context: context, current: pinned, ecosystem: Ecosystem, id: id, reason: "an exact pin is a deliberate hold; its project moves it").ConfigureAwait(continueOnCapturedContext: false);
            }
            return null;
        }
        if (!DependencyVersion.TryParse(text: spec, version: out var floor) || spec.Contains(value: '*')) { return null; }

        return (await FloorTargetAsync(context: context, current: floor, ecosystem: Ecosystem, holdReason: "a plain version stays within its major", id: id).ConfigureAwait(continueOnCapturedContext: false))?.Text;
    }
    private static async Task UpdateFileAppDirectivesAsync(DependencyContext context) {
        if (!CliGit.TryListFiles(paths: out var paths, reason: out var reason, repository: context.RepositoryRoot)) { throw new InvalidOperationException(message: $"git could not list the repository's files: {reason}"); }

        foreach (var relative in paths.Where(predicate: static path => path.EndsWith(value: ".cs", comparisonType: StringComparison.Ordinal) && !path.StartsWith(value: "experimental/", comparisonType: StringComparison.Ordinal)).Order(comparer: StringComparer.Ordinal)) {
            var path = Path.Combine(path1: context.RepositoryRoot, path2: relative);

            if (!File.Exists(path: path)) { continue; }
            // A file-based app declares its packages in its leading directive lines.
            var first = File.ReadLines(path: path).SkipWhile(predicate: static line => line.StartsWith(value: "#!", comparisonType: StringComparison.Ordinal)).FirstOrDefault();

            if ((first is null) || !first.StartsWith(value: "#:", comparisonType: StringComparison.Ordinal)) { continue; }
            var text = File.ReadAllText(path: path);
            var edited = text;

            foreach (var line in text.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n')) {
                if ((PackageDirective().Match(input: line) is not { Success: true } match) || !DependencyVersion.TryParse(text: match.Groups["version"].Value, version: out var current)) { continue; }
                var id = match.Groups["id"].Value;

                if (await FloorTargetAsync(context: context, current: current, ecosystem: Ecosystem, holdReason: "a file-based app's pin stays within its major", id: id).ConfigureAwait(continueOnCapturedContext: false) is not { } target) { continue; }
                edited = edited.Replace(oldValue: $"#:package {id}@{current.Text}", newValue: $"#:package {id}@{target.Text}", comparisonType: StringComparison.Ordinal);
                context.Report.Add(move: new DependencyMove(Ecosystem: Ecosystem, From: current.Text, Name: $"{id} ({relative})", To: target.Text));
            }
            if (edited != text) { context.Files.Write(path: path, text: edited); }
        }
    }

    /// <summary>Moves every NuGet dependency the policy allows and reports what moved and what it held.</summary>
    /// <param name="context">The run.</param>
    /// <returns>A task that completes when the lock files and file-based apps are updated.</returns>
    /// <exception cref="InvalidOperationException">The restore failed; every lock file is as it was.</exception>
    public static async Task RunAsync(DependencyContext context) {
        var projects = LockFiles.Projects(repositoryRoot: context.RepositoryRoot);
        var locks = LockPaths(repositoryRoot: context.RepositoryRoot);

        foreach (var path in locks) { context.Files.Track(path: path); }
        await UpdateProjectDeclarationsAsync(context: context, projects: projects).ConfigureAwait(continueOnCapturedContext: false);
        await UpdateFileAppDirectivesAsync(context: context).ConfigureAwait(continueOnCapturedContext: false);
        var before = Resolved(lockTexts: locks.Where(predicate: File.Exists).Select(selector: static path => File.ReadAllText(path: path)));
        var result = LockFiles.Record(
            advanced: out _,
            changed: out _,
            repositoryRoot: context.RepositoryRoot,
            restore: arguments => LocksCommand.Restore(arguments: arguments, cancellationToken: context.CancellationToken, repositoryRoot: context.RepositoryRoot),
            update: true
        );

        if (result.ExitCode != 0) { throw new InvalidOperationException(message: $"the NuGet restore failed (exit {result.ExitCode}); every lock file is as it was.{Environment.NewLine}{result.Output.TrimEnd()}"); }
        var after = Resolved(lockTexts: locks.Where(predicate: File.Exists).Select(selector: static path => File.ReadAllText(path: path)));

        foreach (var id in before.Keys.Union(second: after.Keys, comparer: StringComparer.OrdinalIgnoreCase)) {
            before.TryGetValue(key: id, value: out var from);
            after.TryGetValue(key: id, value: out var to);
            if ((from is not null) && (to is not null) && from.SetEquals(other: to)) { continue; }
            context.Report.Add(move: new DependencyMove(Ecosystem: Ecosystem, From: Spell(versions: from), Name: id, To: Spell(versions: to)));
        }
    }
}
