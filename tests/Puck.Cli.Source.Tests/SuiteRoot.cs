using System.CommandLine;

using Puck.Cli.Analysis;
using Puck.Cli.Scan;

namespace Puck.Cli.Source.Tests;

/// <summary>The source verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Source.Tests invokes.",
        verbs: [ReferencesCommand.Create(), ScanCommand.Create()]
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
