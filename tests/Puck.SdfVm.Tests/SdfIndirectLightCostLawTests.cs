using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    [Theory]
    public async Task HeavyFieldsSplitLightMapsWhileLightFieldsRetainTheirRowsAsync(SdfIndirectTier tier) {
        var light = await LightMapCostSubmissionsAsync(tier, shapes: 1);
        var heavy = await LightMapCostSubmissionsAsync(tier, shapes: 768);
        var rows = SdfIndirectLightViews.RowsPerSubmission(layout: new SdfIndirectLayout(tier: tier));

        Assert.Equal(actual: light, expected: (((512 + rows) - 1) / rows));
        Assert.True(condition: (heavy > light), userMessage: $"Heavy-field maps must use more submissions: light={light}, heavy={heavy}.");
    }

    private static async Task<int> LightMapCostSubmissionsAsync(SdfIndirectTier tier, int shapes) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2);
        for (var shape = 0; (shape < shapes); shape++) { builder.Sphere(material: material, radius: 1); }
        builder.EndInstance();
        var lights = SdfLights.Default();

        lights.ShadowSlots.SetOwner(owner: "sun", slot: 0);
        var frame = Frame() with { Program = builder.Build(), Lights = lights, IndirectTier = tier, FarDistance = 12 };
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame), height: Extent, width: Extent, name: "light-cost",
            kernels: SdfTestPipelines.Kernels(), pipelines: pipelines);
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        const string Instance = "light-cost.camera";

        views.RegisterLightView(name: Instance, residency: residency);
        var fragment = views.FragmentOf(instance: Instance)!;
        var pass = fragment.Passes.Single(predicate: candidate => (candidate.Name == SdfWorldPackage.LightDepth));
        var recorderContext = new RenderGraphPackageRecorderContext(Device: gpu, Services: gpu.Services,
            Instance: Instance, Pass: SdfWorldPackage.LightDepth, Part: SdfWorldPackage.LightDepth,
            Package: RenderGraphPackageCatalog.SdfWorld, Pipelines: pipelines.Pipelines, HostsOnDirectX: false,
            InFlightFrames: 1, Width: 512, Height: 512, Parameters: SdfWorldInterfaces.WorldParameters,
            Inputs: [.. pass.Inputs.Select(selector: port => fragment.Resources.Single(predicate: resource => (resource.Name == port.Name)))],
            Outputs: [.. pass.Outputs.Select(selector: port => fragment.Resources.Single(predicate: resource => (resource.Name == port.Name)))]);
        var built = await views.BuildAsync(recorderContext, CancellationToken.None);
        using var block = gpu.Services.BufferFactory.CreateHostVisible(name: default,
            sizeBytes: recorderContext.Parameters.SizeBytes, usage: GpuBufferUsage.Uniform);
        var pool = gpu.Services.Bindings.CreatePool(name: default,
            sizes: GpuDescriptorPoolSizes.ForGroups(recorderContext.Parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute).Groups));

        try {
            using var recorder = views.Create(recorderContext, built, new RenderGraphPackageGroups(
                DescriptorPool: pool, FrameBlocks: [block], OutputImages: [], PassBlocks: [block], Regions: []));
            var visited = new bool[512, 512];
            var count = 0;
            var constants = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "src/Puck.SdfVm/Assets/Shaders/Sdf/march/sdf-march-constants.hlsli"));

            int Steps(string name) => int.Parse(Regex.Match(input: constants, pattern: $@"\b{name} = (\d+);").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            var beamSteps = ((Steps(name: "ConeMarchSteps") + Steps(name: "TileGapSteps")) + Steps(name: "TileFarSteps"));

            Assert.Equal(actual: beamSteps, expected: SdfIndirectLightLayout.BeamSteps);
            if (shapes > 1) {
                views.BeginFrame(context: context);
                Assert.False(condition: recorder.Skips(context: context));
                _ = residency.Submit(context: context);
                Assert.InRange(residency.IndirectLightViews.ColumnCount, 8, 504);
                Assert.False(condition: residency.IndirectLightViews.Submitted());
                views.BeginFrame(context: context);
                Assert.False(condition: recorder.Skips(context: context));
                Assert.True(condition: (residency.IndirectLightViews.FirstColumn > 0));
                residency.IndirectLightViews.InvalidateStorage();
            }
            while ((residency.IndirectLightViews.Publications == 0) && (count < 4096)) {
                views.BeginFrame(context: context);
                Assert.False(condition: recorder.Skips(context: context));
                _ = residency.Submit(context: context);
                var schedule = residency.IndirectLightViews;

                Assert.True(condition: SdfIndirectLightLayout.TryUnpackSlice(schedule.Slice, out _, out var row, out var rows, out var column, out var columns));
                var tiles = (((((column + columns) + 15) / 16) - (column / 16)) * ((((row + rows) + 15) / 16) - (row / 16)));
                var queries = (((((long)rows) * columns) * 128) + (tiles * beamSteps));
                var cost = (queries * frame.Program.InstructionCount);

                Assert.InRange(actual: cost, high: SdfIndirectCost.SubmissionCostLimit, low: 1);
                Assert.InRange(queries, 1, residency.Tables!.Indirect!.Layout.TraceEvaluationCeiling);
                Assert.Equal(queries, schedule.EstimatedQueries);
                Assert.False(condition: schedule.Snapshot(index: schedule.Pending).Valid);
                for (var y = row; (y < (row + rows)); y++) {
                    for (var x = column; (x < (column + columns)); x++) {
                        Assert.False(condition: visited[y, x], userMessage: $"Texel ({x}, {y}) submitted twice.");
                        visited[y, x] = true;
                    }
                }
                if (schedule.Submitted()) {
                    Assert.All(visited.Cast<bool>(), value => Assert.True(condition: value));
                }
                count++;
            }
            Assert.Equal(1UL, residency.IndirectLightViews.Publications);
            return count;
        } finally {
            gpu.Services.Bindings.DestroyPool(poolHandle: pool);
        }
    }
}
