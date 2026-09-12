using System.CommandLine;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary>Inspects or compiles a complete pipeline without creating GPU resources.</summary>
internal static class PipelineCommand {
    public static Command Create() {
        var source = new Argument<string>("source") { Description = "Pipeline document or one-off shader source." };
        var inspect = new Option<bool>("--inspect") { Description = "Validate and print the execution plan without compiling shaders." };
        var toolchain = new Option<string?>("--toolchain");
        var cache = new Option<string?>("--cache") { Description = "Shader cache directory; defaults beneath the temporary directory." };
        var command = new Command("pipeline", "Validate a connected pipeline and compile every pass for both GPU backends.") { source, inspect, toolchain, cache };
        command.SetAction((result, cancellationToken) => {
            try {
                var path = Path.GetFullPath(result.GetRequiredValue(source));
                var name = Path.GetFileNameWithoutExtension(path);
                var compiler = new ShaderCompiler(result.GetValue(cache) ?? Path.Combine(Path.GetTempPath(), "puck-shader-cache"), result.GetValue(toolchain));
                var loader = new ShaderPipelineLoader(compiler);
                if (result.GetValue(inspect)) {
                    var plan = new ShaderPipelineCompiler().Compile(loader.ReadDefinition(name, path));
                    Console.WriteLine($"{plan.Definition.Name}: {plan.Passes.Count} passes, {plan.Resources.Count} resources");
                    foreach (var pass in plan.Passes) {
                        Console.WriteLine($"  {pass.Name}: {pass.Declaration.Kind}; inputs={string.Join(",", pass.Declaration.InputReferences.Select(input => input.Name + (input.PreviousFrame ? "@previous" : string.Empty)))}; outputs={string.Join(",", pass.Declaration.OutputReferences.Select(output => output.Name))}");
                    }
                    foreach (var output in plan.Outputs) { Console.WriteLine($"  output {output.Name} -> {output.Resource.Name}"); }
                    return Task.FromResult(0);
                }
                var compiled = loader.Load(name, path, cancellationToken);
                (compiled.Pipeline is null ? Console.Error : Console.Out).WriteLine(compiled.Message);
                return Task.FromResult(compiled.Pipeline is null ? 1 : 0);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or ShaderPipelineCompilationException) {
                Console.Error.WriteLine($"shaders pipeline: {exception.Message}");
                return Task.FromResult(1);
            }
        });
        return command;
    }
}
