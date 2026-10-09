using System.Text.Json;
using Puck.Cli.Affected;
using Puck.Cli.Baselines;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Gate.Tests;

/// <summary>What every gate law runs over: a small git checkout whose target advanced after the branch left it
/// (<see cref="Branches"/>), and a runner that records each step in place of building, copying or running anything.
/// Each file of gate laws is its own test class over these, so xUnit runs the files side by side.</summary>
public abstract class GateRunLaws {
    private protected sealed class FakeRunner(GateStepResult build) : IGateRunner {
        public bool Copied { get; private set; }

        public Func<string[], int> ExitCode { get; init; } = static _ => 0;

        public string[]? FormatSources { get; private set; }

        public List<string[]> Steps { get; } = [];
        public List<string> Events { get; } = [];
        public List<(string Step, bool Device, bool HeavySuite)> Admissions { get; } = [];
        public List<string[]> Devices { get; } = [];
        public GateClock Clock { get; } = new();
        public bool Admitted { get; init; } = true;

        public Action<string>? Executing { get; init; }

        private void Execute(string name) {
            Events.Add(item: ("run " + name));
            Executing?.Invoke(name);
            Clock.Advance(duration: TimeSpan.FromMilliseconds(milliseconds: 2700));
        }

        public bool WaitForCapacity(string repositoryRoot, string step, bool device, bool heavySuite) {
            Events.Add(item: ("admit " + step));
            Admissions.Add(item: (step, device, heavySuite));
            return Admitted;
        }

        /// <summary>Every restore the gate ran, in order.</summary>
        public List<string[]> Restores { get; } = [];
        /// <summary>Every <c>dotnet build</c> the gate ran, in order.</summary>
        public List<string[]> Builds { get; } = [];

        /// <summary>Runs a restore in place of <c>dotnet</c>; by default it exits as <see cref="ExitCode"/> says for
        /// <c>locks --check</c>, the step a restore belongs to.</summary>
        public Func<string, IReadOnlyList<string>, GateStepResult>? Restore { get; init; }

        public GateStepResult Dotnet(string repositoryRoot, IReadOnlyList<string> arguments) {
            if (arguments[0] == "restore") {
                Restores.Add(item: [.. arguments]);
                Execute(name: "locks");
                return ((Restore is null) ? new GateStepResult(ExitCode: ExitCode(["locks", "--check"]), Output: "restore output") : Restore(repositoryRoot, arguments));
            }
            if (arguments[0] == "build") {
                Builds.Add(item: [.. arguments]);
                // The one file app compiles on its own, as an ordinary step.
                if (arguments[1].EndsWith(comparisonType: StringComparison.Ordinal, value: ".cs")) {
                    Execute(name: "bootstrap");
                    return new GateStepResult(ExitCode: ExitCode([.. arguments]), Output: "bootstrap output");
                }
                Execute(name: "build");
                return build;
            }
            Devices.Add(item: [.. arguments]);
            Execute(name: Path.GetFileNameWithoutExtension(path: arguments[(arguments.ToList().IndexOf(item: "--project") + 1)]));
            return new GateStepResult(ExitCode: ExitCode([.. arguments]), Output: "device output");
        }
        public string CopyCli(string repositoryRoot, string directory) {
            Execute(name: "copy CLI");
            Copied = true;

            return Path.Combine(path1: directory, path2: "Puck.Cli.dll");
        }
        public GateStepResult Puck(string cli, string repositoryRoot, IReadOnlyList<string> arguments, Action<string>? progress = null) {
            var name = (((arguments[0] == "docs") || (arguments[0] == "shaders") || (arguments[0] == "baselines")) ? string.Join(separator: ' ', values: arguments.Take(count: 2)) : arguments[0]);

            if (arguments[0] == "canary") { name = "affected canaries"; }
            if ((name == "shaders interface") && arguments.Contains(value: "--echo-fixtures")) { name += " echo"; }

            if (arguments.Contains(value: "--record")) { name += " record"; }
            if (arguments[0] == "counters") { name += (" " + Path.GetFileName(path: arguments[3])[..^".world.json".Length]); }
            Execute(name: name);
            Steps.Add(item: [.. arguments]);

            if (arguments[0] == "format") {
                FormatSources = JsonSerializer.Deserialize<string[]>(json: File.ReadAllText(path: arguments[^1]));
            }

            // A canary run reports each canary's verdict as it lands; the progress sink sees each line as written.
            string[] lines = ((arguments[0] == "canary")
                ? [.. arguments.Skip(count: 1).Select(selector: static id => $"PASS: canary {id} held")]
                : [$"output of {arguments[0]}"]);

            foreach (var line in lines) { progress?.Invoke(obj: line); }
            var output = string.Join(separator: Environment.NewLine, values: lines);

            return new GateStepResult(ExitCode: ExitCode(arg: [.. arguments]), Output: output);
        }
    }
    // main: A; feature leaves at A and commits B (src/Branch.cs); main then commits C (src/Target.cs). HEAD is feature.
    private protected sealed class Branches : IDisposable {
        public GitScratchCheckout Checkout { get; } = new();
        public string Base { get; }

