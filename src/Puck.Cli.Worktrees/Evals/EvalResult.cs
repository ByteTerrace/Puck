using System.Globalization;
using System.Text.Json;

namespace Puck.Cli.Evals;

/// <summary>One grader's verdict in one run, as <c>claude plugin eval</c> records it.</summary>
/// <param name="Name">The grader's name, its file name without <c>.md</c>.</param>
/// <param name="Passed">The verdict, or <see langword="null"/> when the grader did not execute (a cost-ceiling skip).</param>
/// <param name="Weight">The grader's weight in the run's score.</param>
/// <param name="WithOnly">Whether the runner excluded the grader from the two-arm score.</param>
/// <param name="Votes">The judge's votes for an <c>llm</c> or <c>baseline</c> grader; empty for the others.</param>
/// <param name="Explanation">The runner's one-line explanation of the verdict.</param>
/// <param name="Evidence">The excerpt a judge was shown; empty for a grader that calls no judge.</param>
public sealed record EvalGraderVerdict(string Name, bool? Passed, double Weight, bool WithOnly, IReadOnlyList<bool> Votes, string Explanation, string Evidence);

/// <summary>One grader as the case defines it.</summary>
/// <param name="Name">The grader's name.</param>
/// <param name="Type">The grader type: <c>regex</c>, <c>tool_used</c>, <c>tool_order</c>, <c>file_exists</c>, <c>llm</c>
/// or <c>baseline</c>.</param>
/// <param name="Weight">The grader's weight.</param>
/// <param name="Arm">The grader's <c>arm</c> key (<c>with-only</c> or <c>both</c>), or <see langword="null"/> when unset.</param>
/// <param name="Tool">The tool a <c>tool_used</c> grader counts, or <see langword="null"/>.</param>
/// <param name="Text">The rubric, pattern or tool condition the grader checks, for a reader.</param>
public sealed record EvalGraderDefinition(string Name, string Type, double Weight, string? Arm, string? Tool, string Text) {
    /// <summary>Gets whether the grader measures whether the skill fired rather than what the reply says: a
    /// <c>tool_used</c> grader on <c>Skill</c> or one marked <c>arm: with-only</c>, unless it is marked <c>arm: both</c>.
    /// Under <c>--ablation none</c> the runner scores these, so a with-arm from that mode and one from a two-arm run
    /// differ by them alone; the comparable score leaves them out of both.</summary>
    public bool IsActivation => ((Arm != "both") && ((Arm == "with-only") || ((Type == "tool_used") && (Tool == "Skill"))));
}

/// <summary>One agent run of one case in one arm.</summary>
/// <param name="Index">The run's one-based position in its arm.</param>
/// <param name="Turns">The turns the run took.</param>
/// <param name="Error">Why the run ended abnormally, or <see langword="null"/>.</param>
/// <param name="TracePath">The transcript path the runner recorded, or <see langword="null"/>.</param>
/// <param name="SkippedPaidGraders">Whether the cost ceiling skipped the run's judge graders.</param>
/// <param name="Aborted">Whether a mock aborted the run.</param>
/// <param name="Graders">Each grader's verdict.</param>
public sealed record EvalRun(int Index, int Turns, string? Error, string? TracePath, bool SkippedPaidGraders, bool Aborted, IReadOnlyList<EvalGraderVerdict> Graders);

/// <summary>One case of a suite result.</summary>
/// <param name="Name">The case name.</param>
/// <param name="MaxTurns">The case's turn cap.</param>
/// <param name="Tags">The case's tags (<c>regression</c>, <c>capability</c>), empty when it has none.</param>
/// <param name="Graders">The graders the case defines.</param>
/// <param name="Arms">Each arm's runs, keyed <c>with</c> or <c>without</c>.</param>
public sealed record EvalCase(string Name, int MaxTurns, IReadOnlyList<string> Tags, IReadOnlyList<EvalGraderDefinition> Graders, IReadOnlyDictionary<string, IReadOnlyList<EvalRun>> Arms) {
    /// <summary>Gets the case's reporting group: <c>regression</c>, <c>capability</c> or <c>untagged</c>.</summary>
    public string Group => (Tags.Contains(value: "regression") ? "regression" : (Tags.Contains(value: "capability") ? "capability" : "untagged"));
    /// <summary>Returns the definition of the grader named <paramref name="name"/>, or <see langword="null"/>.</summary>
    /// <param name="name">The grader's name.</param>
    /// <returns>The definition, or <see langword="null"/> when the case defines no such grader.</returns>
    public EvalGraderDefinition? Definition(string name) => Graders.FirstOrDefault(predicate: grader => (grader.Name == name));
}

