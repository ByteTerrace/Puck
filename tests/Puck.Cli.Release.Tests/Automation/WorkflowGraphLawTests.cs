using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Puck.Cli.Release.Tests.Automation;

/// <summary>
/// The GitHub Actions graph is checked by GitHub only when a run starts, and a broken edge fails that run late or, worse,
/// skips a job silently. These laws read every workflow and composite action the way the runner does and hold the edges a
/// change to the graph can break: a job waits only on jobs its workflow declares and reads only the outputs of jobs it
/// waits on, a local workflow or action it calls exists, every artifact a step downloads has a producer, and a job that
/// runs once per matrix entry uploads under a name that differs per entry.
/// </summary>
public sealed partial class WorkflowGraphLawTests {
    private static readonly string Root = RepositoryPaths.RequireRoot();

    [GeneratedRegex(pattern: @"\$\{\{[^}]*\}\}")]
    private static partial Regex Expression();
    [GeneratedRegex(pattern: @"\bneeds\.([A-Za-z0-9_-]+)\.")]
    private static partial Regex NeedsReference();
    [GeneratedRegex(pattern: @"\$\{\{\s*matrix\.([A-Za-z0-9_-]+)\s*\}\}")]
    private static partial Regex MatrixReference();

    // A step and the matrix of its job, if the job runs once per matrix entry.
    private sealed record Step(string File, string Job, YamlMappingNode Node, YamlMappingNode? Matrix);

    private static IEnumerable<(string File, YamlMappingNode Document)> Documents() {
        var github = Path.Combine(path1: Root, path2: ".github");

        foreach (var path in Directory.EnumerateFiles(path: Path.Combine(path1: github, path2: "workflows"), searchPattern: "*.yml")
            .Concat(second: Directory.EnumerateFiles(path: Path.Combine(path1: github, path2: "actions"), searchOption: SearchOption.AllDirectories, searchPattern: "action.yml"))
            .Order(comparer: StringComparer.Ordinal)) {
            var stream = new YamlStream();

            using (var reader = new StreamReader(path: path)) { stream.Load(input: reader); }
            yield return (Path.GetRelativePath(path: path, relativeTo: Root).Replace(newChar: '/', oldChar: '\\'), ((YamlMappingNode)stream.Documents.Single().RootNode));
        }
    }
    private static YamlNode? Child(YamlMappingNode node, string key) => (node.Children.TryGetValue(key: new YamlScalarNode(value: key), value: out var child) ? child : null);
    private static string? Scalar(YamlMappingNode node, string key) => ((Child(key: key, node: node) as YamlScalarNode)?.Value);
    private static IEnumerable<(string Id, YamlMappingNode Job)> Jobs(YamlMappingNode document) =>
        ((Child(key: "jobs", node: document) as YamlMappingNode)?.Children.Select(selector: entry => (((YamlScalarNode)entry.Key).Value!, ((YamlMappingNode)entry.Value))) ?? []);
    private static IEnumerable<string> Needs(YamlMappingNode job) => (Child(key: "needs", node: job) switch {
        YamlScalarNode single => [single.Value!],
        YamlSequenceNode many => many.Children.Select(selector: need => ((YamlScalarNode)need).Value!),
        _ => [],
    });
    private static IEnumerable<Step> Steps() {
        foreach (var (file, document) in Documents()) {
            foreach (var (id, job) in Jobs(document: document)) {
                var matrix = (((Child(key: "strategy", node: job) as YamlMappingNode) is { } strategy) ? (Child(key: "matrix", node: strategy) as YamlMappingNode) : null);

                foreach (var step in ((Child(key: "steps", node: job) as YamlSequenceNode)?.Children.Cast<YamlMappingNode>() ?? [])) { yield return new Step(File: file, Job: id, Matrix: matrix, Node: step); }
            }
            if ((Child(key: "runs", node: document) as YamlMappingNode) is { } runs) {
                foreach (var step in ((Child(key: "steps", node: runs) as YamlSequenceNode)?.Children.Cast<YamlMappingNode>() ?? [])) { yield return new Step(File: file, Job: "(composite)", Matrix: null, Node: step); }
            }
        }
    }
    private static IEnumerable<string> Scalars(YamlNode node) => node switch {
        YamlScalarNode scalar => [(scalar.Value ?? "")],
        YamlSequenceNode sequence => sequence.Children.SelectMany(selector: Scalars),
        YamlMappingNode mapping => mapping.Children.SelectMany(selector: entry => Scalars(node: entry.Key).Concat(second: Scalars(node: entry.Value))),
        _ => [],
    };
    private static bool Uses(Step step, string action) => (Scalar(node: step.Node, key: "uses")?.StartsWith(comparisonType: StringComparison.Ordinal, value: (action + "@")) ?? false);
    private static string? With(Step step, string key) => (((Child(node: step.Node, key: "with") as YamlMappingNode) is { } with) ? Scalar(key: key, node: with) : null);
    // An artifact name with an expression in it names a family: the expression stands for any nonempty text.
    private static Regex Family(string name) => new(pattern: (("^" + string.Join(separator: ".+", values: Expression().Split(input: name).Select(selector: Regex.Escape))) + "$"));
    // The names one upload step writes: each matrix value its job runs with stands in for the expression that reads it,
    // and a name that is nothing but expressions after that could be any name, so it names none.
    private static IEnumerable<string> UploadNames(Step step) {
        IEnumerable<string> names = [With(key: "name", step: step)!];

        foreach (var key in MatrixReference().Matches(input: names.Single()).Select(selector: match => match.Groups[1].Value).Distinct()) {
            var values = ((step.Matrix is { } matrix)
                ? ((Child(key: key, node: matrix) is YamlSequenceNode axis) ? axis.Children.OfType<YamlScalarNode>().Select(selector: value => value.Value!) : [])
                    .Concat(second: ((Child(key: "include", node: matrix) as YamlSequenceNode)?.Children.OfType<YamlMappingNode>().Select(selector: entry => Scalar(key: key, node: entry)).OfType<string>() ?? []))
                    .ToArray()
                : []);
            var reference = new Regex(pattern: ((@"\$\{\{\s*matrix\." + Regex.Escape(str: key)) + @"\s*\}\}"));

            names = names.SelectMany(selector: name => values.Select(selector: value => reference.Replace(input: name, replacement: value.Replace(newValue: "$$", oldValue: "$")))).ToArray();
        }
        return names.Where(predicate: name => (Expression().Replace(input: name, replacement: "").Length != 0));
    }

