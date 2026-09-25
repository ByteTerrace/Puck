using System.Runtime.Versioning;
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
    // Buffer<T> read through a structured copy, and RWBuffer<T> read and written in place: each is a typed buffer both
    // bytecode forms carry (a DXIL buffer-dimension view, a SPIR-V image of Dim Buffer).
    [InlineData("[[vk::binding(0, 0)]] Buffer<float4> texels : register(t0, space0);\n[[vk::binding(1, 0)]] RWStructuredBuffer<float4> output : register(u1, space0);\n[numthreads(1, 1, 1)] void CSMain(uint3 id : SV_DispatchThreadID) { output[id.x] = texels[id.x]; }")]
    [InlineData("[[vk::binding(0, 0)]] RWBuffer<float4> texels : register(u0, space0);\n[numthreads(1, 1, 1)] void CSMain(uint3 id : SV_DispatchThreadID) { texels[id.x] = (texels[id.x] * 2.0); }")]
    [Theory]
    public async Task Both_readers_refuse_a_typed_buffer_by_name(string source) {
        SkipWithoutDxc();

        var build = await ShaderInterfaceSpike.CompileSourceAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            entryPoint: "CSMain",
            profile: "cs_6_6",
            source: source
        );

        Assert.Contains(
            actualString: Assert.Throws<InvalidDataException>(testCode: () => SpirvInterfaceReader.Read(module: build.Spirv)).Message,
            expectedSubstring: "SPIR-V binding 'texels' is a typed buffer"
        );

        if (OperatingSystem.IsWindows()) {
            AssertDxilRefuses(container: build.Dxil);
        }

        [SupportedOSPlatform("windows")]
        static void AssertDxilRefuses(byte[] container) {
            using var reader = DxilInterfaceReader.Load(toolchain: new ShaderToolchain());
            InvalidDataException? refusal = null;

            // Read directly rather than through Assert.Throws: the platform analyzer does not carry this function's
            // Windows-only attribute into a lambda.
            try {
                _ = reader.Read(container: container);
            } catch (InvalidDataException exception) {
                refusal = exception;
            }

            Assert.Contains(
                actualString: Assert.IsType<InvalidDataException>(@object: refusal).Message,
                expectedSubstring: "DXIL binding 'texels' is a typed buffer"
            );
        }
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
