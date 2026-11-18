using System.Text.RegularExpressions;

namespace Puck.Cli.Deps;

/// <summary>The GitHub Actions half of <c>puck deps update</c>. Every external action is pinned to a full commit SHA
/// beside a version comment (<c>uses: owner/repo@&lt;sha&gt; # v1.2.3</c>), as the repository's Actions policy requires.
/// The newest stable <c>vX.Y.Z</c> tag of the action's repository (its own tags, listed with <c>git ls-remote</c>: no API
/// token and no rate limit) replaces the pin and the comment together; a tag is stable when it has exactly three numbers
/// and no suffix, so a floating major tag such as <c>v7</c> is never a pin. Without <c>--major</c> an action moves within
/// the major its comment names.</summary>
public static partial class ActionDependencies {
    [GeneratedRegex(pattern: @"(?<head>uses:\s*(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?<path>/[^@\s]*)?@)(?<sha>[0-9a-f]{40})(?<gap>\s*#\s*)(?<tag>v?\d+(?:\.\d+)*)", options: RegexOptions.CultureInvariant)]
    private static partial Regex Pin();
    [GeneratedRegex(pattern: @"^v?\d+\.\d+\.\d+$", options: RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();

    private const string Ecosystem = DependencyEcosystems.Actions;
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(minutes: 2);

    // Every tag of a repository with the commit it names, an annotated tag resolved to the commit it points at.
    private static async Task<Dictionary<string, string>> TagsAsync(string repository, CancellationToken cancellationToken) {
        var run = await CliProcess.RunAsync(arguments: ["ls-remote", "--tags", $"https://github.com/{repository}"], cancellationToken: cancellationToken, fileName: "git", timeout: ListTimeout).ConfigureAwait(continueOnCapturedContext: false);

        if (run.ExitCode != 0) { throw new InvalidOperationException(message: $"git ls-remote for {repository} exited {run.ExitCode}: {run.Stderr.Trim()}"); }
        var tags = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var line in CliText.Lines(text: run.Stdout)) {
            var parts = line.Split(separator: '\t');

            if ((parts.Length != 2) || !parts[1].StartsWith(value: "refs/tags/", comparisonType: StringComparison.Ordinal)) { continue; }
            var name = parts[1]["refs/tags/".Length..];
            var peeled = name.EndsWith(value: "^{}", comparisonType: StringComparison.Ordinal);

            // The peeled line follows its annotated tag's own and names the commit a pin must hold.
            if (peeled) { tags[name[..^3]] = parts[0]; }
            else { tags.TryAdd(key: name, value: parts[0]); }
        }

        return tags;
    }
    private static IEnumerable<string> Files(string repositoryRoot) {
        var workflows = Path.Combine(path1: repositoryRoot, path2: ".github", path3: "workflows");
        var actions = Path.Combine(path1: repositoryRoot, path2: ".github", path3: "actions");

        if (Directory.Exists(path: workflows)) {
            foreach (var file in Directory.EnumerateFiles(path: workflows, searchPattern: "*.yml").Order(comparer: StringComparer.Ordinal)) { yield return file; }
        }
        if (Directory.Exists(path: actions)) {
            foreach (var file in Directory.EnumerateFiles(path: actions, searchOption: SearchOption.AllDirectories, searchPattern: "action.yml").Order(comparer: StringComparer.Ordinal)) { yield return file; }
        }
    }

    /// <summary>Moves every pinned action as far as the policy allows and reports what moved and what it held.</summary>
    /// <param name="context">The run.</param>
    /// <returns>A task that completes when every workflow and composite action is rewritten.</returns>
    /// <exception cref="InvalidOperationException"><c>git ls-remote</c> refused a repository.</exception>
    public static async Task RunAsync(DependencyContext context) {
        var files = Files(repositoryRoot: context.RepositoryRoot).ToArray();
        var repositories = files.SelectMany(selector: file => Pin().Matches(input: File.ReadAllText(path: file)).Select(selector: static match => match.Groups["repo"].Value)).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();
        var tags = new Dictionary<string, Dictionary<string, string>>(comparer: StringComparer.Ordinal);

        foreach (var repository in repositories) { tags[repository] = await TagsAsync(cancellationToken: context.CancellationToken, repository: repository).ConfigureAwait(continueOnCapturedContext: false); }
        var reported = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var file in files) {
            var text = File.ReadAllText(path: file);
            var edited = Pin().Replace(
                evaluator: match => {
                    var repository = match.Groups["repo"].Value;
                    var path = match.Groups["path"].Value;
                    var name = (repository + path);

                    if (!DependencyVersion.TryParse(text: match.Groups["tag"].Value, version: out var current)) { return match.Value; }
                    var released = tags[repository].Where(predicate: static tag => ReleaseTag().IsMatch(input: tag.Key)).ToArray();
                    var latest = DependencyVersion.HighestStable(spellings: released.Select(selector: static tag => tag.Key));
                    var target = DependencyVersion.HighestStable(admits: candidate => (context.Major || (candidate.Major == current.Major)), spellings: released.Select(selector: static tag => tag.Key));

                    if ((target is null) || (target.CompareTo(other: current) <= 0)) {
                        if ((latest is not null) && (latest.CompareTo(other: current) > 0) && reported.Add(item: $"hold {name}")) {
                            context.Report.Add(hold: new DependencyHold(Available: latest.Text, Current: match.Groups["tag"].Value, Ecosystem: Ecosystem, Name: name, Reason: $"the pin stays within major {current.Major}"));
                        }
                        return match.Value;
                    }
                    var tag = released.First(predicate: pair => (DependencyVersion.Parse(text: pair.Key).CompareTo(other: target) == 0));

                    if (reported.Add(item: $"move {name} {target.Text}")) { context.Report.Add(move: new DependencyMove(Ecosystem: Ecosystem, From: match.Groups["tag"].Value, Name: name, To: tag.Key)); }
                    return $"{match.Groups["head"].Value}{tag.Value}{match.Groups["gap"].Value}{tag.Key}";
                },
                input: text
            );

            if (edited != text) { context.Files.Write(path: file, text: edited); }
        }
    }
}
