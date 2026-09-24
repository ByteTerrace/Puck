using System.CommandLine;

namespace Puck.Cli.PullRequest;

/// <summary><c>puck pull-request</c> — the formatting bot's two halves: preparing a pull request's formatting artifact
/// in an unprivileged checkout, and applying it from the privileged workflow.</summary>
internal static class PullRequestCommand {
    public static Command Create() => new(
        description: "Prepare and apply a pull request's automatic formatting.",
        name: "pull-request"
    ) {
        PullRequestFormatCommand.Create(),
        PullRequestSubmitFormatCommand.Create(),
    };
}