        public Branches() {
            Checkout.Write(name: "build/Architecture.props", text: "<Project />");
            // The solution the locked restore reads; it lists no project unless a law adds one.
            Checkout.Write(name: "Puck.slnx", text: "<Solution />\n");
            foreach (var artifact in BaselinesCommand.Artifacts) {
                Checkout.Write(name: $"tests/{artifact.Project}/{artifact.Project}.csproj", text: "<Project />");
            }
            Checkout.Write(name: "tests/Puck.World.Canaries/example/world.json", text: "{}");
            Checkout.Write(name: "tests/Puck.World.Canaries/example/positive.script.txt", text: "wire.errors\n");
            Checkout.Write(name: "tests/Puck.World.Canaries/example/discriminating.script.txt", text: "wire.errors\n");
            Checkout.Write(name: "tests/Puck.World.Canaries/example/canary.json", text: """
                {
                  "id": "example", "title": "gate selection", "binding": "gate selection",
                  "bootShape": "windowed", "requirements": ["gpu"], "timeoutSeconds": 10,
                  "positive": {
                    "world": "tests/Puck.World.Canaries/example/world.json", "script": "positive.script.txt",
                    "commands": [{ "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" }],
                    "expect": [{ "type": "line", "name": "clean", "stream": "stdout", "match": "contains", "text": "clean", "present": true }]
                  },
                  "discriminating": {
                    "world": "tests/Puck.World.Canaries/example/world.json", "script": "discriminating.script.txt",
                    "commands": [{ "verb": "wire.errors", "occurrence": 1, "outcome": "accepted" }],
                    "expect": [{ "type": "line", "name": "clean", "stream": "stdout", "match": "contains", "text": "clean", "present": true }]
                  }
                }
                """);
            Assert.True(condition: Puck.Cli.Canary.CanaryManifestLoader.TryLoadAll(repositoryRoot: Checkout.Root, strict: true,
                manifests: out _, refused: out _, error: out var manifestError), userMessage: manifestError);
            Checkout.Write(name: "src/Shared.cs", text: "shared\n");
            Checkout.Write(name: "docs/guide.md", text: "# Guide\n");
            Base = Checkout.Commit(message: "a");
            _ = Checkout.Git("switch", "--quiet", "--create", "feature");
            Checkout.Write(name: "src/Branch.cs", text: "branch\n");
            Checkout.Write(name: "src/Branch.puck", text: "world branch {}\n");
            Checkout.Write(name: "docs/branch.md", text: "# Branch\n");
            _ = Checkout.Commit(message: "b");
            _ = Checkout.Git("switch", "--quiet", "main");
            Checkout.Write(name: "src/Target.cs", text: "target\n");
            _ = Checkout.Commit(message: "c");
            _ = Checkout.Git("switch", "--quiet", "feature");
        }

        public void Dispose() => Checkout.Dispose();
    }

