using System.CommandLine;

using Puck.Cli.Affected;
using Puck.Cli.Baselines;
using Puck.Cli.Host;

namespace Puck.Cli.Gate.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: affected, baselines, gate and host.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Gate.Tests invokes.",
        verbs: [AffectedCommand.Create(), BaselinesCommand.Create(), GateCommand.Create(clock: TimeProvider.System), HostCommand.Create()]
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
