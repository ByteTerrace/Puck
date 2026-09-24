namespace Puck.Shaders.Tests;

public sealed class CompiledShaderSnapshotTests {
    [Fact]
    public void Candidate_owns_bytecode_even_when_a_caller_reuses_its_build_arrays() {
        byte[] spirv = [1, 2, 3, 4];
        byte[] dxil = [5, 6, 7, 8];
        var source = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = spirv };
        var target = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = dxil };
        var candidate = new CompiledShader(
            "owned",
            "owned.hlsl",
            "hash",
            source,
            target,
            []
        );

        spirv[0] = 99;
        dxil[0] = 99;
        source.Clear();
        target.Clear();
        Assert.True(condition: candidate.IsSuccess);
        Assert.Equal(
            new byte[] { 1, 2, 3, 4 },
            candidate.Spirv.ToArray()
        );
        Assert.Equal(
            new byte[] { 5, 6, 7, 8 },
            candidate.Dxil.ToArray()
        );
    }
    [Fact]
    public void Duplicate_stages_are_refused_before_native_tool_invocation() {
        var stage = new ShaderStageSource(
            ShaderStage.Compute,
            "main.hlsl",
            "void main() {}"
        );
        var error = Assert.Throws<ArgumentException>(testCode: () => new ShaderCompilationRequest(
            name: "duplicate",
            stages: [stage, stage]
        ));

        Assert.Contains(
            "more than once",
            error.Message
        );
    }
}
