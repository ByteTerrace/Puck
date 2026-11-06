using System.CommandLine;
using System.Globalization;
using System.Text.Json;

namespace Puck.Cli.Evals;

/// <summary>
/// <c>puck evals</c> — the skill evaluation family over <c>claude plugin eval</c>: <c>run</c> (<see cref="EvalsRun"/>)
/// runs a skill's suite against a frozen tree with the rules-file fixture, <c>compare</c> compares two result documents
/// on the comparable score (<see cref="EvalScoring"/>), and <c>graders</c> lists the graders a hand-labelling pass
/// starts from.
/// </summary>
public static class EvalsCommand {
    private const string Verb = "evals";

    /// <summary>Creates the <c>evals</c> command family.</summary>
    /// <param name="clock">The CLI host's clock, which stamps each run's results directory.</param>
    /// <returns>The family.</returns>
    public static Command Create(TimeProvider clock) => new(
        description: "Run a skill's eval suite on a frozen tree, compare two results comparably, and list graders to label.",
        name: "evals"
    ) {
        EvalsRun.Create(clock: clock),
        CreateCompare(),
        CreateGraders(),
    };

    private static string F(double value) => value.ToString(format: "0.00", provider: CultureInfo.InvariantCulture);
    private static string Signed(double value) => value.ToString(format: "+0.00;-0.00;+0.00", provider: CultureInfo.InvariantCulture);
    private static bool TryLoad(string path, string verb, out EvalSuite suite) {
        suite = null!;
        try {
            suite = EvalSuite.Load(path: path);
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) {
            CliExit.Refuse(verb: verb, what: path, why: exception.Message);
            return false;
        }
    }

    private static Command CreateCompare() {
        var aArgument = new Argument<string>(name: "a") { Description = "The earlier result document (aggregate-result.json), such as the old tree's." };
        var bArgument = new Argument<string>(name: "b") { Description = "The later result document, such as the new tree's." };
        var aArm = new Option<string>(name: "--a-arm") { DefaultValueFactory = static _ => "with", Description = "The arm of a to compare: with or without." };
        var bArm = new Option<string>(name: "--b-arm") { DefaultValueFactory = static _ => "with", Description = "The arm of b to compare: with or without." };
        var command = new Command(description: "Compare two eval results case by case on the comparable score.", name: "compare") { aArgument, bArgument, aArm, bArm };

        aArm.AcceptOnlyFromAmong("with", "without");
        bArm.AcceptOnlyFromAmong("with", "without");
        command.Detail(detail: """
            Use: puck evals compare <a.json> <b.json> [--a-arm with|without] [--b-arm with|without]
            Recomputes each run's score so two documents compare whatever ablation mode each ran in:
            activation graders (tool_used on Skill, arm: with-only, or withOnly in the result) are
            left out on both sides, only executed graders count, and a run that reached max_turns,
            ended on an error, was aborted by a mock or had its judge graders skipped by the cost
            ceiling is left out and listed. Compare a skill edit as the old tree's with-arm against
            the new tree's; compare one document's arms (the skill against no skill) by naming it
            twice with --a-arm without.

            Regression cases (tagged regression) report pass^k: whether every included run passed
            every comparable grader. A regression case that held on a and fails on b is a stop.
            Capability cases (tagged capability) and untagged cases report the mean and the per-run
            vector; a difference is marked real when it exceeds one grader's share of a run's score
            and the two vectors do not overlap. Tags come from the result, else the run.json manifest
            puck evals run writes beside it, else the case files under the suite root.

            The documents should share a judge and a model: a difference in either is printed as a
            warning, since it moves every judged grader.

            Exit codes: 0 compared, no regression case regressed; 1 a regression case regressed;
            2 a document could not be read.
            """);
        command.SetAction(action: parseResult => Compare(
            a: parseResult.GetRequiredValue(argument: aArgument),
            aArm: parseResult.GetRequiredValue(option: aArm),
            b: parseResult.GetRequiredValue(argument: bArgument),
            bArm: parseResult.GetRequiredValue(option: bArm)
        ));
        return command;
    }

