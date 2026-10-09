using System.CommandLine;

using Puck.Cli.WorktreeReport;

namespace Puck.Cli.Worktrees.Tests;

/// <summary>The verbs this suite's laws invoke, composed the way the <c>puck</c> root composes them: worktree-report.</summary>
internal static class SuiteRoot {
    public static RootCommand Create() => CliRoot.Compose(
        description: "The verbs Puck.Cli.Worktrees.Tests invokes.",
        verbs: [WorktreeReportCommand.Create(clock: TimeProvider.System)]
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
