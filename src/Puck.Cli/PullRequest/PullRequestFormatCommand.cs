using System.CommandLine;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Puck.Cli.Format;

namespace Puck.Cli.PullRequest;

// `puck pull-request format`: runs only in a clean disposable checkout. The artifact contains data, never an executable
// patch consumer: a separate trusted workflow (`puck pull-request submit-format`) validates the paths and commits with
// an expected-head guard.
internal static class PullRequestFormatCommand {
    // KEEP IN SYNC with the solution build in .github/workflows/format.yml: the semantic passes bind
    // against this configuration's compile closure, and a configuration nothing built resolves nothing.
    private const string Configuration = CliOptions.DefaultConfiguration;
    private const string Verb = "pull-request format";

    internal static Command Create() {
        var baseArgument = new Argument<string>(name: "base-sha") { Description = "The full 40-hex-digit commit the pull request is measured from." };
        var headArgument = new Argument<string>(name: "head-sha") { Description = "The full 40-hex-digit commit the checkout must already be at." };
        var output = CliOptions.Output(
            description: "A fresh directory for files.json, format.patch, and format.json.",
            required: true
        );
        var command = new Command(
            description: "Format a pull request's changed sources in a clean checkout and write the result as an artifact.",
            name: "format"
        ) { baseArgument, headArgument, output };

        command.Detail(detail: """
            Formats every C# and .puck source the pull request adds or changes, as
            `puck format` would, then formats again under --check to prove the result is a
            fixed point. It writes the changed paths (files.json), a textual patch
            (format.patch), and the formatted contents (format.json) for
            `puck pull-request submit-format` to apply, and sets changed=true|false in
            GITHUB_OUTPUT. It refuses a checkout that is not at <head-sha> or has tracked
            changes, and a formatter that touches a file outside the selection.

            Exit codes: 0 the artifact was written; 2 a refusal or a formatting failure.
            """);
        command.Validators.Add(item: result => {
            foreach (var argument in ((Argument<string>[])[baseArgument, headArgument])) {
                var sha = result.GetValue(argument: argument);

                if (
                    (sha is null) ||
                    !Regex.IsMatch(
                    input: sha,
                    pattern: "\\A[0-9a-f]{40}\\z"
                )
                ) {
                    result.AddError(errorMessage: $"<{argument.Name}> must be a full 40-hex-digit commit sha.");

                    return;
                }
            }
        });
        command.SetAction(action: (parseResult, cancellationToken) => RunAsync(
            baseSha: parseResult.GetRequiredValue(argument: baseArgument),
            cancellationToken: cancellationToken,
            headSha: parseResult.GetRequiredValue(argument: headArgument),
            output: parseResult.GetRequiredValue(option: output)
        ));
        return command;
    }
    internal static async Task<int> RunAsync(string baseSha, string headSha, string output, CancellationToken cancellationToken = default) {
        try {
            var root = RepositoryPaths.RequireRoot();

            if (
                ((await CliGit.CaptureAsync(
                arguments: ["rev-parse", "HEAD"],
                cancellationToken: cancellationToken,
                repository: root
            )).Trim() != headSha) ||
                !string.IsNullOrWhiteSpace(value: await CliGit.CaptureAsync(
                arguments: ["status", "--porcelain", "--untracked-files=no"],
                cancellationToken: cancellationToken,
                repository: root
            ))
            ) {
                throw new InvalidOperationException(message: "Pull-request formatting requires the requested HEAD and a clean tracked working tree.");
            }
            output = Path.GetFullPath(path: output);
            if (Directory.Exists(path: output)) { throw new IOException(message: "Use a fresh formatting output directory."); }
            Directory.CreateDirectory(path: output);
            var changed = (await CliGit.CaptureAsync(
                arguments: ["diff", "--name-only", "-z", "--diff-filter=ACMR", $"{baseSha}...{headSha}", "--"],
                cancellationToken: cancellationToken,
                repository: root
            ))
                .Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: '\0'
            ).Where(predicate: FormatSources.Admits).Order(comparer: StringComparer.Ordinal).ToArray();
            var list = new JsonArray();

            foreach (var path in changed) {
                // SDK-generated source is not a formatting input even when it happens to be tracked.
                if (File.ReadLines(path: Path.Combine(
                    path1: root,
                    path2: path
                )).Take(count: 5).Any(predicate: static line => line.Contains(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: "<auto-generated"
                ))) {
                    Console.Error.WriteLine(value: $"puck {Verb}: generated source excluded: {path}");
                    continue;
                }
                list.Add(item: JsonValue.Create(value: path));
            }
            var manifest = Path.Combine(
                path1: output,
                path2: "files.json"
            );

            File.WriteAllText(
                path: manifest,
                contents: list.ToJsonString()
            );
            var targets = FormatSelection.Read(
                manifest: manifest,
                root: root
            );
            var before = targets.ToDictionary(
                keySelector: static path => path,
                elementSelector: static path => File.ReadAllBytes(path: path),
                comparer: StringComparer.Ordinal
            );
            var selected = FormatPasses.DefaultSelection();

            if (
                (FormatSelection.Run(
                check: false,
                configuration: Configuration,
                root: root,
                selected: selected,
                targets: targets
            ) != CliExit.Success) ||
                (FormatSelection.Run(
                check: true,
                configuration: Configuration,
                root: root,
                selected: selected,
                targets: targets
            ) != CliExit.Success)
            ) {
                throw new InvalidOperationException(message: "Formatting failed or did not converge. No commit artifact was produced.");
            }
            var modifications = new JsonArray();

            foreach (var target in targets) {
                var after = File.ReadAllBytes(path: target);

                if (!before[target].AsSpan().SequenceEqual(other: after)) {
                    modifications.Add(item: new JsonObject {
                        ["path"] = CliPaths.ToDisplay(
                        fullPath: target,
                        relativeTo: root
                    ),
                        ["contents"] = Convert.ToBase64String(inArray: after),
                    });
                }
            }
            var touched = (await CliGit.CaptureAsync(
                arguments: ["diff", "HEAD", "--name-only", "-z", "--"],
                cancellationToken: cancellationToken,
                repository: root
            ))
                .Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: '\0'
            );
            var allowed = targets.Select(selector: path => CliPaths.ToDisplay(
                fullPath: path,
                relativeTo: root
            )).ToHashSet(comparer: StringComparer.Ordinal);

