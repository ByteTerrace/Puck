namespace Puck.Shaders;

public interface IShaderModuleLoader {
    ShaderStageInfo ValidateShader(ShaderStage stage, string path);
    ValidatedShaderSet ValidateShaderSet(ShaderSet shaderSet);
}
