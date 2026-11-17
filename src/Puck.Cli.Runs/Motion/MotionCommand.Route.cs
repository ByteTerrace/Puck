using Puck.World;
using Puck.Assets;
using System.Text.Json;

namespace Puck.Cli.Motion;

public static partial class MotionCommand {
    private static int RunRoute(string routePath, string? output, string? worldPath, string? worldArtifact,
        string? againstArtifact, WorldBackendPreference? backend, string? cacheDirectory, int timeoutSeconds, CancellationToken cancellationToken) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repository)) { return CliExit.Refused; }
        var directory = Path.GetFullPath(path: (output ?? RunDirectory.CreatePath(prefix: "puck-motion-route-")));

        Directory.CreateDirectory(path: directory);
        try {
            var route = Path.GetFullPath(path: routePath);
            var plan = MotionRoutePlan.Read(path: route);
            var world = Path.GetFullPath(path: (worldPath ?? Path.Combine(path1: repository, path2: DefaultWorld)));
            var spirv = WorldLaunch.NeedsSpirv(backend: backend, shape: WorldShape.Document);

            if (!WorldLaunch.TryResolve(build: out var lease, cancellationToken: cancellationToken, error: out var error, logDirectory: directory,
                named: worldArtifact, repositoryRoot: repository, spirv: spirv, verb: Verb)) {
                return CliExit.Refuse(verb: Verb, what: "the route World build", why: error);
            }
            using var retained = lease;
            var artifact = lease.Path;
            // A/A is the first qualification. Cross-artifact runs require the same runtime measurement protocol;
            // an old artifact refusing the startup flag or trace verb is explicitly unmatched, never tolerated.
            WorldBuild? secondLease = null;

            if ((againstArtifact is not null) && !WorldLaunch.TryResolve(build: out secondLease, cancellationToken: cancellationToken, error: out var secondError,
                logDirectory: directory, named: againstArtifact, repositoryRoot: repository,
                spirv: spirv, verb: Verb)) {
                return CliExit.Refuse(verb: Verb, what: "the comparison artifact", why: secondError);
            }
            using var retainedSecond = secondLease;
            var second = (secondLease?.Path ?? artifact);
            var deadline = TimeSpan.FromSeconds(seconds: timeoutSeconds);
            var suite = System.Diagnostics.Stopwatch.StartNew();

            TimeSpan Remaining() => ((suite.Elapsed < deadline) ? (deadline - suite.Elapsed) : throw new TimeoutException(message: "The whole route run exhausted its deadline."));
            var cache = ((cacheDirectory is null) ? null : Path.GetFullPath(path: cacheDirectory));

            Console.Error.WriteLine(value: $"motion: recording canonical authority input for route {plan.Name}; no capture holds, fixed tick clock.");
            var recorded = MotionRouteRun.Execute(plan, artifact, world, repository, Path.Combine(path1: directory, path2: "record"), backend, cache, Remaining(), null, lease.Key, cancellationToken);
            var tape = Path.Combine(path1: recorded.Directory, path2: MotionRouteRun.TapeFile);

            if ((recorded.Problems.Count != 0) || !File.Exists(path: tape)) {
                File.WriteAllText(Path.Combine(path1: directory, path2: "record-failure.json"), JsonSerializer.Serialize(recorded));
                return CliExit.Refuse(verb: Verb, what: "the canonical route recording", why: string.Join(separator: "; ", values: recorded.Problems));
            }
            var tapeHash = ContentPin.OfFile(path: tape).Hex;

            Console.Error.WriteLine(value: "motion: replay leg A in a fresh World.");
            var a = MotionRouteRun.Execute(plan, artifact, world, repository, Path.Combine(path1: directory, path2: "run-a"), backend, cache, Remaining(), tape, lease.Key, cancellationToken);

            Console.Error.WriteLine(value: "motion: replay leg B in a fresh World.");
            var b = MotionRouteRun.Execute(plan, second, world, repository, Path.Combine(path1: directory, path2: "run-b"), backend, cache, Remaining(), tape, (secondLease?.Key ?? ((againstArtifact is null) ? lease.Key : null)), cancellationToken);
            var problems = MotionRouteReport.Compare(first: a, plan: plan, second: b).ToList();

            if ((againstArtifact is null) && !a.BuildFiles.SequenceEqual(second: b.BuildFiles)) { problems.Add(item: "The A/A runtime or shader files changed between legs."); }
            MotionRouteReport.Write(a: a, b: b, directory: directory, plan: plan, problems: problems, recorded: recorded, routePath: route, tapeSha256: tapeHash);
            Console.Out.WriteLine(value: $"motion: route {((problems.Count == 0) ? "MATCHED" : "UNMATCHED")} — controlled tick-throughput; normal realtime frame bar NOT measured.");
            foreach (var problem in problems) { Console.Out.WriteLine(value: $"motion: {problem}"); }
            Console.Out.WriteLine(value: $"motion: report {Puck.Abstractions.PuckPaths.Slashed(path: Path.Combine(path1: directory, path2: "route-report.json"))}");
            return ((problems.Count == 0) ? CliExit.Success : CliExit.Failed);
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or JsonException or ArgumentException or TimeoutException)) {
            return CliExit.Refuse(verb: Verb, what: "the route", why: exception.Message);
        }
    }
}
