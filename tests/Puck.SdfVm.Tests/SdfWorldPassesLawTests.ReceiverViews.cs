using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void ACompletedCameraDoesNotReadAnEarlierCamerasDeferredWork() {
        const string Earlier = "view.earlier";
        const string Captured = "view.captured";
        var naming = new RecordingGpuObjectNaming(isEnabled: true);
        var gpu = new FakeGpuDevice(holdFences: true, trackObjects: true, naming: naming) {
            BufferClears = [],
            BufferCopies = [],
            BufferTransitions = [],
            DistinctBuffers = true,
        };
        var copies = gpu.BufferCopies!;
        var clears = gpu.BufferClears!;
        var transitions = gpu.BufferTransitions!;
        var pipelines = SdfTestPipelines.Cache();
        var original = Frame();
        var source = original with {
            FarDistance = 12f,
            IndirectTier = SdfIndirectTier.Medium,
            EnableCadenceGate = false,
            Views = [original.Views[0], original.Views[0]],
        };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: source), height: Extent, width: Extent,
            name: "shared", kernels: SdfTestPipelines.Kernels(), pipelines: pipelines);
        var views = new SdfWorldPasses(name => new SdfWorldView(Residency: residency, View: ((name == Earlier) ? 0 : 1)));
        using var indirect = new SdfIndirectPasses(views: views);

        indirect.Register(name: residency.IndirectInstanceName, residency: residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        packages.Register(factory: indirect, package: RenderGraphPackageCatalog.Indirect);
        var context = ContextOf(gpu: gpu);

        residency.ProduceFirstFrame(context: context);
        RenderGraphInstance View(string name) => new(Name: name, ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
            Passes: views.FragmentOf(instance: name)!.Passes.Count,
            Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate([View(name: Earlier), View(name: Captured),
            new(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame)],
            out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[3], Captured, packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var sources = new Dictionary<string, nint>(comparer: StringComparer.Ordinal);
        var completed = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        gpu.WriteReadback = (name, bytes) => {
            if (name.Detail != "indirect-deferred") { return; }
            var destination = naming.Applied.Last(predicate: item => ((item.Kind == GpuObjectKind.Buffer) && (item.Name == name.ToString()))).Handle;
            var copy = copies.Last(predicate: item => (item.Destination == destination));
            var sourceName = naming.Applied.Last(predicate: item => ((item.Kind == GpuObjectKind.Buffer) && (item.Handle == copy.Source))).Name;
            // Model two different GPU outcomes through the actual copied source, not the destination's label:
            // the earlier camera keeps deferring; only the captured camera's own buffer contains a completed zero.
            var deferred = (sourceName.StartsWith(comparisonType: StringComparison.Ordinal, value: (Captured + "/")) ? 0u : 1u);

            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: deferred);
            Assert.Equal(actual: copy.SourceOffset, expected: 0UL);
            Assert.Equal(actual: copy.Bytes, expected: ((ulong)sizeof(uint)));
            sources[name.Owner!] = copy.Source;
            completed[name.Owner!] = (completed.GetValueOrDefault(key: name.Owner!) + 1);
        };
        var frame = 0L;

        void Produce(bool complete = true) {
            if (complete) { foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; } }
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayWidth: ((int)Extent), DisplayHertz: 60,
                Footprints: [], Index: frame, Tick: frame++, Roots: [new(Height: 1, Instance: Earlier, Width: 1), new(Height: 1, Instance: Captured, Width: 1)]);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
        }
        TestLiveness.Within(frames: 64, step: () => {
            Produce();
            return (views.CaptureReadinessOf(instance: Captured).IsRendered && (completed.GetValueOrDefault(key: Earlier) > 0));
        }, building: () => Enumerable.Range(count: 3, start: 0).Any(predicate: index => graph.Node(instance: index).IsBuildingCandidate), reason: () => graph.Render.Reason);
        var cache = residency.Tables!.Indirect!;

        Assert.NotEqual(sources[Earlier], sources[Captured]);
        Assert.NotEqual(cache.Buffer.BufferHandle, sources[Captured]);
        Assert.False(condition: views.CaptureReadinessOf(instance: Earlier).IsRendered);
        var observed = completed.GetValueOrDefault(key: Captured);

        Produce(complete: false);
        Assert.Equal(observed, completed.GetValueOrDefault(key: Captured));
        clears.Clear();
        for (var repeat = 0; (repeat < 4); repeat++) {
            Produce();
            Assert.True(condition: views.CaptureReadinessOf(instance: Captured).IsRendered);
            Assert.False(condition: views.CaptureReadinessOf(instance: Earlier).IsRendered);
        }
        Assert.Equal(4, clears.Count(predicate: item => ((item.Buffer == sources[Earlier]) && (item.Bytes == sizeof(uint)))));
        Assert.Equal(4, clears.Count(predicate: item => ((item.Buffer == sources[Captured]) && (item.Bytes == sizeof(uint)))));
        Assert.DoesNotContain(collection: clears, filter: item => (item.Buffer == cache.Buffer.BufferHandle));
        // Views reads the cleared predecessor before its preserving output. Both ports share this allocation;
        // the atomic write must follow that read, and its copy must follow the write on the same buffer.
        var expected = new[] {
            (GpuAccess.TransferWrite, GpuAccess.ShaderRead, GpuStage.Transfer, GpuStage.ComputeShader),
            (GpuAccess.ShaderRead, GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuStage.ComputeShader, GpuStage.ComputeShader),
            (GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuAccess.TransferRead, GpuStage.ComputeShader, GpuStage.Transfer),
        };

        foreach (var (owner, buffer) in sources) {
            var actual = transitions.Where(predicate: item => (item.Buffer == buffer))
                .Select(selector: item => (item.SourceAccess, item.DestinationAccess, item.SourceStage, item.DestinationStage)).ToArray();
            var ordered = Enumerable.Range(0, Math.Max(val1: 0, val2: ((actual.Length - expected.Length) + 1)))
                .Any(predicate: index => actual.AsSpan(index, expected.Length).SequenceEqual(other: expected));

            Assert.True(condition: ordered,
                userMessage: $"Camera '{owner}' copied source buffer {buffer}; expected reset/read/write/copy chain {string.Join(separator: "; ", values: expected)}; actual transitions: {string.Join(separator: "; ", values: actual)}");
        }
    }
}
