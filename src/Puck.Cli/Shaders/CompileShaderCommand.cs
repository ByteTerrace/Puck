using System.CommandLine;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary>Compiles a source stage using the same compiler as live pipelines.</summary>
internal static class CompileShaderCommand {
    public static Command Create() {
        var source = new Argument<string>("source") { Description = "Shader source path (.glsl defaults to Shadertoy; .hlsl to HLSL)." };
        var name = new Option<string?>("--name");
        var output = new Option<string>("--out") { Required = true, Description = "SPIR-V and DXIL output directory." };
        var toolchain = new Option<string?>("--toolchain");
        var language = new Option<string?>("--language") { Description = "hlsl, glsl, or shadertoy." };
        var stage = new Option<string>("--stage") { DefaultValueFactory = _ => "compute", Description = "compute, vertex, or fragment." };
        var entry = new Option<string?>("--entry") { Description = "Defaults to main, or mainImage for Shadertoy." };
        var command = new Command("compile", "Compile one shader stage for Vulkan and Direct3D 12.") { source, name, output, toolchain, language, stage, entry };
        command.SetAction(async (result, cancellationToken) => {
            var path = Path.GetFullPath(result.GetRequiredValue(source));
            var languageText = result.GetValue(language) ?? (Path.GetExtension(path).Equals(".glsl", StringComparison.OrdinalIgnoreCase) ? "shadertoy" : "hlsl");
            var sourceLanguage = languageText switch { "hlsl" => ShaderSourceLanguage.Hlsl, "glsl" => ShaderSourceLanguage.Glsl, "shadertoy" => ShaderSourceLanguage.ShadertoyGlsl, _ => (ShaderSourceLanguage)(-1) };
            var shaderStage = result.GetRequiredValue(stage) switch { "compute" => ShaderStage.Compute, "vertex" => ShaderStage.Vertex, "fragment" => ShaderStage.Fragment, _ => (ShaderStage)255 };
            if (!Enum.IsDefined(sourceLanguage) || !Enum.IsDefined(shaderStage)) { Console.Error.WriteLine("shaders compile: invalid --language or --stage"); return 1; }
            try {
                var directory = Path.GetFullPath(result.GetRequiredValue(output));
                Directory.CreateDirectory(directory);
                var shaderName = result.GetValue(name) ?? Path.GetFileNameWithoutExtension(path);
                var compiler = new ShaderCompiler(Path.Combine(directory, ".puck-shader-cache"), result.GetValue(toolchain));
                var text = await File.ReadAllTextAsync(path, cancellationToken);
                var request = new ShaderCompilationRequest(shaderName, [new ShaderStageSource(shaderStage, path, text, sourceLanguage,
                    result.GetValue(entry) ?? (sourceLanguage == ShaderSourceLanguage.ShadertoyGlsl ? "mainImage" : "main"))]);
                var compiled = await compiler.CompileAsync(request, cancellationToken);
                foreach (var diagnostic in compiled.Diagnostics) {
                    var writer = diagnostic.IsError ? Console.Error : Console.Out;
                    writer.WriteLine($"{diagnostic.Path ?? path}:{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}");
                }
                if (!compiled.IsSuccess) { return 1; }
                var suffix = shaderStage switch { ShaderStage.Vertex => "vert", ShaderStage.Fragment => "frag", _ => "comp" };
                var spirvPath = Path.Combine(directory, $"{shaderName}.{suffix}.spv");
                var dxilPath = Path.Combine(directory, $"{shaderName}.{suffix}.dxil");
                await File.WriteAllBytesAsync(spirvPath, compiled.SpirvByStage[shaderStage].ToArray(), cancellationToken);
                await File.WriteAllBytesAsync(dxilPath, compiled.DxilByStage[shaderStage].ToArray(), cancellationToken);
                Console.WriteLine($"shaders compile: wrote {spirvPath} and {dxilPath}");
                return 0;
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ShaderToolMissingException) {
                Console.Error.WriteLine($"shaders compile: {exception.Message}"); return 1;
            }
        });
        return command;
    }
}
