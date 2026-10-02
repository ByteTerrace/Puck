using System.CommandLine;
using Puck.Cli.Affected;

namespace Puck.Cli.Gate;

/// <summary><c>puck gate</c> — the change-scoped CPU gate for a branch; <see cref="GateRun"/> holds its steps.</summary>
internal static class GateCommand {
    private const string Verb = "gate";

    private static int Run(string target, bool gpu) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        // The build rewrites every project's output, and a CLI running from one holds its assemblies open.
        var running = Path.GetDirectoryName(path: typeof(GateCommand).Assembly.Location)!;

        if (!Path.GetRelativePath(path: running, relativeTo: repositoryRoot).StartsWith(comparisonType: StringComparison.Ordinal, value: "..")) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: running), why: "the gate rebuilds the checkout this CLI runs from; run it from a copy of the CLI outside the checkout.");
        }

        var directory = Directory.CreateTempSubdirectory(prefix: "puck-gate-").FullName;

        return GateRun.Run(
            directory: directory,
            gpu: gpu,
            repositoryRoot: repositoryRoot,
            runner: new ProcessGateRunner(),
            target: target
        );
    }

    public static Command Create() {
        var mergeBaseOption = AffectedCommand.MergeBase(description: "The branch the change lands on; the change is read against its merge base with HEAD.");
        var gpuOption = AffectedCommand.Gpu();
        var command = new Command(
            description: "Build the solution and run the checks a branch's change needs, against its merge base.",
            name: Verb
        ) { mergeBaseOption, gpuOption };

        mergeBaseOption.DefaultValueFactory = static _ => GateRun.DefaultTarget;
        command.Detail(detail: """
              Steps, in order:
                1. dotnet build Puck.slnx -c Release; a failed build prints its errors and stops the gate.
                2. Copy the CLI that build wrote into the run's own temporary directory; every later
                   step runs that copy, so it runs the candidate's code and nothing else overwrites it.
                3. puck affected --merge-base <merge base> --run: the suites, the .puck test worlds and
                   the catalog check the change reaches, read against the merge base of HEAD and
                   --merge-base, so commits the target gained after the branch left it are not counted.
                   --gpu adds --gpu: the chosen canaries, then parity, one after the other.
                4. puck format --check over the changed C# and .puck sources, puck lengths --check,
                   puck comment-smells --check, puck docs links, puck schema --check,
                   puck architecture --check, puck registry --check, puck vocabulary --check,
                   puck shaders generate --check and puck branding --check. Nothing is rewritten.
              Each step's full output goes to gate.log in the run's directory, which the summary names
              and the run keeps; the CLI copy is removed. Run it from a CLI outside the checkout: the
              build rewrites src/Puck.Cli/bin.

              Exit codes: 0 every step passed; 1 the build or a step failed; 2 refused (no merge base,
              or the CLI runs from the checkout it would rebuild).
            """);
        command.SetAction(action: parseResult => Run(
            gpu: parseResult.GetValue(option: gpuOption),
            target: parseResult.GetValue(option: mergeBaseOption)!
        ));

        return command;
    }
}
