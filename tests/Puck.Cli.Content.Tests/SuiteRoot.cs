using System.CommandLine;

using Puck.Cli.Firmware;

namespace Puck.Cli.Content.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: firmware.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Content.Tests invokes.",
        verbs: [FirmwareCommand.Create()]
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
