using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
    [Fact]
    public void TheFirstFramePacksItsDefaultShadeProgramIntoTheRegionItWasBuiltWith() {
        var gpu = new FakeGpuDevice(countCalls: true);
        var shade = SdfShadeProgram.Compile(new("clock", [], [new(SdfShadePhase.Surface, "roughness", new("input", Name: "clock"))]));
        using var rig = new Rig(device: gpu, shadeDefault: shade);
        var capacity = rig.Engine.ProgramWordCapacity;
        var waits = gpu.Count(key: "IGpuDeviceContext.WaitIdle");

        Assert.Equal(rig.Frame.Program.Words.Length + shade.Words.Length + shade.ParameterWords.Length, capacity);
        rig.Render();
        Assert.Equal(waits, gpu.Count(key: "IGpuDeviceContext.WaitIdle"));
        Assert.Equal(capacity, rig.Engine.ProgramWordCapacity);

        // A larger program grows the region once, keeping room for the default, and nothing waits for the device.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: System.Numerics.Vector3.One));

        for (var index = 0; (index < 64); index++) { builder.Sphere(material: material, radius: (1f + index)); }
        var larger = builder.Build();

        rig.Engine.UploadProgram(program: larger);
        var grown = rig.Engine.ProgramWordCapacity;

        Assert.True(grown >= (larger.Words.Length + shade.Words.Length + shade.ParameterWords.Length));
        rig.Engine.Pack(frame: rig.Frame with { Program = larger });
        rig.Engine.SubmitUpload();
        Assert.Equal(grown, rig.Engine.ProgramWordCapacity);
        Assert.Equal(waits, gpu.Count(key: "IGpuDeviceContext.WaitIdle"));
    }
}
