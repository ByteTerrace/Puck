using System.IO.Compression;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Cli.Counters;
using Puck.Testing;
using Puck.World;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>The compact spelling preserves captured ledgers' exact checker observations, and shipped ledgers keep the writer fixed point.</summary>
public sealed class CountersCeilingsCompactLawTests {
    [Fact]
    public void EveryShippedLedgerPreservesTheCapturedVerdictsAndMessages() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        using var archive = ZipFile.OpenRead(archiveFileName: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "counters-ceilings-equivalence.zip"));
        var covered = new HashSet<string>(comparer: StringComparer.Ordinal);
        using var directory = new TemporaryDirectory(prefix: "puck-counters-equivalence-");
        var capturedPath = Path.Combine(path1: directory.RootPath, path2: "captured.ceilings.json");

        foreach (var entry in archive.Entries.Where(predicate: entry => entry.FullName.EndsWith(comparisonType: StringComparison.Ordinal, value: ".cases.json"))) {
            var stem = entry.FullName[..^".cases.json".Length];
            var file = stem[..stem.LastIndexOf(value: '.')];

            covered.Add(item: file);
            using var reportStream = archive.GetEntry(entryName: (stem + ".report.json"))!.Open();
            var report = JsonSerializer.Deserialize(reportStream, WorldJsonContext.Default.WorldCountersReport)!;
            using var casesStream = entry.Open();
            using var cases = JsonDocument.Parse(casesStream);

            // The equivalence corpus holds the ledgers whose verdicts it captures; later device recordings
            // change measured work, not the compact format's checker contract.
            var ledger = archive.GetEntry(entryName: ("ledgers/" + file));

            Assert.NotNull(@object: ledger);
            using (var captured = ledger.Open()) {
                using var output = File.Create(path: capturedPath);

                captured.CopyTo(destination: output);
            }
            Assert.True(condition: CountersCeilings.TryRead(ceilings: out var ceilings, path: capturedPath, reason: out var reason),
                userMessage: $"{stem}: the checker refused instead of returning its captured verdict: {reason}");
            foreach (var test in cases.RootElement.EnumerateArray()) {
                var variant = test.GetProperty(propertyName: "Variant").GetString()!;
                var changed = Perturb(report, variant, test.GetProperty(propertyName: "Run").GetInt32(), test.GetProperty(propertyName: "Count").GetInt32());
                var verdict = CountersCeilings.Check(ceilings: ceilings, report: changed);
                var failures = test.GetProperty(propertyName: "Failures").EnumerateArray().Select(selector: item => item.GetString()!).ToArray();
                var notes = test.GetProperty(propertyName: "Notes").EnumerateArray().Select(selector: item => item.GetString()!).ToArray();

                Assert.True(condition: failures.SequenceEqual(second: verdict.Failures), userMessage: $"{stem}/{variant}: failures differ\nexpected: {string.Join('\n', failures)}\nactual: {string.Join(separator: '\n', values: verdict.Failures)}");
                Assert.True(condition: notes.SequenceEqual(second: verdict.Notes), userMessage: $"{stem}/{variant}: driver notes differ");
            }
        }
        var shipped = Directory.GetFiles(Path.Combine(path1: root, path2: "tests/Puck.Counters"), "*.ceilings.json", SearchOption.AllDirectories)
            .Select(selector: path => Path.GetRelativePath(Path.Combine(path1: root, path2: "tests/Puck.Counters"), path).Replace(newChar: '/', oldChar: '\\')).Order(comparer: StringComparer.Ordinal);

        Assert.Equal(shipped, covered.Order(comparer: StringComparer.Ordinal));
    }

    private static WorldCountersReport Perturb(WorldCountersReport report, string variant, int runIndex, int countIndex) {
        if (variant == "recorded") { return report; }
        var run = report.Runs[runIndex];
        var changed = variant switch {
            "exceed-zero" or "exceed-positive" => run with { Counts = run.Counts.Select(selector: (count, index) => ((index == countIndex) ? count with { Value = (count.Value + 1) } : count)).ToArray() },
            "missing-zero" or "missing-positive" => run with { Counts = run.Counts.Where(predicate: (_, index) => (index != countIndex)).ToArray() },
            "class-zero" or "class-positive" => run with { Counts = run.Counts.Select(selector: (count, index) => ((index == countIndex) ? count with { Class = WorkClass.Pacing } : count)).ToArray() },
            "missing-node" => run with { Counts = run.Counts.Where(predicate: count => (count.Node != run.Counts[0].Node)).ToArray() },
            "driver" => run with { Device = run.Device with { DriverVersionRaw = (run.Device.DriverVersionRaw + 1), DriverVersion = "changed" } },
            "unknown-device" => run with { Device = run.Device with { DeviceId = uint.MaxValue } },
            "unknown-0" or "unknown-1" => run with { Counts = [.. run.Counts, new("gpu", "unrecorded", "unrecorded", GpuWork.Dispatches.Name, WorkClass.Deterministic, ((variant == "unknown-0") ? 0 : 1))] },
            _ => throw new InvalidOperationException(message: variant),
        };

        return report with { Runs = report.Runs.Select(selector: (item, index) => ((index == runIndex) ? changed : item)).ToArray() };
    }
}
