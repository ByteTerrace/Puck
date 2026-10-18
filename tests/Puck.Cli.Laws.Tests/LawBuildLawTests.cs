using Microsoft.Build.Framework;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Laws.Tests;

/// <summary>Proof build counts distinguish compiler work from skipped targets, and a proof's real builds compile the
/// withheld source, then only the project its fix changes, and run the changed source's binary. The one real-build
/// fixture has two tiny projects, no packages and no network sources. The persistent tree's reuse across proofs, its
/// kept outputs and its untouched timestamps, is held over a fake runner by <see cref="LawProofTreeLeaseLawTests"/>.</summary>
public sealed class LawBuildLawTests {
    private sealed class BinaryRunner : ILawRunner {
        public List<LawBuildCounts> Builds { get; } = [];

        public LawBuild Build(string tree, string project, string logDirectory, CancellationToken cancellationToken) {
            var result = new DotnetLawRunner().Build(cancellationToken: cancellationToken, logDirectory: logDirectory, project: project, tree: tree);

            Builds.Add(item: result.Counts);
            return result;
        }
        public LawRun Run(string tree, string project, string law, string results, CancellationToken cancellationToken) {
            var run = CliProcess.RunCaptured(fileName: "dotnet", arguments: [Path.Combine(path1: tree, path2: "Application/bin/Release/net11.0/Application.dll")], input: string.Empty, timeout: TimeSpan.FromMinutes(value: 1), workingDirectory: tree, cancellationToken: cancellationToken);
            var value = run.Stdout.Trim();

            return new LawRun(Tests: [law], Failures: ((value == "1") ? [new LawFailure(Message: "the binary still returns the withheld value", Test: law)] : []), Error: (((run.ExitCode != 0) || run.TimedOut || (value is not ("1" or "2"))) ? "the binary did not return a law verdict" : null));
        }
    }

    [Fact]
    public void AProofCompilesTheWithheldSourceThenOnlyTheProjectItsFixChanges() {
        using var checkout = new GitScratchCheckout();
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-build-law-");

        checkout.Write(name: ".gitignore", text: "bin/\nobj/\n");
        checkout.Write(name: "global.json", text: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "global.json")));
        checkout.Write(name: "Directory.Build.props", text: "<Project />\n");
        checkout.Write(name: "Directory.Build.targets", text: "<Project />\n");
        checkout.Write(name: "NuGet.Config", text: "<configuration><packageSources><clear /></packageSources></configuration>\n");
        checkout.Write(name: "Library/Library.csproj", text: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>\n");
        checkout.Write(name: "Library/Value.cs", text: "public static class Value { public static int Read() => 1; }\n");
        checkout.Write(name: "Application/Application.csproj", text: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><ProjectReference Include=\"../Library/Library.csproj\" /></ItemGroup></Project>\n");
        checkout.Write(name: "Application/Program.cs", text: "System.Console.WriteLine(Value.Read());\n");
        // An unrelated, unbuildable project proves that only the selected project's closure is built.
        checkout.Write(name: "Unrelated/Unrelated.csproj", text: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>\n");
        checkout.Write(name: "Unrelated/Failure.cs", text: "#error OUTSIDE_SELECTED_CLOSURE\n");
        _ = checkout.Commit(message: "initial");
        checkout.Write(name: "Library/Value.cs", text: "public static class Value { public static int Read() => 2; }\n");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new BinaryRunner();
        var result = ConsoleCapture.RunSplit(run: () => LawProof.Prove(repositoryRoot: checkout.Root, law: "ValueLaw.Holds", project: "Application/Application.csproj", fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratchRoot: scratch.RootPath, lawTreesRoot: Path.Combine(path1: Path.GetDirectoryName(path: checkout.Root)!, path2: "law-trees"), cancellationToken: TestContext.Current.CancellationToken));

        // The red leg fails on the withheld value and the green leg passes on the fixed one, so each ran its own binary.
        Assert.True(condition: (result.ExitCode == CliExit.Success), userMessage: (result.Error + result.Output));
        // The withheld build compiles both projects cold; restoring the fix compiles the library alone, and the
        // application, whose reference assembly did not change, is up to date.
        Assert.Equal(expected: [2, 1], actual: runner.Builds.Select(selector: static build => build.Compiled));
        Assert.Equal(expected: [0, 1], actual: runner.Builds.Select(selector: static build => build.UpToDate));
        Assert.Empty(collection: Directory.EnumerateFileSystemEntries(path: scratch.RootPath));
        Assert.Equal(expected: string.Empty, actual: checkout.Git("status", "--porcelain"));
    }
    [Fact]
    public void CountsExcludeSkippedTargetsAndCountEachCompiledProjectOnce() {
        var logger = new LawBuildLogger();
        var executed = new BuildEventContext(nodeId: 1, projectContextId: 1, targetId: 1, taskId: -1);
        var skipped = new BuildEventContext(nodeId: 1, projectContextId: 1, targetId: 2, taskId: -1);

        logger.Observe(args: new TargetStartedEventArgs(helpKeyword: "", message: "", projectFile: "Library.csproj", targetFile: "build.targets", targetName: "CoreCompile") { BuildEventContext = executed });
        logger.Observe(args: new TargetStartedEventArgs(helpKeyword: "", message: "", projectFile: "Application.csproj", targetFile: "build.targets", targetName: "CoreCompile") { BuildEventContext = skipped });
        logger.Observe(args: new TargetSkippedEventArgs { BuildEventContext = skipped, ProjectFile = "Application.csproj", SkipReason = TargetSkipReason.OutputsUpToDate, TargetName = "CoreCompile" });
        logger.Observe(args: new TargetSkippedEventArgs { ProjectFile = "Condition.csproj", SkipReason = TargetSkipReason.ConditionWasFalse, TargetName = "CoreCompile" });
        logger.Observe(args: new TargetSkippedEventArgs { ProjectFile = "Previous.csproj", SkipReason = TargetSkipReason.PreviouslyBuiltSuccessfully, TargetName = "CoreCompile" });
        logger.Observe(args: new TaskStartedEventArgs(helpKeyword: "", message: "", projectFile: "Library.csproj", taskFile: "build.targets", taskName: "Csc"));
        logger.Observe(args: new TaskStartedEventArgs(helpKeyword: "", message: "", projectFile: "Library.csproj", taskFile: "build.targets", taskName: "Csc"));
        logger.Observe(args: new TargetSkippedEventArgs { ProjectFile = "Library.csproj", SkipReason = TargetSkipReason.OutputsUpToDate, TargetName = "CoreCompile" });

        Assert.Equal(expected: new LawBuildCounts(Compiled: 1, Targets: 1, UpToDate: 1), actual: logger.Counts);
    }
}
