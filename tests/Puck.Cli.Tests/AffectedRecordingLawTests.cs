using Puck.Cli.Affected;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class AffectedRecordingLawTests {
    [InlineData("missing")]
    [InlineData("pdb")]
    [InlineData("publish")]
    [Theory]
    public void IncompleteOrUnpublishableCoverageRefusesAndKeepsTheEvidence(string failure) {
        if ((failure == "publish") && !OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows file sharing supplies the publication failure."); }
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: "build/Architecture.props", text: "<Project />");
        checkout.Write(name: "src/Puck.World/Puck.World.csproj", text: "<Project />");
        checkout.Write(name: "src/Puck.World/Example.cs", text: "class Example {}");
        checkout.Write(name: AffectedCommand.CoveragePath, text: "previous coverage bytes\n");
        _ = checkout.Commit(message: "fixture");
        var path = Path.Combine(path1: checkout.Root, path2: AffectedCommand.CoveragePath);
        var original = File.ReadAllBytes(path: path);
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-record-failure-law-");
        var leg = scratch.PathOf(name: "leg");

        Directory.CreateDirectory(path: leg);
        var output = $"canary one passed transcripts {leg}\n";

        if (failure == "missing") { output += $"canary two passed transcripts {scratch.PathOf(name: "missing")}\n"; }
        if (failure == "pdb") {
            scratch.WriteText(name: "leg/methods.one.txt", text: "broken\t06000001\n");
            scratch.WriteText(name: "world/broken.pdb", text: "invalid portable PDB");
        }
        using var held = ((failure == "publish") ? new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: path, share: FileShare.Read) : null);
        var result = ConsoleCapture.RunSplit(run: () => AffectedCommand.Record(checkout.Root, "candidate.dll", scratch.RootPath, (arguments, _) =>
            new CliProcessResult(ExitCode: 0, OutputLines: [], Stderr: "", Stdout: ((arguments[0] == "build") ? "built" : output), TimedOut: false)));

        Assert.Equal(actual: result.ExitCode, expected: CliExit.Refused);
        Assert.Equal(original, File.ReadAllBytes(path: path));
        Assert.True(condition: Directory.Exists(path: leg), userMessage: "A failed recording keeps every leg it read.");
        var transcript = scratch.PathOf(name: AffectedCoverage.CanaryTranscriptName);

        Assert.Equal($"exit 0\n--- stdout\n{output}\n--- stderr\n\n", File.ReadAllText(path: transcript));
        Assert.Contains(CliPaths.ToDisplay(fullPath: transcript), result.Error);
        Assert.Contains(actualString: result.Error, expectedSubstring: "coverage is unchanged");
    }
    [InlineData(7, false)]
    [InlineData(0, true)]
    [Theory]
    public void AFailedRecordingBuildLeavesCoverageUntouchedAndStartsNoCanary(int buildExit, bool timedOut) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: AffectedCommand.CoveragePath, text: "previous coverage bytes\n");
        var path = Path.Combine(path1: checkout.Root, path2: AffectedCommand.CoveragePath);
        var original = File.ReadAllBytes(path: path);
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-record-build-law-");
        var calls = 0;
        var result = ConsoleCapture.RunSplit(run: () => AffectedCommand.Record(checkout.Root, "candidate.dll", scratch.RootPath, (arguments, _) => {
            calls++;
            Assert.Equal("build", arguments[0]);
            return new CliProcessResult(ExitCode: buildExit, OutputLines: [], Stderr: "", Stdout: "build diagnostic", TimedOut: timedOut);
        }));

        Assert.Equal(actual: result.ExitCode, expected: CliExit.Refused);
        Assert.Equal(actual: calls, expected: 1);
        Assert.Equal(original, File.ReadAllBytes(path: path));
        Assert.Contains(actualString: result.Error, expectedSubstring: "build diagnostic");
    }
    [InlineData(7, false)]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [Theory]
    public void OnlyASuccessfulInnerRunCanReplaceCoverage(int innerExit, bool timedOut) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: "build/Architecture.props", text: "<Project />");
        checkout.Write(name: "src/Puck.World/Puck.World.csproj", text: "<Project />");
        checkout.Write(name: "src/Puck.World/Example.cs", text: "class Example {}");
        checkout.Write(name: AffectedCommand.CoveragePath, text: "previous coverage bytes\n");
        _ = checkout.Commit(message: "fixture");
        var path = Path.Combine(path1: checkout.Root, path2: AffectedCommand.CoveragePath);
        var original = File.ReadAllBytes(path: path);
        using var scratch = new TemporaryDirectory(prefix: "puck-affected-record-law-");
        var leg = scratch.PathOf(name: "leg");

        Directory.CreateDirectory(path: leg);
        var calls = new List<string[]>();
        var result = ConsoleCapture.RunSplit(run: () => AffectedCommand.Record(checkout.Root, "candidate.dll", scratch.RootPath, (arguments, _) => {
            calls.Add(item: [.. arguments]);
            if (arguments[0] == "build") { return new CliProcessResult(ExitCode: 0, OutputLines: [], Stderr: "", Stdout: "built", TimedOut: false); }
            return new CliProcessResult(ExitCode: innerExit, OutputLines: [], Stderr: "inner diagnostic", Stdout: $"canary one passed transcripts {leg}\n", TimedOut: timedOut);
        }));

        Assert.Equal(2, calls.Count);
        Assert.Contains("-p:PuckRecordMethods=true", calls[0]);
        Assert.Contains(CliOptions.NoNodeReuse, calls[0]);
        Assert.Contains("--keep-transcripts", calls[1]);
        var transcript = scratch.PathOf(name: AffectedCoverage.CanaryTranscriptName);

        Assert.Contains($"exit {innerExit}", File.ReadAllText(path: transcript));
        Assert.Contains("inner diagnostic", File.ReadAllText(path: transcript));
        if ((innerExit != 0) || timedOut) {
            Assert.Equal(actual: result.ExitCode, expected: CliExit.Refused);
            Assert.Equal(original, File.ReadAllBytes(path: path));
            Assert.Contains(actualString: result.Error, expectedSubstring: $"inner canary run exited {innerExit}");
            Assert.Contains(CliPaths.ToDisplay(fullPath: transcript), result.Error);
            Assert.True(condition: Directory.Exists(path: leg));
        } else {
            Assert.Equal(actual: result.ExitCode, expected: CliExit.Success);
            Assert.Contains(AffectedCoverage.Schema, File.ReadAllText(path: path));
            Assert.False(condition: Directory.Exists(path: leg));
        }
    }
}
