using System.CommandLine.Parsing;
using Puck.Cli.Affected;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: the <c>puck canary --list</c> line <c>puck affected</c> prints for a prose-only manifest edit
/// is exactly the argument vector its run dispatches through the composed tool's canary verb, whose refusal it
/// propagates (<c>AffectedManifestEditsLawTests</c> holds the selection itself).</summary>
public sealed class AffectedManifestCheckLawTests {
    private const string Code = "class Value { int Read() => 1; }";
    private const string Json = """{"id":"example","title":"old","binding":"old","bootShape":"headless","requirements":[],"timeoutSeconds":10,"positive":{"expect":[{"name":"old","text":"old"}]},"discriminating":{}}""";
    private const string Manifest = "tests/Puck.World.Canaries/example/canary.json";
    private const string Source = "src/World/Value.cs";

    private static AffectedPlan Select(GitScratchCheckout checkout, string since, params string[] changed) {
        using var before = new AffectedRevisionTree(documentTrees: AffectedRevisionExport.DocumentTrees, root: checkout.Root, revision: since);
        var after = new AffectedWorkingTree(root: checkout.Root);

        return AffectedSelection.Select(
            changed: changed,
            projects: [new(Directory: "src/World", IsSuite: false, Name: "World", References: []), new(Directory: "tests/World.Tests", IsSuite: true, Name: "World.Tests", References: ["World"])],
            canaries: [new(Directory: "tests/Puck.World.Canaries/example", Files: [], Id: "example", RequiresGpu: true)],
            coverage: new Dictionary<string, IReadOnlySet<string>> { [Source] = new HashSet<string>(collection: ["example"]), [Manifest] = new HashSet<string>(collection: ["example"]) },
            consumersOf: _ => ["World"], worldClosure: new HashSet<string>(collection: ["World"]), declaresTests: _ => false,
            catalogInputs: (_, _) => true, standInsFor: _ => [], canariesReaching: _ => new HashSet<string>(collection: ["example"]), worldInput: _ => true,
            triviaOnly: path => AffectedCSharpTrivia.IsUnchanged(after: after, before: before, path: path),
            proseOnly: path => AffectedManifestProse.IsUnchanged(after: after, before: before, path: path));
    }

    [Fact]
    public void ThePrintedManifestCheckIsTheExecutedArgvAndPropagatesRefusal() {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: Manifest, text: Json);
        var since = checkout.Commit(message: "manifest");

        checkout.Write(name: Manifest, text: Json.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"binding\":null", oldValue: "\"binding\":\"old\""));
        var plan = Select(checkout: checkout, since: since, Manifest);
        using var output = new StringWriter();

        AffectedCommand.Describe(into: output, plan: plan);
        Assert.Contains(expectedSubstring: "canary-check example", actualString: output.ToString());
        var command = output.ToString().Split('\n').Single(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "puck ")).Trim();
        var printed = CommandLineParser.SplitCommandLine(commandLine: command[5..]).ToArray();

        Assert.Equal(actual: printed, expected: ["canary", "--list", "example"]);
        var root = PuckRootCommand.Create(clock: TimeProvider.System);
        var calls = new List<string[]>();

        root.Subcommands.Single(predicate: command => (command.Name == "canary")).SetAction(action: parsed => {
            calls.Add(item: [.. parsed.Tokens.Select(selector: token => token.Value)]);
            return CliExit.Refused;
        });

        Assert.Equal(expected: CliExit.Refused, actual: AffectedCommand.CheckCanaries(ids: plan.CanaryChecks, root: root));
        Assert.Equal(expected: printed, actual: Assert.Single(collection: calls));
        Assert.Equal(expected: CliExit.Success, actual: AffectedCommand.CheckCanaries(ids: [], root: root));
        Assert.Single(collection: calls);
    }
}
