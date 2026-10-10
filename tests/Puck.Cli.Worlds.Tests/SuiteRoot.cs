using System.CommandLine;



namespace Puck.Cli.Worlds.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: every world-authoring verb.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Worlds.Tests invokes.",
        verbs: WorldsRoot.Verbs()
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
