using System.Globalization;

namespace Puck.Shaders.Generator;

/// <summary>
/// Runs one project's shader build for <c>build/Shaders.targets</c>: <c>compile</c> makes every output current
/// (<see cref="ShaderBuild.CompileAsync"/>), and <c>check</c> holds the published outputs to their sources without
/// compiling (<see cref="ShaderBuild.Check"/>), for a pack that skips the build.
/// </summary>
/// <remarks>
/// Options: <c>--project &lt;directory&gt;</c>, <c>--request &lt;file&gt;</c> (one <see cref="ShaderBuildOutput"/> a
/// line), <c>--lock &lt;file&gt;</c> (the publication lock), <c>--cache &lt;directory&gt;</c> (default
/// <see cref="ShaderCompiler.DefaultCacheDirectory"/>), <c>--dxc &lt;command&gt;</c> (default <c>dxc</c>) and
/// <c>--jobs &lt;n&gt;</c>. Without <c>--jobs</c> a compile asks its host for every core over its standard streams
/// (<see cref="StreamShaderCoreBroker"/>); the build host answers from MSBuild's own core budget. Every message is one
/// line on standard output, and an error is a line in MSBuild's canonical format. A host's <c>cancel</c> line, or the end
/// of its answers, cancels the build: every compile it started ends first, then the generator prints one line saying so
/// and exits 1.
/// </remarks>
internal static class ShaderBuildCommand {
    public static async Task<int> RunAsync(string[] arguments) {
        var options = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        for (var index = 1; (index < arguments.Length); index += 2) {
            if (
                ((index + 1) >= arguments.Length) ||
                (arguments[index] is not ("--project" or "--request" or "--lock" or "--cache" or "--dxc" or "--jobs")) ||
                !options.TryAdd(key: arguments[index], value: arguments[(index + 1)])
            ) {
                return Refuse(reason: $"'{arguments[index]}' is not a shader build option, or it repeats or has no value.");
            }
        }
        if (!options.TryGetValue(key: "--project", value: out var project) || !options.TryGetValue(key: "--request", value: out var request) || !options.TryGetValue(key: "--lock", value: out var lockFile)) {
            return Refuse(reason: "a shader build needs --project, --request and --lock.");
        }

        var jobs = 0;

        if (options.TryGetValue(key: "--jobs", value: out var jobsText) && (!int.TryParse(provider: CultureInfo.InvariantCulture, result: out jobs, s: jobsText, style: NumberStyles.None) || (jobs < 1))) {
            return Refuse(reason: $"--jobs '{jobsText}' is not a positive number.");
        }

        try {
            var outputs = File.ReadAllLines(path: request).Where(predicate: static line => (line.Length != 0)).Select(selector: ShaderBuildOutput.Parse).ToArray();
            var compiler = new ShaderCompiler(
                cacheDirectory: ((options.TryGetValue(key: "--cache", value: out var cache) && !string.IsNullOrWhiteSpace(value: cache))
                    ? cache
                    : ShaderCompiler.DefaultCacheDirectory),
                toolchain: ShaderToolchain.OfCommand(command: options.GetValueOrDefault(key: "--dxc"))
            );
            var build = new ShaderBuild(
                availableMemory: Puck.Hosting.HostMemory.AvailablePhysicalBytes,
                compiler: compiler,
                lockFile: lockFile,
                log: Console.Out,
                projectDirectory: project
            );

            if (arguments[0] == "check") {
                return (build.Check(outputs: outputs) ? 0 : 1);
            }

            using var stopping = new CancellationTokenSource();

            return (await build.CompileAsync(
                cancellationToken: stopping.Token,
                cores: ((jobs > 0) ? new FixedShaderCoreBroker(cores: jobs) : new StreamShaderCoreBroker(answers: new StreamReader(stream: Console.OpenStandardInput()), requests: Console.Out, cancel: stopping.Cancel)),
                outputs: outputs
            ).ConfigureAwait(continueOnCapturedContext: false) ? 0 : 1);
        } catch (OperationCanceledException) {
            // The build joins every compile it started before it reports cancellation, so its host can tell a graceful
            // stop from the process-tree kill it falls back to.
            Console.Out.WriteLine(value: CancelledLine);

            return 1;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or FormatException)) {
            return Refuse(reason: exception.Message);
        }
    }

    /// <summary>The line the generator prints when its build was cancelled and every compile it started has ended.</summary>
    private const string CancelledLine = "Shader build cancelled: every compile it started has ended.";

    private static int Refuse(string reason) {
        Console.Out.WriteLine(value: $"Puck.Shaders.Generator: error PUCKSHADER: {reason.ReplaceLineEndings(replacementText: " ")}");

        return 2;
    }
}
