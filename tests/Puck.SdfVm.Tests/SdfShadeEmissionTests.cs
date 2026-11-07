using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfShadeEmissionTests {
    [Fact]
    public void ScalarComponentsEmitTheScalarWithoutSubscriptingIt() {
        var scalar = SdfShadeProgram.Compile(new("scalar", [], [new(SdfShadePhase.Surface, "roughness",
            new("component", [new("input", Name: "clock")], Name: "0"))]));
        Assert.DoesNotContain(".x[0u]", SdfShadeHlsl.Emit(scalar, SdfShadePhase.Surface));
        var integer = new SdfShadeExpression("component", [new("input", Name: "light.kind")], Name: "0");
        var selected = SdfShadeProgram.Compile(new("integer", [], [new(SdfShadePhase.Light, "attenuation",
            new("==", [integer, new("constant", Type: SdfShadeType.UInt, Bits: [0])]))]));
        Assert.DoesNotContain(".x[0u]", SdfShadeHlsl.Emit(selected, SdfShadePhase.Light));
        var vector = SdfShadeProgram.Compile(new("vector", [], [new(SdfShadePhase.Surface, "roughness",
            new("component", [new("input", Name: "surface.position")], Name: "1"))]));
        Assert.Contains(".xyz[1u]", SdfShadeHlsl.Emit(vector, SdfShadePhase.Surface));
    }
}
