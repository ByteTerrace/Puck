using System.Text.Json;
using Puck.Cli.Affected;
using Puck.Cli.Baselines;
using Puck.Cli.Gate;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="GateRun"/> stops at a failed build, reads a branch's change against its
/// merge base with the target rather than the target's tip, runs the repository checks only in their check forms, and
/// adds GPU work only when asked. Each law runs over a small git checkout whose target advanced after the branch left it,
/// and a runner that records each step in place of building, copying or running anything.</summary>
public sealed partial class GateRunLawTests {
    private sealed class FakeRunner(GateStepResult build) : IGateRunner {
        public bool Copied { get; private set; }

        public Func<string[], int> ExitCode { get; init; } = static _ => 0;

        public string[]? FormatSources { get; private set; }

        public List<string[]> Steps { get; } = [];
        public List<string> Events { get; } = [];
        public List<string[]> Devices { get; } = [];
        public GateClock Clock { get; } = new();
        public bool Admitted { get; init; } = true;

        public Action<string>? Executing { get; init; }

        private void Execute(string name) {
            Events.Add(item: ("run " + name));
            Executing?.Invoke(name);
            Clock.Advance(duration: TimeSpan.FromMilliseconds(milliseconds: 2700));
        }

        public bool WaitForCapacity(string repositoryRoot, string step) {
            Events.Add(item: ("admit " + step));
            return Admitted;
        }
        public GateStepResult Dotnet(string repositoryRoot, IReadOnlyList<string> arguments) {
            if (arguments[0] == "build") { Execute(name: "build"); return build; }
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
    private sealed class Branches : IDisposable {
        public GitScratchCheckout Checkout { get; } = new();
        public string Base { get; }

        public Branches() {
            Checkout.Write(name: "build/Architecture.props", text: "<Project />");
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

    private static (int ExitCode, string Output, string Error) Gate(Branches branches, FakeRunner runner, TemporaryDirectory directory, bool gpu = false, string target = "main", bool record = false) => ConsoleCapture.RunSplit(run: () => GateRun.Run(
        directory: directory.RootPath,
        gpu: gpu,
        record: record,
        clock: runner.Clock,
        repositoryRoot: branches.Checkout.Root,
        runner: runner,
        target: target
    ));

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
        Assert.Equal(actual: runner.Steps[0], expected: ["affected", "--merge-base", branches.Base, "--run"]);
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
                ["branding", "--check"],
                ["formats", "--check"],
                ["canary-ceilings", "--check"],
                ["derivations", "--check"],
            }
        );
        // The run keeps its log and step summary: the CLI copy and the file list go when it ends.
        Assert.Equal(actual: Directory.EnumerateFileSystemEntries(path: directory.RootPath).Select(selector: static entry => Path.GetFileName(path: entry)), expected: ["gate.log", "gate.steps"]);
    }
    [Fact]
    public void AFailedStepFailsTheGateWhileTheRestStillRun() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty)) { ExitCode = static arguments => ((arguments[0] == "lengths") ? 1 : 0) };

        var (exitCode, output, _) = Gate(branches: branches, directory: directory, runner: runner);

        Assert.Equal(actual: exitCode, expected: CliExit.Failed);
        Assert.Equal(actual: runner.Steps.Count, expected: 14);
        Assert.Contains(actualString: output, expectedSubstring: "gate: lengths FAILED (exit 1, ");
        Assert.Contains(actualString: output, expectedSubstring: "gate: FAILED: lengths; full output in ");
        Assert.Contains(expectedSubstring: "===== lengths (exit 1)\noutput of lengths", actualString: File.ReadAllText(path: directory.PathOf(name: "gate.log")).ReplaceLineEndings(replacementText: "\n"));
    }
    [Fact]
    public void GpuWorkRunsOnlyWhenAskedFor() {
        using var branches = new Branches();

        branches.Checkout.Write(name: "tests/Puck.World.Canaries/example/positive.script.txt", text: "wire.errors\n\n");

        foreach (var gpu in ((bool[])[false, true])) {
            using var directory = new TemporaryDirectory(prefix: "puck-gate-law-");
            var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty));

            _ = Gate(branches: branches, directory: directory, gpu: gpu, runner: runner);

            Assert.DoesNotContain(collection: runner.Steps[0], expected: "--gpu");
            Assert.Equal(actual: runner.Steps.Count(predicate: static step => (step[0] == "canary")), expected: (gpu ? 1 : 0));
            Assert.Equal(actual: runner.Steps.Count(predicate: static step => (step[0] == "parity")), expected: (gpu ? 1 : 0));
        }

        Assert.Equal(actual: ConsoleCapture.RunSplit(run: static () => PuckRootCommand.Invoke(args: ["affected", "--gpu"])).ExitCode, expected: CliExit.Refused);
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
