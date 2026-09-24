using System.CommandLine;
using System.Net.Http.Headers;

namespace Puck.Cli.PullRequest;

/// <summary>
/// <c>puck pull-request submit-format</c> — applies a completed formatting run's artifact to its pull request. Runs only from
/// the privileged workflow on a CLI built off the default branch, naming the formatting run with <c>--run-id</c>, with
/// GitHub's GITHUB_REPOSITORY, GITHUB_API_URL and GITHUB_GRAPHQL_URL and the workflow's GH_TOKEN in the environment.
/// </summary>
internal static class PullRequestSubmitFormatCommand {
    private static async Task<int> RunAsync(long runId, CancellationToken cancellationToken) {
        using var client = new HttpClient { BaseAddress = new Uri(uriString: (CliGitHub.ApiUrl.TrimEnd(trimChar: '/') + "/")), Timeout = TimeSpan.FromMinutes(value: 2) };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            parameter: CliGitHub.Token,
            scheme: "Bearer"
        );
        client.DefaultRequestHeaders.UserAgent.ParseAdd(input: "Puck-Formatter/1.0");
        client.DefaultRequestHeaders.Add(
            name: "X-GitHub-Api-Version",
            value: "2022-11-28"
        );
        await new FormatSubmission(
            client: client,
            report: CliGitHub.Summarize
        ).RunAsync(
            graphUrl: CliGitHub.GraphQlUrl,
            repository: CliGitHub.Repository,
            runId: runId
        ).WaitAsync(cancellationToken: cancellationToken);
        return 0;
    }

    public static Command Create() {
        var runIdOption = new Option<long>(name: "--run-id") {
            Description = "The completed formatting workflow run whose artifact is applied.",
            Required = true,
        };
        var command = new Command(
            description: "Apply a completed pull-request formatting run's artifact to its pull request.",
            name: "submit-format"
        ) { runIdOption };

        command.Detail(detail: """
            Runs only from the privileged workflow, on a CLI built off the default branch.
            --run-id names the formatting run; the environment carries GitHub's own values and
            the credentials: GITHUB_REPOSITORY, GITHUB_API_URL, GITHUB_GRAPHQL_URL, and GH_TOKEN. The
            artifact is read as data and every path it names is validated before a commit is
            made with an expected-head guard.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => RunAsync(
            cancellationToken: cancellationToken,
            runId: parseResult.GetValue(option: runIdOption)
        ));
        return command;
    }
}
