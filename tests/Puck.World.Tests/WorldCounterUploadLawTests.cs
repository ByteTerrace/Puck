using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The canary's CPU-recorded pass states and upload bytes follow its real boot frame. The boot's device seal
/// rejects every physical device activation, and the render graph records over the upload model. Every executed pass line
/// the canary holds exactly is a line <see cref="GpuWorkReport"/> can write: each submission kind once, in column order,
/// each count a canonical decimal.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldCounterUploadLawTests {
    [Fact]
    public void CanaryExactPassLinesNameEverySubmissionKindOnceInColumnOrder() {
        using var manifest = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: AuthoredGameFixtures.Root, path2: "tests/Puck.World.Canaries/world-counters/canary.json")));
        var kinds = new List<string>();

        foreach (var kind in GpuWork.SubmissionKinds) {
            kinds.Add(item: (kind.Name.StartsWith(comparisonType: StringComparison.Ordinal, value: "gpu.") ? kind.Name["gpu.".Length..] : kind.Name));
        }
        var checkedLines = 0;

        foreach (var observation in manifest.RootElement.GetProperty(propertyName: "positive").GetProperty(propertyName: "expect").EnumerateArray()) {
            if ((observation.GetProperty(propertyName: "type").GetString() != "lines") || !observation.TryGetProperty(propertyName: "match", value: out var match) || (match.GetString() != "exact")) {
                continue;
            }
            foreach (var element in observation.GetProperty(propertyName: "lines").EnumerateArray()) {
                var line = element.GetString()!;
                var colon = line.IndexOf(comparisonType: StringComparison.Ordinal, value: " executed:");

                if (colon < 0) {
                    continue;
                }
                var keys = new List<string>();

                foreach (var pair in line[(colon + " executed:".Length)..].Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ')) {
                    var equals = pair.IndexOf(value: '=');

                    Assert.True(condition: (equals > 0), userMessage: $"'{pair}' is no count in: {line}");
                    var value = pair[(equals + 1)..];

                    Assert.True(condition: (long.TryParse(s: value, provider: CultureInfo.InvariantCulture, style: NumberStyles.None, result: out var count)
                        && (count.ToString(provider: CultureInfo.InvariantCulture) == value)), userMessage: $"'{pair}' is no canonical count in: {line}");
                    keys.Add(item: pair[..equals]);
                }
                Assert.True(condition: keys.SequenceEqual(second: kinds), userMessage:
                    $"The line names {string.Join(separator: ' ', values: keys)}; a report line names {string.Join(separator: ' ', values: kinds)}: {line}");
                checkedLines++;
            }
        }
        Assert.True(condition: (checkedLines > 0), userMessage: "The canary holds no exact pass line.");
    }
    [Fact]
    public void CanaryPassUploadsMatchItsDeviceSealedFirstFrame() {
        const string Canary = "tests/Puck.World.Canaries/world-counters";
        using var directory = new TemporaryDirectory(prefix: "s60b-resources-world-counters-");
        using var host = WorldBootHarness.Compose(stateDirectory: directory,
            presentation: WorldHostPresentation.Offscreen, world: $"{Canary}/fixture.world.json").Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        using var view = new SdfTestView(device: gpu, extent: 64, hostsOnDirectX: false, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: presenter, height: 64,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: 64));
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: 64, TargetWidth: 64);

        TestLiveness.Until(step: () => view.Produce(context: in context), reason: () => view.NotReadyReason,
            wait: view.Residency.WaitPipelineBuilds);
        for (var index = 0; (index < 3); index++) {
            _ = view.Produce(context: in context);
        }
        var sample = new GpuWorkSample();

        Assert.True(condition: view.Runtime.Work(instance: 0).TryReadCompleted(sample: sample));
        Assert.Equal(expected: 1L, actual: sample.Submission);
        using var manifest = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: AuthoredGameFixtures.Root, path2: $"{Canary}/canary.json")));
        var lines = manifest.RootElement.GetProperty(propertyName: "positive").GetProperty(propertyName: "expect")
            .EnumerateArray().Single(predicate: observation => (observation.GetProperty(propertyName: "name").GetString() == "the-view-passes-are-exact"))
            .GetProperty(propertyName: "lines").EnumerateArray();
        var expected = new List<string>();
        var actual = new List<string>();
        var uploads = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.HostVisibleUploadBytes);

        foreach (var line in lines) {
            var fields = line.GetString()!.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ');

            // A named detail row is part of its pass's total, which the pass's own line states; its uploads are the pass's.
            if (fields[2].StartsWith(comparisonType: StringComparison.Ordinal, value: "detail=")) {
                continue;
            }
            var pass = sample.PassLabels.IndexOf(value: fields[1]);

            Assert.True(condition: (pass >= 0), userMessage: $"No recorded pass {fields[1]}.");
            expected.Add(item: $"{fields[1]} {fields[2].TrimEnd(trimChar: ':')} {fields.SingleOrDefault(predicate: static field => field.StartsWith(comparisonType: StringComparison.Ordinal, value: "uploads.host-visible="))}".TrimEnd());
            var state = sample.GetPassState(pass: pass);

            if (state == GpuPassState.Skipped) {
                actual.Add(item: $"{fields[1]} skipped");
            } else {
                Assert.Equal(actual: state, expected: GpuPassState.Executed);
                Assert.True(condition: sample.TryGetPassCount(column: uploads, pass: pass, value: out var bytes));
                actual.Add(item: $"{fields[1]} executed uploads.host-visible={bytes}");
            }
        }
        Assert.True(condition: expected.SequenceEqual(second: actual), userMessage:
            $"Expected:\n{string.Join(separator: '\n', values: expected)}\nActual:\n{string.Join(separator: '\n', values: actual)}\nCPU work:\n{GpuWorkReport.AppendSample(builder: new System.Text.StringBuilder(), sample: sample)}");
        Assert.Empty(collection: gpu.StateConflicts);
    }
}
