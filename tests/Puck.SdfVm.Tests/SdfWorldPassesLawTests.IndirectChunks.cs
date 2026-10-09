using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // Each kind's unit, with an item count and a field heavy enough that one of its items exceeds a submission.
    public static TheoryData<string, int, int> OverBudgetKinds => new() {
        { SdfWorldPackage.IndirectClassify, 4, 1_536 },
        { SdfWorldPackage.IndirectClassify, 8, 11_217 },
        { SdfWorldPackage.IndirectTrace, 128, 11_217 },
        { SdfWorldPackage.IndirectShade, 1, 11_217 },
        { SdfWorldPackage.IndirectShade, 3, 60_000 },
    };

    [MemberData(nameof(OverBudgetKinds))]
    [Theory]
    public void AnOverBudgetItemSplitsIntoUnitChunksThatEachFitTheBudgetAndTogetherCoverExactlyTheItem(string kind, int count, int instructions) {
        var units = UnitsOf(kind: kind);

        Assert.True(condition: (units.ItemCost(instructionCount: instructions) > SdfIndirectCost.SubmissionCostLimit),
            userMessage: "The fixture's item must exceed one submission's cap.");
        foreach (var firstUnit in new[] { 0, (units.UnitsPerItem / 2), (units.UnitsPerItem + 1) }) {
            if (firstUnit >= (count * units.UnitsPerItem)) { continue; }
            var refusal = Record.Exception(testCode: () => SdfIndirectCost.Admit(count: count, firstUnit: firstUnit, instructionCount: instructions, units: units));

            Assert.True(condition: (refusal is null), userMessage: $"An over-budget {kind} item must split into chunks, not refuse: {refusal?.Message}");
            var chunks = SdfIndirectCost.Admit(count: count, firstUnit: firstUnit, instructionCount: instructions, units: units);
            var position = firstUnit;

            foreach (var chunk in chunks) {
                Assert.Equal(expected: position, actual: ((chunk.ItemFirst * units.UnitsPerItem) + chunk.UnitFirst));
                Assert.InRange(actual: chunk.UnitFirst, high: (units.UnitsPerItem - 1), low: 0);
                Assert.InRange(actual: units.CostOf(chunk: chunk, instructionCount: instructions), high: SdfIndirectCost.SubmissionCostLimit, low: 1);
                position += chunk.UnitCount;
            }
            Assert.Equal(expected: (count * units.UnitsPerItem), actual: position);
            Assert.Contains(collection: chunks, filter: chunk => (chunk.PartialItems(unitsPerItem: units.UnitsPerItem) != 0));
        }
        var light = SdfIndirectCost.Admit(count: count, instructionCount: 1, units: units);

        Assert.All(collection: light, action: chunk => Assert.Equal(expected: 0, actual: chunk.PartialItems(unitsPerItem: units.UnitsPerItem)));
    }
    [Fact]
    public void AHeavyFieldSubmitsEachPlanInOrderedChunksThatEachFitAndCommitsThePlanOnlyWithItsLastChunk() {
        var source = CostFrame(shapes: 1_536);
        var instructions = source.Program.InstructionCount;

        Assert.True(condition: (SdfIndirectCost.ClassifyUnits.ItemCost(instructionCount: instructions) > SdfIndirectCost.SubmissionCostLimit),
            userMessage: "One brick's partition must exceed a submission.");
        var gpu = new FakeGpuDevice();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-chunks", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;
        string[] parts = [SdfWorldPackage.IndirectPlace, SdfWorldPackage.IndirectClassify, SdfWorldPackage.IndirectTrace];
        var cursors = new int[parts.Length];
        var splitChunks = 0;
        var splitPlans = 0;
        var splitInPlan = false;

        for (var frame = 0; (frame < 96); frame++) {
            residency.BeginFrame();
            var failure = Record.Exception(testCode: () => residency.Submit(context: context));

            Assert.True(condition: (failure is null), userMessage: $"An over-budget transport item must be split, not refused: {failure?.Message}");
            Assert.InRange(actual: cache.TransportStepCost, high: SdfIndirectCost.SubmissionCostLimit, low: 0);
            int[] totals = [(cache.PlaceCount * 64), (cache.ClassifyCount * 64), (cache.TraceCount * 64)];

            for (var kind = 0; (kind < parts.Length); kind++) {
                if (cache.TransportChunk(part: parts[kind]) is not { } chunk) { continue; }
                // Earlier kinds' cursors already include this step's chunks, which their passes record first.
                for (var earlier = 0; (earlier < kind); earlier++) {
                    Assert.True(condition: (cursors[earlier] == totals[earlier]), userMessage: $"A {parts[kind]} chunk must follow every {parts[earlier]} chunk of its plan.");
                }
                Assert.Equal(expected: cursors[kind], actual: ((chunk.ItemFirst * 64) + chunk.UnitFirst));
                Assert.InRange(actual: SdfIndirectCache.TransportUnits(part: parts[kind]).CostOf(chunk: chunk, instructionCount: instructions),
                    high: SdfIndirectCost.SubmissionCostLimit, low: 1);
                if (chunk.PartialItems(unitsPerItem: 64) != 0) { splitChunks++; splitInPlan = true; }
                cursors[kind] += chunk.UnitCount;
                Assert.InRange(actual: cursors[kind], high: totals[kind], low: 0);
            }
            var completes = cursors.SequenceEqual(second: totals);

            if (!completes) { Assert.False(condition: cache.IsComplete, userMessage: "A plan with unsubmitted chunks is incomplete."); }
            cache.Submitted();
            if (completes) {
                Assert.Equal(expected: 0, actual: cache.PendingTransportChunks);
                if (splitInPlan) { splitPlans++; }
                splitInPlan = false;
                Array.Clear(array: cursors);
            }
        }
        Assert.True(condition: (splitChunks > 1), userMessage: "The heavy field must split a brick's partition across submissions.");
        Assert.True(condition: (splitPlans > 0), userMessage: "A plan holding a split item must commit after its last chunk.");
    }
    [Fact]
    public void ASingleOverLimitQueryRefusesByName() {
        const int Instructions = (((int)SdfIndirectCost.SubmissionCostLimit) + 1);

        foreach (var units in new[] { SdfIndirectCost.PlaceUnits, SdfIndirectCost.ClassifyUnits, SdfIndirectCost.TraceUnits, SdfIndirectCost.ReceiverUnits }) {
            Assert.StartsWith(expectedStartString: "one field query costs", actualString: units.RefusalOf(instructionCount: Instructions));
            Exception? refusal = null;

            try { _ = SdfIndirectCost.Admit(count: 1, instructionCount: Instructions, units: units); } catch (SdfIndirectCostRefusedException exception) { refusal = exception; }
            Assert.NotNull(@object: refusal);
            Assert.Contains(expectedSubstring: "one field query costs", actualString: refusal.Message);
        }
        var frame = CostFrame(shapes: 1);

        Assert.Null(@object: SdfIndirectCost.RefusalOf(frame: frame, layout: new SdfIndirectLayout(tier: SdfIndirectTier.Medium)));
    }
    [Fact]
    public void AFieldWhoseIndivisibleUnitExceedsASubmissionRendersWithoutIndirectLightingAndWaitsForNothing() {
        var source = CostFrame(shapes: 75_000);

        Assert.NotNull(@object: SdfIndirectCost.ClassifyUnits.RefusalOf(instructionCount: source.Program.InstructionCount));
        var gpu = new FakeGpuDevice();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-refused", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(gpu: gpu);
        var failure = Record.Exception(testCode: () => {
            TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
                reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
            for (var frame = 0; (frame < 4); frame++) {
                residency.BeginFrame();
                _ = residency.Submit(context: context);
            }
        });

        Assert.True(condition: (failure is null), userMessage: $"A refused field must render without indirect lighting, not throw: {failure?.Message}");
        Assert.Contains(expectedSubstring: SdfWorldPackage.IndirectClassify, actualString: residency.IndirectRefusal);
        Assert.Equal(expected: SdfIndirectTier.Off, actual: residency.IndirectTier);
        Assert.Equal(expected: SdfIndirectTier.Off, actual: residency.Frame!.IndirectTier);
        Assert.Null(@object: residency.Tables!.Indirect);
        Assert.False(condition: residency.IsIndirectReady, userMessage: "An off cache is never a solve to wait for.");
    }

    // A shaded ray with one directional channel's bounded visibility march.
    private static SdfIndirectUnits UnitsOf(string kind) => kind switch {
        SdfWorldPackage.IndirectShade => (SdfIndirectCost.ShadeUnits(frame: CostFrame(shapes: 1), layout: new SdfIndirectLayout(tier: SdfIndirectTier.Medium))
            with { QueriesPerUnit = SdfIndirectLightLayout.MarchSteps }),
        _ => SdfIndirectCache.TransportUnits(part: kind),
    };
}
