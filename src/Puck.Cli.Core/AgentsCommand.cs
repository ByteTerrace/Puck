using System.CommandLine;
using Puck.Abstractions;

namespace Puck.Cli;

/// <summary>Connects native agent discovery to the checkout's shared skills, and checks which checkout a session's
/// agent configuration comes from.</summary>
public static class AgentsCommand {
    /// <summary>The paths an agent session's configuration is read from: agent definitions, skills, hooks, harness
    /// settings and the rules file.</summary>
    public static IReadOnlyList<string> ConfigurationPaths { get; } = [".claude/agents", ".claude/skills", ".claude/hooks", ".claude/settings.json", "AGENTS.md"];

    /// <summary>One checkout the provenance check reads.</summary>
    /// <param name="Roles">What the checkout is to the session: the checkout it works in, the project root its harness
    /// loads configuration from, the main checkout its git common directory names, or several of these.</param>
    /// <param name="Path">The checkout's top level, forward-slashed.</param>
    /// <param name="Branch">The checked-out branch, or <c>(detached abc1234)</c>.</param>
    /// <param name="Keys">Each of <see cref="ConfigurationPaths"/> to the key of what the checkout holds there.</param>
    public sealed record ProvenanceRoot(string Roles, string Path, string Branch, IReadOnlyDictionary<string, string> Keys);

    /// <summary>Creates the agent setup commands.</summary>
    /// <returns>The agents command.</returns>
    public static Command Create() {
        var check = CliOptions.Check(description: "Inspect the skills link without creating it.");
        var link = new Command(description: "Link .agents/skills to this checkout's .claude/skills; --check only inspects it.", name: "link") { check };
        var checkout = new Option<string?>(name: "--checkout") { Description = "The checkout the session works in; defaults to the current directory's." };
        var projectRoot = new Option<string?>(name: "--project-root") { Description = "The project root the harness loads agents, skills and hooks from (Claude Code's CLAUDE_PROJECT_DIR, which its hooks pass); without it only the working and main checkouts are compared." };
        var provenance = new Command(description: "Compare the agent configuration of the working checkout, the harness's project root and the main checkout; exit 1 when they differ.", name: "provenance") { checkout, projectRoot };

        provenance.Detail(detail: """
            Use: puck agents provenance [--checkout <dir>] [--project-root <dir>]
            Reads .claude/agents, .claude/skills, .claude/hooks, .claude/settings.json and AGENTS.md
            in three checkouts: the one the session works in (expected), the project root Claude
            Code loads agents, skills and hooks from (--project-root), and the main checkout
            (the parent of git's common directory, which puck-mcp and ancestor walks reach). Each
            path's key is computed now from git's committed entry and the bytes of every changed or
            untracked file under it; nothing is recorded. Prints each checkout with its branch and
            exits 0 when all agree, 1 naming the paths, checkouts and branches that differ, 2 when a
            checkout cannot be read. The SessionStart hook runs it on startup, resume and compact,
            and the lead runs it before dispatching lanes.
            """);
        link.SetAction(action: async (parse, cancellationToken) => await LinkAsync(root: RepositoryPaths.RequireRoot(), check: parse.GetValue(option: check), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
        provenance.SetAction(action: async (parse, cancellationToken) => await ProvenanceAsync(
            cancellationToken: cancellationToken,
            checkout: (parse.GetValue(option: checkout) ?? Environment.CurrentDirectory),
            projectRoot: parse.GetValue(option: projectRoot)
        ).ConfigureAwait(continueOnCapturedContext: false));
        return new Command(description: "Set up the checkout's agent tools.", name: "agents") { link, provenance };
    }

    private static async Task<string?> GitLineAsync(string directory, CancellationToken cancellationToken, params string[] arguments) {
        var run = await CliGit.RunAsync(arguments: arguments, cancellationToken: cancellationToken, repository: directory, timeout: TimeSpan.FromSeconds(seconds: 30)).ConfigureAwait(continueOnCapturedContext: false);

        return ((run.ExitCode == 0) ? run.Stdout.Trim() : null);
    }
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: path)).Replace(newChar: '/', oldChar: '\\');
    /// <summary>Reads one checkout's branch and configuration keys.</summary>
    /// <param name="roles">What the checkout is to the session.</param>
    /// <param name="path">The checkout's top level.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The checkout, or <see langword="null"/> with the reason when git cannot read it.</returns>
    private static async Task<(ProvenanceRoot? Root, string Reason)> ReadRootAsync(string roles, string path, CancellationToken cancellationToken) {
        var branch = await GitLineAsync(path, cancellationToken, "symbolic-ref", "--short", "-q", "HEAD").ConfigureAwait(continueOnCapturedContext: false);

        if (string.IsNullOrEmpty(value: branch)) {
            var head = await GitLineAsync(path, cancellationToken, "rev-parse", "--short", "HEAD").ConfigureAwait(continueOnCapturedContext: false);

            branch = $"(detached {(head ?? "?")})";
        }
        if (!CheckoutContent.TryRead(reason: out var reason, repositoryRoot: path, roots: [.. ConfigurationPaths], snapshot: out var snapshot)) {
            return (null, reason);
        }
        return (new ProvenanceRoot(Branch: branch, Keys: ConfigurationPaths.ToDictionary(keySelector: static item => item, elementSelector: snapshot.KeyOf, comparer: StringComparer.Ordinal), Path: path, Roles: roles), string.Empty);
    }

