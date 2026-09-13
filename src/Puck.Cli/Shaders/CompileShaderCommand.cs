using System.CommandLine;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary>Compiles a source stage using the same compiler as live pipelines.</summary>
internal static class CompileShaderCommand {
    public static Command Create() {
        var source = new Argument<string>(name: "source") { Description = "Shader source path (.glsl defaults to Shadertoy; .hlsl to HLSL)." };
        var name = new Option<string?>("--name");
        var output = new Option<string>("--out") { Description = "SPIR-V and DXIL output directory.", Required = true };
        var toolchain = new Option<string?>("--toolchain");
        var language = new Option<string?>("--language") { Description = "hlsl, glsl, or shadertoy." };
        var stage = new Option<string>("--stage") { DefaultValueFactory = _ => "compute", Description = "compute, vertex, or fragment." };
        var entry = new Option<string?>("--entry") { Description = "Defaults to main, or mainImage for Shadertoy." };
        var command = new Command(
            description: "Compile one shader stage for Vulkan and Direct3D 12.",
            name: "compile"
        ) { source, name, output, toolchain, language, stage, entry };

        command.SetAction(action: async (result, cancellationToken) => {
            var path = Path.GetFullPath(path: result.GetRequiredValue(argument: source));
            var languageText = (result.GetValue(option: language) ?? (Path.GetExtension(path: path).Equals(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: ".glsl"
            )
                ? "shadertoy"
                : "hlsl"));
            var sourceLanguage = languageText switch { "hlsl" => ShaderSourceLanguage.Hlsl, "glsl" => ShaderSourceLanguage.Glsl, "shadertoy" => ShaderSourceLanguage.ShadertoyGlsl, _ => ((ShaderSourceLanguage)(-1)) };
            var shaderStage = result.GetRequiredValue(option: stage) switch { "compute" => ShaderStage.Compute, "vertex" => ShaderStage.Vertex, "fragment" => ShaderStage.Fragment, _ => ((ShaderStage)255) };

            if (
                !Enum.IsDefined(value: sourceLanguage) ||
                !Enum.IsDefined(value: shaderStage)
            ) { Console.Error.WriteLine(value: "shaders compile: invalid --language or --stage"); return 1; }
            try {
                var directory = Path.GetFullPath(path: result.GetRequiredValue(option: output));

                Directory.CreateDirectory(path: directory);
                var shaderName = (result.GetValue(option: name) ?? Path.GetFileNameWithoutExtension(path: path));
                var compiler = new ShaderCompiler(
                    cacheDirectory: Path.Combine(
                        path1: directory,
                        path2: ".puck-shader-cache"
                    ),
                    toolchainDirectory: result.GetValue(option: toolchain)
                );
                var text = await File.ReadAllTextAsync(
                    cancellationToken: cancellationToken,
                    path: path
                );
                var request = new ShaderCompilationRequest(
                    shaderName,
                    [new ShaderStageSource(
                            shaderStage,
                            path,
                            text,
                            sourceLanguage,
                            (result.GetValue(option: entry) ?? ((sourceLanguage == ShaderSourceLanguage.ShadertoyGlsl)
                    ? "mainImage"
                    : "main"))
                        )]
                );
                var compiled = await compiler.CompileAsync(
                    cancellationToken: cancellationToken,
                    descriptor: request
                );

                foreach (var diagnostic in compiled.Diagnostics) {
                    var writer = (diagnostic.IsError
                        ? Console.Error
                        : Console.Out
                    );

                    writer.WriteLine(value: $"{(diagnostic.Path ?? path)}:{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}");
                }
                if (!compiled.IsSuccess) { return 1; }
                var suffix = shaderStage switch { ShaderStage.Vertex => "vert", ShaderStage.Fragment => "frag", _ => "comp" };
                var spirvPath = Path.Combine(
                    path1: directory,
                    path2: $"{shaderName}.{suffix}.spv"
                );
                var dxilPath = Path.Combine(
                    path1: directory,
                    path2: $"{shaderName}.{suffix}.dxil"
                );

                await File.WriteAllBytesAsync(
                    spirvPath,
                    compiled.SpirvByStage[shaderStage].ToArray(),
                    cancellationToken
                );
                await File.WriteAllBytesAsync(
                    dxilPath,
                    compiled.DxilByStage[shaderStage].ToArray(),
                    cancellationToken
                );
                Console.WriteLine(value: $"shaders compile: wrote {spirvPath} and {dxilPath}");
                return 0;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or ShaderToolMissingException)) {
                Console.Error.WriteLine(value: $"shaders compile: {exception.Message}"); return 1;
            }
        });
        return command;
    }
}
