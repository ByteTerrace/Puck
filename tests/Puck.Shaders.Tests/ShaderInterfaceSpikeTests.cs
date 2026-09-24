using Puck.Assets;

namespace Puck.Shaders.Tests;

/// <summary>The two-group binding spike's build-time laws: each variant pass compiles for both backends against the
/// include its interface generates, both bytecode readers find every binding and block member exactly where the
/// interface's layout put it, and DXC writes the same bytes on a second build.</summary>
public sealed class ShaderInterfaceSpikeTests {
    public static TheoryData<string> PassNames => new(values: ShaderInterfaceSpike.Passes.Select(selector: static pass => pass.Interface.Name).ToArray());

    private static ShaderInterfaceSpike.Pass PassNamed(string name) =>
        ShaderInterfaceSpike.Passes.Single(predicate: pass => string.Equals(
            a: pass.Interface.Name,
            b: name,
            comparisonType: StringComparison.Ordinal
        ));
    private static void SkipWithoutDxc() =>
        Assert.SkipWhen(
            condition: (ShaderInterfaceSpike.Dxc is null),
            reason: "DXC is required to build the spike's variant passes."
        );

    [MemberData(memberName: nameof(PassNames))]
    [Theory]
    public async Task The_spirv_reader_finds_every_binding_and_member_where_the_layout_put_it(string name) {
        SkipWithoutDxc();

        var pass = PassNamed(name: name);
        var build = await ShaderInterfaceSpike.CompileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            generatedInclude: ShaderInterfaceHlsl.Generate(shaderInterface: pass.Interface),
            pass: pass
        );

        Assert.Equal(
            actual: SpirvInterfaceReader.Read(module: build.Spirv),
            expected: pass.Interface.Layout().Bindings
        );
    }
    [MemberData(memberName: nameof(PassNames))]
    [Theory]
    public async Task The_dxil_reader_finds_every_binding_and_member_where_the_layout_put_it(string name) {
        SkipWithoutDxc();
        Assert.SkipUnless(
            condition: OperatingSystem.IsWindows(),
            reason: "The DXIL reader calls dxcompiler through its Windows COM layout."
        );

        var pass = PassNamed(name: name);
        var build = await ShaderInterfaceSpike.CompileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            generatedInclude: ShaderInterfaceHlsl.Generate(shaderInterface: pass.Interface),
            pass: pass
        );

        if (OperatingSystem.IsWindows()) {
            using var reader = DxilInterfaceReader.Load(toolchain: new ShaderToolchain());

            Assert.Equal(
                actual: reader.Read(container: build.Dxil),
                expected: pass.Interface.Layout().Bindings
            );
        }
    }
    [MemberData(memberName: nameof(PassNames))]
    [Theory]
    public async Task Dxc_writes_the_same_spirv_and_dxil_bytes_on_a_second_build(string name) {
        SkipWithoutDxc();

        var pass = PassNamed(name: name);
        var include = ShaderInterfaceHlsl.Generate(shaderInterface: pass.Interface);
        var first = await ShaderInterfaceSpike.CompileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            generatedInclude: include,
            pass: pass
        );
        var second = await ShaderInterfaceSpike.CompileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            generatedInclude: include,
            pass: pass
        );

        Assert.Equal(
            actual: second.Spirv,
            expected: first.Spirv
        );
        Assert.Equal(
            actual: second.Dxil,
            expected: first.Dxil
        );
        // The pins let two hosts running the same DXC compare their builds byte for byte (-showLiveOutput prints them).
        TestContext.Current.TestOutputHelper?.WriteLine(message: $"{name} {ShaderInterfaceSpike.Dxc}: spirv {ContentPin.Compute(content: first.Spirv)} dxil {ContentPin.Compute(content: first.Dxil)}");
    }
    [Fact]
    public void Both_passes_place_the_frame_group_identically() {
        var bindings = ShaderInterfaceSpike.Passes.Select(selector: static pass => pass.Interface.Layout().Bindings.Where(predicate: static binding => (binding.Set == ((uint)ShaderInterfaceGroup.Frame))).ToArray()).ToArray();

        Assert.Equal(
            actual: bindings[1],
            expected: bindings[0]
        );
    }
}
