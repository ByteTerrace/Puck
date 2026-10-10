using System.CommandLine;



namespace Puck.Cli.Format.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: format.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Format.Tests invokes.",
        verbs: [Puck.Cli.Format.FormatCommand.Create()]
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