    // The change a run reads, planned once for laws that execute it under many runners.
    private protected static GateChange Plan(Branches branches, string target = "main") {
        Assert.True(condition: GateRun.TryPlan(change: out var change, error: out var error, refused: out _, repositoryRoot: branches.Checkout.Root, schemaSourceTypes: SuiteRoot.Composition.SchemaSourceTypes, target: target), userMessage: error);
        return change;
    }
    private protected static (int ExitCode, string Output, string Error) Execute(Branches branches, GateChange change, FakeRunner runner, TemporaryDirectory directory, bool gpu = false, bool record = false) => ConsoleCapture.RunSplit(run: () => GateRun.Execute(
        change: change,
        clock: runner.Clock,
        directory: directory.RootPath,
        gpu: gpu,
        gpuJobs: 3,
        record: record,
        repositoryRoot: branches.Checkout.Root,
        runner: runner,
        suiteJobs: 2
    ));
    private protected static (int ExitCode, string Output, string Error) Gate(Branches branches, FakeRunner runner, TemporaryDirectory directory, bool gpu = false, string target = "main", bool record = false) => ConsoleCapture.RunSplit(run: () => GateRun.Run(
        directory: directory.RootPath,
        schemaSourceTypes: SuiteRoot.Composition.SchemaSourceTypes,
        gpu: gpu,
        record: record,
        clock: runner.Clock,
        gpuJobs: 3,
        repositoryRoot: branches.Checkout.Root,
        runner: runner,
        suiteJobs: 2,
        target: target
    ));
}
/// <summary>CONTRACT UNDER TEST: <see cref="GateRun"/> stops at a failed build, reads a branch's change against its
/// merge base with the target rather than the target's tip, runs the repository checks only in their check forms, and
/// adds GPU work only when asked.</summary>
public sealed partial class GateRunLawTests : GateRunLaws {
    [Fact]
    public void AFailedBuildStopsTheGateAndShowsItsErrors() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 1, Output: "restore ok\nsrc/Branch.cs(1,1): error CS1002: ; expected\nBuild FAILED."));

        var (exitCode, output, _) = Gate(branches: branches, directory: directory, runner: runner);

        Assert.Equal(actual: exitCode, expected: CliExit.Failed);
        Assert.Contains(actualString: output, expectedSubstring: "gate: build FAILED (exit 1, ");
        Assert.Contains(actualString: output, expectedSubstring: "s); nothing else ran.");
        Assert.Contains(actualString: output, expectedSubstring: "  src/Branch.cs(1,1): error CS1002: ; expected");
        Assert.DoesNotContain(actualString: output, expectedSubstring: "restore ok");
        Assert.False(condition: runner.Copied);
        Assert.Empty(collection: runner.Steps);
        Assert.Contains(expectedSubstring: "restore ok", actualString: File.ReadAllText(path: directory.PathOf(name: "gate.log")));
    }
    [Fact]
    public void TheChangeIsReadAgainstTheMergeBaseAndNotTheTargetsTip() {
        using var branches = new Branches();
        var root = branches.Checkout.Root;

        Assert.True(condition: AffectedCommand.TryResolveBase(error: out var error, mergeBase: "main", repositoryRoot: root, resolved: out var resolved, since: null), userMessage: error);
        Assert.Equal(actual: resolved, expected: branches.Base);
        Assert.True(condition: AffectedCommand.TryReadChanged(changed: out var againstBase, deleted: out _, error: out error, repositoryRoot: root, since: resolved), userMessage: error);
        Assert.Equal(actual: againstBase, expected: ["docs/branch.md", "src/Branch.cs", "src/Branch.puck"]);
        // The control: the target's tip counts the commit the target gained after the branch left it.
        Assert.True(condition: AffectedCommand.TryReadChanged(changed: out var againstTip, deleted: out _, error: out error, repositoryRoot: root, since: "main"), userMessage: error);
        Assert.Contains(collection: againstTip, expected: "src/Target.cs");

        using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty));

        var (exitCode, output, _) = Gate(branches: branches, directory: directory, runner: runner);

        Assert.Equal(actual: exitCode, expected: CliExit.Success);
        Assert.Equal(actual: runner.Steps[0], expected: ["affected", "--merge-base", branches.Base, "--run", "--suite-jobs", "2"]);
        Assert.Equal(actual: runner.FormatSources, expected: ["src/Branch.cs", "src/Branch.puck"]);
        Assert.Contains(expectedSubstring: $"gate: 3 changed file(s) against {branches.Base[..12]}, the merge base of HEAD and main;", actualString: output);
    }
    [Fact]
    public void EveryRepositoryCheckRunsInItsCheckFormOnly() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty));

        var (exitCode, _, _) = Gate(branches: branches, directory: directory, runner: runner);
        var fileList = directory.PathOf(name: "format-sources.json");

        Assert.Equal(actual: exitCode, expected: CliExit.Success);
        Assert.Equal(
            actual: runner.Steps.Skip(count: 1),
            expected: new[] {
                new[] { "format", "--check", "--file-list", fileList },
                ["lengths", "--check"],
                ["comment-smells", "--check"],
                ["docs", "links"],
                ["schema", "--check"],
                ["architecture", "--check"],
                ["registry", "--check"],
                ["vocabulary", "--check"],
                ["shaders", "generate", "--check"],
                ["shaders", "interface", "--echo-fixtures", "--check", "tests/Puck.World.Canaries/interface-echo"],
                ["branding", "--check"],
                ["formats", "--check"],
                ["canary-ceilings", "--check"],
                ["derivations", "--check"],
            }
        );
        // The run keeps its log and step summary: the CLI copy and the file list go when it ends.
        Assert.Equal(actual: Directory.EnumerateFileSystemEntries(path: directory.RootPath).Select(selector: static entry => Path.GetFileName(path: entry)).Order(comparer: StringComparer.Ordinal), expected: ["gate.log", "gate.steps"]);
    }
    [Fact]
    public void AFailedStepFailsTheGateWhileTheRestStillRun() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty)) { ExitCode = static arguments => ((arguments[0] == "lengths") ? 1 : 0) };

        var (exitCode, output, _) = Gate(branches: branches, directory: directory, runner: runner);

        Assert.Equal(actual: exitCode, expected: CliExit.Failed);
        Assert.Equal(actual: runner.Steps.Count, expected: 15);
        Assert.Contains(actualString: output, expectedSubstring: "gate: lengths FAILED (exit 1, ");
        Assert.Contains(actualString: output, expectedSubstring: "gate: FAILED: lengths; full output in ");
        Assert.Contains(expectedSubstring: "===== lengths (exit 1)\noutput of lengths", actualString: File.ReadAllText(path: directory.PathOf(name: "gate.log")).ReplaceLineEndings(replacementText: "\n"));
    }
    [Fact]
    public void TheBootstrapFileAppCompilesAsAnOrdinaryStepWithoutABuildServer() {
        const string Bootstrap = "src/Puck.Azure.Resources/bootstrap.cs";
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty)) { ExitCode = static arguments => (arguments.Contains(value: Bootstrap) ? 1 : 0) };

        var (exitCode, output, _) = Gate(branches: branches, directory: directory, runner: runner);

        Assert.Equal(actual: exitCode, expected: CliExit.Failed);
        Assert.Contains(actualString: output, expectedSubstring: "gate: FAILED: bootstrap; full output in ");
        var compile = runner.Builds.Single(predicate: static build => (build[1] == Bootstrap));

        // A file-based app's build reads -nodeReuse:false as its project; the SDK switch leaves no server instead.
        Assert.Equal(actual: compile[..5], expected: ["build", Bootstrap, "-c", "Release", "--disable-build-servers"]);
        Assert.DoesNotContain(collection: compile, expected: CliOptions.NoNodeReuse);
        // An ordinary step: the checks after it still run.
        Assert.Contains(collection: runner.Steps, filter: static step => (step[0] == "derivations"));
    }
    [Fact]
    public void GpuWorkRunsOnlyWhenAskedFor() {
        using var branches = new Branches();

        branches.Checkout.Write(name: "tests/Puck.World.Canaries/example/positive.script.txt", text: "wire.errors\n\n");
        // The change is the same with and without the GPU, so it is read once and executed both ways.
        var change = Plan(branches: branches);

        foreach (var gpu in ((bool[])[false, true])) {
            using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
            var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty));

            _ = Execute(branches: branches, change: change, directory: directory, gpu: gpu, runner: runner);

            Assert.DoesNotContain(collection: runner.Steps[0], expected: "--gpu");
            Assert.Equal(actual: runner.Steps.Count(predicate: static step => (step[0] == "canary")), expected: (gpu ? 1 : 0));
            Assert.Equal(actual: runner.Steps.Count(predicate: static step => (step[0] == "parity")), expected: (gpu ? 1 : 0));
            // Each bound travels with the step it bounds.
            Assert.Equal(actual: runner.Steps[0][^2..], expected: ["--suite-jobs", "2"]);
            if (gpu) {
                Assert.Equal(actual: runner.Steps.Single(predicate: static step => (step[0] == "canary"))[1..3], expected: ["--gpu-jobs", "3"]);
            }
        }

        Assert.Equal(actual: ConsoleCapture.RunSplit(run: static () => SuiteRoot.Invoke(args: ["affected", "--gpu"])).ExitCode, expected: CliExit.Refused);
    }
    [Fact]
    public void ATargetWithNoMergeBaseIsRefusedBeforeTheBuild() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty));

        var (exitCode, _, error) = Gate(branches: branches, directory: directory, runner: runner, target: "no-such-branch");

        Assert.Equal(actual: exitCode, expected: CliExit.Refused);
        Assert.Contains(actualString: error, expectedSubstring: "no-such-branch");
        Assert.False(condition: runner.Copied);
        Assert.Empty(collection: runner.Steps);
    }
}
