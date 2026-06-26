using Xunit;

namespace Puck.HumbleGamingBrick.Conformance.Tests;

/// <summary>Runs the entire focused cycle-accuracy corpus in one pass, writes the Markdown scorecard to
/// <c>docs/gb-cycle-accuracy-scorecard.md</c>, and reports the overall verdict. This is both the verdict generator
/// and a single-command way to see where the emulator stands.</summary>
public sealed class ScorecardFact {
    private readonly ITestOutputHelper m_output;

    /// <summary>Creates the fact with the xUnit output sink.</summary>
    /// <param name="output">The test output helper.</param>
    public ScorecardFact(ITestOutputHelper output) =>
        m_output = output;

    [Fact]
    public void GenerateScorecard() {
        Assert.SkipUnless(condition: RomCatalog.IsAvailable, reason: "PUCK_GB_TESTROMS not set; GB test corpus unavailable.");

        var outcomes = RomCatalog.Enumerate().Select(selector: ConformanceEngine.Execute).ToList();
        var markdown = Scorecard.ToMarkdown(outcomes: outcomes);
        var verdict = Scorecard.ComputeVerdict(outcomes: outcomes);
        var outputPath = WriteScorecard(markdown: markdown);

        var passed = outcomes.Count(predicate: static o => o.Status == TestStatus.Pass);
        var failed = outcomes.Count(predicate: static o => o.Status == TestStatus.Fail);
        var inconclusive = outcomes.Count(predicate: static o => o.Status == TestStatus.Inconclusive);

        m_output.WriteLine(message: "Verdict: " + verdict);
        m_output.WriteLine(message: $"Pass {passed} / Fail {failed} / Inconclusive {inconclusive} (of {outcomes.Count}).");
        m_output.WriteLine(message: "Scorecard: " + outputPath);

        Assert.True(condition: verdict != Verdict.Insufficient, userMessage: "No timing tests were decided — the harness produced no usable signal.");
    }

    private static string WriteScorecard(string markdown) {
        var root = FindRepositoryRoot() ?? AppContext.BaseDirectory;
        var directory = Path.Combine(root, "docs");

        Directory.CreateDirectory(path: directory);

        var path = Path.Combine(directory, "gb-cycle-accuracy-scorecard.md");

        File.WriteAllText(path: path, contents: markdown);

        return path;
    }

    private static string? FindRepositoryRoot() {
        var current = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (current is not null) {
            if (File.Exists(path: Path.Combine(current.FullName, "Puck.slnx"))) {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
