using System.CommandLine;
using Puck.Cli.Affected;
using Puck.Cli.Host;

namespace Puck.Cli.Gate;

/// <summary><c>puck gate</c> — the batch qualification for a branch; <see cref="GatePlan"/> holds its steps.</summary>
internal static class GateCommand {
    private const string Verb = "gate";

    private static int Run(string target, bool gpu, bool record, TimeProvider clock, CancellationToken cancellationToken) {
        if (record && !gpu) {
            return CliExit.Refuse(verb: Verb, what: "--record", why: "requires --gpu and an all-green qualification.");
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        // The build rewrites every project's output, and a CLI running from one holds its assemblies open.
        var running = Path.GetDirectoryName(path: typeof(GateCommand).Assembly.Location)!;

        if (!Path.GetRelativePath(path: running, relativeTo: repositoryRoot).StartsWith(comparisonType: StringComparison.Ordinal, value: "..")) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: running), why: "the gate rebuilds the checkout this CLI runs from; run it from a copy of the CLI outside the checkout.");
        }

        var directory = RunDirectory.CreatePath(prefix: "puck-gate-");

        return GateRun.Run(
            directory: directory,
            gpu: gpu,
            repositoryRoot: repositoryRoot,
            record: record,
            clock: clock,
            runner: new ProcessGateRunner(cancellationToken: cancellationToken, clock: clock),
            target: target
        );
    }

    public static Command Create(TimeProvider clock) {
        var mergeBaseOption = AffectedCommand.MergeBase(description: "The branch the change lands on; the change is read against its merge base with HEAD.");
        var gpuOption = AffectedCommand.Gpu();

        gpuOption.Description = "Add affected canaries and parity, device suites, every recorded counters workload and docs citations, serially.";
        var recordOption = new Option<bool>("--record") { Description = "Requires --gpu; refresh canary coverage only after every qualification step passes." };
        var command = new Command(
            description: "Build the solution and run the checks a branch's change needs, against its merge base.",
            name: Verb
        ) { mergeBaseOption, gpuOption, recordOption };

        mergeBaseOption.DefaultValueFactory = static _ => GateRun.DefaultTarget;
        command.Detail(detail: (GatePlan.Detail() + $"""

            Baseline steps run only when affected reaches their owning project or declared data inputs.
            The chosen canaries and parity follow the baseline checks, only with --gpu. Counters expands every
            tests/Puck.Counters/*.world.json with matching ceilings, using its sibling script when
            present or the script recorded in its ceilings. Checks write nothing; --record writes coverage.
            Before each heavy step, admission waits for memory and disk headroom by host load's default thresholds.
            CPU load is advisory: a step runs whatever the CPU, and the gate prints a load over the threshold.
            A step that opens a device (the --gpu steps) also waits for an idle GPU.
            It waits at most {HostAdmission.Timeout.TotalMinutes:0} minutes, reporting when waiting starts and capacity returns.
            A failed build or CLI copy stops the run. Other failures allow later checks, but skip recording.
            gate.log holds full output; gate.steps records each start and exit with UTC time and whole seconds.
            Both files are kept and named in the summary. Run from a CLI copy outside the checkout.

            Exit codes: 0 every step passed; 1 a step failed; 2 refused (invalid record, no merge base,
            admission timeout, or a CLI running from the checkout it would rebuild).
            """));
        command.SetAction(action: (parseResult, cancellationToken) => Task.FromResult(result: Run(
            gpu: parseResult.GetValue(option: gpuOption),
            record: parseResult.GetValue(option: recordOption),
            clock: clock,
            cancellationToken: cancellationToken,
            target: parseResult.GetValue(option: mergeBaseOption)!
        )));
        return command;
    }
}
