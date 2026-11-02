using System.CommandLine;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    /// <summary>The fixture directory <c>puck evals run</c> adds to every case and grants through
    /// <c>context.add_dirs</c>.</summary>
    public const string FixtureDirectory = "puck-rules";
    private const string DefaultModel = "claude-sonnet-5-5";
    // Reading the fixture costs the agent one turn (both files in one parallel read).
    private const int FixtureTurns = 1;
    // The lead's own skill; every other skill is loaded by a lane, which preloads delegate.
    private static readonly string[] SkillsWithoutDelegate = ["delegate", "orchestration"];
    // Weakest first: a judge's tier must be at least the tier of the model under test.
    private static readonly string[] Tiers = ["haiku", "sonnet", "opus", "fable"];

    [GeneratedRegex(pattern: @"^claude-(?<tier>[a-z]+)-[0-9][0-9a-z-]*$")]
    private static partial Regex FullModelId();
    [GeneratedRegex(pattern: @"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SkillName();

    private sealed record Settings(string Skill, string? Cases, IReadOnlyList<string> Tags, string Revision, string Model, string Judge, int? Runs, string Ablation, double MaxCostUsd, int Concurrency, bool Fixture, bool DryRun, bool KeepTree, bool KeepSandboxes);

    /// <summary>Creates the <c>evals run</c> command.</summary>
    /// <param name="clock">The CLI host's clock, which stamps the results directory.</param>
    /// <returns>The command.</returns>
    public static Command Create(TimeProvider clock) {
        var skillArgument = new Argument<string>(name: "skill") { Description = "The skill under .claude/skills/ whose evals/ suite runs." };
        var casesOption = new Option<string?>(name: "--cases") { Description = "Run only the cases whose name matches this glob (claude plugin eval --case)." };
        var tagOption = new Option<string[]>(name: "--tag") { AllowMultipleArgumentsPerToken = true, Description = "Run only cases carrying any of these tags, such as regression." };
        var revisionOption = new Option<string>(name: "--revision") { DefaultValueFactory = static _ => "HEAD", Description = "The revision whose skill runs; HEAD's evals/ replace that revision's, so both sides of an edit run identical cases." };
        var modelOption = new Option<string>(name: "--model") { DefaultValueFactory = static _ => DefaultModel, Description = "The full model ID under test, such as claude-sonnet-5-5; an alias is refused." };
        var judgeOption = new Option<string?>(name: "--judge") { Description = "The full judge model ID; defaults to the model under test, and a weaker tier is refused." };
        var runsOption = new Option<int?>(name: "--runs") { Description = "Runs per case per arm; defaults to each case's own runs (a capability case sets 5, the runner's default is 3)." };
        var ablationOption = new Option<string>(name: "--ablation") { DefaultValueFactory = static _ => "with-without", Description = "with-without adds the no-skill arm; none runs the with-arm only." };
        var costOption = new Option<double>(name: "--max-cost-usd") { DefaultValueFactory = static _ => 20, Description = "The list-price ceiling for the run, in US dollars." };
        var concurrencyOption = new Option<int>(name: "--concurrency", aliases: ["-j"]) { DefaultValueFactory = static _ => 4, Description = "Agent runs at once, 1 to 8." };
        var noFixtureOption = new Option<bool>(name: "--no-fixture") { Description = "Leave out the rules-file fixture, to repeat a run made without it." };
        var dryRunOption = new Option<bool>(name: "--dry-run") { Description = "Print the plan and the claude command line; create and start nothing." };
        var keepTreeOption = new Option<bool>(name: "--keep-tree") { Description = "Keep the frozen worktree after the run." };
        var keepSandboxesOption = new Option<bool>(name: "--keep-sandboxes") { Description = "Keep each run's temporary sandbox after its trace is copied." };
        var command = new Command(description: "Run a skill's eval suite on a frozen tree, with the rules-file fixture and full model IDs.", name: "run") {
            skillArgument, casesOption, tagOption, revisionOption, modelOption, judgeOption, runsOption, ablationOption, costOption, concurrencyOption, noFixtureOption, dryRunOption, keepTreeOption, keepSandboxesOption,
        };

        ablationOption.AcceptOnlyFromAmong("with-without", "none");
        command.Detail(detail: """
            Use: puck evals run <skill> [--cases <glob>] [--tag <tag>...] [--revision <rev>]
                   [--model <full id>] [--judge <full id>] [--runs n] [--ablation with-without|none]
                   [--max-cost-usd x] [-j n] [--no-fixture] [--dry-run] [--keep-tree] [--keep-sandboxes]

            1. Checks <rev> out as a detached worktree under .claude/worktrees/, never running the
               live tree, which claude plugin eval would read as it changes. With a --revision other
               than HEAD, HEAD's .claude/skills/<skill>/evals/ replaces the revision's, so the old
               and new skill run identical cases.
            2. Adds the fixture to every case in that tree: a puck-rules/ directory holding HEAD's
               AGENTS.md (and, for a skill a lane loads, HEAD's delegate SKILL.md as
               delegate-SKILL.md), granted through case.yaml context.add_dirs and named by an
               append_system_prompt line, and one more max_turns for reading it. Both arms read it,
               so the delta measures what the skill adds over the rules a session already has. The
               repository and .claude/skills/ are never granted.
            3. Runs claude plugin eval on the tree's skill directory with --trust-plugin,
               --no-publish, --keep-temp, --threshold 0 and the models given, writing
               aggregate-result.json and report.html to .claude/skills/<skill>/evals/results/<stamp>/.
            4. Copies every run's transcript to traces/<case>/<arm>-<run>.jsonl there, removes the
               run's sandbox, writes run.json (the plan, the revisions, each case's tags and the
               exit code) and removes the frozen worktree.

            Models are full IDs (claude-haiku-5-5, claude-sonnet-5-5, claude-opus-5-5,
            claude-fable-5-1). The judge defaults to the model under test; a judge of a weaker tier
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
                Cases: parseResult.GetValue(option: casesOption),
                Concurrency: parseResult.GetValue(option: concurrencyOption),
                DryRun: parseResult.GetValue(option: dryRunOption),
                Fixture: !parseResult.GetValue(option: noFixtureOption),
                Judge: (parseResult.GetValue(option: judgeOption) ?? parseResult.GetRequiredValue(option: modelOption)),
                KeepSandboxes: parseResult.GetValue(option: keepSandboxesOption),
                KeepTree: parseResult.GetValue(option: keepTreeOption),
                MaxCostUsd: parseResult.GetValue(option: costOption),
                Model: parseResult.GetRequiredValue(option: modelOption),
                Revision: parseResult.GetRequiredValue(option: revisionOption),
                Runs: parseResult.GetValue(option: runsOption),
                Skill: parseResult.GetRequiredValue(argument: skillArgument),
                Tags: (parseResult.GetValue(option: tagOption) ?? [])
            )
        ).ConfigureAwait(continueOnCapturedContext: false));
        return command;
    }

    private static int Tier(string model) => ((FullModelId().Match(input: model) is { Success: true } match) ? System.Array.IndexOf(array: Tiers, value: match.Groups["tier"].Value) : -1);
    private static string Quote(string argument) => ((argument.Length != 0) && !argument.Any(predicate: static c => (char.IsWhiteSpace(c: c) || (c is '"' or '\'' or '*' or '?' or '[' or '$')))) ? argument : $"'{argument.Replace(oldValue: "'", newValue: "'\\''")}'";
    private static string Display(string path) => path.Replace(oldChar: '\\', newChar: '/');
    private static string YamlString(string text) => $"\"{text.Replace(oldValue: "\\", newValue: "\\\\").Replace(oldValue: "\"", newValue: "\\\"")}\"";

    /// <summary>The line appended to every case's system prompt, naming the fixture the agent reads.</summary>
    /// <param name="withDelegate">Whether the fixture holds the delegate skill.</param>
    /// <returns>The prompt text.</returns>
    public static string FixturePrompt(bool withDelegate) =>
        "You are working in the Puck repository, and every session there has its rules file, AGENTS.md, loaded. " +
        $"A copy of it is AGENTS.md in the additional working directory named {FixtureDirectory}" +
        (withDelegate ? "; beside it, delegate-SKILL.md is the delegate skill every Puck lane preloads, and you are such a lane" : string.Empty) +
        (withDelegate ? ". Read both before you answer, and follow them." : ". Read it before you answer, and follow it.");

    private static async Task<int> RunAsync(Settings settings, TimeProvider clock, CancellationToken cancellationToken) {
        if (!SkillName().IsMatch(input: settings.Skill)) { return CliExit.Refuse(verb: Verb, what: settings.Skill, why: "is not a skill directory name."); }
        foreach (var (role, model) in new[] { ("--model", settings.Model), ("--judge", settings.Judge) }) {
            if (Tier(model: model) < 0) { return CliExit.Refuse(verb: Verb, what: $"{role} {model}", why: $"is not a full model ID of a known tier ({string.Join(separator: ", ", values: Tiers)}), such as {DefaultModel}; a full ID keeps a model rollout from reading as a skill change."); }
        }
        if (Tier(model: settings.Judge) < Tier(model: settings.Model)) { return CliExit.Refuse(verb: Verb, what: $"--judge {settings.Judge}", why: $"is a weaker tier than the model under test ({settings.Model}); a weaker judge fails correct replies it cannot follow."); }
        if (settings.Concurrency is < 1 or > 8) { return CliExit.Refuse(verb: Verb, what: $"-j {settings.Concurrency}", why: "must be 1 to 8."); }
        if (settings.Runs is < 1 or > 50) { return CliExit.Refuse(verb: Verb, what: $"--runs {settings.Runs}", why: "must be 1 to 50."); }
        if (settings.MaxCostUsd <= 0) { return CliExit.Refuse(verb: Verb, what: "--max-cost-usd", why: "must be positive."); }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) { return CliExit.Refused; }

        var skillPath = $".claude/skills/{settings.Skill}";
        var evalsPath = $"{skillPath}/evals";

        if (!CliGit.TryResolveCommit(repository: root, resolved: out var head, revision: "HEAD")) { return CliExit.Refuse(verb: Verb, what: root, why: "HEAD names no commit."); }
        if (!CliGit.TryResolveCommit(repository: root, resolved: out var revision, revision: settings.Revision)) { return CliExit.Refuse(verb: Verb, what: $"--revision {settings.Revision}", why: "unknown revision."); }
        if (CliGit.Run(root, "cat-file", "-e", $"{revision}:{skillPath}/SKILL.md").ExitCode != 0) { return CliExit.Refuse(verb: Verb, what: settings.Skill, why: $"{revision[..12]} has no {skillPath}/SKILL.md."); }
        if (CliGit.Run(root, "cat-file", "-e", $"{head}:{evalsPath}").ExitCode != 0) { return CliExit.Refuse(verb: Verb, what: settings.Skill, why: $"HEAD has no {evalsPath}/ to run."); }

        var withDelegate = !SkillsWithoutDelegate.Contains(value: settings.Skill);
        var stamp = clock.GetUtcNow().ToString(format: "yyyyMMdd-HHmmss", formatProvider: CultureInfo.InvariantCulture);
        var label = $"{stamp}-{revision[..10]}-{Tiers[Tier(model: settings.Model)]}-{settings.Ablation}{(settings.Fixture ? string.Empty : "-nofixture")}";
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
            "-j", settings.Concurrency.ToString(provider: CultureInfo.InvariantCulture),
            "--output-dir", Display(path: results),
        ];

        if (settings.Runs is { } runs) { arguments.AddRange(collection: ["--runs", runs.ToString(provider: CultureInfo.InvariantCulture)]); }
        if (settings.Cases is { } cases) { arguments.AddRange(collection: ["--case", cases]); }
        // --tag takes a list, so it goes last, where nothing after it can be read as one of its values.
        if (settings.Tags.Count != 0) { arguments.AddRange(collection: ["--tag", .. settings.Tags]); }

        Console.WriteLine(value: $"{Verb}: {settings.Skill} at {settings.Revision} ({revision[..12]}), cases from HEAD ({head[..12]}){((revision == head) ? string.Empty : ", copied into the old tree")}");
        Console.WriteLine(value: $"{Verb}: frozen tree {Display(path: tree)} (detached at {revision[..12]}; {(settings.KeepTree ? "kept" : "removed after the run")})");
        Console.WriteLine(value: (settings.Fixture
            ? $"{Verb}: fixture {FixtureDirectory}/ in every case: HEAD's AGENTS.md{(withDelegate ? " and delegate SKILL.md" : string.Empty)}, granted by case.yaml context.add_dirs, named by append_system_prompt, max_turns +{FixtureTurns}"
            : $"{Verb}: no fixture (--no-fixture): the cases run without the rules file"));
        Console.WriteLine(value: $"{Verb}: model {settings.Model}, judge {settings.Judge}, ablation {settings.Ablation}, ceiling ${settings.MaxCostUsd.ToString(provider: CultureInfo.InvariantCulture)}");
        Console.WriteLine(value: $"{Verb}: results {Display(path: results)}");
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
            var caseDirectories = CaseFiles.Enumerate(evals: Path.Combine(target, "evals"));

            if (settings.Fixture) {
                var agents = await CliGit.CaptureAsync(arguments: ["show", $"{head}:AGENTS.md"], cancellationToken: cancellationToken, repository: root).ConfigureAwait(continueOnCapturedContext: false);
                var delegateSkill = (withDelegate ? await CliGit.CaptureAsync(arguments: ["show", $"{head}:.claude/skills/delegate/SKILL.md"], cancellationToken: cancellationToken, repository: root).ConfigureAwait(continueOnCapturedContext: false) : null);

                if (AddFixture(agents: agents, caseDirectories: caseDirectories, delegateSkill: delegateSkill) is { } refusal) {
                    return CliExit.Refuse(verb: Verb, what: Display(path: refusal.Case), why: refusal.Why);
                }
            }

            Directory.CreateDirectory(path: results);
            var manifest = Path.Combine(results, EvalSuite.ManifestName);

            await WriteManifestAsync(arguments: arguments, cancellationToken: cancellationToken, caseDirectories: caseDirectories, exitCode: null, head: head, path: manifest, revision: revision, settings: settings, traces: 0, withDelegate: withDelegate).ConfigureAwait(continueOnCapturedContext: false);

            int claudeExit;

            try {
                claudeExit = (await CliProcess.RunAsync(arguments: arguments, cancellationToken: cancellationToken, capture: false, fileName: "claude", workingDirectory: tree).ConfigureAwait(continueOnCapturedContext: false)).ExitCode;
            } catch (Win32Exception exception) {
                return CliExit.Refuse(verb: Verb, what: "claude", why: $"could not start: {exception.Message}");
            }

            var traces = CopyTraces(keepSandboxes: settings.KeepSandboxes, results: results);

            await WriteManifestAsync(arguments: arguments, cancellationToken: cancellationToken, caseDirectories: caseDirectories, exitCode: claudeExit, head: head, path: manifest, revision: revision, settings: settings, traces: traces, withDelegate: withDelegate).ConfigureAwait(continueOnCapturedContext: false);
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

    /// <summary>Adds the rules-file fixture to every case directory: a <see cref="FixtureDirectory"/> directory holding
    /// <c>AGENTS.md</c> (and <c>delegate-SKILL.md</c> when <paramref name="delegateSkill"/> is given), granted through
    /// <c>case.yaml</c> <c>context.add_dirs</c> and named by <c>execution.append_system_prompt</c>, with the
    /// <c>max_turns</c> a <c>prompt.md</c> sets raised by one for the read. Every case is checked before any is
    /// written.</summary>
    /// <param name="caseDirectories">The case directories, in a tree the run owns.</param>
    /// <param name="agents">The text of <c>AGENTS.md</c>.</param>
    /// <param name="delegateSkill">The text of the delegate skill, or <see langword="null"/> to leave it out.</param>
    /// <returns><see langword="null"/> when every case took the fixture, or the first case that cannot, with why; a
    /// refusal writes nothing.</returns>
    public static (string Case, string Why)? AddFixture(IReadOnlyList<string> caseDirectories, string agents, string? delegateSkill) {
        foreach (var caseDirectory in caseDirectories) {
            if (CaseFiles.PromptSets(caseDirectory: caseDirectory, key: "append_system_prompt")) { return (caseDirectory, "its prompt.md sets append_system_prompt, which would override the fixture's; move that text into the prompt."); }
            if (Directory.Exists(path: Path.Combine(caseDirectory, FixtureDirectory))) { return (caseDirectory, $"it already holds {FixtureDirectory}/, the directory the fixture writes."); }
            if (File.Exists(path: Path.Combine(caseDirectory, CaseFiles.Case)) && File.ReadAllLines(path: Path.Combine(caseDirectory, CaseFiles.Case)).Any(predicate: static line => (line.TrimStart().StartsWith(value: "add_dirs:", comparisonType: StringComparison.Ordinal) || line.TrimStart().StartsWith(value: "append_system_prompt:", comparisonType: StringComparison.Ordinal)))) {
                return (caseDirectory, "its case.yaml sets add_dirs or append_system_prompt already; merge the fixture's by hand or drop them.");
            }
        }
        var prompt = YamlString(text: FixturePrompt(withDelegate: (delegateSkill is not null)));

        foreach (var caseDirectory in caseDirectories) {
            var fixture = Directory.CreateDirectory(path: Path.Combine(caseDirectory, FixtureDirectory)).FullName;

            File.WriteAllText(path: Path.Combine(fixture, "AGENTS.md"), contents: agents);
            if (delegateSkill is not null) { File.WriteAllText(path: Path.Combine(fixture, "delegate-SKILL.md"), contents: delegateSkill); }
            CaseFiles.SetCaseKey(caseDirectory: caseDirectory, key: "add_dirs", section: "context", value: $"[{FixtureDirectory}]");
            CaseFiles.SetCaseKey(caseDirectory: caseDirectory, key: "append_system_prompt", section: "execution", value: prompt);
            _ = CaseFiles.RaiseMaxTurns(caseDirectory: caseDirectory, extra: FixtureTurns);
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
                        try { sandbox.Delete(recursive: true); } catch (IOException exception) { Console.Error.WriteLine(value: $"puck {Verb}: kept {sandbox.FullName}: {exception.Message}"); } catch (UnauthorizedAccessException exception) { Console.Error.WriteLine(value: $"puck {Verb}: kept {sandbox.FullName}: {exception.Message}"); }
                    }
                }
            }
        }
        return copied;
    }
    private static async Task WriteManifestAsync(string path, Settings settings, string head, string revision, bool withDelegate, IReadOnlyList<string> caseDirectories, IReadOnlyList<string> arguments, int? exitCode, int traces, CancellationToken cancellationToken) {
        var manifest = new Dictionary<string, object?> {
            ["skill"] = settings.Skill,
            ["revision"] = settings.Revision,
            ["revisionCommit"] = revision,
            ["casesCommit"] = head,
            ["model"] = settings.Model,
            ["judge"] = settings.Judge,
            ["ablation"] = settings.Ablation,
            ["fixture"] = (settings.Fixture ? (withDelegate ? new[] { "AGENTS.md", "delegate-SKILL.md" } : ["AGENTS.md"]) : []),
            ["fixtureTurns"] = (settings.Fixture ? FixtureTurns : 0),
            ["command"] = (IReadOnlyList<string>)["claude", .. arguments],
            ["exitCode"] = exitCode,
            ["traces"] = traces,
            ["cases"] = caseDirectories.ToDictionary(keySelector: static directory => Path.GetFileName(path: directory), elementSelector: static directory => new Dictionary<string, IReadOnlyList<string>> { ["tags"] = CaseFiles.Tags(caseDirectory: directory) }, comparer: StringComparer.Ordinal),
        };

        await File.WriteAllTextAsync(path: path, contents: (JsonSerializer.Serialize(value: manifest, options: new JsonSerializerOptions { WriteIndented = true }) + "\n"), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }
}