/// <summary>A <c>claude plugin eval</c> result document (<c>aggregate-result.json</c>, schema version 1), with the
/// tags of its cases.</summary>
/// <param name="Path">The document's path.</param>
/// <param name="Ablation">The suite's ablation mode.</param>
/// <param name="Model">The model under test, as the run named it.</param>
/// <param name="Judge">The judge model, as the run named it.</param>
/// <param name="ClaudeVersion">The Claude Code version that ran the suite.</param>
/// <param name="Partial">Whether the suite stopped before every run started.</param>
/// <param name="CostUsd">The run's list-price cost estimate, in US dollars.</param>
/// <param name="Cases">The cases.</param>
public sealed record EvalSuite(string Path, string Ablation, string Model, string Judge, string ClaudeVersion, bool Partial, double CostUsd, IReadOnlyList<EvalCase> Cases) {
    /// <summary>The name of the manifest <c>puck evals run</c> writes beside each result document.</summary>
    public const string ManifestName = "run.json";

    private static string Text(JsonElement element, string name) =>
        ((element.TryGetProperty(propertyName: name, value: out var value) && (value.ValueKind == JsonValueKind.String)) ? value.GetString()! : string.Empty);
    private static double Number(JsonElement element, string name, double fallback) =>
        ((element.TryGetProperty(propertyName: name, value: out var value) && (value.ValueKind == JsonValueKind.Number)) ? value.GetDouble() : fallback);
    private static bool Flag(JsonElement element, string name) =>
        (element.TryGetProperty(propertyName: name, value: out var value) && (value.ValueKind == JsonValueKind.True));
    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        ((element.TryGetProperty(propertyName: name, value: out var value) && (value.ValueKind == JsonValueKind.Array)) ? value.EnumerateArray() : []);