            if (touched.Any(predicate: path => !allowed.Contains(item: path))) {
                throw new InvalidOperationException(message: "A formatter changed a file outside the PR selection. No commit artifact was produced.");
            }
            // A textual patch remains usable for fork PRs, where the repository token cannot push. Every tracked change
            // was just proven to lie inside the selection, so the whole diff is the selection's diff, and no pathspec
            // carries a large PR's paths onto git's command line.
            File.WriteAllText(
                path: Path.Combine(
                    path1: output,
                    path2: "format.patch"
                ),
                contents: await CliGit.CaptureAsync(
                    arguments: ["diff", "--binary", "--no-ext-diff", "HEAD", "--"],
                    cancellationToken: cancellationToken,
                    repository: root
                )
            );
            var report = new JsonObject { ["base"] = baseSha, ["head"] = headSha, ["files"] = modifications };

            File.WriteAllText(
                path: Path.Combine(
                    path1: output,
                    path2: "format.json"
                ),
                contents: report.ToJsonString()
            );
            if (Environment.GetEnvironmentVariable(variable: "GITHUB_OUTPUT") is { Length: > 0 } githubOutput) {
                File.AppendAllText(
                    path: githubOutput,
                    contents: $"changed={((modifications.Count > 0)
                    ? "true"
                    : "false")}\n"
                );
            }
            Console.WriteLine(value: $"puck {Verb}: {targets.Length} selected file(s), {modifications.Count} formatted file(s).");
            return CliExit.Success;
        } catch (Exception error) when ((error is not OperationCanceledException)) {
            return CliExit.Refuse(
                verb: Verb,
                what: headSha,
                why: error.Message
            );
        }
    }
}
