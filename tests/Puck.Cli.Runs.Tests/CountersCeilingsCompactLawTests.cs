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
    [Fact]
    public void EveryShippedLedgerRoundTripsAtTheWritersFixedPoint() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        using var directory = new TemporaryDirectory(prefix: "puck-counters-compact-law-");
        var output = Path.Combine(path1: directory.RootPath, path2: "ceilings.json");

        foreach (var file in Directory.GetFiles(Path.Combine(path1: root, path2: "tests/Puck.Counters"), "*.ceilings.json", SearchOption.AllDirectories)) {
            Assert.True(condition: CountersCeilings.TryRead(ceilings: out var first, path: file, reason: out var firstReason), userMessage: firstReason);
            CountersCeilings.Write(ceilings: first, path: output);
            Assert.Equal(File.ReadAllBytes(path: file), File.ReadAllBytes(path: output));
            Assert.True(condition: CountersCeilings.TryRead(ceilings: out var second, path: output, reason: out var secondReason), userMessage: secondReason);
            CountersCeilings.Write(ceilings: second, path: output);
            Assert.Equal(File.ReadAllBytes(path: file), File.ReadAllBytes(path: output));
            foreach (var (left, right) in first.Backends.Zip(second: second.Backends)) {
                Assert.Equal(left.Ceilings, right.Ceilings);
                foreach (var (a, b) in left.Devices.Zip(second: right.Devices)) {
                    Assert.Equal(a.Device, b.Device);
                    Assert.Equal(a.Ceilings, b.Ceilings);
                }
            }
        }
    }
    [Fact]
    public void KindDefaultsCoverEverySubmissionKindWithoutStoredClassesOrZeroBudgets() {
        var kinds = GpuWork.SubmissionKinds.ToArray();
        var original = Report(counts: kinds.Select(selector: kind => new WorldCount("gpu", "node", "pass", kind.Name, kind.Class, 0)).ToArray());
        using var directory = new TemporaryDirectory(prefix: "puck-counters-kind-law-");
        var path = Path.Combine(path1: directory.RootPath, path2: "ceilings.json");

        CountersCeilings.Write(path, CountersCeilings.Record(report: original));
        var text = File.ReadAllText(path: path);

        Assert.DoesNotContain(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"class\"");
        Assert.DoesNotContain(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"classes\"");
        Assert.DoesNotContain(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"values\"");
        Assert.DoesNotContain(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "requiredZero");
        Assert.True(condition: CountersCeilings.TryRead(ceilings: out var ceilings, path: path, reason: out var reason), userMessage: reason);
        var rows = ceilings.Backends[0].Ceilings.Concat(second: ceilings.Backends[0].Devices[0].Ceilings).ToArray();

        Assert.Equal(kinds.Length, rows.Length);
        foreach (var kind in kinds) {
            Assert.Equal(kind.Class, Assert.Single(collection: rows, predicate: row => (row.Kind == kind.Name)).Class);
        }
        Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: original).Failures);
    }
    [Fact]
    public void IdenticalDeviceReadingsHaveNoStoredDifferences() {
        var report = Report(counts: [new("gpu", "node", "pass", GpuWork.MarchSteps.Name, WorkClass.PerBackendDeterministic, 17),
            new("gpu", "node", "pass", GpuWork.Dispatches.Name, WorkClass.Deterministic, 1)]);
        var other = report with { Runs = [report.Runs[0] with { Device = report.Runs[0].Device with { DeviceId = 3 } }] };

        Assert.True(condition: CountersCeilings.TryMerge(CountersCeilings.Record(report: other), CountersCeilings.Record(report: report), out var merged, out var mergeReason), userMessage: mergeReason);
        using var directory = new TemporaryDirectory(prefix: "puck-counters-delta-law-");
        var path = Path.Combine(path1: directory.RootPath, path2: "ceilings.json");

        CountersCeilings.Write(ceilings: merged, path: path);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path: path));

        foreach (var device in json.RootElement.GetProperty(propertyName: "backends")[0].GetProperty(propertyName: "devices").EnumerateArray()) {
            var differences = device.GetProperty(propertyName: "ceilings");

            Assert.Equal(JsonValueKind.Object, differences.ValueKind);
            Assert.Empty(collection: differences.EnumerateObject());
        }
        Assert.True(condition: CountersCeilings.TryRead(ceilings: out var read, path: path, reason: out var reason), userMessage: reason);
        Assert.Empty(collection: CountersCeilings.Check(ceilings: read, report: report).Failures);
        Assert.Empty(collection: CountersCeilings.Check(ceilings: read, report: other).Failures);

        var changed = other with {
            Runs = [other.Runs[0] with { Counts = [
            new("gpu", "other-node", "other-pass", GpuWork.Dispatches.Name, WorkClass.PerBackendDeterministic, 0),
            report.Runs[0].Counts[1],
        ] }],
        };

        Assert.True(condition: CountersCeilings.TryMerge(CountersCeilings.Record(report: changed), merged, out var different, out var differenceReason), userMessage: differenceReason);
        CountersCeilings.Write(ceilings: different, path: path);
        using var deltaJson = JsonDocument.Parse(File.ReadAllBytes(path: path));
        var backend = deltaJson.RootElement.GetProperty(propertyName: "backends")[0];
        var secondDevice = backend.GetProperty(propertyName: "devices")[1].GetProperty(propertyName: "ceilings");

        Assert.Empty(collection: secondDevice.GetProperty(propertyName: "node").GetProperty(propertyName: "pass").GetProperty(propertyName: "device").EnumerateArray());
        Assert.False(condition: secondDevice.GetProperty(propertyName: "other-node").GetProperty(propertyName: "other-pass").TryGetProperty(propertyName: "values", value: out _));
        Assert.True(condition: CountersCeilings.TryRead(ceilings: out var differingRead, path: path, reason: out var differingReason), userMessage: differingReason);
        Assert.Empty(collection: CountersCeilings.Check(ceilings: differingRead, report: report).Failures);
        Assert.Empty(collection: CountersCeilings.Check(ceilings: differingRead, report: changed).Failures);
    }
    [Fact]
    public void AnUnrecordedNonzeroAfterRoundTripFailsNamingTheRow() {
        var report = Report(counts: [new("gpu", "node", "pass", GpuWork.MarchSteps.Name, WorkClass.PerBackendDeterministic, 17)]);
        using var directory = new TemporaryDirectory(prefix: "puck-counters-missing-law-");
        var path = Path.Combine(path1: directory.RootPath, path2: "ceilings.json");

        CountersCeilings.Write(path, CountersCeilings.Record(report: report));
        using var json = JsonDocument.Parse(File.ReadAllBytes(path: path));

        Assert.True(condition: json.RootElement.TryGetProperty(propertyName: "layouts", value: out _), userMessage: "The checker must read the compact layout spelling.");
        Assert.True(condition: CountersCeilings.TryRead(ceilings: out var read, path: path, reason: out var reason), userMessage: reason);
        var changed = Perturb(countIndex: -1, report: report, runIndex: 0, variant: "unknown-1");

        Assert.Equal("vulkan: deterministic kind=gpu.dispatches pass=unrecorded detail=- node=unrecorded reads 1 with no ceiling recorded",
            Assert.Single(collection: CountersCeilings.Check(ceilings: read, report: changed).Failures));
    }

    private static WorldCountersReport Report(IReadOnlyList<WorldCount> counts) => new(new(Commit: "test", SourceState: "test"), "world.json", "script.txt",
        [new("vulkan", new("vulkan", "GPU", 1, 2, 0, "driver", "api"), 8, 8, "test", "test", counts, [])]);
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
