using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.Shaders;

namespace Puck.Cli.Shaders;

/// <summary><c>puck shaders package</c>: compiles a graph document or one-off shader and writes its
/// <c>puck.shader.package.v1</c> source-closure package, through the same loader and compiler as live pipelines.</summary>
internal static class PackageCommand {
    /// <summary>The shader cache a package build compiles through when none is named: a directory beneath the temporary
    /// directory, shared by every run of this CLI.</summary>
    public static string DefaultCacheDirectory => Path.Combine(
        path1: Path.GetTempPath(),
        path2: "puck-shader-cache"
    );

    /// <summary>Maps a package outcome onto the shared exit codes: compiled is success, a pass that did not compile is a
    /// failure, and a refusal, a missing tool, or a source edited mid-read is a refusal.</summary>
    /// <param name="result">The outcome.</param>
    /// <returns>The exit code.</returns>
    public static int ExitCodeOf(ShaderPackageResult result) => result.Status switch {
        ShaderPipelineLoadStatus.Compiled => CliExit.Success,
        ShaderPipelineLoadStatus.Failed when (result.Code is null) => CliExit.Failed,
        _ => CliExit.Refused,
    };
    /// <summary>Writes a package outcome: one JSON object on standard output with <paramref name="json"/>, otherwise
    /// the summary on standard output or the reason on standard error.</summary>
    /// <param name="verb">The command path that ran, such as <c>shaders package</c>.</param>
    /// <param name="package">The package directory.</param>
    /// <param name="result">The outcome.</param>
    /// <param name="json">Whether to write JSON.</param>
    public static void Report(string verb, string package, ShaderPackageResult result, bool json) {
        var path = CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: package));

        if (json) {
            var record = new JsonObject {
                ["status"] = result.Status.ToString(),
                ["package"] = path,
                ["code"] = result.Code,
                ["name"] = result.Manifest?.Name,
                ["files"] = result.Manifest?.Files.Count,
                ["passes"] = result.Manifest?.Passes.Count,
                ["message"] = result.Message,
            };

            Console.Out.WriteLine(value: record.ToJsonString());

            return;
        }

        if (result.Status == ShaderPipelineLoadStatus.Compiled) {
            Console.Out.WriteLine(value: $"{verb}: {path}: {result.Message}");
        } else {
            Console.Error.WriteLine(value: $"puck {verb}: {path}: {result.Message.ReplaceLineEndings(replacementText: " ")}");
        }
    }
    public static Command Create() {
        var source = new Argument<string>(name: "source") { Description = "The graph document or one-off shader source to package." };
        var output = CliOptions.Output(
            description: "The package directory; replaced only once the new package is complete, and only when it is absent, empty, or already a package.",
            required: true
        );
        var root = new Option<string?>(name: "--root") { Description = "The directory every file of the closure must lie within and logical paths are relative to; defaults to the source's directory." };
        var toolchain = new Option<string?>(name: "--toolchain") { Description = "A directory holding DXC; defaults to the search path." };
        var cache = new Option<string?>(name: "--cache") { Description = "The shader cache directory; defaults beneath the temporary directory." };
        var json = CliOptions.Json();
        var command = new Command(
            description: "Compile a pipeline and write its source-closure package.",
            name: "package"
        ) { source, output, root, toolchain, cache, json };

        command.SetAction(action: async (result, cancellationToken) => {
            var outputPath = result.GetRequiredValue(option: output);
            var sourcePath = result.GetRequiredValue(argument: source);

            if (!File.Exists(path: sourcePath)) {
                return CliExit.Refuse(
                    verb: "shaders package",
                    what: sourcePath,
                    why: "no such file."
                );
            }

            var packaged = await new ShaderPackager(compiler: new ShaderCompiler(
                cacheDirectory: (result.GetValue(option: cache) ?? DefaultCacheDirectory),
                toolchainDirectory: result.GetValue(option: toolchain)
            )).BuildAsync(
                cancellationToken: cancellationToken,
                output: outputPath,
                root: result.GetValue(option: root),
                source: sourcePath
            );

            Report(
                json: result.GetValue(option: json),
                package: outputPath,
                result: packaged,
                verb: "shaders package"
            );

            return ExitCodeOf(result: packaged);
        });

        return command;
    }
}
