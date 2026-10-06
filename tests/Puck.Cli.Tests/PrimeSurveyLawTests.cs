using System.Globalization;
using System.Text.Json;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class PrimeSurveyLawTests {
    [InlineData(97UL, 211UL)]
    [InlineData(4_294_967_291UL, 4_294_967_411UL)]
    [Theory]
    public void NonzeroIntervalsKeepModesLayoutsAndBatchesComparable(ulong low, ulong high) {
        using var scratch = new TemporaryDirectory(prefix: "puck-prime-survey-");
        var output = scratch.PathOf(name: "reports");

        var (exit, _, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: [
            "bench", "primes", "--low", low.ToString(provider: CultureInfo.InvariantCulture), "--upper", high.ToString(provider: CultureInfo.InvariantCulture),
            "--mode", "Automatic", "Eratosthenes", "Presieve", "--strategies", "EightStreams", "BucketPackets",
            "--layouts", "Numeric", "Algebraic", "--patterns", "Enabled", "--segments", "32768",
            "--warmups", "1", "--samples", "1", "--batch", "2", "--output", output,
        ]));
        Assert.True(condition: (exit == 0), userMessage: error);
        using var report = JsonDocument.Parse(json: File.ReadAllText(path: Assert.Single(collection: Directory.GetFiles(path: output, searchOption: SearchOption.AllDirectories, searchPattern: "report.json"))));
        var root = report.RootElement;
        var expected = 0UL;

        for (var value = low; (value <= high); ++value) {
            var prime = (value >= 2);

            for (var divisor = 2UL; (prime && (divisor <= (value / divisor))); ++divisor) {
                if ((value % divisor) == 0) { prime = false; }
            }
            if (prime) { ++expected; }
        }
        Assert.Equal(expected: low, actual: root.GetProperty(propertyName: "Low").GetUInt64());
        Assert.Equal(expected: high, actual: root.GetProperty(propertyName: "Upper").GetUInt64());
        var rows = root.GetProperty(propertyName: "Rows").EnumerateArray().ToArray();

        Assert.Equal(expected: 24, actual: rows.Length);
        foreach (var row in rows) {
            Assert.Equal(expected: expected, actual: row.GetProperty(propertyName: "Count").GetUInt64());
            Assert.Equal(expected: 2, actual: row.GetProperty(propertyName: "CallsPerSample").GetInt32());
            Assert.Equal(expected: JsonValueKind.Null, actual: row.GetProperty(propertyName: "ManagedPrimeMultipleUpdates").ValueKind);
            Assert.True(condition: (row.GetProperty(propertyName: "ManagedAllocatedBytesPerCall").GetDouble() >= 0));
            Assert.InRange(actual: row.GetProperty(propertyName: "ManagedActiveBitmapBytes").GetInt32(), low: 1, high: 6);
        }
    }
    [Fact]
    public void TopUnsignedIntervalDoesNotWrap() {
        using var scratch = new TemporaryDirectory(prefix: "puck-prime-survey-top-");
        var output = scratch.PathOf(name: "reports");

        var (exit, _, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: [
            "bench", "primes", "--low", "18446744073709551557", "--upper", "18446744073709551615",
            "--mode", "Automatic", "Presieve", "--strategies", "BucketPackets", "--layouts", "Numeric",
            "--patterns", "Enabled", "--segments", "32768", "--count-only", "--warmups", "1", "--samples", "1", "--output", output,
        ]));
        Assert.True(condition: (exit == 0), userMessage: error);
        using var report = JsonDocument.Parse(json: File.ReadAllText(path: Assert.Single(collection: Directory.GetFiles(path: output, searchOption: SearchOption.AllDirectories, searchPattern: "report.json"))));

        Assert.Equal(expected: ulong.MaxValue, actual: report.RootElement.GetProperty(propertyName: "Upper").GetUInt64());
        foreach (var row in report.RootElement.GetProperty(propertyName: "Rows").EnumerateArray()) {
            Assert.Equal(expected: 1UL, actual: row.GetProperty(propertyName: "Count").GetUInt64());
            Assert.Equal(expected: 3, actual: row.GetProperty(propertyName: "ManagedActiveBitmapBytes").GetInt32());
        }
    }
    [InlineData("--low", "10", "--upper", "9")]
    [InlineData("--mode", "Automatic", "Automatic")]
    [InlineData("--layouts", "Numeric", "Numeric")]
    [InlineData("--mode", "999")]
    [InlineData("--patterns", "999")]
    [InlineData("--batch", "0")]
    [Theory]
    public void InvalidSurveyShapesAreRefused(params string[] options) {
        var (exit, _, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["bench", "primes", .. options]));
        Assert.Equal(actual: exit, expected: 2);
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "primes: require");
    }
}
