namespace Puck.Shaders.Tests;

public sealed class CompiledShaderSnapshotTests {
    [Fact]
    public void Request_owns_defaults_after_the_authoring_document_is_disposed() {
        ShaderCompilationRequest request;
        using (var document = System.Text.Json.JsonDocument.Parse("0.5")) {
            var config = new Dictionary<string, ShaderConfigField> { ["gain"] = new(ShaderValueType.Float, document.RootElement) };
            request = ShaderCompilationRequest.Compute("defaults", "defaults.glsl", "void mainImage(out vec4 c, in vec2 p) { c=vec4(gain); }", config: config);
            config.Clear();
        }
        Assert.Equal(0.5, request.Config["gain"].Default!.Value.GetDouble());
    }

    [Fact]
    public void Duplicate_stages_are_refused_before_native_tool_invocation() {
        var stage = new ShaderStageSource(ShaderStage.Compute, "main.hlsl", "void main() {}", ShaderSourceLanguage.Hlsl);
        var error = Assert.Throws<ArgumentException>(() => new ShaderCompilationRequest("duplicate", [stage, stage]));
        Assert.Contains("more than once", error.Message);
    }
    [Fact]
    public void Candidate_owns_bytecode_even_when_a_caller_reuses_its_build_arrays() {
        byte[] spirv = [1, 2, 3, 4];
        byte[] dxil = [5, 6, 7, 8];
        var source = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = spirv };
        var target = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = dxil };
        var candidate = new CompiledShader("owned", "owned.hlsl", "hash", source, target, []);
        spirv[0] = 99;
        dxil[0] = 99;
        source.Clear();
        target.Clear();
        Assert.True(candidate.IsSuccess);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, candidate.Spirv.ToArray());
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, candidate.Dxil.ToArray());
    }
}