    /// <summary>Reads a result document and the tags of its cases. Tags come from the case record when the runner
    /// carries them, else from the <see cref="ManifestName"/> manifest beside the document, else from the case's own
    /// <c>prompt.md</c> or <c>case.yaml</c> under the suite root, when that tree still exists.</summary>
    /// <param name="path">The document's path.</param>
    /// <returns>The suite.</returns>
    /// <exception cref="JsonException">The file is not JSON.</exception>
    /// <exception cref="InvalidDataException">The document is not a schema-version-1 result.</exception>
    public static EvalSuite Load(string path) {
        using var document = JsonDocument.Parse(json: File.ReadAllText(path: path));
        var root = document.RootElement;

        if ((root.ValueKind != JsonValueKind.Object) || (Number(element: root, name: "schemaVersion", fallback: 0) != 1) || !root.TryGetProperty(propertyName: "cases", value: out _)) {
            throw new InvalidDataException(message: $"{path} is not a claude plugin eval result (schemaVersion 1 with cases).");
        }
        var suite = (root.TryGetProperty(propertyName: "suite", value: out var suiteElement) ? suiteElement : default);
        var suiteRoot = ((suite.ValueKind == JsonValueKind.Object) ? Text(element: suite, name: "root") : string.Empty);
        var manifestTags = ManifestTags(path: System.IO.Path.Combine(path1: (System.IO.Path.GetDirectoryName(path: System.IO.Path.GetFullPath(path: path)) ?? "."), path2: ManifestName));
        var cases = new List<EvalCase>();

        foreach (var caseElement in Array(element: root, name: "cases")) {
            var name = Text(element: caseElement, name: "name");
            var graders = Array(element: caseElement, name: "graders").Select(selector: static grader => {
                var config = (grader.TryGetProperty(propertyName: "config", value: out var value) ? value : default);
                var configured = (config.ValueKind == JsonValueKind.Object);
                var tool = (configured ? Text(element: config, name: "tool") : string.Empty);
                var text = Text(element: grader, name: "graderMarkdown");

                if ((text.Length == 0) && configured) {
                    text = string.Join(separator: " ", values: new[] { "criteria", "pattern", "match", "target", "focus", "tool", "input_match", "path" }
                        .Select(selector: key => (Text(element: config, name: key) is { Length: > 0 } item ? $"{key}: {item}" : string.Empty))
                        .Where(predicate: static item => (item.Length != 0)));
                }
                var arm = Text(element: grader, name: "arm");

                if ((arm.Length == 0) && configured) { arm = Text(element: config, name: "arm"); }
                return new EvalGraderDefinition(
                    Arm: ((arm.Length == 0) ? null : arm),
                    Name: Text(element: grader, name: "name"),
                    Text: text.Trim(),
                    Tool: ((tool.Length == 0) ? null : tool),
                    Type: Text(element: grader, name: "type"),
                    Weight: Number(element: grader, name: "weight", fallback: 1)
                );
            }).ToList();
            var arms = new Dictionary<string, IReadOnlyList<EvalRun>>(comparer: StringComparer.Ordinal);

            if (caseElement.TryGetProperty(propertyName: "arms", value: out var armsElement) && (armsElement.ValueKind == JsonValueKind.Object)) {
                foreach (var arm in armsElement.EnumerateObject()) {
                    var runs = ((arm.Value.ValueKind == JsonValueKind.Array) ? arm.Value.EnumerateArray().ToList() : [arm.Value]);

                    arms[arm.Name] = [.. runs.Select(selector: static (run, index) => new EvalRun(
                        Aborted: (run.TryGetProperty(propertyName: "aborted", value: out var aborted) && (aborted.ValueKind == JsonValueKind.Object)),
                        Error: (Text(element: run, name: "error") is { Length: > 0 } error ? error : null),
                        Graders: [.. Array(element: run, name: "graders").Select(selector: static grader => new EvalGraderVerdict(
                            Evidence: Text(element: grader, name: "evidence"),
                            Explanation: Text(element: grader, name: "explanation"),
                            Name: Text(element: grader, name: "name"),
                            Passed: (grader.TryGetProperty(propertyName: "passed", value: out var passed) ? passed.ValueKind switch {
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => null,
                            } : null),
                            Votes: [.. Array(element: grader, name: "judgeVotes").Select(selector: static vote => (vote.ValueKind == JsonValueKind.True))],
                            Weight: Number(element: grader, name: "weight", fallback: 1),
                            WithOnly: Flag(element: grader, name: "withOnly")
                        ))],
                        Index: (index + 1),
                        SkippedPaidGraders: Flag(element: run, name: "skippedPaidGraders"),
                        TracePath: (Text(element: run, name: "tracePath") is { Length: > 0 } trace ? trace : null),
                        Turns: (int)Number(element: run, name: "turns", fallback: 0)
                    ))];
                }
            }
            IReadOnlyList<string> tags = [.. Array(element: caseElement, name: "tags").Where(predicate: static tag => (tag.ValueKind == JsonValueKind.String)).Select(selector: static tag => tag.GetString()!)];

            if (tags.Count == 0) {
                tags = (manifestTags.TryGetValue(key: name, value: out var recorded)
                    ? recorded
                    : CaseFiles.Tags(caseDirectory: System.IO.Path.Combine(path1: suiteRoot, path2: Text(element: caseElement, name: "dir"))));
            }
            cases.Add(item: new EvalCase(Arms: arms, Graders: graders, MaxTurns: (int)Number(element: caseElement, name: "maxTurns", fallback: 10), Name: name, Tags: tags));
        }

        return new EvalSuite(
            Ablation: ((suite.ValueKind == JsonValueKind.Object) ? Text(element: suite, name: "ablation") : string.Empty),
            Cases: cases,
            ClaudeVersion: Text(element: root, name: "claudeVersion"),
            CostUsd: Number(element: root, name: "costUsd", fallback: 0),
            Judge: ((suite.ValueKind == JsonValueKind.Object) ? Text(element: suite, name: "judgeModel") : string.Empty),
            Model: ((suite.ValueKind == JsonValueKind.Object) ? Text(element: suite, name: "modelOverride") : string.Empty),
            Partial: Flag(element: root, name: "partial"),
            Path: path
        );
    }
    private static Dictionary<string, IReadOnlyList<string>> ManifestTags(string path) {
        var tags = new Dictionary<string, IReadOnlyList<string>>(comparer: StringComparer.Ordinal);

        if (!File.Exists(path: path)) { return tags; }
        using var document = JsonDocument.Parse(json: File.ReadAllText(path: path));

        if (document.RootElement.TryGetProperty(propertyName: "cases", value: out var cases) && (cases.ValueKind == JsonValueKind.Object)) {
            foreach (var entry in cases.EnumerateObject()) {
                tags[entry.Name] = [.. Array(element: entry.Value, name: "tags").Select(selector: static tag => tag.GetString() ?? string.Empty)];
            }
        }
        return tags;
    }
    /// <summary>Describes how the suite ran, for a report line.</summary>
    /// <returns>The ablation, model, judge and runner version.</returns>
    public string Describe() => string.Create(provider: CultureInfo.InvariantCulture, handler: $"ablation {Ablation}, model {Model}, judge {Judge}, claude {ClaudeVersion}, ${CostUsd:0.00}{(Partial ? ", PARTIAL" : string.Empty)}");
}