    /// <summary>Compares arm <paramref name="aArm"/> of <paramref name="a"/> with arm <paramref name="bArm"/> of
    /// <paramref name="b"/> case by case and prints the report.</summary>
    /// <param name="a">The earlier document's path.</param>
    /// <param name="aArm">The earlier document's arm.</param>
    /// <param name="b">The later document's path.</param>
    /// <param name="bArm">The later document's arm.</param>
    /// <returns><see cref="CliExit.Success"/>, <see cref="CliExit.Failed"/> when a regression case regressed, or
    /// <see cref="CliExit.Refused"/> when a document cannot be read.</returns>
    public static int Compare(string a, string aArm, string b, string bArm) {
        const string Name = "evals compare";

        if (!TryLoad(path: a, suite: out var suiteA, verb: Name) || !TryLoad(path: b, suite: out var suiteB, verb: Name)) { return CliExit.Refused; }
        Console.WriteLine(value: $"{Name}: a = {a} [{aArm}] ({suiteA.Describe()})");
        Console.WriteLine(value: $"{Name}: b = {b} [{bArm}] ({suiteB.Describe()})");
        if (suiteA.Judge != suiteB.Judge) { Console.WriteLine(value: $"WARNING: the judges differ ({suiteA.Judge} against {suiteB.Judge}); every llm grader's difference mixes the judges with the teaching."); }
        if (suiteA.Model != suiteB.Model) { Console.WriteLine(value: $"WARNING: the models differ ({suiteA.Model} against {suiteB.Model})."); }
        if (suiteA.Partial || suiteB.Partial) { Console.WriteLine(value: "WARNING: a document is partial; cases it never started are missing from it."); }
        Console.WriteLine(value: "Activation graders are left out of both sides; only executed graders count.");

        var inAByName = suiteA.Cases.ToDictionary(keySelector: static evalCase => evalCase.Name, comparer: StringComparer.Ordinal);
        var byName = suiteB.Cases.ToDictionary(keySelector: static evalCase => evalCase.Name, comparer: StringComparer.Ordinal);
        // A case's group is the later document's tag when it has one: a re-tag lands with the cases b ran.
        var names = suiteA.Cases.Select(selector: static evalCase => evalCase.Name).Concat(second: suiteB.Cases.Select(selector: static evalCase => evalCase.Name)).Distinct(comparer: StringComparer.Ordinal).ToList();
        var groupOf = names.ToDictionary(keySelector: static name => name, elementSelector: name => ((byName.TryGetValue(key: name, value: out var later) && (later.Group != "untagged")) ? later.Group : (inAByName.TryGetValue(key: name, value: out var earlier) ? earlier.Group : "untagged")), comparer: StringComparer.Ordinal);
        var excluded = new List<(string Side, EvalExclusion Exclusion)>();
        var groups = new Dictionary<string, List<(double A, double B)>>(comparer: StringComparer.Ordinal);
        var regressed = 0;

        foreach (var group in new[] { "regression", "capability", "untagged" }) {
            var rows = names.Where(predicate: name => (groupOf[name] == group)).ToList();

            if (rows.Count == 0) { continue; }
            Console.WriteLine();
            Console.WriteLine(value: group switch {
                "regression" => "regression cases: pass^k, every included run passing every comparable grader",
                "capability" => "capability cases: mean and per-run vector",
                _ => "untagged cases: mean and per-run vector (tag each regression or capability)",
            });
            foreach (var caseName in rows) {
                if (!inAByName.TryGetValue(key: caseName, value: out var inA) || !byName.TryGetValue(key: caseName, value: out var inB)) {
                    Console.WriteLine(value: $"  {caseName}: only in {(inAByName.ContainsKey(key: caseName) ? "a" : "b")}");
                    continue;
                }
                var evalCase = inB;
                var scoreA = EvalScoring.Score(arm: aArm, evalCase: inA);
                var scoreB = EvalScoring.Score(arm: bArm, evalCase: inB);

                excluded.AddRange(collection: scoreA.Excluded.Select(selector: static exclusion => ("a", exclusion)));
                excluded.AddRange(collection: scoreB.Excluded.Select(selector: static exclusion => ("b", exclusion)));
                if ((scoreA.Mean is not { } meanA) || (scoreB.Mean is not { } meanB)) {
                    Console.WriteLine(value: $"  {evalCase.Name}: unmeasured (a {scoreA.Vector.Count} run(s), b {scoreB.Vector.Count} run(s) included)");
                    continue;
                }
                if (!groups.TryGetValue(key: group, value: out var pairs)) { groups[group] = pairs = []; }
                pairs.Add(item: (meanA, meanB));
                if (group == "regression") {
                    var verdict = ((scoreA.PassAll, scoreB.PassAll) switch {
                        (true, false) => "REGRESSED",
                        (false, true) => "now holds",
                        (true, true) => "holds",
                        _ => "fails on both",
                    });

                    if (verdict == "REGRESSED") { regressed++; }
                    Console.WriteLine(value: $"  {evalCase.Name,-48} a {scoreA.Perfect}/{scoreA.Vector.Count}  b {scoreB.Perfect}/{scoreB.Vector.Count}  {verdict}  a {scoreA.FormatVector()} b {scoreB.FormatVector()}");
                } else {
                    var real = (EvalScoring.IsReal(a: scoreA, b: scoreB) ? $"REAL (> {F(value: Math.Max(val1: scoreA.GraderShare, val2: scoreB.GraderShare))}, vectors apart)" : "noise");

                    Console.WriteLine(value: $"  {evalCase.Name,-48} a {F(value: meanA)}  b {F(value: meanB)}  {Signed(value: (meanB - meanA))}  a {scoreA.FormatVector()} b {scoreB.FormatVector()}  {real}");
                }
            }
        }

        Console.WriteLine();
        foreach (var (group, pairs) in groups) {
            var meanA = pairs.Average(selector: static pair => pair.A);
            var meanB = pairs.Average(selector: static pair => pair.B);

            Console.WriteLine(value: $"{Name}: {group}: {pairs.Count} case(s) on both sides, mean of case means a {F(value: meanA)}  b {F(value: meanB)}  {Signed(value: (meanB - meanA))}");
        }
        if (excluded.Count != 0) {
            Console.WriteLine(value: $"{Name}: {excluded.Count} run(s) left out as infrastructure evidence:");
            foreach (var (side, exclusion) in excluded) { Console.WriteLine(value: $"  {side} {exclusion.Case} {exclusion.Arm} run {exclusion.Run}: {exclusion.Reason}"); }
        }
        if (regressed != 0) {
            Console.WriteLine(value: $"{Name}: {regressed} regression case(s) regressed: a stop until each is explained.");
            return CliExit.Failed;
        }
        return CliExit.Success;
    }

