using System.CommandLine;

namespace Puck.Cli.Docs;

/// <summary><c>puck docs</c> — the documentation family: <c>build</c> stages the website reference, <c>citations</c>
/// checks the console-verb tokens skills and XML docs cite, and <c>links</c> checks relative links and cited
/// repository paths.</summary>
internal static class DocsCommand {
    public static Command Create() => new(
        description: "Build the reference site and check the documentation's links and citations.",
        name: "docs"
    ) {
        DocsBuildCommand.Create(),
        DocsCitationsCommand.Create(),
        DocsLinksCommand.Create(),
    };
}
