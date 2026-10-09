using System.CommandLine;



namespace Puck.Cli.Bench.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: bench.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Bench.Tests invokes.",
        verbs: [Puck.Cli.Bench.BenchRunner.Create(clock: TimeProvider.System)]
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
