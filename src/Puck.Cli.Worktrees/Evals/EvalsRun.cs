using System.CommandLine;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Confinement;

namespace Puck.Cli.Evals;

/// <summary>
/// <c>puck evals run</c>: runs one skill's eval suite through <c>claude plugin eval</c> against a frozen detached
/// worktree at a revision, never the live tree, with the rules-file fixture in every case, full model IDs, a judge at
/// least as strong as the model under test, and every result, report, manifest and trace under the skill's git-ignored
/// <c>evals/results/</c>.
/// </summary>
/// <remarks>The verb starts an agent session as its own child, where no command hook can see it, so the lane rule that
/// a lane starts no agent session is enforced where the verb is invoked: the session-launch hook policy refuses
/// <c>puck evals run</c> from a lane, except its <c>--dry-run</c> and help forms, as it refuses <c>claude</c>.</remarks>
public static partial class EvalsRun {
    private const string Verb = "evals run";
    /// <summary>The directory, beside a result document, that holds each run's copied transcript as
    /// <c>&lt;case&gt;/&lt;arm&gt;-&lt;run&gt;.jsonl</c>.</summary>
    public const string TracesDirectory = "traces";
    private const string DefaultModel = "claude-sonnet-5-5";
    // The lead's own skill; every other skill is loaded by a lane, which preloads delegate.
    private static readonly string[] SkillsWithoutDelegate = ["delegate", "orchestration"];
    // Weakest first: a judge's tier must be at least the tier of the model under test.
    private static readonly string[] Tiers = ["haiku", "sonnet", "opus", "fable"];

