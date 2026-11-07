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
