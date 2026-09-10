using System.CommandLine;
using System.Globalization;
using System.Net.Http.Headers;

namespace Puck.Cli.Format;

/// <summary>
/// <c>puck format submit</c> — applies a completed formatting run's artifact to its pull request. Runs only from
/// the privileged workflow on a CLI built off the default branch, with FORMAT_RUN_ID, GITHUB_REPOSITORY,
/// GITHUB_API_URL, GITHUB_GRAPHQL_URL, and GH_TOKEN in the environment.
/// </summary>
internal static class FormatSubmitCommand {
    public static Command Create() {
        var command = new Command(description: "Validate and apply a completed formatting run's artifact to its pull request; the environment names the run and the token.", name: "submit");

        command.SetAction(action: (_, cancellationToken) => RunAsync(cancellationToken: cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(CancellationToken cancellationToken) {
        using var client = new HttpClient { BaseAddress = new Uri(uriString: (CliGitHub.EnvironmentVariable(name: "GITHUB_API_URL").TrimEnd(trimChar: '/') + "/")), Timeout = TimeSpan.FromMinutes(value: 2) };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(parameter: CliGitHub.EnvironmentVariable(name: "GH_TOKEN"), scheme: "Bearer");
        client.DefaultRequestHeaders.UserAgent.ParseAdd(input: "Puck-Formatter/1.0");
        client.DefaultRequestHeaders.Add(name: "X-GitHub-Api-Version", value: "2022-11-28");
        await new FormatSubmission(client: client, report: CliGitHub.Summarize).RunAsync(
            graphUrl: CliGitHub.EnvironmentVariable(name: "GITHUB_GRAPHQL_URL"),
            repository: CliGitHub.EnvironmentVariable(name: "GITHUB_REPOSITORY"),
            runId: long.Parse(provider: CultureInfo.InvariantCulture, s: CliGitHub.EnvironmentVariable(name: "FORMAT_RUN_ID"))
        ).WaitAsync(cancellationToken: cancellationToken);
        return 0;
    }
}
