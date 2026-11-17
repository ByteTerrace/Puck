using System.Text.Json;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Source.Tests;

public sealed class ExactKeyAnalyzerLawTests {
    [Fact]
    public void ContinuousCollectionKeysAndTupleMembersAreReportedButDiscreteKeysAreNot() {
        using var scratch = new TemporaryDirectory(prefix: "puck-exact-keys-");

        File.WriteAllText(Path.Combine(path1: scratch.RootPath, path2: "Keys.cs"), """
            class C {
                Dictionary<float, int> scalarCache = new Dictionary<float, int>();
                Dictionary<System.Numerics.Vector3, int> vectorCache;
                Dictionary<(int Id, (double X, int Y) Position), int> tupleCache;
                HashSet<System.ValueTuple<int, float>> recent;
                Dictionary<(int Id, string Name), int> discreteCache;
                Dictionary<float, int> authoredValues;
                Dictionary<CustomKey, int> memo;
                record CustomKey((int Id, float Value) Position);
                record FloatQuery(float Value);
                record DiscreteQuery(int Value);
            }
            """);

        var (exit, output, error) = ConsoleCapture.RunSplit(run: () => SuiteRoot.Invoke(args: ["scan", scratch.RootPath, "--only", "exact-keys"]));
        Assert.True(condition: (exit == 0), userMessage: error);
        var records = output.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').Select(selector: line => JsonDocument.Parse(line)).ToArray();

        try {
            var keys = records.Where(predicate: record => (record.RootElement.GetProperty(propertyName: "kind").GetString() == "collection-key")).ToArray();

            Assert.Equal(4, keys.Length);
            foreach (var name in new[] { "scalarCache", "vectorCache", "tupleCache", "recent", "CustomKey", "FloatQuery" }) {
                Assert.Contains(collection: records, filter: record => record.RootElement.GetProperty(propertyName: "text").GetString()!.StartsWith(comparisonType: StringComparison.Ordinal, value: name));
            }
            Assert.Equal(6, records.Length);
        } finally {
            foreach (var record in records) { record.Dispose(); }
        }
    }
}
