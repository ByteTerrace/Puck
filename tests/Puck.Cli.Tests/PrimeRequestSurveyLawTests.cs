using System.Text.Json;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class PrimeRequestSurveyLawTests {
    [Fact]
    public void FirstRequestAndWarmBatchRemainSeparateAndProfileMatches() {
        using var scratch = new TemporaryDirectory(prefix: "puck-prime-request-survey-");
        var output = scratch.PathOf(name: "reports");

        var (exit, _, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: [
            "bench", "prime-requests", "--cases", "nth-table-10", "--samples", "1", "--warmups", "0", "--batch", "3", "--output", output,
        ]));

        Assert.True(condition: (exit == 0), userMessage: error);
        using var report = ReadReport(output: output);
        var row = Assert.Single(collection: report.RootElement.GetProperty(propertyName: "Rows").EnumerateArray().ToArray());

        Assert.Equal(expected: "complete", actual: row.GetProperty(propertyName: "Status").GetString());
        Assert.Equal(expected: 29UL, actual: row.GetProperty(propertyName: "Result").GetUInt64());
        Assert.Equal(expected: 1, actual: row.GetProperty(propertyName: "First").GetProperty(propertyName: "Requests").GetInt32());
        Assert.Equal(expected: 3, actual: Assert.Single(collection: row.GetProperty(propertyName: "Samples").EnumerateArray().ToArray()).GetProperty(propertyName: "Requests").GetInt32());
        Assert.Equal(expected: 29UL, actual: row.GetProperty(propertyName: "Work").GetProperty(propertyName: "Result").GetUInt64());
        Assert.True(condition: row.GetProperty(propertyName: "FirstPrimeRequestInProcess").GetBoolean());
    }
    [InlineData("random-small", 2UL, 65535UL)]
    [InlineData("random-high-window", 1_000_000_000_000_000_000UL, 1_000_000_000_000_999_999UL)]
    [Theory]
    public void RandomControlsShareBoundsBudgetAndSeedAndReportDraws(string name, ulong low, ulong high) {
        using var scratch = new TemporaryDirectory(prefix: "puck-prime-request-random-");
        var output = scratch.PathOf(name: "reports");

        var (exit, _, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: [
            "bench", "prime-requests", "--cases", name, "--samples", "1", "--warmups", "0", "--batch", "4", "--seeds", "42", "--output", output,
        ]));

        Assert.True(condition: (exit == 0), userMessage: error);
        using var report = ReadReport(output: output);
        var rows = report.RootElement.GetProperty(propertyName: "Rows").EnumerateArray().ToArray();

        Assert.Equal(expected: 4, actual: rows.Length);
        Assert.Equal(expected: 2048, actual: report.RootElement.GetProperty(propertyName: "RawDrawBudget").GetInt32());
        foreach (var row in rows) {
            Assert.Equal(expected: "complete", actual: row.GetProperty(propertyName: "Status").GetString());
            Assert.Equal(expected: low, actual: row.GetProperty(propertyName: "Low").GetUInt64());
            Assert.Equal(expected: high, actual: row.GetProperty(propertyName: "High").GetUInt64());
            Assert.Equal(expected: 42UL, actual: row.GetProperty(propertyName: "Seed").GetUInt64());
            var work = row.GetProperty(propertyName: "RandomWork");

            Assert.Equal(expected: (work.GetProperty(propertyName: "UInt64Draws").GetUInt64() * 2),
                actual: work.GetProperty(propertyName: "UInt32Draws").GetUInt64());
        }
        Assert.Equal(expected: rows[0].GetProperty(propertyName: "Result").GetUInt64(), actual: rows[1].GetProperty(propertyName: "Result").GetUInt64());
        if (high <= 65535) {
            Assert.Equal(expected: 0UL, actual: rows[2].GetProperty(propertyName: "RandomWork").GetProperty(propertyName: "CandidateDecisionUpperBound").GetUInt64());
        } else {
            Assert.True(condition: (rows[2].GetProperty(propertyName: "RandomWork").GetProperty(propertyName: "CandidateDecisionUpperBound").GetUInt64() > 0));
        }
        Assert.Equal(expected: rows[2].GetProperty(propertyName: "Result").GetUInt64(), actual: rows[3].GetProperty(propertyName: "Result").GetUInt64());
        Assert.Equal(expected: rows[2].GetProperty(propertyName: "RandomWork").GetProperty(propertyName: "UInt32Draws").GetUInt64(),
            actual: rows[3].GetProperty(propertyName: "RandomWork").GetProperty(propertyName: "UInt32Draws").GetUInt64());
    }
    [InlineData("--cases", "missing")]
    [InlineData("--batch", "0")]
    [InlineData("--warmups", "-1")]
    [InlineData("--samples", "0")]
    [Theory]
    public void InvalidRequestSurveyShapeIsRefused(params string[] options) {
        var (exit, _, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["bench", "prime-requests", .. options]));

        Assert.Equal(actual: exit, expected: 2);
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "prime-requests: require");
    }

    private static JsonDocument ReadReport(string output) => JsonDocument.Parse(json: File.ReadAllText(path:
        Assert.Single(collection: Directory.GetFiles(path: output, searchOption: SearchOption.AllDirectories, searchPattern: "report.json"))));
}
