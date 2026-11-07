using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void PaletteOnlyShadeDemandChangesRunTheSurfacePass() {
        using var rig = new TemporalRig(views: 1, cadence: true);
        SdfProgram Palette(float wrap) {
            var shade = SdfShadeProgram.Compile(new("wrap", [], [new(SdfShadePhase.Surface, "wrap",
                new("constant", Bits: [BitConverter.SingleToUInt32Bits(wrap)]))]));
            return SdfProgram.Of(rig.SourceFrame.Program.Instructions, new SdfAppearance([new(Vector3.One, Shade: shade)]));
        }
        rig.SourceFrame = rig.SourceFrame with { Program = Palette(0f), ProgramChanged = true };
        rig.Produce();
        rig.SourceFrame = rig.SourceFrame with { ProgramChanged = false };
        for (var frame = 0; frame < 12; frame++) { rig.Produce(); }
        Assert.True(rig.Stood());
        rig.SourceFrame = rig.SourceFrame with { Program = Palette(1f), ProgramChanged = true };
        rig.Produce();
        Assert.Contains(SdfWorldPackage.Parts.Surface, Executed(CadenceWork(rig)));
    }
}
