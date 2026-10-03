using Puck.Cli.Affected;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class AffectedRecordingLawTests {
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