    [Fact]
    public void EveryJobWaitsOnlyOnJobsOfItsWorkflowAndReadsOnlyThoseItWaitsOn() {
        foreach (var (file, document) in Documents()) {
            var jobs = Jobs(document: document).ToDictionary(elementSelector: job => job.Job, keySelector: job => job.Id);

            foreach (var (id, job) in jobs) {
                var needs = Needs(job: job).ToHashSet(comparer: StringComparer.Ordinal);

                foreach (var need in needs) { Assert.True(condition: jobs.ContainsKey(key: need), userMessage: $"{file}: job {id} needs {need}, which the workflow does not declare."); }
                foreach (var reference in Scalars(node: job).SelectMany(selector: text => NeedsReference().Matches(input: text)).Select(selector: match => match.Groups[1].Value)) {
                    Assert.True(condition: needs.Contains(item: reference), userMessage: $"{file}: job {id} reads needs.{reference} without waiting on it.");
                }
            }
        }
    }
    [Fact]
    public void EveryLocalWorkflowAndActionACallNamesExists() {
        foreach (var (file, document) in Documents()) {
            var calls = Jobs(document: document).Select(selector: job => Scalar(key: "uses", node: job.Job)).Concat(second: Steps().Where(predicate: step => (step.File == file)).Select(selector: step => Scalar(node: step.Node, key: "uses")));

            foreach (var call in calls.OfType<string>().Where(predicate: call => call.StartsWith(comparisonType: StringComparison.Ordinal, value: "./"))) {
                var path = Path.Combine(path1: Root, path2: call[2..]);

                Assert.True(
                    condition: (call.EndsWith(comparisonType: StringComparison.Ordinal, value: ".yml") ? File.Exists(path: path) : File.Exists(path: Path.Combine(path1: path, path2: "action.yml"))),
                    userMessage: $"{file} calls {call}, which does not exist."
                );
            }
        }
    }
    [Fact]
    public void EveryDownloadedArtifactHasAProducer() {
        var steps = Steps().ToArray();
        var uploads = steps.Where(predicate: step => Uses(action: "actions/upload-artifact", step: step)).SelectMany(selector: UploadNames).ToArray();
        // setup-puck downloads the artifact its caller names.
        var downloads = steps.Where(predicate: step => Uses(action: "actions/download-artifact", step: step)).Select(selector: step => (step, name: (With(key: "name", step: step) ?? With(key: "pattern", step: step))))
            .Concat(second: steps.Where(predicate: step => (Scalar(node: step.Node, key: "uses") == "./.github/actions/setup-puck")).Select(selector: step => (step, name: With(key: "artifact", step: step))))
            .Where(predicate: download => ((download.name is { } name) && !name.StartsWith(comparisonType: StringComparison.Ordinal, value: "${{ inputs.")));

        Assert.NotEmpty(collection: uploads);
        foreach (var (step, name) in downloads) {
            var wanted = ((With(key: "pattern", step: step) is not null) ? new Regex(pattern: (("^" + Regex.Escape(str: name!).Replace(newValue: ".*", oldValue: @"\*")) + "$")) : Family(name: name!));

            Assert.True(
                condition: uploads.Any(predicate: upload => ((upload == name) || Family(name: upload).IsMatch(input: name!) || wanted.IsMatch(input: Expression().Replace(input: upload, replacement: "x")))),
                userMessage: $"{step.File}: job {step.Job} downloads {name}, which no workflow uploads."
            );
        }
    }
    // A composite action runs without the `vars` and `secrets` contexts, so the runner refuses the whole action when it
    // loads one that reads them; a calling job reads them and passes them in as inputs instead.
    [Fact]
    public void ACompositeActionReadsNoContextItCannotSee() {
        foreach (var (file, document) in Documents().Where(predicate: entry => entry.File.StartsWith(comparisonType: StringComparison.Ordinal, value: ".github/actions/"))) {
            foreach (var expression in Scalars(node: document).SelectMany(selector: scalar => Expression().Matches(input: scalar).Select(selector: match => match.Value))) {
                Assert.False(condition: Regex.IsMatch(input: expression, pattern: @"\b(vars|secrets)\."), userMessage: $"{file} reads {expression}, which a composite action cannot see; pass it in as an input.");
            }
        }
    }
    [Fact]
    public void EveryLocalActionCallPassesItsRequiredInputs() {
        var required = Documents()
            .Where(predicate: entry => entry.File.StartsWith(comparisonType: StringComparison.Ordinal, value: ".github/actions/"))
            .ToDictionary(
                elementSelector: entry => (((Child(key: "inputs", node: entry.Document) as YamlMappingNode)?.Children.AsEnumerable() ?? [])
                    .Where(predicate: input => ((input.Value is YamlMappingNode spec) && (Scalar(key: "required", node: spec) == "true") && (Child(key: "default", node: spec) is null)))
                    .Select(selector: input => ((YamlScalarNode)input.Key).Value!)
                    .ToArray()),
                keySelector: entry => ("./" + Path.GetDirectoryName(path: entry.File)!.Replace(newChar: '/', oldChar: '\\'))
            );

        foreach (var step in Steps()) {
            var uses = Scalar(key: "uses", node: step.Node);

            if ((uses is not null) && required.TryGetValue(key: uses, value: out var inputs)) {
                foreach (var input in inputs) {
                    Assert.True(condition: (With(key: input, step: step) is not null), userMessage: $"{step.File}: job {step.Job} calls {uses} without its required input {input}.");
                }
            }
        }
    }
    [Fact]
    public void AMatrixJobUploadsUnderANamePerEntry() {
        foreach (var step in Steps().Where(predicate: step => ((step.Matrix is not null) && Uses(action: "actions/upload-artifact", step: step)))) {
            Assert.Matches(expectedRegexPattern: @"\$\{\{\s*matrix\.", actualString: With(key: "name", step: step)!);
        }
    }
}
