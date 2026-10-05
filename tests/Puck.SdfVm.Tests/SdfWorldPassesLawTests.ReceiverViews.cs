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
            DistinctBuffers = true, BufferClears = [], BufferCopies = [], BufferTransitions = [],
        };
        var copies = gpu.BufferCopies!;
        var clears = gpu.BufferClears!;
        var transitions = gpu.BufferTransitions!;
        var pipelines = SdfTestPipelines.Cache();
        var original = Frame();
        var source = original with { FarDistance = 12f, IndirectTier = SdfIndirectTier.Medium,
            EnableCadenceGate = false, Views = [original.Views[0], original.Views[0]] };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(source), height: Extent, width: Extent,
            name: "shared", kernels: SdfTestPipelines.Kernels(), pipelines: pipelines);
        var views = new SdfWorldPasses(name => new SdfWorldView(residency, name == Earlier ? 0 : 1));
        using var indirect = new SdfIndirectPasses(views);
        indirect.Register(residency.IndirectInstanceName, residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);
        packages.Register(RenderGraphPackageCatalog.SdfWorld, views);
        packages.Register(RenderGraphPackageCatalog.Indirect, indirect);
        var context = ContextOf(gpu);
        residency.ProduceFirstFrame(context);
        RenderGraphInstance View(string name) => new(Name: name, ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
            Passes: views.FragmentOf(name)!.Passes.Count,
            Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame);
        Assert.True(RenderGraphInstanceSet.TryCreate([View(Earlier), View(Captured),
            new(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame)],
            out var set, out var setRefusal), setRefusal?.Message);
        Assert.True(RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[3], Captured, packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), refusal?.Message);
        using var graph = runtime;
        var sources = new Dictionary<string, nint>(StringComparer.Ordinal);
        var completed = new Dictionary<string, int>(StringComparer.Ordinal);
        gpu.WriteReadback = (name, bytes) => {
            if (name.Detail != "indirect-deferred") { return; }
            var destination = naming.Applied.Last(item => item.Kind == GpuObjectKind.Buffer && item.Name == name.ToString()).Handle;
            var copy = copies.Last(item => item.Destination == destination);
            var sourceName = naming.Applied.Last(item => item.Kind == GpuObjectKind.Buffer && item.Handle == copy.Source).Name;
            // Model two different GPU outcomes through the actual copied source, not the destination's label:
            // the earlier camera keeps deferring; only the captured camera's own buffer contains a completed zero.
            var deferred = sourceName.StartsWith(Captured + "/", StringComparison.Ordinal) ? 0u : 1u;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, deferred);
            Assert.Equal(0UL, copy.SourceOffset);
            Assert.Equal((ulong)sizeof(uint), copy.Bytes);
            sources[name.Owner!] = copy.Source;
            completed[name.Owner!] = completed.GetValueOrDefault(name.Owner!) + 1;
        };
        var frame = 0L;
        void Produce(bool complete = true) {
            if (complete) { foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; } }
            var scheduled = new RenderGraphFrame(DisplayHeight: (int)Extent, DisplayWidth: (int)Extent, DisplayHertz: 60,
                Footprints: [], Index: frame, Tick: frame++, Roots: [new(Height: 1, Width: 1, Instance: Earlier), new(Height: 1, Width: 1, Instance: Captured)]);
            _ = graph.ProduceFrame(frame: scheduled, context: context);
        }
        TestLiveness.Within(frames: 64, step: () => {
            Produce();
            return views.CaptureReadinessOf(Captured).IsRendered && completed.GetValueOrDefault(Earlier) > 0;
        }, building: () => Enumerable.Range(0, 3).Any(index => graph.Node(index).IsBuildingCandidate), reason: () => graph.Render.Reason);
        var cache = residency.Tables!.Indirect!;
        Assert.NotEqual(sources[Earlier], sources[Captured]);
        Assert.NotEqual(cache.Buffer.BufferHandle, sources[Captured]);
        Assert.False(views.CaptureReadinessOf(Earlier).IsRendered);
        var observed = completed.GetValueOrDefault(Captured);
        Produce(complete: false);
        Assert.Equal(observed, completed.GetValueOrDefault(Captured));
        clears.Clear();
        for (var repeat = 0; repeat < 4; repeat++) {
            Produce();
            Assert.True(views.CaptureReadinessOf(Captured).IsRendered);
            Assert.False(views.CaptureReadinessOf(Earlier).IsRendered);
        }
        Assert.Equal(4, clears.Count(item => item.Buffer == sources[Earlier] && item.Bytes == sizeof(uint)));
        Assert.Equal(4, clears.Count(item => item.Buffer == sources[Captured] && item.Bytes == sizeof(uint)));
        Assert.DoesNotContain(clears, item => item.Buffer == cache.Buffer.BufferHandle);
        // Views reads the cleared predecessor before its preserving output. Both ports share this allocation;
        // the atomic write must follow that read, and its copy must follow the write on the same buffer.
        var expected = new[] {
            (GpuAccess.TransferWrite, GpuAccess.ShaderRead, GpuStage.Transfer, GpuStage.ComputeShader),
            (GpuAccess.ShaderRead, GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuStage.ComputeShader, GpuStage.ComputeShader),
            (GpuAccess.ShaderRead | GpuAccess.ShaderWrite, GpuAccess.TransferRead, GpuStage.ComputeShader, GpuStage.Transfer),
        };
        foreach (var (owner, buffer) in sources) {
            var actual = transitions.Where(item => item.Buffer == buffer)
                .Select(item => (item.SourceAccess, item.DestinationAccess, item.SourceStage, item.DestinationStage)).ToArray();
            var ordered = Enumerable.Range(0, Math.Max(0, actual.Length - expected.Length + 1))
                .Any(index => actual.AsSpan(index, expected.Length).SequenceEqual(expected));
            Assert.True(ordered,
                $"Camera '{owner}' copied source buffer {buffer}; expected reset/read/write/copy chain {string.Join("; ", expected)}; actual transitions: {string.Join("; ", actual)}");
        }
    }
}
