using System.CommandLine;

using Puck.Cli.Automation;
using Puck.Cli.NuGet;
using Puck.Cli.Test;

namespace Puck.Cli.Release.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: bundle, nuget, and the test verb a world document is carried through.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Release.Tests invokes.",
        verbs: [BundleCommand.Create(), NuGetCommand.Create(), TestCommand.Create()]
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