    [GeneratedRegex(pattern: @"^claude-(?<tier>[a-z]+)-[0-9][0-9a-z-]*$")]
    private static partial Regex FullModelId();
    [GeneratedRegex(pattern: @"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SkillName();

    private sealed record Settings(string Skill, IReadOnlyList<string> Cases, IReadOnlyList<string> Tags, string Revision, string Model, string Judge, int? Runs, string Ablation, double MaxCostUsd, int Jobs, bool Fixture, bool Preload, bool DryRun, bool KeepTree, bool KeepSandboxes);

    /// <summary>Creates the <c>evals run</c> command.</summary>
    /// <param name="clock">The CLI host's clock, which stamps the results directory.</param>
    /// <returns>The command.</returns>
    public static Command Create(TimeProvider clock) {
        var skillArgument = new Argument<string>(name: "skill") { Description = "The skill under .claude/skills/ whose evals/ suite runs." };
        var casesOption = new Option<string[]>(name: "--cases") { AllowMultipleArgumentsPerToken = true, Description = "Run only the cases whose directory name matches any of these globs (* and ?), such as o09-* o17-*." };
        var tagOption = new Option<string[]>(name: "--tag") { AllowMultipleArgumentsPerToken = true, Description = "Run only cases carrying any of these tags, such as regression." };
        var revisionOption = new Option<string>(name: "--revision") { DefaultValueFactory = static _ => "HEAD", Description = "The revision whose skill runs; HEAD's evals/ replace that revision's, so both sides of an edit run identical cases." };
        var modelOption = new Option<string>(name: "--model") { DefaultValueFactory = static _ => DefaultModel, Description = "The full model ID under test, such as claude-sonnet-5-5; an alias is refused." };
        var judgeOption = new Option<string?>(name: "--judge") { Description = "The full judge model ID; defaults to the model under test, and a weaker tier is refused." };
        var runsOption = CliOptions.OptionalCount(description: "Runs per case per arm, 1 to 50; defaults to each case's own runs (a capability case sets 5, the runner's default is 3).", maximum: 50, minimum: 1, name: "--runs");
        var ablationOption = new Option<string>(name: "--ablation") { DefaultValueFactory = static _ => "with-without", Description = "with-without adds the no-skill arm; none runs the with-arm only." };
        var costOption = CliOptions.Amount(defaultValue: 20, description: "The list-price ceiling for the run, in US dollars.", minimum: double.Epsilon, name: "--max-cost-usd");
        var jobsOption = CliOptions.Jobs(defaultValue: 4, description: "Agent runs at once, 1 to 8.", maximum: 8);
        var noFixtureOption = new Option<bool>(name: "--no-fixture") { Description = "Leave out the rules-file fixture, to repeat a run made without it." };
        var preloadOption = new Option<bool>(name: "--preload") { Description = "Put the skill's SKILL.md text at --revision into the fixture, as an agent that preloads the skill has it, instead of leaving the run to invoke it; needs --ablation none." };
        var dryRunOption = new Option<bool>(name: "--dry-run") { Description = "Print the plan and the claude command line; create and start nothing." };
        var keepTreeOption = new Option<bool>(name: "--keep-tree") { Description = "Keep the frozen worktree after the run." };
        var keepSandboxesOption = new Option<bool>(name: "--keep-sandboxes") { Description = "Keep each run's temporary sandbox after its trace is copied." };
        var command = new Command(description: "Run a skill's eval suite on a frozen tree, with the rules-file fixture and full model IDs.", name: "run") {
            skillArgument, casesOption, tagOption, revisionOption, modelOption, judgeOption, runsOption, ablationOption, costOption, jobsOption, noFixtureOption, preloadOption, dryRunOption, keepTreeOption, keepSandboxesOption,
        };

        ablationOption.AcceptOnlyFromAmong("with-without", "none");
        command.Detail(detail: """
            Use: puck evals run <skill> [--cases <glob>...] [--tag <tag>...] [--revision <rev>]
                   [--model <full id>] [--judge <full id>] [--runs n] [--ablation with-without|none]
                   [--max-cost-usd x] [--jobs n] [--no-fixture] [--preload] [--dry-run] [--keep-tree]
                   [--keep-sandboxes]

            1. Checks <rev> out as a detached worktree under .claude/worktrees/, never running the
               live tree, which claude plugin eval would read as it changes. With a --revision other
               than HEAD, HEAD's .claude/skills/<skill>/evals/ replaces the revision's, so the old
               and new skill run identical cases.
            2. Adds the fixture to every case in that tree: HEAD's AGENTS.md text (and, for a skill
               a lane loads, HEAD's delegate SKILL.md text) as a project-instructions block ahead of
               each prompt.md's task, as a real session delivers them. Both arms carry it, so the
               delta measures what the skill adds over the rules a session already has. The
               repository and .claude/skills/ are never granted. --preload adds the skill's own
               SKILL.md text at <rev> as well, as an agent that preloads the skill has it, so a run
               never depends on the model choosing to invoke a skill a lane always has; it needs
               --ablation none, and its baseline is a with-without run's without arm.
            3. Runs claude plugin eval on the tree's skill directory with --trust-plugin,
               --no-publish, --keep-temp, --threshold 0 and the models given, writing
               aggregate-result.json and report.html to .claude/skills/<skill>/evals/results/<stamp>/.
            4. Copies every run's transcript to traces/<case>/<arm>-<run>.jsonl there, removes the
               run's sandbox, writes run.json (the plan, the revisions, each case's tags and the
               exit code) and removes the frozen worktree.

            Models are full IDs (claude-haiku-5-5, claude-sonnet-5-5, claude-opus-5-5,
            claude-fable-5-2). The judge defaults to the model under test; a judge of a weaker tier
            is refused. A lane cannot run this verb (the session-launch hook refuses it, as it
            refuses claude); --dry-run and --help pass.

            Exit codes: 0 every run completed; 1 claude plugin eval reported a load or API error, or
            stopped partway (the cost ceiling); 2 invalid input or a refusal; 130 interrupted.
            """);
        command.SetAction(action: async (parseResult, cancellationToken) => await RunAsync(
            cancellationToken: cancellationToken,
            clock: clock,
            settings: new Settings(
                Ablation: parseResult.GetRequiredValue(option: ablationOption),
                Cases: (parseResult.GetValue(option: casesOption) ?? []),
                Jobs: parseResult.GetValue(option: jobsOption),
                DryRun: parseResult.GetValue(option: dryRunOption),
                Fixture: !parseResult.GetValue(option: noFixtureOption),
                Preload: parseResult.GetValue(option: preloadOption),
                Judge: (parseResult.GetValue(option: judgeOption) ?? parseResult.GetRequiredValue(option: modelOption)),
                KeepSandboxes: parseResult.GetValue(option: keepSandboxesOption),
                KeepTree: parseResult.GetValue(option: keepTreeOption),
                MaxCostUsd: parseResult.GetValue(option: costOption)!.Value,
                Model: parseResult.GetRequiredValue(option: modelOption),
                Revision: parseResult.GetRequiredValue(option: revisionOption),
                Runs: parseResult.GetValue(option: runsOption),
                Skill: parseResult.GetRequiredValue(argument: skillArgument),
                Tags: (parseResult.GetValue(option: tagOption) ?? [])
            )
        ).ConfigureAwait(continueOnCapturedContext: false));
        return command;
    }

    /// <summary>Keeps the case directories whose name matches any glob (<c>*</c> and <c>?</c>, case-sensitive) and that
    /// carry any of the tags; no glob or no tag leaves that filter out.</summary>
    private static IReadOnlyList<string> SelectCases(IReadOnlyList<string> caseDirectories, IReadOnlyList<string> globs, IReadOnlyList<string> tags) => [
        .. caseDirectories
            .Where(predicate: directory => ((globs.Count == 0) || globs.Any(predicate: glob => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(expression: glob, name: Path.GetFileName(path: directory), ignoreCase: false))))
            .Where(predicate: directory => ((tags.Count == 0) || CaseFiles.Tags(caseDirectory: directory).Intersect(second: tags, comparer: StringComparer.Ordinal).Any())),
    ];
    private static string SelectionText(Settings settings) => string.Join(separator: ' ', values: [
        .. ((settings.Cases.Count == 0) ? [] : (IEnumerable<string>)["--cases", .. settings.Cases]),
        .. ((settings.Tags.Count == 0) ? [] : (IEnumerable<string>)["--tag", .. settings.Tags]),
    ]);

    private static int Tier(string model) => ((FullModelId().Match(input: model) is { Success: true } match) ? System.Array.IndexOf(array: Tiers, value: match.Groups["tier"].Value) : -1);
    private static string Quote(string argument) => ((argument.Length != 0) && !argument.Any(predicate: static c => (char.IsWhiteSpace(c: c) || (c is '"' or '\'' or '*' or '?' or '[' or '$')))) ? argument : $"'{argument.Replace(oldValue: "'", newValue: "'\\''")}'";
    private static string Display(string path) => path.Replace(oldChar: '\\', newChar: '/');

    /// <summary>Builds the fixture text ahead of every case's task: the rules file a session in the repository has
    /// loaded, then each skill the lane preloads, each under a heading naming what it is.</summary>
    /// <param name="agents">The text of <c>AGENTS.md</c>.</param>
    /// <param name="skills">The preloaded skills in order, each its directory name and the text of its <c>SKILL.md</c>;
    /// each skill's YAML frontmatter is dropped, as a preloaded skill's body is what a lane reads.</param>
    /// <returns>The prompt text, with <c>\n</c> line endings.</returns>
    public static string FixturePrompt(string agents, IReadOnlyList<(string Name, string Text)> skills) {
        var text = new StringBuilder();

        text.Append(value: "You are working in the Puck repository. Its rules file, AGENTS.md, is loaded in every session there; its contents follow.\n\n");
        text.Append(value: "# Contents of AGENTS.md (project instructions, checked into the codebase)\n\n");
        text.Append(value: agents.ReplaceLineEndings(replacementText: "\n").Trim());
        foreach (var (name, skill) in skills) {
            var lines = skill.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');
            var end = CaseFiles.FrontmatterEnd(lines: lines);

            text.Append(value: $"\n\nYou are a delegated Puck lane, and your agent preloads the {name} skill; its contents follow.\n\n");
            text.Append(value: $"# Contents of the {name} skill (.claude/skills/{name}/SKILL.md)\n\n");
            text.Append(value: string.Join(separator: '\n', value: lines[((end < 0) ? 0 : (end + 1))..]).Trim());
        }
        return text.ToString();
    }

    private static async Task<int> RunAsync(Settings settings, TimeProvider clock, CancellationToken cancellationToken) {
        if (!SkillName().IsMatch(input: settings.Skill)) { return CliExit.Refuse(verb: Verb, what: settings.Skill, why: "is not a skill directory name."); }
        foreach (var (role, model) in new[] { ("--model", settings.Model), ("--judge", settings.Judge) }) {
            if (Tier(model: model) < 0) { return CliExit.Refuse(verb: Verb, what: $"{role} {model}", why: $"is not a full model ID of a known tier ({string.Join(separator: ", ", values: Tiers)}), such as {DefaultModel}; a full ID keeps a model rollout from reading as a skill change."); }
        }
        if (Tier(model: settings.Judge) < Tier(model: settings.Model)) { return CliExit.Refuse(verb: Verb, what: $"--judge {settings.Judge}", why: $"is a weaker tier than the model under test ({settings.Model}); a weaker judge fails correct replies it cannot follow."); }

        if (settings.Preload && !settings.Fixture) { return CliExit.Refuse(verb: Verb, what: "--preload", why: "puts the skill in the fixture, which --no-fixture leaves out."); }
        if (settings.Preload && (settings.Ablation != "none")) { return CliExit.Refuse(verb: Verb, what: "--preload", why: $"puts the skill ahead of both arms' prompts, so a {settings.Ablation} baseline arm would carry it too; pass --ablation none and compare against a with-without run's without arm."); }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) { return CliExit.Refused; }

        var skillPath = $".claude/skills/{settings.Skill}";
        var evalsPath = $"{skillPath}/evals";

        if (!CliGit.TryResolveCommit(repository: root, resolved: out var head, revision: "HEAD")) { return CliExit.Refuse(verb: Verb, what: root, why: "HEAD names no commit."); }
        if (!CliGit.TryResolveCommit(repository: root, resolved: out var revision, revision: settings.Revision)) { return CliExit.Refuse(verb: Verb, what: $"--revision {settings.Revision}", why: "unknown revision."); }
        if (CliGit.Run(root, "cat-file", "-e", $"{revision}:{skillPath}/SKILL.md").ExitCode != 0) { return CliExit.Refuse(verb: Verb, what: settings.Skill, why: $"{revision[..12]} has no {skillPath}/SKILL.md."); }
        if (CliGit.Run(root, "cat-file", "-e", $"{head}:{evalsPath}").ExitCode != 0) { return CliExit.Refuse(verb: Verb, what: settings.Skill, why: $"HEAD has no {evalsPath}/ to run."); }

        // Each preloaded skill and the revision its text comes from: delegate at HEAD for a skill a lane loads, and the
        // skill under test at --revision when it is preloaded, so a before-and-after pair differs only in that text.
        List<(string Name, string Revision)> preloads = [];

        if (settings.Fixture) {
            if (!SkillsWithoutDelegate.Contains(value: settings.Skill)) { preloads.Add(item: ("delegate", head)); }
            if (settings.Preload) { preloads.Add(item: (settings.Skill, revision)); }
        }
        var fixtureFiles = (settings.Fixture ? (IReadOnlyList<string>)["AGENTS.md", .. preloads.Select(selector: static preload => $".claude/skills/{preload.Name}/SKILL.md")] : []);
        var stamp = clock.GetUtcNow().ToString(format: "yyyyMMdd-HHmmss", formatProvider: CultureInfo.InvariantCulture);
        var label = $"{stamp}-{revision[..10]}-{Tiers[Tier(model: settings.Model)]}-{settings.Ablation}{(settings.Fixture ? string.Empty : "-nofixture")}{(settings.Preload ? "-preload" : string.Empty)}";
        var results = Path.GetFullPath(path: Path.Combine(root, ".claude", "skills", settings.Skill, "evals", "results", label));
        // The frozen tree goes under the main checkout's worktrees folder, beside every other worktree, never nested
        // inside the linked worktree the lead runs from.
        if (!CliGit.TryResolveMainCheckout(repository: root, mainCheckout: out var mainCheckout)) { return CliExit.Refuse(verb: Verb, what: root, why: "git names no common directory for this checkout."); }
        var tree = Path.GetFullPath(path: Path.Combine(mainCheckout, ".claude", "worktrees", $"evals-{settings.Skill}-{stamp}"));
        var target = Path.Combine(tree, ".claude", "skills", settings.Skill);
        List<string> arguments = [
            "plugin", "eval", Display(path: target),
            "--trust-plugin", "--no-publish", "--keep-temp",
            "--threshold", "0",
            "--model", settings.Model,
            "--judge-model", settings.Judge,
            "--ablation", settings.Ablation,
            "--max-cost-usd", settings.MaxCostUsd.ToString(provider: CultureInfo.InvariantCulture),
            "-j", settings.Jobs.ToString(provider: CultureInfo.InvariantCulture),
            "--output-dir", Display(path: results),
        ];

        if (settings.Runs is { } runs) { arguments.AddRange(collection: ["--runs", runs.ToString(provider: CultureInfo.InvariantCulture)]); }
        // Case selection is this verb's, not the runner's: claude plugin eval --case takes one glob, without braces or
        // lists, so the frozen tree keeps only the cases --cases and --tag select and the runner runs every case there.
        var selected = SelectCases(caseDirectories: CaseFiles.Enumerate(evals: Path.Combine(root, ".claude", "skills", settings.Skill, "evals")), globs: settings.Cases, tags: settings.Tags);

        if (selected.Count == 0) { return CliExit.Empty(verb: Verb, selection: ((SelectionText(settings: settings) is { Length: > 0 } named) ? named : $"{evalsPath}/"), why: "selects no case"); }

        Console.WriteLine(value: $"{Verb}: {settings.Skill} at {settings.Revision} ({revision[..12]}), cases from HEAD ({head[..12]}){((revision == head) ? string.Empty : ", copied into the old tree")}");
        Console.WriteLine(value: $"{Verb}: frozen tree {Display(path: tree)} (detached at {revision[..12]}; {(settings.KeepTree ? "kept" : "removed after the run")})");
        Console.WriteLine(value: (settings.Fixture
            ? $"{Verb}: fixture ahead of every case's prompt, both arms: HEAD's AGENTS.md{string.Concat(values: preloads.Select(selector: preload => $", {((preload.Revision == head) ? "HEAD" : settings.Revision)}'s {preload.Name} SKILL.md"))} text"
            : $"{Verb}: no fixture (--no-fixture): the cases run without the rules file"));
        Console.WriteLine(value: $"{Verb}: model {settings.Model}, judge {settings.Judge}, ablation {settings.Ablation}, ceiling ${settings.MaxCostUsd.ToString(provider: CultureInfo.InvariantCulture)}");
        Console.WriteLine(value: $"{Verb}: results {Display(path: results)}");
        Console.WriteLine(value: $"{Verb}: {selected.Count} case(s): {string.Join(separator: ' ', values: selected.Select(selector: static directory => Path.GetFileName(path: directory)))}");
        Console.WriteLine(value: $"{Verb}: claude {string.Join(separator: ' ', values: arguments.Select(selector: Quote))}");
        if (settings.DryRun) {
            Console.WriteLine(value: $"{Verb}: dry run: nothing was created or started.");
            return CliExit.Success;
        }

        if (await CliGit.RunAsync(arguments: ["worktree", "add", "--detach", "--quiet", tree, revision], cancellationToken: cancellationToken, repository: root).ConfigureAwait(continueOnCapturedContext: false) is { ExitCode: not 0 } added) {
            return CliExit.Refuse(verb: Verb, what: Display(path: tree), why: $"git worktree add failed: {added.Stderr.Trim()}");
        }
        try {
            if (revision != head) {
                foreach (var step in new[] { new[] { "rm", "-r", "-q", "--ignore-unmatch", "--", evalsPath }, new[] { "checkout", head, "--", evalsPath } }) {
                    if (await CliGit.RunAsync(arguments: step, cancellationToken: cancellationToken, repository: tree).ConfigureAwait(continueOnCapturedContext: false) is { ExitCode: not 0 } failed) {
                        return CliExit.Refuse(verb: Verb, what: Display(path: tree), why: $"git {string.Join(separator: ' ', values: step)} failed: {failed.Stderr.Trim()}");
                    }
                }
            }
            var found = CaseFiles.Enumerate(evals: Path.Combine(target, "evals"));
            var caseDirectories = SelectCases(caseDirectories: found, globs: settings.Cases, tags: settings.Tags);

            if (caseDirectories.Count == 0) { return CliExit.Empty(verb: Verb, selection: ((SelectionText(settings: settings) is { Length: > 0 } named) ? named : $"the frozen tree's {evalsPath}/"), why: "selects no case"); }
            foreach (var unselected in found.Except(second: caseDirectories, comparer: StringComparer.Ordinal)) { ConfinedDirectory.RemoveTree(path: unselected); }

            if (settings.Fixture) {
                var agents = await CliGit.CaptureAsync(arguments: ["show", $"{head}:AGENTS.md"], cancellationToken: cancellationToken, repository: root).ConfigureAwait(continueOnCapturedContext: false);
                List<(string Name, string Text)> skills = [];

                foreach (var (name, at) in preloads) {
                    skills.Add(item: (name, await CliGit.CaptureAsync(arguments: ["show", $"{at}:.claude/skills/{name}/SKILL.md"], cancellationToken: cancellationToken, repository: root).ConfigureAwait(continueOnCapturedContext: false)));
                }
                if (AddFixture(agents: agents, caseDirectories: caseDirectories, skills: skills) is { } refusal) {
                    return CliExit.Refuse(verb: Verb, what: Display(path: refusal.Case), why: refusal.Why);
                }
            }

            Directory.CreateDirectory(path: results);
            var manifest = Path.Combine(results, EvalSuite.ManifestName);

            await WriteManifestAsync(arguments: arguments, cancellationToken: cancellationToken, caseDirectories: caseDirectories, exitCode: null, head: head, path: manifest, revision: revision, settings: settings, traces: 0, fixtureFiles: fixtureFiles).ConfigureAwait(continueOnCapturedContext: false);

            int claudeExit;

            try {
                claudeExit = (await CliProcess.RunAsync(arguments: arguments, cancellationToken: cancellationToken, capture: false, fileName: "claude", workingDirectory: tree).ConfigureAwait(continueOnCapturedContext: false)).ExitCode;
            } catch (Win32Exception exception) {
                return CliExit.Refuse(verb: Verb, what: "claude", why: $"could not start: {exception.Message}");
            }

            var traces = CopyTraces(keepSandboxes: settings.KeepSandboxes, results: results);

            await WriteManifestAsync(arguments: arguments, cancellationToken: cancellationToken, caseDirectories: caseDirectories, exitCode: claudeExit, head: head, path: manifest, revision: revision, settings: settings, traces: traces, fixtureFiles: fixtureFiles).ConfigureAwait(continueOnCapturedContext: false);
            Console.WriteLine(value: $"{Verb}: claude plugin eval exited {claudeExit}; {traces} trace(s) copied to {Display(path: Path.Combine(results, TracesDirectory))}.");
            Console.WriteLine(value: $"{Verb}: next: puck evals graders {Display(path: Path.Combine(results, "aggregate-result.json"))}, and puck evals compare <old> <new>.");
            return claudeExit switch {
                0 => CliExit.Success,
                1 or 2 => CliExit.Failed,
                130 => CliExit.Cancelled,
                _ => CliExit.Refused,
            };
        } finally {
            if (!settings.KeepTree) {
                var removed = CliGit.Run(root, "worktree", "remove", "--force", "--force", tree);

                if (removed.ExitCode != 0) { Console.Error.WriteLine(value: $"puck {Verb}: the frozen tree stays at {Display(path: tree)}: git worktree remove failed: {removed.Stderr.Trim()}"); }
            }
        }
    }

    /// <summary>Adds the rules-file fixture to every case directory: <see cref="FixturePrompt"/> as a
    /// <c>project-instructions</c> block ahead of the task in its <c>prompt.md</c> body, the same in both arms, as a
    /// session delivers project instructions in the user turn. A case that resumes a transcript takes the block ahead of
    /// the transcript's first user turn instead, since a real session holds its rules from the start, not only beside
    /// the turn under test. It is never <c>append_system_prompt</c>:
    /// <c>claude plugin eval</c> passes that on the child's command line, which Windows caps far below the rules file's
    /// size. Every case is checked before any is written.</summary>
    /// <param name="caseDirectories">The case directories, in a tree the run owns.</param>
    /// <param name="agents">The text of <c>AGENTS.md</c>.</param>
    /// <param name="skills">The preloaded skills, each its directory name and the text of its <c>SKILL.md</c>.</param>
    /// <returns><see langword="null"/> when every case took the fixture, or the first case that cannot, with why; a
    /// refusal writes nothing.</returns>
    public static (string Case, string Why)? AddFixture(IReadOnlyList<string> caseDirectories, string agents, IReadOnlyList<(string Name, string Text)> skills) {
        Dictionary<string, (string[] Lines, int Index, JsonNode Turn)> histories = new(comparer: StringComparer.Ordinal);

        foreach (var caseDirectory in caseDirectories) {
            if (!File.Exists(path: Path.Combine(caseDirectory, CaseFiles.Prompt))) { return (caseDirectory, "it has no prompt.md body to carry the fixture; keep the prompt in prompt.md."); }
            if (CaseFiles.HistoryFile(caseDirectory: caseDirectory) is not { } history) { continue; }
            if (!File.Exists(path: history)) { return (caseDirectory, $"its case.yaml names a history_file, {Path.GetFileName(path: history)}, that does not exist."); }
            var lines = File.ReadAllText(path: history).ReplaceLineEndings(replacementText: "\n").TrimEnd(trimChar: '\n').Split(separator: '\n');
            var index = -1;
            JsonNode? turn = null;

            try {
                for (var line = 0; (line < lines.Length) && (turn is null); line++) {
                    if ((JsonNode.Parse(json: lines[line]) is { } node) && ((string?)node["type"] == "user")) { (index, turn) = (line, node); }
                }
            } catch (JsonException exception) {
                return (caseDirectory, $"its history_file is not JSON Lines: {exception.Message}");
            }
            if ((turn is null) || (turn["message"]?["content"] is not (JsonValue or JsonArray))) {
                return (caseDirectory, "its history_file has no user turn whose message content can carry the fixture.");
            }
            histories[caseDirectory] = (lines, index, turn);
        }
        var block = $"<project-instructions>\n{FixturePrompt(agents: agents, skills: skills)}\n</project-instructions>\n\n";

        foreach (var caseDirectory in caseDirectories) {
            if (histories.TryGetValue(key: caseDirectory, value: out var history)) {
                var message = history.Turn["message"]!;

                if (message["content"] is JsonArray parts) {
                    parts.Insert(index: 0, item: new JsonObject { ["type"] = "text", ["text"] = block });
                } else {
                    message["content"] = (block + (string?)message["content"]);
                }
                history.Lines[history.Index] = history.Turn.ToJsonString();
                File.WriteAllText(contents: (string.Join(separator: '\n', value: history.Lines) + "\n"), path: CaseFiles.HistoryFile(caseDirectory: caseDirectory)!);
                continue;
            }
            var path = Path.Combine(caseDirectory, CaseFiles.Prompt);
            var lines = File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');
            var end = CaseFiles.FrontmatterEnd(lines: lines);
            var head = ((end < 0) ? string.Empty : (string.Join(separator: '\n', value: lines[..(end + 1)]) + "\n"));
            var body = string.Join(separator: '\n', value: lines[((end < 0) ? 0 : (end + 1))..]);

            File.WriteAllText(contents: (head + block + body), path: path);
        }
        return null;
    }
    // Copies each run's transcript beside the result and removes the run's sandbox (the claude-eval-* directory whose
    // out/ holds the transcript) unless asked to keep it. Returns how many transcripts were copied.
    private static int CopyTraces(string results, bool keepSandboxes) {
        var document = Path.Combine(results, "aggregate-result.json");

        if (!File.Exists(path: document)) { return 0; }
        var copied = 0;

        foreach (var evalCase in EvalSuite.Load(path: document).Cases) {
            foreach (var (arm, runs) in evalCase.Arms) {
                foreach (var run in runs) {
                    if ((run.TracePath is not { } trace) || !File.Exists(path: trace)) { continue; }
                    var destination = Path.Combine(results, TracesDirectory, evalCase.Name, $"{arm}-{run.Index}.jsonl");

                    Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                    File.Copy(destFileName: destination, overwrite: true, sourceFileName: trace);
                    copied++;
                    if (!keepSandboxes && (Directory.GetParent(path: trace)?.Parent is { } sandbox) && sandbox.Name.StartsWith(value: "claude-eval-", comparisonType: StringComparison.Ordinal)) {
                        try { ConfinedDirectory.RemoveTree(path: sandbox.FullName); } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) { Console.Error.WriteLine(value: $"puck {Verb}: kept {sandbox.FullName}: {exception.Message}"); }
                    }
                }
            }
        }
        return copied;
    }
    private static async Task WriteManifestAsync(string path, Settings settings, string head, string revision, IReadOnlyList<string> fixtureFiles, IReadOnlyList<string> caseDirectories, IReadOnlyList<string> arguments, int? exitCode, int traces, CancellationToken cancellationToken) {
        var manifest = new Dictionary<string, object?> {
            ["skill"] = settings.Skill,
            ["revision"] = settings.Revision,
            ["revisionCommit"] = revision,
            ["casesCommit"] = head,
            ["model"] = settings.Model,
            ["judge"] = settings.Judge,
            ["ablation"] = settings.Ablation,
            ["fixture"] = fixtureFiles,
            ["preload"] = settings.Preload,
            ["command"] = (IReadOnlyList<string>)["claude", .. arguments],
            ["exitCode"] = exitCode,
            ["traces"] = traces,
            ["cases"] = caseDirectories.ToDictionary(keySelector: static directory => Path.GetFileName(path: directory), elementSelector: static directory => new Dictionary<string, IReadOnlyList<string>> { ["tags"] = CaseFiles.Tags(caseDirectory: directory) }, comparer: StringComparer.Ordinal),
        };

        await File.WriteAllTextAsync(path: path, contents: (JsonSerializer.Serialize(value: manifest, options: new JsonSerializerOptions { WriteIndented = true }) + "\n"), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }
}
