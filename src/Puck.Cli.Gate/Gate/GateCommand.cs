using System.CommandLine;
using Puck.Cli.Affected;
using Puck.Cli.Host;

namespace Puck.Cli.Gate;

/// <summary><c>puck gate</c> — the batch qualification for a branch; <see cref="GatePlan"/> holds its steps.</summary>
public static class GateCommand {
    private const string Verb = "gate";

    private static int Run(string target, bool gpu, int suiteJobs, int gpuJobs, TimeProvider clock, GateComposition composition, CancellationToken cancellationToken) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        // The build rewrites every project's output, and a CLI running from one holds its assemblies open.
        var running = Path.GetDirectoryName(path: typeof(GateCommand).Assembly.Location)!;
        var relative = Path.GetRelativePath(path: running, relativeTo: repositoryRoot);

        if (!Path.IsPathRooted(path: relative) && !relative.StartsWith(comparisonType: StringComparison.Ordinal, value: "..")) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: running), why: "the gate rebuilds the checkout this CLI runs from; run it from a copy of the CLI outside the checkout.");
        }

        var directory = RunDirectory.CreatePath(prefix: "puck-gate-");

        return GateRun.Run(
            directory: directory,
            gpu: gpu,
            gpuJobs: gpuJobs,
            repositoryRoot: repositoryRoot,
            suiteJobs: suiteJobs,
            clock: clock,
            runner: new ProcessGateRunner(cancellationToken: cancellationToken, clock: clock, gpuVerbGrammars: composition.GpuVerbGrammars),
            target: target
        );
    }

    /// <summary>Creates <c>puck gate</c>.</summary>
    /// <param name="clock">The CLI host's clock.</param>
    /// <param name="composition">What the gate reads from the verbs composed beside it.</param>
    /// <returns>The command.</returns>
    public static Command Create(TimeProvider clock, GateComposition composition) {
        var mergeBaseOption = AffectedCommand.MergeBase(description: "Required. The branch the change lands on, such as origin/main or the integration branch your brief names; the change is read against its merge base with HEAD.");
        var gpuOption = AffectedCommand.Gpu();

        gpuOption.Description = "Add affected canaries (side by side, up to --gpu-jobs legs on the GPU) and parity, then device suites and docs citations, one step after another.";
        var gpuJobsOption = Canary.CanaryGpuJobs.Create();
        var suiteJobsOption = AffectedSuites.Jobs();
        var command = new Command(
            description: "Build the solution and run the checks a branch's change needs, against its merge base.",
            name: Verb
        ) { mergeBaseOption, gpuOption, gpuJobsOption, suiteJobsOption };

        mergeBaseOption.Required = true;
        command.Detail(detail: (GatePlan.Detail() + $"""

            Baseline steps run only when affected reaches their owning project or declared data inputs.
            The affected step runs its suites side by side up to --suite-jobs. The chosen canaries, side by
            side up to --gpu-jobs legs on the GPU, and parity follow the baseline checks, only with --gpu.
            Checks write nothing.
            Before each heavy step, admission waits for memory and disk headroom by host load's default thresholds.
            CPU load is advisory: a step runs whatever the CPU, and the gate prints a load over the threshold.
            A step that opens a device (the --gpu steps) also waits for an idle GPU, and a heavy suite
            ({string.Join(separator: ", ", values: HostProcesses.HeavyTestAssemblies)}, also inside affected --run) holds the shared
            heavy lease through admission and execution. Nested runs use serialized child leases after
            validating the ancestor holder, so they never wait on their own enclosing lease.
            A step waits at most {HostAdmission.Timeout.TotalMinutes:0} minutes and a heavy suite {CliLease.DefaultWait.TotalHours:0} hours, reporting when waiting
            starts, each new holder, a still-waiting line every {HostAdmission.Heartbeat.TotalMinutes:0} minutes, and when capacity returns.
            The locks check runs first, from this CLI rather than the copy it builds, and the solution build restores nothing.
            A failed locked restore, build or CLI copy stops the run. Other failures allow later checks.
            gate.log holds full output; gate.steps records each start and exit with UTC time and whole seconds.
            Both files are kept and named in the summary. Run from a CLI copy outside the checkout.

            Exit codes: 0 every step passed; 1 a step failed; 2 refused (no merge base,
            admission timeout, or a CLI running from the checkout it would rebuild).
            """));
        command.SetAction(action: (parseResult, cancellationToken) => Task.FromResult(result: Run(
            gpu: parseResult.GetValue(option: gpuOption),
            gpuJobs: parseResult.GetValue(option: gpuJobsOption),
            suiteJobs: parseResult.GetValue(option: suiteJobsOption),
            clock: clock,
            composition: composition,
            cancellationToken: cancellationToken,
            target: parseResult.GetRequiredValue(option: mergeBaseOption)
        )));
        return command;
    }
}
