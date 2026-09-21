using Puck.Cli.Bench;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class StartupBenchmarkTests {
    private static CliProcessResult Transcript(int exit = 0, bool timeout = false, params CliProcessOutputLine[] extra) =>
        new(exit, [new("[world.status: ready]", 1, CliProcessOutputStream.Stdout, 100), new("[wire.errors: 0 rejected]", 2, CliProcessOutputStream.Stdout, 120), .. extra], "", "", timeout);

    [InlineData(1, false)]
    [InlineData(0, true)]
    [Theory]
    public void ReadinessDoesNotHideFailure(int exit, bool timeout) {
        var sample = StartupBenchmarks.Assess("world", 1, Transcript(exit, timeout), true, "unused.png");

        Assert.NotNull(sample.Error);
        Assert.Null(StartupBenchmarks.Summarize([sample], 1, true));
    }
    [Fact]
    public void PartialCorpusCannotClaimAnAverage() {
        var sample = StartupBenchmarks.Assess("world", 1, Transcript(), true, "unused.png");

        Assert.Null(sample.Error);
        Assert.Null(StartupBenchmarks.Summarize([sample], 2, true));
        Assert.Equal(100, StartupBenchmarks.Summarize([sample], 1, true)!.MeanMilliseconds);
    }
    [Fact]
    public void PendingCaptureAndUnrelatedCaptureDoNotProveReadiness() {
        var path = Path.GetTempFileName();

        try {
            File.WriteAllText(contents: "existing bytes", path: path);
            var pending = new CliProcessOutputLine($"[world.screenshot: pending {path} — lands on the next composed frame]", 3, CliProcessOutputStream.Stdout, 150);
            var unrelated = new CliProcessOutputLine($"[capture] unified overlay -> {path}.other", 4, CliProcessOutputStream.Stderr, 200);

            Assert.NotNull(StartupBenchmarks.Assess("world", 1, Transcript(extra: [pending, unrelated]), false, path).Error);
            var completion = new CliProcessOutputLine($"[capture] unified overlay -> {path}", 5, CliProcessOutputStream.Stderr, 250);
            var sample = StartupBenchmarks.Assess("world", 1, Transcript(extra: [pending, completion]), false, path);

            Assert.Null(sample.Error);
            Assert.Equal(250, StartupBenchmarks.Summarize([sample], 1, false)!.MeanMilliseconds);
            File.Delete(path: path);
            Assert.NotNull(StartupBenchmarks.Assess("world", 1, Transcript(extra: [completion]), false, path).Error);
        } finally { File.Delete(path: path); }
    }
    [Fact]
    public void CompletedCaptureWithMissingOverlaysCannotClaimReadiness() {
        var path = Path.GetTempFileName();

        try {
            File.WriteAllText(contents: "completed capture", path: path);
            var sample = StartupBenchmarks.Assess("world", 1, Transcript(extra: [
                new("[unified-overlay] skipped: no usable glyph atlas", 3, CliProcessOutputStream.Stderr, 130),
                new($"[capture] unified overlay -> {path}", 4, CliProcessOutputStream.Stderr, 200),
            ]), false, path);

            Assert.Equal("overlay unavailable; rendered startup is degraded", sample.Error);
            Assert.Null(StartupBenchmarks.Summarize([sample], 1, false));
        } finally { File.Delete(path: path); }
    }
    [Fact]
    public void ChildOutputCarriesObservedProcessElapsedTime() {
        var result = CliProcess.RunCaptured("dotnet", ["--version"], "", TimeSpan.FromSeconds(seconds: 30));

        Assert.Equal(actual: result.ExitCode, expected: 0);
        Assert.False(condition: result.TimedOut);
        Assert.NotEmpty(collection: result.OutputLines);
        Assert.All(result.OutputLines, line => Assert.InRange(line.ElapsedMilliseconds, 0.001, 30_000));
    }
}
