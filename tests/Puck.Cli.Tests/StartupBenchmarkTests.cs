using Puck.Cli.Bench;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class StartupBenchmarkTests {
    private static CliProcessResult Transcript(int exit = 0, bool timeout = false, params CliProcessOutputLine[] extra) =>
        new(ExitCode: exit, OutputLines: [new(ElapsedMilliseconds: 100, Line: "[world.status: ready]", Sequence: 1, Stream: CliProcessOutputStream.Stdout), new(ElapsedMilliseconds: 120, Line: "[wire.errors: 0 rejected]", Sequence: 2, Stream: CliProcessOutputStream.Stdout), .. extra], Stderr: "", Stdout: "", TimedOut: timeout);

    [InlineData(1, false)]
    [InlineData(0, true)]
    [Theory]
    public void ReadinessDoesNotHideFailure(int exit, bool timeout) {
        var sample = StartupBenchmarks.Assess("world", 1, Transcript(exit, timeout), true, "unused.png");

        Assert.NotNull(@object: sample.Error);
        Assert.Null(@object: StartupBenchmarks.Summarize(expected: 1, headless: true, rows: [sample]));
    }
    [Fact]
    public void PartialCorpusCannotClaimAnAverage() {
        var sample = StartupBenchmarks.Assess("world", 1, Transcript(), true, "unused.png");

        Assert.Null(@object: sample.Error);
        Assert.Null(@object: StartupBenchmarks.Summarize(expected: 2, headless: true, rows: [sample]));
        Assert.Equal(100, StartupBenchmarks.Summarize(expected: 1, headless: true, rows: [sample])!.MeanMilliseconds);
    }
    [Fact]
    public void PendingCaptureAndUnrelatedCaptureDoNotProveReadiness() {
        var path = Path.GetTempFileName();

        try {
            File.WriteAllText(contents: "existing bytes", path: path);
            var pending = new CliProcessOutputLine(ElapsedMilliseconds: 150, Line: $"[world.screenshot: pending {path} — lands on the next composed frame]", Sequence: 3, Stream: CliProcessOutputStream.Stdout);
            var unrelated = new CliProcessOutputLine(ElapsedMilliseconds: 200, Line: $"[capture] unified overlay -> {path}.other", Sequence: 4, Stream: CliProcessOutputStream.Stderr);

            Assert.NotNull(@object: StartupBenchmarks.Assess("world", 1, Transcript(extra: [pending, unrelated]), false, path).Error);
            var completion = new CliProcessOutputLine(ElapsedMilliseconds: 250, Line: $"[capture] unified overlay -> {path}", Sequence: 5, Stream: CliProcessOutputStream.Stderr);
            var sample = StartupBenchmarks.Assess("world", 1, Transcript(extra: [pending, completion]), false, path);

            Assert.Null(@object: sample.Error);
            Assert.Equal(250, StartupBenchmarks.Summarize(expected: 1, headless: false, rows: [sample])!.MeanMilliseconds);
            File.Delete(path: path);
            Assert.NotNull(@object: StartupBenchmarks.Assess("world", 1, Transcript(extra: [completion]), false, path).Error);
        } finally { File.Delete(path: path); }
    }
    [Fact]
    public void CompletedCaptureWithMissingOverlaysCannotClaimReadiness() {
        var path = Path.GetTempFileName();

        try {
            File.WriteAllText(contents: "completed capture", path: path);
            var sample = StartupBenchmarks.Assess("world", 1, Transcript(extra: [
                new(ElapsedMilliseconds: 130, Line: "[unified-overlay] skipped: no usable glyph atlas", Sequence: 3, Stream: CliProcessOutputStream.Stderr),
                new(ElapsedMilliseconds: 200, Line: $"[capture] unified overlay -> {path}", Sequence: 4, Stream: CliProcessOutputStream.Stderr),
            ]), false, path);

            Assert.Equal("overlay unavailable; rendered startup is degraded", sample.Error);
            Assert.Null(@object: StartupBenchmarks.Summarize(expected: 1, headless: false, rows: [sample]));
        } finally { File.Delete(path: path); }
    }
    [Fact]
    public void ChildOutputCarriesObservedProcessElapsedTime() {
        var result = CliProcess.RunCaptured("dotnet", ["--version"], "", Timeout.InfiniteTimeSpan,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(actual: result.ExitCode, expected: 0);
        Assert.NotEmpty(collection: result.OutputLines);
        Assert.All(result.OutputLines, line => Assert.True(condition: (line.ElapsedMilliseconds > 0), userMessage: $"line {line.Sequence} carries {line.ElapsedMilliseconds} ms"));
    }
}
