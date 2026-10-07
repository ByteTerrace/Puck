using System.Buffers.Binary;
using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void HeavyFieldsSplitShadeSweepsWhileLightFieldsKeepTheirAdmission() {
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.Medium);
        var light = CostFrame(shapes: 1);
        var heavy = CostFrame(shapes: 768);
        var original = SdfIndirectWork.ShadeProbeBudget(layout: layout, lights: light.Lights);
        var lightBudget = SdfIndirectWork.ShadeProbeBudget(frame: light, layout: layout);
        var heavyBudget = SdfIndirectWork.ShadeProbeBudget(frame: heavy, layout: layout);

        Assert.Equal(actual: lightBudget, expected: original);
        Assert.True(condition: (heavyBudget < lightBudget), userMessage: "A heavy field must admit fewer shade probes than the same light field.");
        Assert.InRange((SdfIndirectCost.EstimateCost((((long)heavyBudget) * SdfIndirectCost.ShadeQueries(frame: heavy, layout: layout)), heavy.Program.InstructionCount)
            + (heavyBudget * SdfIndirectCost.ShadeCacheCost(layout: layout))), 1, SdfIndirectCost.SubmissionCostLimit);
        IReadOnlyList<IrradianceProbeKey>[] levels = [[.. Enumerable.Range(count: 257, start: 0).Select(selector: x => new IrradianceProbeKey(Level: 0, X: x, Y: 0, Z: 0))]];
        var lightSolve = new IrradianceSolveSchedule(bounces: 2, levels: levels, probeBudget: lightBudget, publishedGeneration: -1);
        var heavySolve = new IrradianceSolveSchedule(bounces: 2, levels: levels, probeBudget: heavyBudget, publishedGeneration: -1);
        var lightVisits = Drain(solve: lightSolve);
        var heavyVisits = Drain(solve: heavySolve);

        Assert.Equal(actual: heavyVisits.Probes, expected: lightVisits.Probes);
        Assert.True(condition: (heavyVisits.Submissions > lightVisits.Submissions));
        Assert.Equal(lightSolve.PublishedGeneration, heavySolve.PublishedGeneration);
        Assert.Equal(3, heavySolve.CompletedSweeps);

        static (List<IrradianceProbeKey> Probes, int Submissions) Drain(IrradianceSolveSchedule solve) {
            var probes = new List<IrradianceProbeKey>();
            var submissions = 0;

            while (!solve.IsComplete) {
                var batch = solve.Plan()!;

                Assert.Same(batch, solve.Plan());
                probes.AddRange(collection: batch.Probes);
                solve.Submitted();
                submissions++;
            }
            return (probes, submissions);
        }
    }
    [Fact]
    public void DisabledDirectStillBoundsShadeCacheTraversal() {
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.High);
        var source = CostFrame(shapes: 1) with { IndirectSources = SdfIndirectSources.Feedback };
        var admitted = SdfIndirectWork.ShadeProbeBudget(frame: source, layout: layout);

        Assert.True(condition: (admitted < layout.ShadeBudget), userMessage: "Continuation traversal must be bounded even without field visibility queries.");
        Assert.InRange((admitted * SdfIndirectCost.ShadeCacheCost(layout: layout)), 1, SdfIndirectCost.SubmissionCostLimit);
    }
    [Fact]
    public void ResidencyScalesTransportAdmissionByItsActualField() {
        var light = PlanCostFrame(source: CostFrame(shapes: 1));
        var heavy = PlanCostFrame(source: CostFrame(shapes: 768));

        Assert.Equal(actual: light.Placed, expected: 4);
        Assert.True(condition: (heavy.Classified < light.Classified), userMessage: "The heavy residency must split brick classification across frames.");
        Assert.True(condition: (heavy.Traced < light.Traced), userMessage: "The heavy residency must split trace strata across frames.");
        Assert.InRange(SdfIndirectCost.EstimateCost(instructionCount: heavy.Instructions, queries: (((long)heavy.Classified) * SdfIndirectCost.ClassifyQueries)), 1, SdfIndirectCost.SubmissionCostLimit);
        Assert.InRange(SdfIndirectCost.EstimateCost(instructionCount: heavy.Instructions, queries: (((long)heavy.Traced) * SdfIndirectCost.TraceQueries)), 1, SdfIndirectCost.SubmissionCostLimit);
    }
    [Fact]
    public void ReceiverPassBlockUsesTheFieldScaledSharedAllowance() {
        var heavy = PlanCostFrame(source: CostFrame(shapes: 768));

        Assert.True(condition: (heavy.ReceiverBudget < new SdfIndirectLayout(tier: SdfIndirectTier.Medium).ReceiverProofBudget),
            userMessage: "The uploaded receiver allowance must scale with the field program.");
        Assert.InRange(SdfIndirectCost.EstimateCost(instructionCount: heavy.Instructions, queries: (((long)heavy.ReceiverBudget) * SdfIndirectCost.ReceiverQueries)), 1, SdfIndirectCost.SubmissionCostLimit);
    }

    private static (int Placed, int Classified, int Traced, uint ReceiverBudget, int Instructions) PlanCostFrame(SdfFrame source) {
        var gpu = new FakeGpuDevice();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-cost", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;
        var placed = 0;
        var classified = 0;
        var traced = 0;

        for (var frame = 0; (frame < 32); frame++) {
            residency.BeginFrame();
            _ = residency.Submit(context: context);
            placed = Math.Max(val1: placed, val2: cache.PlaceCount);
            classified = Math.Max(val1: classified, val2: cache.ClassifyCount);
            traced = Math.Max(val1: traced, val2: cache.TraceCount);
            var queries = (((((long)cache.PlaceCount) * SdfIndirectCost.PlaceQueries)
                + (((long)cache.ClassifyCount) * SdfIndirectCost.ClassifyQueries)) + (((long)cache.TraceCount) * SdfIndirectCost.TraceQueries));

            Assert.InRange(SdfIndirectCost.EstimateCost(queries, source.Program.InstructionCount), 0, SdfIndirectCost.SubmissionCostLimit);
            cache.Submitted();
        }
        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.WriteIndirect(block: block, cache: cache);
        var receiverBudget = BinaryPrimitives.ReadUInt32LittleEndian(source: block.AsSpan(start: ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.IndirectReceiverProofs))));

        return (placed, classified, traced, receiverBudget, source.Program.InstructionCount);
    }
    private static SdfFrame CostFrame(int shapes) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.BeginInstance(Vector3.Zero, 2);
        for (var index = 0; (index < shapes); index++) { builder.Sphere(1, material); }
        builder.EndInstance();
        return Frame() with { Program = builder.Build(), FarDistance = 1, IndirectTier = SdfIndirectTier.Medium };
    }
}
