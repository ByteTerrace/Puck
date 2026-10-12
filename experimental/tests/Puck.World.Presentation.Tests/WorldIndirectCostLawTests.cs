using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>The authored parity field admits finite indirect work without exceeding the instruction-cost cap.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldIndirectCostLawTests(ITestOutputHelper output) {
    [Fact]
    public void ParityFieldSplitsShadeAndAdmitsAtomicTransportWork() {
        const string Path = "tests/Puck.Parity/parity.puck";
        var definition = AuthoredGameFixtures.Load(relativePath: Path);
        var composed = ComposedSdfWorldFixture.Capture(definition: definition, relativePath: Path);
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var environment = resolver.Resolve(definition: definition, mirror: ClientFixtures.StateMirror(definition), revision: 0);
        var frame = composed with {
            Lights = environment.Lights,
            IndirectSources = environment.Indirect.Gains.Sources,
            IndirectGains = environment.Indirect.Gains,
        };
        var instructions = frame.Program.InstructionCount;

        output.WriteLine(message: $"parity field instructions={instructions}; directional channels={SdfIndirectCost.DirectionalChannels(lights: frame.Lights)}");
        Assert.True(condition: (instructions > 0));
        foreach (var tier in new[] { SdfIndirectTier.Medium, SdfIndirectTier.High }) {
            var layout = new SdfIndirectLayout(tier: tier);
            var queryOnly = SdfIndirectWork.ShadeProbeBudget(layout: layout, lights: frame.Lights);
            var shade = SdfIndirectWork.ShadeProbeBudget(frame: frame, layout: layout);
            var queriesPerProbe = SdfIndirectCost.ShadeQueries(frame: frame, layout: layout);
            var shadeCost = (SdfIndirectCost.EstimateCost(instructionCount: instructions, queries: (((long)shade) * queriesPerProbe))
                + (shade * SdfIndirectCost.ShadeCacheCost(layout: layout)));

            output.WriteLine(message: $"{tier}: shade probes={shade}; query-only probes={queryOnly}; estimated cost={shadeCost}");
            Assert.InRange(actual: shade, high: queryOnly, low: 1);
            Assert.InRange(actual: shadeCost, high: SdfIndirectCost.SubmissionCostLimit, low: 1);
            Assert.True(condition: (shade < queryOnly), userMessage: "The actual parity field must split both tiers' query-only shade batches.");

            var place = SdfIndirectCost.WholeItems(count: layout.ClassifyBudget, instructionCount: instructions, units: SdfIndirectCost.PlaceUnits);
            var classify = SdfIndirectCost.WholeItems(count: layout.ClassifyBudget, instructionCount: instructions, units: SdfIndirectCost.ClassifyUnits);
            var trace = SdfIndirectCost.WholeItems(count: layout.TraceBudget, instructionCount: instructions, units: SdfIndirectCost.TraceUnits);

            output.WriteLine(message: $"{tier}: place bricks={place}; classify bricks={classify}; trace strata={trace}");
            Assert.InRange(place, 1, layout.ClassifyBudget);
            Assert.InRange(classify, 1, layout.ClassifyBudget);
            Assert.InRange(trace, 1, layout.TraceBudget);
            Assert.InRange(SdfIndirectCost.EstimateCost(instructionCount: instructions, queries: (((long)place) * SdfIndirectCost.PlaceQueries)), 1, SdfIndirectCost.SubmissionCostLimit);
            Assert.InRange(SdfIndirectCost.EstimateCost(instructionCount: instructions, queries: (((long)classify) * SdfIndirectCost.ClassifyQueries)), 1, SdfIndirectCost.SubmissionCostLimit);
            Assert.InRange(SdfIndirectCost.EstimateCost(instructionCount: instructions, queries: (((long)trace) * SdfIndirectCost.TraceQueries)), 1, SdfIndirectCost.SubmissionCostLimit);
        }
    }
}
