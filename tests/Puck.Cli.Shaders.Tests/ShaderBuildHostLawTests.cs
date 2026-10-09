using Xunit;

namespace Puck.Cli.Shaders.Tests;

/// <summary>The shader build compiles DXIL on Windows alone, whatever a project sets: Direct3D 12 is its one reader,
/// so a build on any other host plans SPIR-V for every stage and no DXIL, and the cross-host comparison holds SPIR-V
/// alone.</summary>
public sealed class ShaderBuildHostLawTests {
    // A project declaring a stage of each kind, with every DXIL knob set on, evaluated as this host plans it and as a
    // host other than Windows plans it. The host fact belongs to build/Shaders.targets; the law stands in for another
    // host by setting it.
    [Fact]
    public void ABuildOnAHostOtherThanWindowsPlansNoDxil() {
        using var fixture = new ShaderBuildFixture();

        fixture.ShaderProject(body: """
            <ItemGroup>
              <VertexShaderSource Include="Assets/Shaders/blit.vert.hlsl" />
              <FragmentShaderSource Include="Assets/Shaders/blit.frag.hlsl" />
              <ComputeShaderSource Include="Assets/Shaders/place.comp.hlsl" />
            </ItemGroup>
            """);

        string[] enabled = ["PuckShaderDxilEnabled=true", "PuckComputeShaderDxilEnabled=true", "PuckShaderSpirvEnabled=true"];
        string[] spirv = ["blit.frag.spv", "blit.vert.spv", "place.comp.spv"];
        string[] windows = ["blit.frag.dxil", "blit.frag.spv", "blit.vert.dxil", "blit.vert.spv", "place.comp.dxil", "place.comp.spv"];

        Assert.Equal(
            actual: fixture.Evaluate(item: "PuckShaderOutput", properties: enabled),
            expected: (OperatingSystem.IsWindows() ? windows : spirv)
        );
        Assert.Equal(
            actual: fixture.Evaluate(item: "PuckShaderOutput", properties: [.. enabled, "_PuckShaderHostBuildsDxil=false"]),
            expected: spirv
        );
    }
}
