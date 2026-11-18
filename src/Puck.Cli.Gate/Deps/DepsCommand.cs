using System.CommandLine;
using Puck.Confinement;

namespace Puck.Cli.Deps;

/// <summary><c>puck deps</c>: moves the repository's external dependencies to newer versions and proposes the result. The
/// <c>update</c> verb rewrites manifests and lock files for every ecosystem (<see cref="DependencyEcosystems"/>) and
/// reports what moved and what it held; <c>submit</c> turns the changed tree into the branch and pull request the weekly
/// workflow opens.</summary>
public static class DepsCommand {
    private const string Verb = "deps";

    private static async Task<int> UpdateAsync(IReadOnlyList<string> only, bool major, bool check, string? reportPath, CancellationToken cancellationToken) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) { return CliExit.Refused; }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(minutes: 2) };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(input: "Puck-Deps/1.0");
        var files = new DependencyFiles();
        var report = new DependencyReport();
        var context = new DependencyContext(CancellationToken: cancellationToken, Feed: new NuGetFeed(http: http), Files: files, Http: http, Major: major, Report: report, RepositoryRoot: repositoryRoot);

        try {
            foreach (var ecosystem in DependencyEcosystems.All.Where(predicate: ecosystem => ((only.Count == 0) || only.Contains(value: ecosystem)))) {
                switch (ecosystem) {
                    case DependencyEcosystems.NuGet: await NuGetDependencies.RunAsync(context: context).ConfigureAwait(continueOnCapturedContext: false); break;
                    case DependencyEcosystems.Npm: await NpmDependencies.RunAsync(context: context).ConfigureAwait(continueOnCapturedContext: false); break;
                    case DependencyEcosystems.Sdk: await ToolchainDependencies.RunSdkAsync(context: context).ConfigureAwait(continueOnCapturedContext: false); break;
                    case DependencyEcosystems.Tools: await ToolchainDependencies.RunToolsAsync(context: context).ConfigureAwait(continueOnCapturedContext: false); break;
                    case DependencyEcosystems.Actions: await ActionDependencies.RunAsync(context: context).ConfigureAwait(continueOnCapturedContext: false); break;
                }
            }
        } catch {
            files.Restore();
            throw;
        }
        if (check) { files.Restore(); }
        report.Write(dryRun: check, writer: Console.Out);
        if (reportPath is not null) { AtomicFile.WriteAllText(contents: report.ToMarkdown(dryRun: check), path: reportPath); }
        if (!check && report.Moves.Any(predicate: static move => (move.Ecosystem == DependencyEcosystems.Npm))) { Console.Out.WriteLine(value: $"{Verb}: npm's node_modules is untouched; run `npm ci` in each project before building it."); }
        return ((check && (report.Moves.Count > 0)) ? CliExit.Failed : CliExit.Success);
    }
    private static async Task<int> SubmitAsync(string reportPath, string branch, CancellationToken cancellationToken) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) { return CliExit.Refused; }
        if (!File.Exists(path: reportPath)) { return CliExit.Refuse(verb: $"{Verb} submit", what: reportPath, why: "no such report; run `puck deps update --output` first."); }

        await DependencySubmission.RunAsync(body: await File.ReadAllTextAsync(cancellationToken: cancellationToken, path: reportPath).ConfigureAwait(continueOnCapturedContext: false), branch: branch, cancellationToken: cancellationToken, repositoryRoot: repositoryRoot).ConfigureAwait(continueOnCapturedContext: false);
        return CliExit.Success;
    }
    private static Command CreateUpdate() {
        var only = new Option<string[]>(name: "--only") { AllowMultipleArgumentsPerToken = true, Description = $"Update only these ecosystems ({string.Join(separator: ", ", values: DependencyEcosystems.All)}); every one by default." };
        var major = new Option<bool>(name: "--major") { Description = "Also move past a declared range's major bound: bounded NuGet ranges, npm ranges, and actions and tools of another major." };
        var check = CliOptions.Check(description: "Compute every move and hold, write nothing, and exit 1 when anything would move.");
        var output = CliOptions.Output(description: "Also write the report as Markdown to this file.");

        only.AcceptOnlyFromAmong(values: [.. DependencyEcosystems.All]);
        var command = new Command(
            description: "Move NuGet, npm, SDK, tool and GitHub Actions dependencies to newer versions and report what moved.",
            name: "update"
        ) { only, major, check, output };

        command.Detail(detail: """
            One run covers every ecosystem the repository pins, each by its own rule, and names every move and every
            dependency it held back. The rule is the same everywhere: a dependency moves to the newest stable version its
            declared range admits; --major also moves a range's major bound.

            nuget     Every project's floating ranges, through the lock policy (`puck locks --update`): lock files list
                      the current closure and a locked restore judges the result. A bounded range ([1.*,2.0)) whose
                      bound excludes a newer stable package is held; --major moves its bound. An exact pin ([5.12.0])
                      is a deliberate hold and only reported. A plain version and a file-based app's `#:package`
                      directive (no lock file) move to the newest stable version of their major.
            npm       Every package-lock.json's package.json files (workspaces and overrides included): ^, ~ and exact
                      versions move to the newest stable version they admit, keeping their spelling, and the lock file
                      follows through `npm update --package-lock-only`. node_modules is untouched; run `npm ci`.
            sdk       global.json's .NET SDK pin to the newest stable SDK of its major (Microsoft's release index), the
                      MSBuild SDK packages beside it, and every Dockerfile's SDK image to match. A newer major is held.
            tools     The .NET tools of .config/dotnet-tools.json.
            actions   The `uses: owner/repo@<sha> # vX.Y.Z` pins of every workflow and composite action: the newest
                      stable vX.Y.Z tag of the action's repository replaces the SHA and the comment together.

            --check computes all of it and then puts every file back, exiting 1 when anything would move. A failed run
            leaves the tree as it found it.

            Exit codes: 0 done (or --check found nothing to move); 1 --check found a move; 2 refused or failed.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => UpdateAsync(
            cancellationToken: cancellationToken,
            check: parseResult.GetValue(option: check),
            major: parseResult.GetValue(option: major),
            only: (parseResult.GetValue(option: only) ?? []),
            reportPath: parseResult.GetValue(option: output)
        ));
        return command;
    }
    private static Command CreateSubmit() {
        var output = CliOptions.Output(description: "The Markdown report `puck deps update --output` wrote; it becomes the pull request's description.", required: true);
        var branch = new Option<string>(name: "--branch") { DefaultValueFactory = static _ => "deps/update", Description = "The branch this verb owns; every run replaces it." };
        var command = new Command(
            description: "Commit the tree `puck deps update` changed to a branch and open or refresh its pull request.",
            name: "submit"
        ) { output, branch };

        command.Detail(detail: """
            Runs in the weekly workflow after `puck deps update`, with the checkout at the default branch and GITHUB_*
            and GH_TOKEN in the environment. Commits the tracked changes as the Actions bot on --branch, force-pushes it
            (the branch is this verb's alone), opens its pull request or refreshes the open one, and dispatches the
            formatting check and the shared validation graph for it, since a push made with the workflow's own token
            starts no workflow. With no change in the tree it changes nothing.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => SubmitAsync(
            branch: parseResult.GetValue(option: branch)!,
            cancellationToken: cancellationToken,
            reportPath: parseResult.GetValue(option: output)!
        ));
        return command;
    }

    /// <summary>Creates <c>puck deps</c>.</summary>
    /// <returns>The command.</returns>
    public static Command Create() => new(
        description: "Update the repository's external dependencies and propose the result.",
        name: Verb
    ) { CreateUpdate(), CreateSubmit() };
}