    private static Command CreateGraders() {
        var resultArgument = new Argument<string>(name: "result") { Description = "The result document (aggregate-result.json)." };
        var armOption = new Option<string>(name: "--arm") { DefaultValueFactory = static _ => "with", Description = "The arm whose runs are listed: with or without." };
        var evidenceOption = CliOptions.Count(defaultValue: 0, description: "Cut each judge excerpt to this many characters; 0 prints it whole.", minimum: 0, name: "--evidence-chars");
        var command = new Command(description: "List every grader below ceiling with its votes and evidence, the hand-labelling work list.", name: "graders") { resultArgument, armOption, evidenceOption };

        armOption.AcceptOnlyFromAmong("with", "without");
        command.Detail(detail: """
            Use: puck evals graders <result.json> [--arm with|without] [--evidence-chars n]
            Lists every comparable grader (activation graders left out) that fails at least one
            executed run of the arm, with its rubric and, per run, the verdict, the judge's votes,
            the runner's explanation, the copied trace when puck evals run kept one, and the
            excerpt the judge read. Runs left out of the comparable score are marked with why.

            For each listed run, read the excerpt, label it PASS or FAIL yourself, and record whether
            you agree with the judge. Repair a rubric you disagree with before believing any score
            built on it: a rubric states what the reply says, with an explicit PASS and FAIL
            condition, never that the agent fixed, ran or committed something.

            Exit codes: 0 listed (or nothing below ceiling); 2 the document could not be read.
            """);
        command.SetAction(action: parseResult => Graders(
            arm: parseResult.GetRequiredValue(option: armOption),
            evidenceChars: parseResult.GetValue(option: evidenceOption),
            result: parseResult.GetRequiredValue(argument: resultArgument)
        ));
        return command;
    }

