using System.Collections.Concurrent;
using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The comparison methods (<c>world.indirect-method screen|cone</c>) live in their own receiver kernel, which a
/// residency leases only once a view selects one with indirect light on; the default receiver carries the cache method
/// alone.</summary>
public sealed partial class SdfIndirectComparisonPipelinesLawTests {
    private static readonly string Comparison = SdfKernelSet.StemOf(kernel: SdfKernel.ReceiverComparison);

    [Fact]
    public void TheCacheMethodLeasesNoComparisonReceiverAndAComparisonMethodLeasesItOnce() {
        var created = new ConcurrentBag<string>();
        var gpu = new FakeGpuDevice { BeforeComputePipeline = description => created.Add(item: description.Name) };
        var source = new Source { Frame = Frame(method: SdfIndirectMethod.Cache, tier: SdfIndirectTier.Medium) };
        using var residency = Residency(catalog: SdfTestPipelines.Cache(), source: source);
        var context = Context(gpu: gpu);

        residency.ProduceFirstFrame(context: in context);
        _ = residency.WaitPipelineBuilds(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: residency.Produce(context: in context), userMessage: residency.NotReadyReason);
        Assert.DoesNotContain(collection: created, filter: static name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: Comparison));

        foreach (var method in new[] { SdfIndirectMethod.Screen, SdfIndirectMethod.Cone, SdfIndirectMethod.Cache, SdfIndirectMethod.Screen }) {
            source.Frame = Frame(method: method, tier: SdfIndirectTier.Medium);
            _ = residency.Produce(context: in context);
            _ = residency.WaitPipelineBuilds(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(condition: residency.Produce(context: in context), userMessage: residency.NotReadyReason);
            Assert.Single(collection: created, predicate: static name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: Comparison));
        }
    }
    [Fact]
    public void AComparisonMethodWithIndirectLightOffLeasesNoComparisonReceiver() {
        var created = new ConcurrentBag<string>();
        var gpu = new FakeGpuDevice { BeforeComputePipeline = description => created.Add(item: description.Name) };
        var source = new Source { Frame = Frame(method: SdfIndirectMethod.Screen, tier: SdfIndirectTier.Off) };
        using var residency = Residency(catalog: SdfTestPipelines.Cache(), source: source);
        var context = Context(gpu: gpu);

        residency.ProduceFirstFrame(context: in context);
        _ = residency.WaitPipelineBuilds(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: residency.Produce(context: in context), userMessage: residency.NotReadyReason);
        Assert.DoesNotContain(collection: created, filter: static name => name.StartsWith(comparisonType: StringComparison.Ordinal, value: Comparison));
    }
    [Fact]
    public void TheDefaultReceiverCompilesNoComparisonMethod() {
        var receiver = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-receiver.hlsli"));
        var blocks = ComparisonBlock().Matches(input: receiver);

        Assert.True(condition: (blocks.Count > 0), userMessage: "The receiver has no SDF_INDIRECT_COMPARISON block.");
        // The comparison receiver calls the comparison methods, and the default receiver the near-field sample.
        Assert.Contains(collection: blocks, filter: static block => block.Groups["comparison"].Value.Contains(comparisonType: StringComparison.Ordinal, value: "sdfIndirectAlternativeBegin("));
        Assert.Contains(collection: blocks, filter: static block => block.Groups["default"].Value.Contains(comparisonType: StringComparison.Ordinal, value: "sdfIndirectNearIncomingBegin("));
        var outside = receiver;

        foreach (var block in blocks.Reverse()) {
            Assert.DoesNotContain(expectedSubstring: "sdfIndirectAlternativeBegin(", actualString: block.Groups["default"].Value);
            Assert.DoesNotContain(expectedSubstring: "sdfIndirectAlternativeProc", actualString: block.Groups["default"].Value);
            Assert.DoesNotContain(expectedSubstring: "sdfIndirectNearIncoming", actualString: block.Groups["comparison"].Value);
            outside = outside.Remove(startIndex: block.Index, count: block.Length);
        }
        Assert.DoesNotContain(actualString: outside, expectedSubstring: "sdfIndirectAlternativeBegin(");
        Assert.DoesNotContain(actualString: outside, expectedSubstring: "sdfIndirectAlternativeProc");
    }

    [GeneratedRegex(pattern: @"#ifdef SDF_INDIRECT_COMPARISON(?<comparison>.*?)#else(?<default>.*?)#endif", options: RegexOptions.Singleline)]
    private static partial Regex ComparisonBlock();
    private static SdfFrame Frame(SdfIndirectTier tier, SdfIndirectMethod method) {
        var builder = new SdfProgramBuilder();

        builder.Sphere(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radius: 1f);
        var view = new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(position: new Vector3(x: 0f, y: 0f, z: -5f), target: Vector3.Zero,
            fieldOfViewRadians: 1f, viewportWidth: 32, viewportHeight: 32), Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f));

        return new SdfFrame(Program: builder.Build(), ProgramChanged: false, Time: 0f, Views: [view with { Quality = view.Quality with { IndirectMethod = method } }]) {
            FarDistance = 12f,
            IndirectTier = tier,
        };
    }
    private static SdfWorldResidency Residency(Source source, SdfWorldPipelineCatalog catalog) => new(
        pipelines: catalog, frameSource: source, kernels: SdfTestPipelines.Kernels(), name: "comparison", width: 32, height: 32, brickPoolVoxelCapacity: 0);
    private static FrameContext Context(FakeGpuDevice gpu) => new(AccumulatorTicks: 0UL, DeltaTicks: 0UL, ElapsedTicks: 0UL, FrameDeltaTicks: 0UL,
        Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }), StepTicks: 0UL, TargetHeight: 32, TargetWidth: 32);

    private sealed class Source : ISdfFrameSource {
        public required SdfFrame Frame { get; set; }

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => Frame;
    }
}
