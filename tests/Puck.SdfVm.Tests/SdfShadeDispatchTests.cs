using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfShadeDispatchTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPaddedGroupIsCoveredOnceAndEveryJobFits(bool glass) {
        var shade = SdfShadeProgram.Compile(new("clock", [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: "clock"))]));
        var plan = SdfShadeDispatch.Plan(8193, 4321, [shade, shade], SdfShadeProgram.MaxLights, glass);
        Assert.Equal([shade.Identity], plan.Identities);
        uint end = 0;
        foreach (var job in plan.Jobs) {
            Assert.Equal(end, job.FirstGroup);
            Assert.InRange(job.Groups, 1u, 65535u);
            Assert.Equal(job.Groups * 64UL * shade.PixelWork(SdfShadeProgram.MaxLights) * (glass ? 2UL : 1UL), job.Work);
            Assert.InRange(job.Work, 1UL, SdfShadeProgram.MaxWork);
            end += job.Groups;
        }
        Assert.Equal(((8193u + 7) / 8) * ((4321u + 7) / 8), end);
    }

    // A bin's count exists only on the GPU, so each bin records enough jobs to hold every tile of the extent, and each job
    // of a bin that may run a program fits the bound; tiles with no program, and an extent with no program, run only the
    // engine's fixed stage and are one dispatch within the group-count limit.
    [Fact]
    public void EveryBinHoldsTheExtentAndEveryAuthoredJobFits() {
        var shade = SdfShadeProgram.Compile(new("clock", [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: "clock"))]));
        var tiles = ((1920u + 7) / 8) * ((1080u + 7) / 8);
        var plan = SdfShadeDispatch.PlanBins(1920, 1080, [shade], SdfShadeProgram.MaxLights);

        Assert.True(plan.Binned);
        Assert.Empty(plan.Jobs);
        Assert.Equal([shade.Identity], plan.Identities);
        Assert.Equal(2, plan.Bins.Count);
        Assert.Equal(1u, plan.Bins[0].Jobs);
        Assert.Equal(65535u, plan.Bins[0].Width);
        Assert.Equal(64UL * shade.PixelWork(SdfShadeProgram.MaxLights), plan.Bins[1].TileWork);
        Assert.InRange(plan.Bins[1].Width * plan.Bins[1].TileWork, 1UL, SdfShadeProgram.MaxWork);
        foreach (var bin in plan.Bins) {
            Assert.InRange(bin.Jobs, 1u, tiles);
            Assert.True((ulong)bin.Jobs * bin.Width >= tiles);
            Assert.True((ulong)(bin.Jobs - 1u) * bin.Width < tiles);
        }

        var job = Assert.Single(SdfShadeDispatch.Plan(1920, 1080, [], SdfShadeProgram.MaxLights, glass: false).Jobs);

        Assert.Equal(0u, job.FirstGroup);
        Assert.Equal(tiles, job.Groups);
    }

    [Fact]
    public void SelectedProgramsAloneSupplyCadenceReads() {
        SdfShadeProgram Read(string name) => SdfShadeProgram.Compile(new(name, [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: name))]));
        var appearance = new SdfAppearance([new(System.Numerics.Vector3.One)]);
        Assert.Equal(SdfShadeReads.Clock, appearance.ResolveShadeReads(true, Read("clock")));
        Assert.Equal(SdfShadeReads.World2, appearance.ResolveShadeReads(true, Read("lanes.world2")));
        Assert.Equal(SdfShadeReads.None, appearance.ResolveShadeReads(true, Read("material.roughness")));
        Assert.Equal(SdfShadeReads.None, appearance.ResolveShadeReads(false, Read("clock")));
    }
}
