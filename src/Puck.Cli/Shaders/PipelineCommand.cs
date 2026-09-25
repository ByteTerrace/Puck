using System.CommandLine;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary>Inspects or compiles a complete pipeline without creating GPU resources.</summary>
internal static class PipelineCommand {
    /// <summary>Maps a source load onto the shared exit codes: compiled is success, a document or pass that did not
    /// compile is a failure, and a missing tool or a source edited mid-load is a refusal.</summary>
    /// <param name="result">The outcome.</param>
    /// <returns>The exit code.</returns>
    public static int ExitCodeOf(ShaderPipelineLoadResult result) => result.Status switch {
        ShaderPipelineLoadStatus.Compiled => CliExit.Success,
        ShaderPipelineLoadStatus.Failed => CliExit.Failed,
        _ => CliExit.Refused,
    };
    public static Command Create() {
        var source = new Argument<string>(name: "source") { Description = "Pipeline document, one-off shader source, or shader package directory." };
        var inspect = new Option<bool>("--inspect") { Description = "Validate and print the execution plan without compiling shaders." };
        var toolchain = new Option<string?>("--toolchain");
        var cache = new Option<string?>("--cache") { Description = "Shader cache directory; defaults beneath the temporary directory." };
        var command = new Command(
            description: "Validate a connected pipeline and compile every pass for both GPU backends.",
            name: "pipeline"
        ) { source, inspect, toolchain, cache };

        command.SetAction(action: (result, cancellationToken) => {
            var path = Path.GetFullPath(path: result.GetRequiredValue(argument: source));
            var package = ShaderPackager.IsPackage(path: path);

            if (
                !package &&
                !File.Exists(path: path)
            ) {
                return Task.FromResult(result: CliExit.Refuse(
                    verb: "shaders pipeline",
                    what: CliPaths.ToDisplay(fullPath: path),
                    why: "no such file or package directory."
                ));
            }
            if (string.Equals(
                a: Path.GetFileName(path: path),
                b: ShaderPackageManifest.FileName,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                return Task.FromResult(result: CliExit.Refuse(
                    verb: "shaders pipeline",
                    what: CliPaths.ToDisplay(fullPath: path),
                    why: "a package is named by its directory, not its manifest."
                ));
            }

            try {
                var compiler = new ShaderCompiler(
                    cacheDirectory: (result.GetValue(option: cache) ?? Path.Combine(
                        path1: Path.GetTempPath(),
                        path2: "puck-shader-cache"
                    )),
                    toolchainDirectory: result.GetValue(option: toolchain)
                );

                if (
                    package &&
                    !result.GetValue(option: inspect)
                ) {
                    var loaded = new ShaderPackager(compiler: compiler).LoadAsync(
                        cancellationToken: cancellationToken,
                        package: path
                    ).GetAwaiter().GetResult();

                    PackageCommand.Report(
                        json: false,
                        package: path,
                        result: loaded,
                        verb: "shaders pipeline"
                    );

                    return Task.FromResult(result: PackageCommand.ExitCodeOf(result: loaded));
                }

                var name = Path.GetFileNameWithoutExtension(path: path);

                if (package) {
                    var manifest = ShaderPackager.Open(package: path);

                    name = manifest.Name;
                    path = Path.GetFullPath(path: Path.Combine(
                        path1: path,
                        path2: manifest.Document
                    ));
                }

                if (result.GetValue(option: inspect)) {
                    var plan = RenderGraphCompiler.ShaderPasses.Compile(definition: ShaderPipelineLoader.ReadDefinition(
                        name: name,
                        path: path
                    )).Pipeline;

                    Console.WriteLine(value: $"{plan.Definition.Name}: {plan.Passes.Count} passes, {plan.Resources.Count} resources");
                    foreach (var pass in plan.Passes) {
                        Console.WriteLine(value: $"  {pass.Name}: {pass.Kind}; inputs={string.Join(
                            separator: ",",
                            values: pass.Inputs.Select(selector: input => (input.Name + (input.PreviousFrame
                            ? "@previous"
                            : string.Empty)))
                        )}; outputs={string.Join(
                            separator: ",",
                            values: pass.Outputs.Select(selector: output => output.Name)
                        )}");
                    }
                    foreach (var output in plan.Outputs) { Console.WriteLine(value: $"  output {output}"); }
                    return Task.FromResult(result: CliExit.Success);
                }
                var compiled = new ShaderPipelineLoader(compiler: compiler).Load(
                    cancellationToken: cancellationToken,
                    name: name,
                    path: path
                );

                ((compiled.Pipeline is null)
                    ? Console.Error
                    : Console.Out).WriteLine(value: compiled.Message);
                return Task.FromResult(result: ExitCodeOf(result: compiled));
            } catch (Exception exception) when ((exception is System.Text.Json.JsonException or InvalidDataException or ShaderPipelineCompilationException)) {
                // The document itself is what was checked, as a pass that does not compile is.
                Console.Error.WriteLine(value: $"puck shaders pipeline: {CliPaths.ToDisplay(fullPath: path)}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

                return Task.FromResult(result: CliExit.Failed);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or ShaderClosureRefusedException)) {
                return Task.FromResult(result: CliExit.Refuse(
                    verb: "shaders pipeline",
                    what: CliPaths.ToDisplay(fullPath: path),
                    why: exception.Message.ReplaceLineEndings(replacementText: " ")
                ));
            }
        });
        return command;
    }
}
