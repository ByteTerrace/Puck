using System.CommandLine;
using Puck.Cli.Laws;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed partial class LawProofLawTests {
    private sealed class SelectionRunner(Func<string, string, LawRun> report) : ILawRunner {
        public List<string> Builds { get; } = [];
        public List<(string Law, string Directory)> Runs { get; } = [];

        public LawBuild Build(string tree, string project, string logDirectory, CancellationToken cancellationToken) {
            Assert.Equal(actual: project, expected: Project);
            Builds.Add(item: tree);
            return new LawBuild(Succeeded: true, Errors: []);
        }
        public LawRun Run(string tree, string project, string law, string results, CancellationToken cancellationToken) {
            Assert.Equal(actual: project, expected: Project);
            Runs.Add(item: (law, results));
            return report(File.ReadAllText(path: Path.Combine(path1: tree, path2: FixPath)), law);
        }
    }

    [Fact]
    public void IndependentSelectorsShareBuildsAndRequireEverySelectorsOwnEvidence() {
        const string SECOND = "OtherLawTests.Holds";
        var command = LawsCommand.Create();
        var root = new RootCommand { command };
        var parsed = root.Parse(args: ["laws", "prove", Law, "--also-law", SECOND, "--also-law", "ThirdLawTests.Holds"]);

        Assert.Empty(collection: parsed.Errors);
        var option = Assert.IsType<Option<string[]>>(@object: Assert.Single(collection: Assert.Single(collection: command.Subcommands).Options,
            predicate: static option => (option.Name == "--also-law")));

        Assert.Equal(expected: new[] { SECOND, "ThirdLawTests.Holds" }, actual: parsed.GetValue(option: option));
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-selection-");

        checkout.Write(name: FixPath, text: "fixed");
        checkout.Write(name: "tests/Lib.Tests/OtherLawTests.cs", text: "public sealed class OtherLawTests { }\n");
        var fix = checkout.Commit(message: "lib: repair both independent laws");

        static LawRun Report(string name, bool failed, string? error = null) => new(
            Tests: [name], Error: error,
            Failures: (failed ? [new LawFailure(Message: "independent broken behavior", Test: name)] : [])
        );
        (int Exit, string Output, string Error, SelectionRunner Runner) Run(string scenario, IReadOnlyList<string>? also = null) {
            var runner = new SelectionRunner(report: (content, selector) => {
                var restored = (content == "fixed");
                var second = (selector == SECOND);
                var name = $"Lib.Tests.{selector}";

                if (second) {
                    if (scenario == "empty") { return new LawRun(Error: null, Failures: [], Tests: []); }
                    if (scenario == "skipped") { return Report(error: "every selected test must execute", failed: false, name: name); }
                    if ((scenario == "changed") && restored) { name += "Different"; }
                    if (scenario == "overlap") { name = $"Lib.Tests.{Law}"; }
                }
                return Report(name, ((!restored && !(second && (scenario == "secondary passes"))) ||
                    (restored && second && (scenario == "restored fails"))));
            });
            var captured = ConsoleCapture.RunSplit(run: () => LawProof.Prove(
                repositoryRoot: checkout.Root, law: Law, alsoLaws: (also ?? [SECOND]), project: null,
                fix: new LawFix(Paths: [FixPath], Revision: fix), runner: runner,
                scratchRoot: scratch.RootPath, lawTreesRoot: LawTreesRoot(checkout: checkout),
                cancellationToken: TestContext.Current.CancellationToken
            ));

            AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
            return (captured.ExitCode, captured.Output, captured.Error, runner);
        }

        // A primary red cannot establish that an independent secondary law can fail.
        var secondaryPasses = Run(scenario: "secondary passes");

        Assert.Equal(actual: secondaryPasses.Exit, expected: CliExit.Failed);
        Assert.Contains(actualString: secondaryPasses.Error, expectedSubstring: SECOND);
        Assert.Contains(actualString: secondaryPasses.Error, expectedSubstring: "cannot fail");
        Assert.Single(collection: secondaryPasses.Runner.Builds);

        var proven = Run(scenario: "both fail");

        Assert.Equal(actual: proven.Exit, expected: CliExit.Success);
        Assert.Equal(expected: 2, actual: proven.Runner.Builds.Count);
        Assert.Equal(expected: new[] { Law, SECOND, Law, SECOND }, actual: proven.Runner.Runs.Select(selector: static run => run.Law));
        Assert.Equal(expected: 4, actual: proven.Runner.Runs.Select(selector: static run => run.Directory).Distinct(comparer: StringComparer.Ordinal).Count());
        Assert.Contains(actualString: proven.Output, expectedSubstring: $"Selector: {Law}");
        Assert.Contains(actualString: proven.Output, expectedSubstring: $"Selector: {SECOND}");

        foreach (var scenario in new[] { "empty", "skipped", "changed", "overlap", "restored fails" }) {
            var refused = Run(scenario: scenario);

            Assert.Equal(actual: refused.Exit, expected: ((scenario == "restored fails") ? CliExit.Failed : CliExit.Refused));
            Assert.Contains(actualString: refused.Error, expectedSubstring: SECOND);
            Assert.InRange(actual: refused.Runner.Builds.Count, low: 1, high: 2);
        }
        foreach (var invalid in new[] { Law, "OtherLawTests.*" }) {
            var refused = Run(also: [invalid], scenario: "both fail");

            Assert.Equal(actual: refused.Exit, expected: CliExit.Refused);
            Assert.Empty(collection: refused.Runner.Builds);
        }

        checkout.Write(name: "tests/Other.Tests/Other.Tests.csproj", text: "<Project />\n");
        checkout.Write(name: "tests/Other.Tests/ForeignLawTests.cs", text: "public sealed class ForeignLawTests { }\n");
        _ = checkout.Commit(message: "lib: declare an independent foreign project");
        var mismatch = Run(also: ["ForeignLawTests.Holds"], scenario: "both fail");

        Assert.Equal(actual: mismatch.Exit, expected: CliExit.Refused);
        Assert.Empty(collection: mismatch.Runner.Builds);
        Assert.Contains(actualString: mismatch.Error, expectedSubstring: "must share one project");
    }
}
