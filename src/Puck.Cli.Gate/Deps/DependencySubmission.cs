using System.Text.Json.Nodes;

namespace Puck.Cli.Deps;

/// <summary>The second half of the dependency update: puts the tree <c>puck deps update</c> changed on a branch of its
/// own, opens or refreshes the pull request for that branch, and starts the same validation a pull request gets. The
/// branch belongs to this verb and every run rebuilds it from the checked-out default branch, so at most one dependency
/// pull request is open and each run replaces what the last proposed. A push or pull request made with the workflow's
/// own token starts no workflow, so the formatting check and the shared validation graph are dispatched by name, as the
/// formatting submitter does for its commits.</summary>
public static class DependencySubmission {
    private const string BotEmail = "41898282+github-actions[bot]@users.noreply.github.com";
    private const string BotName = "github-actions[bot]";
    private const string Title = "deps: update dependencies";

    /// <summary>Commits the working tree's tracked changes to <paramref name="branch"/>, pushes it, and opens or updates its pull request.</summary>
    /// <param name="repositoryRoot">The checkout, at the default branch, holding the update's changes.</param>
    /// <param name="body">The pull request description: the update's Markdown report.</param>
    /// <param name="branch">The branch this verb owns.</param>
    /// <param name="cancellationToken">Cancels the submission.</param>
    /// <returns>The pull request's number, or <see langword="null"/> when the tree holds no change to propose.</returns>
    public static async Task<int?> RunAsync(string repositoryRoot, string body, string branch, CancellationToken cancellationToken) {
        if ((await CliGit.CaptureAsync(arguments: ["status", "--porcelain", "--untracked-files=no"], cancellationToken: cancellationToken, repository: repositoryRoot).ConfigureAwait(continueOnCapturedContext: false)).Trim().Length == 0) {
            CliGitHub.Summarize(message: "No dependency moved; no branch or pull request was changed.");
            return null;
        }
        using var client = CliGitHub.CreateClient(userAgent: "Puck-Deps/1.0");
        var repository = CliGitHub.Repository;
        var info = await CliGitHub.GetJsonAsync(client: client, path: $"repos/{repository}").ConfigureAwait(continueOnCapturedContext: false);
        var defaultBranch = ((string)info["default_branch"]!);
        var baseSha = await CliGit.CommitAsync(repository: repositoryRoot).ConfigureAwait(continueOnCapturedContext: false);

        if (branch == defaultBranch) { throw new InvalidOperationException(message: "Refusing to rewrite the default branch."); }
        await CliGit.CaptureAsync(arguments: ["switch", "--force-create", branch], cancellationToken: cancellationToken, repository: repositoryRoot).ConfigureAwait(continueOnCapturedContext: false);
        await CliGit.CaptureAsync(arguments: ["add", "--update"], cancellationToken: cancellationToken, repository: repositoryRoot).ConfigureAwait(continueOnCapturedContext: false);
        await CliGit.CaptureAsync(arguments: ["-c", $"user.name={BotName}", "-c", $"user.email={BotEmail}", "commit", "--message", Title, "--message", body], cancellationToken: cancellationToken, repository: repositoryRoot).ConfigureAwait(continueOnCapturedContext: false);
        // The branch is this verb's alone; a run replaces whatever an earlier run pushed.
        await CliGit.CaptureAsync(arguments: ["push", "--force", "origin", $"refs/heads/{branch}:refs/heads/{branch}"], cancellationToken: cancellationToken, repository: repositoryRoot).ConfigureAwait(continueOnCapturedContext: false);
        var owner = repository[..repository.IndexOf(value: '/')];
        var open = (await CliGitHub.GetJsonAsync(client: client, path: $"repos/{repository}/pulls?state=open&head={Uri.EscapeDataString(stringToEscape: $"{owner}:{branch}")}").ConfigureAwait(continueOnCapturedContext: false)).AsArray();
        int number;

        if (open.Count > 0) {
            number = ((int)open[0]!["number"]!);
            await CliGitHub.SendJsonAsync(client: client, method: HttpMethod.Patch, path: $"repos/{repository}/pulls/{number}", value: new JsonObject { ["body"] = body, ["title"] = Title }).ConfigureAwait(continueOnCapturedContext: false);
        } else {
            var created = await CliGitHub.SendJsonAsync(client: client, method: HttpMethod.Post, path: $"repos/{repository}/pulls", value: new JsonObject { ["base"] = defaultBranch, ["body"] = body, ["head"] = branch, ["title"] = Title }).ConfigureAwait(continueOnCapturedContext: false);

            number = ((int)created!["number"]!);
        }
        await CliGitHub.SendJsonAsync(client: client, method: HttpMethod.Post, path: $"repos/{repository}/actions/workflows/format.yml/dispatches", value: new JsonObject { ["inputs"] = new JsonObject { ["base"] = baseSha }, ["ref"] = branch }).ConfigureAwait(continueOnCapturedContext: false);
        await CliGitHub.SendJsonAsync(client: client, method: HttpMethod.Post, path: $"repos/{repository}/actions/workflows/azure.yml/dispatches", value: new JsonObject { ["inputs"] = new JsonObject { ["deploy"] = "false" }, ["ref"] = branch }).ConfigureAwait(continueOnCapturedContext: false);
        CliGitHub.Summarize(message: $"Proposed the dependency update as pull request #{number} on {branch}, and dispatched formatting and the shared validation graph for it.");
        return number;
    }
}
