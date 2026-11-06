using Puck.World;
using System.Security.Cryptography;
using System.Text.Json;

namespace Puck.Cli.Motion;

public static partial class MotionCommand {
    private static int RunRoute(string routePath, string? output, string? worldPath, string? worldArtifact,
        string? againstArtifact, WorldBackendPreference? backend, string? cacheDirectory, int timeoutSeconds) {
        if (!CliPaths.TryGetRepositoryRoot(out var repository)) { return CliExit.Refused; }
        var directory = Path.GetFullPath(output ?? RunDirectory.CreatePath(prefix: "puck-motion-route-"));
        Directory.CreateDirectory(directory);
        try {
            var route = Path.GetFullPath(routePath);
            var plan = MotionRoutePlan.Read(route);
            var world = Path.GetFullPath(worldPath ?? Path.Combine(repository, DefaultWorld));
            var spirv = WorldLaunch.NeedsSpirv(backend: backend, shape: WorldShape.Document);
            if (!WorldLaunch.TryResolve(named: worldArtifact, verb: Verb, repositoryRoot: repository,
                logDirectory: directory, spirv: spirv, build: out var lease, error: out var error)) {
                return CliExit.Refuse(Verb, "the route World build", error);
            }
            using var retained = lease;
            var artifact = lease.Path;
            // A/A is the first qualification. Cross-artifact runs require the same runtime measurement protocol;
            // an old artifact refusing the startup flag or trace verb is explicitly unmatched, never tolerated.
            WorldBuild? secondLease = null;
            if (againstArtifact is not null && !WorldLaunch.TryResolve(named: againstArtifact, verb: Verb,
                repositoryRoot: repository, logDirectory: directory, spirv: spirv,
                build: out secondLease, error: out var secondError)) {
                return CliExit.Refuse(Verb, "the comparison artifact", secondError);
            }
            using var retainedSecond = secondLease;
            var second = secondLease?.Path ?? artifact;
            var deadline = TimeSpan.FromSeconds(timeoutSeconds);
            var suite = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan Remaining() => suite.Elapsed < deadline ? deadline - suite.Elapsed : throw new TimeoutException("The whole route run exhausted its deadline.");
            var cache = cacheDirectory is null ? null : Path.GetFullPath(cacheDirectory);
            Console.Error.WriteLine($"motion: recording canonical authority input for route {plan.Name}; no capture holds, fixed tick clock.");
            var recorded = MotionRouteRun.Execute(plan, artifact, world, repository, Path.Combine(directory, "record"), backend, cache, Remaining(), null, lease.Key);
            var tape = Path.Combine(recorded.Directory, MotionRouteRun.TapeFile);
            if (recorded.Problems.Count != 0 || !File.Exists(tape)) {
                File.WriteAllText(Path.Combine(directory, "record-failure.json"), JsonSerializer.Serialize(recorded));
                return CliExit.Refuse(Verb, "the canonical route recording", string.Join("; ", recorded.Problems));
            }
            var tapeHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(tape)));
            Console.Error.WriteLine("motion: replay leg A in a fresh World.");
            var a = MotionRouteRun.Execute(plan, artifact, world, repository, Path.Combine(directory, "run-a"), backend, cache, Remaining(), tape, lease.Key);
            Console.Error.WriteLine("motion: replay leg B in a fresh World.");
            var b = MotionRouteRun.Execute(plan, second, world, repository, Path.Combine(directory, "run-b"), backend, cache, Remaining(), tape, secondLease?.Key ?? (againstArtifact is null ? lease.Key : null));
            var problems = MotionRouteReport.Compare(a, b, plan).ToList();
            if (againstArtifact is null && !a.BuildFiles.SequenceEqual(b.BuildFiles)) { problems.Add("The A/A runtime or shader files changed between legs."); }
            MotionRouteReport.Write(directory, plan, route, tapeHash, recorded, a, b, problems);
            Console.Out.WriteLine($"motion: route {(problems.Count == 0 ? "MATCHED" : "UNMATCHED")} — controlled tick-throughput; normal realtime frame bar NOT measured.");
            foreach (var problem in problems) { Console.Out.WriteLine($"motion: {problem}"); }
            Console.Out.WriteLine($"motion: report {Path.Combine(directory, "route-report.json").Replace('\\', '/')}");
            return problems.Count == 0 ? CliExit.Success : CliExit.Failed;
        } catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or ArgumentException or TimeoutException) {
            return CliExit.Refuse(Verb, "the route", exception.Message);
        }
    }
}
