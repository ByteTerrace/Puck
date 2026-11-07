using System.CommandLine;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary>Compiles a source stage using the same compiler as live pipelines.</summary>
internal static class CompileShaderCommand {
    /// <summary>Creates <c>--toolchain</c>, which <c>shaders compile</c> and <c>shaders pipeline</c> share.</summary>
    /// <returns>The option; its value is <see langword="null"/> when omitted.</returns>
    public static Option<string?> Toolchain() => new("--toolchain") { Description = "The shader toolchain directory; defaults to the one the compiler finds." };
    public static Command Create() {
        var source = new Argument<string>(name: "source") { Description = "HLSL source path." };
        var name = new Option<string?>("--name") { Description = "The base name of the written bytecode files; defaults to the source's file name." };
        var output = CliOptions.Output(description: "SPIR-V and DXIL output directory.", required: true);
        var toolchain = Toolchain();
        var stage = new Option<string>("--stage") { DefaultValueFactory = _ => "compute", Description = "compute, vertex, or fragment." };

        stage.AcceptOnlyFromAmong("compute", "vertex", "fragment");
        var entry = new Option<string>("--entry") { DefaultValueFactory = _ => "main", Description = "The entry point." };
        var command = new Command(
            description: "Compile one shader stage for Vulkan and Direct3D 12.",
            name: "compile"
        ) { source, name, output, toolchain, stage, entry };

        command.SetAction(action: async (result, cancellationToken) => {
            var path = Path.GetFullPath(path: result.GetRequiredValue(argument: source));
            var shaderStage = result.GetRequiredValue(option: stage) switch { "vertex" => ShaderStage.Vertex, "fragment" => ShaderStage.Fragment, _ => ShaderStage.Compute };

            try {
                var directory = Path.GetFullPath(path: result.GetRequiredValue(option: output));
                var shaderName = (result.GetValue(option: name) ?? Path.GetFileNameWithoutExtension(path: path));
                var compiler = new ShaderCompiler(
                    cacheDirectory: ShaderCompiler.DefaultCacheDirectory,
                    toolchainDirectory: result.GetValue(option: toolchain)
                );
                var text = await File.ReadAllTextAsync(
                    cancellationToken: cancellationToken,
                    path: path
                );
                var request = new ShaderCompilationRequest(
                    name: shaderName,
                    stages: [new ShaderStageSource(
                            shaderStage,
                            path,
                            text,
                            result.GetRequiredValue(option: entry)
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
                if (!compiled.IsSuccess) { return CliExit.Failed; }
                Directory.CreateDirectory(path: directory);
                var suffix = shaderStage switch { ShaderStage.Vertex => "vert", ShaderStage.Fragment => "frag", _ => "comp" };
                var spirvPath = Path.Combine(
                    path1: directory,
                    path2: $"{shaderName}.{suffix}.spv"
                );
                var dxilPath = Path.Combine(
                    path1: directory,
                    path2: $"{shaderName}.{suffix}.dxil"
                );

                // Bytecode under a project's Assets ships as content that built outputs hard-link to: replace each file,
                // never write through it.
                Puck.Confinement.AtomicFile.WriteAllBytes(
                    bytes: compiled.SpirvByStage[shaderStage].Span,
                    path: spirvPath
                );
                Puck.Confinement.AtomicFile.WriteAllBytes(
                    bytes: compiled.DxilByStage[shaderStage].Span,
                    path: dxilPath
                );
                Console.WriteLine(value: $"shaders compile: wrote {CliPaths.ToDisplay(fullPath: spirvPath)} and {CliPaths.ToDisplay(fullPath: dxilPath)}");
                return CliExit.Success;
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or ShaderToolMissingException)) {
                return CliExit.Refuse(
                    verb: "shaders compile",
                    what: CliPaths.ToDisplay(fullPath: path),
                    why: exception.Message.ReplaceLineEndings(replacementText: " ")
                );
            }
        });
        return command;
    }
}
