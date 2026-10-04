using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

[Collection(AllocationCollection.Name)]
public sealed class WorldIndirectPickTextLawTests {
    private static SdfIndirectPick Pick() => new(SdfIndirectPickStatus.Resolved, SdfIndirectTier.High, 1, 255,
        Vector3.One, Vector3.UnitY, .25f, Vector3.UnitZ, 1, 7,
        Enumerable.Range(0, 8).Select(index => new SdfIndirectPickCorner(index, IrradianceProbeClass.Active, .125f, 7)).ToArray(),
        new SdfIndirectPickSources(Vector3.One, 2 * Vector3.One, 3 * Vector3.One, 4 * Vector3.One, 5 * Vector3.One),
        new SdfIndirectCacheSnapshot(3, SdfIndirectTier.High, 9, 10, 64, true, 0, 0, 0, 0, 8, false, 1, 7, 2, false, default, [], []),
        new SdfIndirectCensus(11, 12, 13, 14, 15), null) { SourcesEnabled = SdfIndirectSources.Direct | SdfIndirectSources.Sky };

    [Fact]
    public void InspectorAndEchoShareCapturedWeightsSourcesDepthAndReferenceWithoutSteadyAllocation() {
        var pick = Pick();
        var reference = new WorldIndirectReferenceResult(null, "captured source unavailable", 17, 19, 1, 0, null);
        var formatter = new WorldIndirectPickText();
        var echo = formatter.Read(pick, reference);
        Assert.Contains("sources=0x09", echo);
        Assert.Contains("published-sweeps=2", echo);
        Assert.Contains("bounces=1 queries=17 casts=19", echo);
        Assert.Contains("unpublished=15", echo);
        Assert.Contains("sources feedback=2,2,2", echo);
        for (var index = 0; index < 8; index++) { Assert.Contains($"c{index}={index}/Active,w=0.125,p=7", echo); }
        var pixel = new SdfPickResult(1, 1, 1, 4, 4, 0, 0, 0, new SdfProgramBuilder().Build(), 0) { Indirect = pick };
        var snapshot = new WorldInspectorSnapshot { Pick = pixel, IndirectReference = reference, ReloadError = "none" };
        var inspector = new WorldInspectorText();
        inspector.Format(in snapshot);
        inspector.Finish();
        var panel = new string(inspector.Text);
        foreach (var row in echo.Split('\n')) { Assert.Contains(row, panel); }
        Assert.Contains("gpu-minus-reference unavailable", panel);
        Assert.Equal(0L, AllocationWindow.Least(() => {
            for (var index = 0; index < 100; index++) {
                _ = formatter.Read(pick, reference);
                inspector.Format(in snapshot);
                inspector.Finish();
            }
        }));
        Assert.Contains("method=screen", formatter.Read(pick with { Method = SdfIndirectMethod.Screen }));
        Assert.Equal("indirect unavailable: no fenced pixel", formatter.Read(null));
    }
}
