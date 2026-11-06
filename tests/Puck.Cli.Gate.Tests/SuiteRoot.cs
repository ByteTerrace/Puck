using System.CommandLine;

using Puck.Cli.Affected;
using Puck.Cli.Baselines;
using Puck.Cli.Host;

namespace Puck.Cli.Gate.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: affected, baselines, gate and host, over a composition that names no other verb.</summary>
internal static class SuiteRoot {
    /// <summary>A composition naming no other verb: no schema the gate places a file through.</summary>
    public static GateComposition Composition { get; } = new(SchemaSourceTypes: static _ => []);

    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Gate.Tests invokes.",
        verbs: [AffectedCommand.Create(composition: Composition), BaselinesCommand.Create(), GateCommand.Create(clock: TimeProvider.System, composition: Composition), HostCommand.Create()]
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
