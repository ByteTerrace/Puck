using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Shaders.Tests;

/// <summary>
/// An interface declaring the work counters (<see cref="ShaderWorkCounters"/>) generates the functions its kernels count
/// their own work through, laid out as <see cref="GpuKernelCounters"/> reads the rows back; an interface declaring neither
/// or an incomplete set of members declares the same functions empty, so a counting kernel compiles under it and counts
/// nothing. Every interface whose passes count their kernels' work declares them: the SDF
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
        Assert.Contains(actualString: generated, expectedSubstring: $"static const uint PuckWorkSkyWord = {(2 * GpuKernelCounters.CountWords)}u;");
        Assert.Contains(actualString: generated, expectedSubstring: "void puckCountWork(uint steps, uint texels) {");
        Assert.Contains(actualString: generated, expectedSubstring: "void puckCountSky(uint evaluations) {");
        Assert.Contains(actualString: generated, expectedSubstring: "void puckCountFragmentWork(uint steps, uint texels) {");
        Assert.Contains(actualString: generated, expectedSubstring: "bool counting = !IsHelperLane();");
        Assert.Contains(actualString: generated, expectedSubstring: $"RWStructuredBuffer<uint> {ShaderWorkCounters.Buffer}");
        Assert.Contains(actualString: generated, expectedSubstring: $"uint {ShaderWorkCounters.Row};");
        Assert.Equal(
            actual: GpuWork.KernelKinds.ToArray(),
            expected: [GpuWork.MarchSteps, GpuWork.TexelsWritten, GpuWork.SkyEvaluations, GpuWork.SkyHashes, GpuWork.SkyTextureLoads]
        );
    }
    [Fact]
    public void AnInterfaceDeclaringNeitherOrOneMemberDeclaresTheCountingFunctionsEmpty() {
        foreach (var generated in ((string[])[
            ShaderInterfaceHlsl.Generate(shaderInterface: Interface()),
            ShaderInterfaceHlsl.Generate(shaderInterface: Interface(members: [ShaderWorkCounters.RowMember])),
            ShaderInterfaceHlsl.Generate(shaderInterface: Interface(members: [ShaderWorkCounters.BufferMember])),
        ])) {
            Assert.Contains(actualString: generated, expectedSubstring: "void puckCountWork(uint steps, uint texels) {\n}");
            Assert.Contains(actualString: generated, expectedSubstring: "void puckCountSky(uint evaluations) {\n}");
            Assert.Contains(actualString: generated, expectedSubstring: "void puckCountFragmentWork(uint steps, uint texels) {\n}");
            Assert.DoesNotContain(actualString: generated, expectedSubstring: "puckAddWork");
            Assert.DoesNotContain(actualString: generated, expectedSubstring: "PuckWorkRowWords");
            Assert.DoesNotContain(actualString: generated, expectedSubstring: "InterlockedAdd");
        }
    }
    [Fact]
    public void EveryCountingInterfaceDeclaresTheWorkCounters() {
        Assert.True(condition: ShaderWorkCounters.IsDeclaredBy(shaderInterface: SdfWorldInterfaces.World));
        Assert.True(condition: ShaderWorkCounters.IsDeclaredBy(shaderInterface: SdfWorldInterfaces.Mesh));
        Assert.True(condition: RenderGraphPackageCatalog.Engine.TryGet(id: RenderGraphPackageCatalog.Place, package: out var place));
        Assert.True(condition: place.CountsKernelWork);
        Assert.True(condition: ShaderWorkCounters.Members.All(predicate: member => place.Members.Contains(value: member)));
    }
    [Fact]
    public void NamedRowsUseEveryKernelColumnAndTheSameAtomicCarryAsPlainRows() {
        var generated = ShaderInterfaceHlsl.Generate(shaderInterface: Interface(members: [.. ShaderWorkCounters.Members]));

        Assert.Contains(actualString: generated, expectedSubstring: "static const uint PuckWorkRowWords = 10u;");
        Assert.Contains(actualString: generated, expectedSubstring: "static const uint PuckWorkSkyHashesWord = 6u;");
        Assert.Contains(actualString: generated, expectedSubstring: "static const uint PuckWorkSkyTextureLoadsWord = 8u;");
        Assert.Contains(actualString: generated, expectedSubstring: "uint row = ((passGroup.workCounterDetailRow + detail) * PuckWorkRowWords);");
        Assert.Contains(actualString: generated, expectedSubstring: "puckAddWork((row + PuckWorkSkyHashesWord), hashes);");
        Assert.Contains(actualString: generated, expectedSubstring: "puckAddWork((row + PuckWorkSkyTextureLoadsWord), loads);");
        Assert.Contains(actualString: generated, expectedSubstring: "if (before > (0xFFFFFFFFu - amount))");
        Assert.Contains(actualString: generated, expectedSubstring: "InterlockedAdd(workCounters[word + 1u], 1u);");
    }
}
