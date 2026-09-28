using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Shaders.Tests;

/// <summary>
/// An interface declaring the work counters (<see cref="ShaderWorkCounters"/>) generates the functions its kernels count
/// their own work through, laid out as <see cref="GpuKernelCounters"/> reads the rows back; an interface declaring only one
/// of the two members generates none. Every interface whose passes count their kernels' work declares them: the SDF
/// engine's compute passes and mesh pass, and the placement pass.
/// </summary>
public sealed class ShaderWorkCountersLawTests {
    private static ShaderInterface Interface(params ShaderInterfaceMember[] members) => new(
        members: [
            ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, name: "gain", type: ShaderValueType.Float),
            .. members,
        ],
        name: "counting"
    );

    [Fact]
    public void AnInterfaceDeclaringTheWorkCountersGeneratesTheCountingFunctions() {
        var generated = ShaderInterfaceHlsl.Generate(shaderInterface: Interface(members: [.. ShaderWorkCounters.Members]));

        Assert.Contains(expectedSubstring: $"static const uint PuckWorkRowWords = {GpuKernelCounters.RowWords}u;", actualString: generated);
        Assert.Contains(actualString: generated, expectedSubstring: "static const uint PuckWorkStepsWord = 0u;");
        Assert.Contains(actualString: generated, expectedSubstring: $"static const uint PuckWorkTexelsWord = {GpuKernelCounters.CountWords}u;");
        Assert.Contains(actualString: generated, expectedSubstring: "void puckCountWork(uint steps, uint texels) {");
        Assert.Contains(actualString: generated, expectedSubstring: "void puckCountWorkEach(uint steps, uint texels) {");
        Assert.Contains(actualString: generated, expectedSubstring: $"RWStructuredBuffer<uint> {ShaderWorkCounters.Buffer}");
        Assert.Contains(actualString: generated, expectedSubstring: $"uint {ShaderWorkCounters.Row};");
        Assert.Equal(
            actual: GpuWork.KernelKinds.ToArray(),
            expected: [GpuWork.MarchSteps, GpuWork.TexelsWritten]
        );
    }
    [Fact]
    public void AnInterfaceDeclaringOneMemberGeneratesNoCountingFunction() {
        Assert.DoesNotContain(expectedSubstring: "puckCountWork", actualString: ShaderInterfaceHlsl.Generate(shaderInterface: Interface()));
        Assert.DoesNotContain(expectedSubstring: "puckCountWork", actualString: ShaderInterfaceHlsl.Generate(shaderInterface: Interface(members: [ShaderWorkCounters.RowMember])));
        Assert.DoesNotContain(expectedSubstring: "puckCountWork", actualString: ShaderInterfaceHlsl.Generate(shaderInterface: Interface(members: [ShaderWorkCounters.BufferMember])));
    }
    [Fact]
    public void EveryCountingInterfaceDeclaresTheWorkCounters() {
        Assert.True(condition: ShaderWorkCounters.IsDeclaredBy(shaderInterface: SdfWorldInterfaces.World));
        Assert.True(condition: ShaderWorkCounters.IsDeclaredBy(shaderInterface: SdfWorldInterfaces.Mesh));
        Assert.True(condition: RenderGraphPackageCatalog.Engine.TryGet(id: RenderGraphPackageCatalog.Place, package: out var place));
        Assert.True(condition: place.CountsKernelWork);
        Assert.True(condition: ShaderWorkCounters.Members.All(predicate: member => place.Members.Contains(value: member)));
    }
}