    /// <summary>Prints every comparable grader of <paramref name="result"/> that fails at least one executed run of
    /// <paramref name="arm"/>, with its rubric, votes and evidence.</summary>
    /// <param name="result">The result document's path.</param>
    /// <param name="arm">The arm, <c>with</c> or <c>without</c>.</param>
    /// <param name="evidenceChars">The longest judge excerpt printed, in characters; 0 for no limit.</param>
    /// <returns><see cref="CliExit.Success"/>, or <see cref="CliExit.Refused"/> when the document cannot be read.</returns>
    public static int Graders(string result, string arm, int evidenceChars) {
        const string Name = "evals graders";

        if (!TryLoad(path: result, suite: out var suite, verb: Name)) { return CliExit.Refused; }
        var traces = Path.Combine(path1: (Path.GetDirectoryName(path: Path.GetFullPath(path: result)) ?? "."), path2: EvalsRun.TracesDirectory);
        var listed = 0;
        var graders = 0;

        Console.WriteLine(value: $"{Name}: {result} [{arm}] ({suite.Describe()})");
        foreach (var evalCase in suite.Cases) {
            var runs = (evalCase.Arms.TryGetValue(key: arm, value: out var armRuns) ? armRuns : []);
            var names = runs.SelectMany(selector: static run => run.Graders).Where(predicate: verdict => EvalScoring.Counts(evalCase: evalCase, verdict: verdict)).Select(selector: static verdict => verdict.Name).Distinct(comparer: StringComparer.Ordinal).ToList();

            graders += names.Count;
            foreach (var name in names) {
                var verdicts = runs.Select(selector: run => (Run: run, Verdict: run.Graders.FirstOrDefault(predicate: verdict => (verdict.Name == name)))).Where(predicate: static pair => (pair.Verdict?.Passed is not null)).ToList();
                var passes = verdicts.Count(predicate: static pair => (pair.Verdict!.Passed == true));

                if (passes == verdicts.Count) { continue; }
                var definition = evalCase.Definition(name: name);

                listed++;
                Console.WriteLine();
                Console.WriteLine(value: $"{evalCase.Name} / {name} ({definition?.Type ?? "?"}, weight {(definition?.Weight ?? 1).ToString(provider: CultureInfo.InvariantCulture)}, {evalCase.Group}): {passes}/{verdicts.Count} executed runs pass");
                if (definition is { Text.Length: > 0 }) { Console.WriteLine(value: $"  rubric: {definition.Text.ReplaceLineEndings(replacementText: " ")}"); }
                foreach (var (run, verdict) in verdicts) {
                    var votes = ((verdict!.Votes.Count == 0) ? string.Empty : $"  votes {string.Join(separator: ' ', values: verdict.Votes.Select(selector: static vote => (vote ? "PASS" : "FAIL")))}");
                    var left = EvalScoring.ExclusionReason(evalCase: evalCase, run: run) is { } reason ? $"  (left out of the score: {reason})" : string.Empty;
                    var trace = Path.Combine(path1: traces, path2: evalCase.Name, path3: $"{arm}-{run.Index}.jsonl");

                    Console.WriteLine(value: $"  run {run.Index}: {((verdict.Passed == true) ? "PASS" : "FAIL")}{votes}{left}  label: ____  agree: ____");
                    Console.WriteLine(value: $"    explanation: {verdict.Explanation.ReplaceLineEndings(replacementText: " ")}");
                    Console.WriteLine(value: $"    trace: {(File.Exists(path: trace) ? trace : (run.TracePath ?? "(none recorded)"))}");
                    if (verdict.Evidence.Length != 0) {
                        var evidence = (((evidenceChars > 0) && (verdict.Evidence.Length > evidenceChars)) ? (verdict.Evidence[..evidenceChars] + " ...") : verdict.Evidence);

                        Console.WriteLine(value: "    evidence:");
                        foreach (var line in evidence.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n')) { Console.WriteLine(value: $"      {line}"); }
                    }
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine(value: $"{Name}: {listed} of {graders} comparable grader(s) fail at least one executed {arm}-arm run.");
        return CliExit.Success;
    }
}
