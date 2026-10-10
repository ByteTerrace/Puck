using Puck.Abstractions;

namespace Puck.Cli.Schema;

/// <summary>Reaches the existing schema generator when a model edit makes its generated getters stale.</summary>
public static class SchemaBootstrap {
    private const string Project = "src/Puck.Cli/Puck.Cli.csproj";
    private const string Artifact = CliPaths.ToolAssembly;

    private static readonly TimeSpan BuildBudget = TimeSpan.FromMinutes(value: 10);
    private static readonly TimeSpan SchemaBudget = TimeSpan.FromMinutes(value: 2);

    public static bool IsBootstrap =>
#if PUCK_SCHEMA_BOOTSTRAP
        true;
#else
        false;
#endif

    internal static int Run(string repositoryRoot, bool bundle, bool check, string? output, TimeProvider clock) {
        using var run = RunDirectory.Create(prefix: "puck-schema-bootstrap-");

        return run.Conclude(exitCode: RunInDirectory(
            repositoryRoot: repositoryRoot, directory: run.Path, bundle: bundle, check: check, output: output, clock: clock
        ));
    }

    // Injectable only at the existing process boundary: the build, required artifact and retained logs remain real.
    public static int RunInDirectory(string repositoryRoot, string directory, bool bundle, bool check, string? output,
        TimeProvider clock, Func<IReadOnlyList<string>, TimeSpan, CliProcessResult>? runner = null) {
        var artifacts = Path.Combine(path1: directory, path2: "artifacts");
        var binaries = Path.Combine(path1: directory, path2: "cli");
        var logs = Path.Combine(path1: directory, path2: "logs");
        var capture = (runner ?? ((arguments, timeout) => CliProcess.RunCaptured(
            fileName: "dotnet", arguments: arguments, input: string.Empty, timeout: timeout,
            workingDirectory: repositoryRoot, clock: clock
        )));

        if (!CliProjectBuild.TryBuild(
            repositoryRoot: repositoryRoot, project: Project, outputDirectory: binaries, logDirectory: logs,
            timeout: BuildBudget, build: out _, error: out var error, artifactName: Artifact,
            runner: (arguments, timeout) => capture([
                .. arguments,
                "-p:PuckSchemaBootstrap=true",
                "-p:UseArtifactsOutput=true",
                $"-p:ArtifactsPath={PuckPaths.Normalize(path: artifacts)}"
            ], timeout)
        )) {
            return CliExit.Refuse(verb: "schema", what: "bootstrap build", why: error);
        }

        List<string> command = [Path.Combine(path1: binaries, path2: Artifact), "schema"];

        if (bundle) { command.Add(item: "--bundle"); }
        if (check) { command.Add(item: "--check"); }
        if (output is not null) { command.AddRange(collection: ["--output", Path.GetFullPath(path: output)]); }
        var result = capture(command, SchemaBudget);
        var log = Path.Combine(path1: logs, path2: "schema.bootstrap.log");

        File.WriteAllText(path: log, contents: $"{result.Stdout}\n--- stderr ---\n{result.Stderr}");
        Console.Out.Write(value: result.Stdout);
        Console.Error.Write(value: result.Stderr);
        if (result.TimedOut) {
            return CliExit.Refuse(verb: "schema", what: "bootstrap generator", why: $"exceeded its two-minute budget; output is in {CliPaths.ToDisplay(fullPath: log)}");
        }
        return result.ExitCode;
    }
}
