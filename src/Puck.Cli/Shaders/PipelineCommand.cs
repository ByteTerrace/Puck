using System.CommandLine;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary>Inspects or compiles a complete pipeline without creating GPU resources.</summary>
internal static class PipelineCommand {
    public static Command Create() {
        var source = new Argument<string>(name: "source") { Description = "Pipeline document or one-off shader source." };
        var inspect = new Option<bool>("--inspect") { Description = "Validate and print the execution plan without compiling shaders." };
        var toolchain = new Option<string?>("--toolchain");
        var cache = new Option<string?>("--cache") { Description = "Shader cache directory; defaults beneath the temporary directory." };
        var command = new Command(
            description: "Validate a connected pipeline and compile every pass for both GPU backends.",
            name: "pipeline"
        ) { source, inspect, toolchain, cache };

        command.SetAction(action: (result, cancellationToken) => {
            try {
                var path = Path.GetFullPath(path: result.GetRequiredValue(argument: source));
                var name = Path.GetFileNameWithoutExtension(path: path);
                var compiler = new ShaderCompiler(
                    cacheDirectory: (result.GetValue(option: cache) ?? Path.Combine(
                        path1: Path.GetTempPath(),
                        path2: "puck-shader-cache"
                    )),
                    toolchainDirectory: result.GetValue(option: toolchain)
                );
                var loader = new ShaderPipelineLoader(compiler: compiler);

                if (result.GetValue(option: inspect)) {
                    var plan = new ShaderPipelineCompiler().Compile(definition: loader.ReadDefinition(
                        name: name,
                        path: path
                    ));

                    Console.WriteLine(value: $"{plan.Definition.Name}: {plan.Passes.Count} passes, {plan.Resources.Count} resources");
                    foreach (var pass in plan.Passes) {
                        Console.WriteLine(value: $"  {pass.Name}: {pass.Declaration.Kind}; inputs={string.Join(
                            separator: ",",
                            values: pass.Declaration.InputReferences.Select(selector: input => (input.Name + (input.PreviousFrame
                            ? "@previous"
                            : string.Empty)))
                        )}; outputs={string.Join(
                            separator: ",",
                            values: pass.Declaration.OutputReferences.Select(selector: output => output.Name)
                        )}");
                    }
                    foreach (var output in plan.Outputs) { Console.WriteLine(value: $"  output {output.Name} -> {output.Resource.Name}"); }
                    return Task.FromResult(result: 0);
                }
                var compiled = loader.Load(
                    cancellationToken: cancellationToken,
                    name: name,
                    path: path
                );

                ((compiled.Pipeline is null)
                    ? Console.Error
                    : Console.Out).WriteLine(value: compiled.Message);
                return Task.FromResult(result: ((compiled.Pipeline is null)
                    ? 1
                    : 0));
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or ShaderPipelineCompilationException)) {
                Console.Error.WriteLine(value: $"shaders pipeline: {exception.Message}");
                return Task.FromResult(result: 1);
            }
        });
        return command;
    }
}