    /// <summary>Reads the configuration of the working checkout, the project root and the main checkout, and names
    /// every path where the project root or the main checkout differs from the working checkout.</summary>
    /// <param name="checkout">A directory inside the checkout the session works in.</param>
    /// <param name="projectRoot">The project root the harness loads configuration from, or <see langword="null"/> when
    /// none is known.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns><see cref="CliExit.Success"/> when every checkout agrees, <see cref="CliExit.Failed"/> on a difference,
    /// or <see cref="CliExit.Refused"/> when a checkout cannot be read.</returns>
    public static async Task<int> ProvenanceAsync(string checkout, string? projectRoot, CancellationToken cancellationToken = default) {
        const string Verb = "agents provenance";
        var top = await GitLineAsync(checkout, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(continueOnCapturedContext: false);

        if (string.IsNullOrEmpty(value: top)) { return CliExit.Refuse(verb: Verb, what: Normalize(path: checkout), why: "is not inside a git checkout."); }
        var common = await CliGit.CommonDirectoryAsync(cancellationToken: cancellationToken, repository: top).ConfigureAwait(continueOnCapturedContext: false);

        if (string.IsNullOrEmpty(value: common)) { return CliExit.Refuse(verb: Verb, what: Normalize(path: top), why: "git names no common directory."); }
        var candidates = new List<(string Role, string Path)> { ("working checkout", Normalize(path: top)) };

        if (!string.IsNullOrWhiteSpace(value: projectRoot)) {
            var projectTop = await GitLineAsync(projectRoot, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(continueOnCapturedContext: false);

            if (string.IsNullOrEmpty(value: projectTop)) { return CliExit.Refuse(verb: Verb, what: Normalize(path: projectRoot), why: "the project root is not inside a git checkout."); }
            candidates.Add(item: ("project root", Normalize(path: projectTop)));
        }
        candidates.Add(item: ("main checkout", Normalize(path: Path.GetDirectoryName(path: Normalize(path: common))!)));
        var distinct = candidates.GroupBy(keySelector: static candidate => candidate.Path, comparer: PuckPaths.Comparer).Select(selector: static group => (Roles: string.Join(separator: " and ", values: group.Select(selector: static candidate => candidate.Role)), Path: group.Key)).ToList();
        var roots = new List<ProvenanceRoot>();

        foreach (var (roles, path) in distinct) {
            var (root, reason) = await ReadRootAsync(cancellationToken: cancellationToken, path: path, roles: roles).ConfigureAwait(continueOnCapturedContext: false);

            if (root is null) { return CliExit.Refuse(verb: Verb, what: path, why: reason); }
            roots.Add(item: root);
        }
        var expected = roots[0];
        var mismatches = new List<string>();

        Console.WriteLine(value: $"{Verb}: expected {expected.Roles} {expected.Path} ({expected.Branch})");
        foreach (var actual in roots.Skip(count: 1)) {
            var differing = ConfigurationPaths.Where(predicate: item => (expected.Keys[item] != actual.Keys[item])).ToArray();

            if (differing.Length == 0) {
                Console.WriteLine(value: $"{Verb}: {actual.Roles} {actual.Path} ({actual.Branch}) holds the same configuration");
                continue;
            }
            mismatches.Add(item: $"the {actual.Roles} {actual.Path} ({actual.Branch}) differs from the {expected.Roles} {expected.Path} ({expected.Branch}) in {string.Join(separator: ", ", values: differing)}");
        }
        if (mismatches.Count == 0) {
            if (roots.Count == 1) { Console.WriteLine(value: $"{Verb}: one checkout; nothing to compare"); }
            return CliExit.Success;
        }
        foreach (var mismatch in mismatches) { Console.WriteLine(value: $"{Verb}: MISMATCH: {mismatch}"); }
        Console.WriteLine(value: $"{Verb}: Claude Code loads agents, skills and hooks from the project root, and puck-mcp and ancestor walks reach the main checkout, so lanes run that configuration, not the working checkout's. Fast-forward the main checkout to the integration branch, or start the session from the integration checkout, and dispatch no lane until this check passes.");
        return CliExit.Failed;
    }
    /// <summary>Creates or checks the one skill-directory link without replacing existing content.</summary>
    /// <param name="root">The checkout root.</param>
    /// <param name="check">Whether to inspect without writing.</param>
    /// <param name="cancellationToken">Cancels link creation.</param>
    /// <returns>Zero for a correct link, one for check drift, or two for a creation refusal.</returns>
    public static async Task<int> LinkAsync(string root, bool check, CancellationToken cancellationToken = default) {
        var target = Path.GetFullPath(path: Path.Combine(path1: root, path2: ".claude/skills"));
        var path = Path.GetFullPath(path: Path.Combine(path1: root, path2: ".agents/skills"));
        var link = new DirectoryInfo(path: path);

        int Refuse(string what, string why) {
            CliExit.Refuse(verb: "agents link", what: what, why: why);
            return (check ? 1 : 2);
        }

        if (!Directory.Exists(path: target)) { return Refuse(what: CliPaths.ToDisplay(fullPath: target), why: "the checkout has no .claude/skills directory."); }
        if (link.LinkTarget is { } existing) {
            var resolved = Path.GetFullPath(path: existing, basePath: Path.GetDirectoryName(path: path)!);

            if (!PuckPaths.Comparer.Equals(x: Path.TrimEndingDirectorySeparator(path: resolved), y: Path.TrimEndingDirectorySeparator(path: target))) {
                return Refuse(what: CliPaths.ToDisplay(fullPath: path), why: "the existing link points elsewhere; it was not replaced.");
            }
        } else if (Path.Exists(path: path)) {
            return Refuse(what: CliPaths.ToDisplay(fullPath: path), why: "an ordinary file or directory already exists; it was not replaced.");
        } else if (check) {
            return Refuse(what: CliPaths.ToDisplay(fullPath: path), why: "the skills link is missing; run puck agents link.");
        } else {
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            if (OperatingSystem.IsWindows()) {
                // Literal PowerShell strings retain spaces, quotes and shell metacharacters in checkout names.
                var script = $"New-Item -ItemType Junction -Path '{path.Replace(newValue: "''", oldValue: "'")}' -Target '{target.Replace(newValue: "''", oldValue: "'")}' -ErrorAction Stop | Out-Null";

                await CliProcess.RunCheckedAsync(fileName: "pwsh", arguments: ["-NoProfile", "-NonInteractive", "-Command", script], cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } else {
                Directory.CreateSymbolicLink(path: path, pathToTarget: target);
            }
            return await LinkAsync(cancellationToken: cancellationToken, check: true, root: root).ConfigureAwait(continueOnCapturedContext: false);
        }
        Console.WriteLine(value: "agents link: .agents/skills -> .claude/skills (current checkout).");
        return 0;
    }
}
