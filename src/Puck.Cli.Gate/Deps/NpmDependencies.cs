using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Puck.Cli.Deps;

/// <summary>The npm half of <c>puck deps update</c>. Every <c>package-lock.json</c> in the tree roots one project: its
/// <c>package.json</c> and the manifests of its workspaces. A dependency declared as <c>^1.2.3</c>, <c>~1.2.3</c> or exactly
/// <c>1.2.3</c> moves to the newest stable version its range admits (an exact pin to the newest of its major), or under
/// <c>--major</c> to the newest stable version there is, keeping the spelling it had. A spec that is not a plain version
/// (a tarball URL, a range with operators) is left alone. Overrides are read the same way. The lock file then moves
/// through <c>npm update --package-lock-only</c>, which takes every transitive package to the newest its parents admit.
/// <c>node_modules</c> is not touched: <c>npm ci</c> installs the result.</summary>
public static partial class NpmDependencies {
    [GeneratedRegex(pattern: @"^(?<operator>[\^~]?)(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex PlainSpec();

    private const string Ecosystem = DependencyEcosystems.Npm;
    private static readonly string[] Sections = ["dependencies", "devDependencies", "optionalDependencies"];
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(minutes: 5);

    // Every name with its spec in a manifest's dependency sections and overrides, however deeply an override nests.
    private static IEnumerable<(string Name, string Spec)> Declarations(JsonObject manifest) {
        foreach (var section in Sections) {
            if (manifest[section] is not JsonObject dependencies) { continue; }
            foreach (var (name, node) in dependencies) {
                if ((string?)node is { } spec) { yield return (name, spec); }
            }
        }
        if (manifest["overrides"] is JsonObject overrides) {
            foreach (var declaration in Overrides(overrides: overrides)) { yield return declaration; }
        }
    }
    private static IEnumerable<(string Name, string Spec)> Overrides(JsonObject overrides) {
        foreach (var (name, node) in overrides) {
            if (node is JsonObject nested) {
                foreach (var declaration in Overrides(overrides: nested)) { yield return declaration; }
            } else if (((string?)node is { } spec) && (name != ".")) { yield return (name, spec); }
        }
    }
    // The manifests of one lock root: its own and every workspace's, workspaces named by directory or one-level glob.
    private static IEnumerable<string> Manifests(string directory) {
        var manifest = Path.Combine(path1: directory, path2: "package.json");

        yield return manifest;
        if (JsonNode.Parse(json: File.ReadAllText(path: manifest)) is not JsonObject { } root) { yield break; }
        if (root["workspaces"] is not JsonArray workspaces) { yield break; }
        foreach (var pattern in workspaces.Select(selector: static node => (string?)node).OfType<string>()) {
            if (pattern.EndsWith(value: "/*", comparisonType: StringComparison.Ordinal)) {
                var parent = Path.Combine(path1: directory, path2: pattern[..^2]);

                if (!Directory.Exists(path: parent)) { continue; }
                foreach (var child in Directory.EnumerateDirectories(path: parent).Order(comparer: StringComparer.Ordinal)) {
                    if (File.Exists(path: Path.Combine(path1: child, path2: "package.json"))) { yield return Path.Combine(path1: child, path2: "package.json"); }
                }
            } else if (File.Exists(path: Path.Combine(path1: directory, path2: pattern, path3: "package.json"))) { yield return Path.Combine(path1: directory, path2: pattern, path3: "package.json"); }
        }
    }
    // Every package name with the versions the lock file installs of it.
    private static SortedDictionary<string, SortedSet<string>> Installed(string lockPath) {
        var installed = new SortedDictionary<string, SortedSet<string>>(comparer: StringComparer.Ordinal);

        if ((JsonNode.Parse(json: File.ReadAllText(path: lockPath)) is not JsonObject root) || (root["packages"] is not JsonObject packages)) { return installed; }
        foreach (var (key, node) in packages) {
            var marker = key.LastIndexOf(value: "node_modules/", comparisonType: StringComparison.Ordinal);

            if ((marker < 0) || (node is not JsonObject entry) || ((bool?)entry["link"] == true) || ((string?)entry["version"] is not { } version)) { continue; }
            var name = key[(marker + "node_modules/".Length)..];

            if (!installed.TryGetValue(key: name, value: out var versions)) { installed[name] = (versions = DependencyVersion.NewSet()); }
            versions.Add(item: version);
        }

        return installed;
    }
    private static async Task<(string[] Versions, string? Latest)> ViewAsync(string name, CancellationToken cancellationToken) {
        var run = await CliProcess.RunAsync(arguments: ["view", name, "versions", "dist-tags", "--json"], cancellationToken: cancellationToken, fileName: "npm", timeout: ToolTimeout).ConfigureAwait(continueOnCapturedContext: false);

        if (run.ExitCode != 0) { throw new InvalidOperationException(message: $"npm view {name} exited {run.ExitCode}: {run.Stderr.Trim()}"); }
        using var document = JsonDocument.Parse(json: run.Stdout);
        var root = document.RootElement;
        var versions = root.GetProperty(propertyName: "versions");

        string[] listed = ((versions.ValueKind == JsonValueKind.String) ? [versions.GetString()!] : [.. versions.EnumerateArray().Select(selector: static version => version.GetString()!)]);

        return (
            Versions: listed,
            Latest: (root.GetProperty(propertyName: "dist-tags").TryGetProperty(propertyName: "latest", value: out var latest) ? latest.GetString() : null)
        );
    }
    private static bool Admits(string @operator, DependencyVersion current, DependencyVersion candidate) => (@operator switch {
        "^" => ((candidate.Major == current.Major) && ((current.Major != 0) || (candidate.Minor == current.Minor))),
        "~" => ((candidate.Major == current.Major) && (candidate.Minor == current.Minor)),
        // An exact pin keeps its spelling and moves within its major.
        _ => (candidate.Major == current.Major),
    });
    private static async Task RunRootAsync(DependencyContext context, string directory) {
        var lockPath = Path.Combine(path1: directory, path2: "package-lock.json");
        var manifests = Manifests(directory: directory).ToArray();
        var specs = new SortedSet<(string Name, string Spec)>();

        foreach (var manifest in manifests) {
            if (JsonNode.Parse(json: File.ReadAllText(path: manifest)) is JsonObject root) {
                foreach (var declaration in Declarations(manifest: root)) { specs.Add(item: declaration); }
            }
        }
        var views = new Dictionary<string, Task<(string[] Versions, string? Latest)>>(comparer: StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(initialCount: 6);

        async Task<(string[] Versions, string? Latest)> Throttled(string name) {
            await gate.WaitAsync(cancellationToken: context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            try { return await ViewAsync(cancellationToken: context.CancellationToken, name: name).ConfigureAwait(continueOnCapturedContext: false); }
            finally { gate.Release(); }
        }
        foreach (var (name, spec) in specs) {
            if (PlainSpec().IsMatch(input: spec) && !views.ContainsKey(key: name)) { views[name] = Throttled(name: name); }
        }
        var replacements = new List<(string Name, string From, string To)>();

        foreach (var (name, spec) in specs) {
            if (PlainSpec().Match(input: spec) is not { Success: true } match) { continue; }
            var @operator = match.Groups["operator"].Value;
            var current = DependencyVersion.Parse(text: match.Groups["version"].Value);
            var (versions, latestTag) = await views[name].ConfigureAwait(continueOnCapturedContext: false);
            var latest = (DependencyVersion.TryParse(text: latestTag, version: out var tagged) && tagged.IsStable) ? tagged : DependencyVersion.HighestStable(spellings: versions);
            // As npm picks a version: the latest tag when the range admits it (a package can publish versions above its latest
            // tag that are not meant to be taken), otherwise the highest version the range admits.
            var target = context.Major
                ? latest
                : (((latest is not null) && Admits(@operator: @operator, candidate: latest, current: current))
                    ? latest
                    : DependencyVersion.HighestStable(admits: candidate => Admits(@operator: @operator, candidate: candidate, current: current), spellings: versions));

            if ((target is not null) && (target.CompareTo(other: current) > 0)) {
                replacements.Add(item: (name, spec, (@operator + target.Text)));
            } else if ((latest is not null) && (latest.CompareTo(other: current) > 0)) {
                context.Report.Add(hold: new DependencyHold(Available: latest.Text, Current: spec, Ecosystem: Ecosystem, Name: name, Reason: $"{spec} admits no newer version"));
            }
        }
        context.Files.Track(path: lockPath);
        foreach (var manifest in manifests) {
            var text = File.ReadAllText(path: manifest);
            var edited = text;

            foreach (var (name, from, to) in replacements) { edited = DependencyFiles.ReplaceJsonPair(name: name, newSpec: to, oldSpec: from, text: edited); }
            if (edited != text) { context.Files.Write(path: manifest, text: edited); }
        }
        var before = Installed(lockPath: lockPath);
        var rootManifest = JsonNode.Parse(json: File.ReadAllText(path: manifests[0]));
        string[] arguments = ((rootManifest?["workspaces"] is not null)
            ? ["update", "--package-lock-only", "--workspaces", "--include-workspace-root", "--no-audit", "--no-fund"]
            : ["update", "--package-lock-only", "--no-audit", "--no-fund"]);
        var run = await CliProcess.RunAsync(arguments: arguments, cancellationToken: context.CancellationToken, fileName: "npm", timeout: ToolTimeout, workingDirectory: directory).ConfigureAwait(continueOnCapturedContext: false);

        if (run.ExitCode != 0) { throw new InvalidOperationException(message: $"npm update in {directory} exited {run.ExitCode}: {run.Stderr.Trim()}"); }
        var after = Installed(lockPath: lockPath);
        var project = Puck.Abstractions.PuckPaths.Relative(path: directory, relativeTo: context.RepositoryRoot);

        context.Report.AddDifferences(after: after, before: before, ecosystem: Ecosystem, scope: project);
    }

    /// <summary>Moves every npm project's dependencies as far as the policy allows and reports what moved and what it held.</summary>
    /// <param name="context">The run.</param>
    /// <returns>A task that completes when every manifest and lock file is updated.</returns>
    /// <exception cref="InvalidOperationException">The package registry or npm refused a request.</exception>
    public static async Task RunAsync(DependencyContext context) {
        if (!CliGit.TryListFiles(paths: out var paths, reason: out var reason, repository: context.RepositoryRoot)) { throw new InvalidOperationException(message: $"git could not list the repository's files: {reason}"); }

        foreach (var relative in paths.Where(predicate: static path => path.EndsWith(value: "package-lock.json", comparisonType: StringComparison.Ordinal) && !path.Contains(value: "node_modules/", comparisonType: StringComparison.Ordinal) && !path.StartsWith(value: "experimental/", comparisonType: StringComparison.Ordinal)).Order(comparer: StringComparer.Ordinal)) {
            await RunRootAsync(context: context, directory: Path.GetDirectoryName(path: Path.GetFullPath(path: Path.Combine(path1: context.RepositoryRoot, path2: relative)))!).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
