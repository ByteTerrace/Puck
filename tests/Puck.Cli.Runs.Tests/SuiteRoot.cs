using System.CommandLine;

using Puck.Cli.Canary;
using Puck.Cli.Counters;
using Puck.Cli.Determinism;
using Puck.Cli.Parity;
using Puck.Cli.Sweep;

namespace Puck.Cli.Runs.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: canary, counters, determinism, parity and sweep.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Runs.Tests invokes.",
        verbs: [CanaryCommand.Create(), CountersCommand.Create(), DeterminismCommand.Create(), ParityCommand.Create(), SweepCommand.Create()]
    );
    public static int Invoke(string[] args) => CliRoot.Invoke(
        args: args,
        root: Create()
    );
    public static Task<int> InvokeAsync(string[] args) => CliRoot.InvokeAsync(
        args: args,
        root: Create()
    );
}
