using System.CommandLine;

namespace Puck.Cli.Docs;

/// <summary><c>puck docs build</c> — generates the DocFX API reference and stages it, the site overview page, and the
/// site theme under one output directory for the website upload.</summary>
internal static class DocsBuildCommand {
    private static async Task<int> RunAsync(string root, string output) {
        foreach (var prefix in new[] { "reference", "_theme" }) {
            if (Directory.Exists(path: Path.Combine(
                path1: output,
                path2: prefix
            ))) {
                return CliExit.Refuse(
                    verb: "docs build",
                    what: CliPaths.ToDisplay(fullPath: output),
                    why: $"already holds {prefix}/ content; name an output directory without it."
                );
            }
        }

        await CliProcess.RunCheckedAsync(
            workingDirectory: root,
            fileName: "dotnet",
            arguments: ["tool", "restore", "--configfile", Path.Combine(
                    path1: root,
                    path2: "nuget.config"
                )]
        );
        await CliProcess.RunCheckedAsync(
            workingDirectory: root,
            fileName: "dotnet",
            arguments: ["tool", "run", "docfx", "--", "docs/api/docfx.json", "--warningsAsErrors"]
        );

        if (!File.Exists(path: Path.Combine(
            path1: root,
            path2: "docs/api/_site/index.html"
        ))) {
            throw new IOException(message: "DocFX omitted its entry point.");
        }

        CliFiles.CopyDirectory(
            destination: Path.Combine(
                path1: output,
                path2: "reference"
            ),
            source: Path.Combine(
                path1: root,
                path2: "docs/api/_site"
            )
        );
        File.Copy(
            destFileName: Path.Combine(
                path1: output,
                path2: "reference/overview.html"
            ),
            sourceFileName: Path.Combine(
                path1: root,
                path2: "docs/site/index.html"
            )
        );
        CliFiles.CopyDirectory(
            destination: Path.Combine(
                path1: output,
                path2: "_theme"
            ),
            source: Path.Combine(
                path1: root,
                path2: "docs/site/_theme"
            )
        );
        Console.WriteLine(value: $"docs build: staged the reference site in {CliPaths.ToDisplay(fullPath: output)}.");

        return CliExit.Success;
    }

    public static Command Create() {
        var outputOption = CliOptions.Output(description: "The directory the reference site and its theme are staged in, resolved against the working directory (default <repo>/artifacts/docs).");
        var command = new Command(
            description: "Generate the DocFX reference and stage it beside the site theme.",
            name: "build"
        ) { outputOption };

        command.SetAction(action: (parseResult, _) => {
            if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) {
                return Task.FromResult(result: CliExit.Refused);
            }

            return RunAsync(
                output: Path.GetFullPath(path: (parseResult.GetValue(option: outputOption) ?? Path.Combine(
                    path1: root,
                    path2: "artifacts/docs"
                ))),
                root: root
            );
        });

        return command;
    }
}
