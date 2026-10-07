using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldIndirectPickTextLawTests {
    private static SdfIndirectPick Pick() => new(SdfIndirectPickStatus.Resolved, SdfIndirectTier.High, 1, 255,
        Vector3.One, Vector3.UnitY, .25f, Vector3.UnitZ, 1, 7,
        Enumerable.Range(count: 8, start: 0).Select(selector: index => new SdfIndirectPickCorner(Classification: IrradianceProbeClass.Active, Index: index, Publication: 7, Weight: .125f)).ToArray(),
        new SdfIndirectPickSources(Vector3.One, (2 * Vector3.One), (3 * Vector3.One), (4 * Vector3.One), (5 * Vector3.One)),
        new SdfIndirectCacheSnapshot(Allocation: 3, Bricks: [], Bytes: default, CompletedSweeps: 8, Epoch: 9, FarDistance: 64, Frozen: false, Levels: [], LightingComplete: false, PendingClassifications: 0, PendingPlacements: 0, PendingShades: 0, PendingTraces: 0, PublishedGeneration: 1, PublishedStamp: 7, PublishedSweeps: 2, Submission: 10, Tier: SdfIndirectTier.High, TraceComplete: true),
        new SdfIndirectCensus(Active: 11, Dormant: 14, Inactive: 13, Relocated: 12, Unpublished: 15), null) { SourcesEnabled = SdfIndirectSources.Direct | SdfIndirectSources.Sky };

    [InlineData(SdfIndirectNearOutcome.NotAttempted, false)]
    [InlineData(SdfIndirectNearOutcome.Unresolved, false)]
    [InlineData(SdfIndirectNearOutcome.Hit, true)]
    [InlineData(SdfIndirectNearOutcome.Continuation, true)]
    [Theory]
    public void NearReplacementNamesItsActualIncomingAnswerInsteadOfACacheReference(SdfIndirectNearOutcome outcome, bool replacement) {
        var pick = Pick() with { Near = outcome };
        var reference = WorldIndirectReference.Evaluate(pick);

        Assert.Null(value: reference.Difference);
        Assert.Null(value: reference.Estimate);
        Assert.Equal(0, reference.FieldQueries);
        Assert.Equal(0, reference.Casts);
        var text = new WorldIndirectPickText().Read(pick: pick, reference: reference);

        Assert.Contains($"near={outcome.ToString().ToLowerInvariant()}", text);
        if (replacement) {
            Assert.Contains($"Near {outcome} reference needs its sampled direction", reference.Refusal);
            Assert.Contains(actualString: text, expectedSubstring: "source-role=cache-fallback-not-near-reference");
        } else {
            Assert.DoesNotContain("Near ", reference.Refusal);
            Assert.Contains(actualString: text, expectedSubstring: "source-role=visible-cache-publication");
        }
        Assert.Contains(actualString: text, expectedSubstring: "gpu-minus-reference unavailable");
    }
    [Fact]
    public void InspectorAndEchoShareCapturedWeightsSourcesDepthAndReferenceWithoutSteadyAllocation() {
        var pick = Pick();
        var reference = new WorldIndirectReferenceResult(Casts: 19, Difference: null, Estimate: null, FeedbackBounces: 1, FieldQueries: 17, Refusal: "captured source unavailable", SourceSequence: 0);
        var formatter = new WorldIndirectPickText();
        var echo = formatter.Read(pick: pick, reference: reference);

        Assert.Contains(actualString: echo, expectedSubstring: "sources=0x09");
        Assert.Contains(actualString: echo, expectedSubstring: "published-sweeps=2");
        Assert.Contains(actualString: echo, expectedSubstring: "bounces=1 queries=17 casts=19");
        Assert.Contains(actualString: echo, expectedSubstring: "unpublished=15");
        Assert.Contains(actualString: echo, expectedSubstring: "sources feedback=2,2,2");
        for (var index = 0; (index < 8); index++) { Assert.Contains(actualString: echo, expectedSubstring: $"c{index}={index}/Active,w=0.125,p=7"); }
        var pixel = new SdfPickResult(1, 1, 1, 4, 4, 0, 0, 0, new SdfProgramBuilder().Build(), 0) { Indirect = pick };
        var snapshot = new WorldInspectorSnapshot { IndirectReference = reference, Pick = pixel, ReloadError = "none" };
        var inspector = new WorldInspectorText();

        inspector.Format(snapshot: in snapshot);
        inspector.Finish();
        var panel = new string(value: inspector.Text);

        foreach (var row in echo.Split('\n')) { Assert.Contains(actualString: panel, expectedSubstring: row); }
        Assert.Contains(actualString: panel, expectedSubstring: "gpu-minus-reference unavailable");
        Assert.Equal(0L, AllocationWindow.Least(() => {
            for (var index = 0; (index < 100); index++) {
                _ = formatter.Read(pick: pick, reference: reference);
                inspector.Format(snapshot: in snapshot);
                inspector.Finish();
            }
        }));
        Assert.Contains("method=screen", formatter.Read(pick with { Method = SdfIndirectMethod.Screen }));
        Assert.Equal("indirect unavailable: no fenced pixel", formatter.Read(null));
    }
}
