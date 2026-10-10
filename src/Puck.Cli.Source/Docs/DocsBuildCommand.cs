using System.CommandLine;
using System.IO.Compression;

namespace Puck.Cli.Docs;

/// <summary><c>puck docs build</c> — generates the DocFX API reference and stages it, the site overview page, and the
/// site theme under one output directory for the website upload. Given <c>--site</c>, it stages a reference DocFX
/// already generated from this checkout, as a site directory or a zip archive of one, which is how the application
/// bundle takes the documentation producer's site.</summary>
public static class DocsBuildCommand {
    /// <summary>The <c>dotnet</c> arguments that generate the reference into <c>docs/api/_site</c>: every DocFX warning
    /// fails the build. The documentation workflow, which has no CLI because it starts before the CLI is compiled, runs
    /// exactly this command.</summary>
    public static IReadOnlyList<string> DocfxArguments { get; } = ["tool", "run", "docfx", "--", "docs/api/docfx.json", "--warningsAsErrors"];

    // The zip archive a site is given as: the path itself when it is a file, or the one file of a directory that holds
    // nothing else, which is how a workflow artifact downloaded without extraction arrives. Extracting it here rather than
    // in the download keeps some twelve thousand small files out of the download action's streaming extraction, which
    // fails on the Windows runner.
    private static string? Archive(string site) {
        if (File.Exists(path: site)) { return site; }
        if (!Directory.Exists(path: site) || (Directory.EnumerateDirectories(path: site).Any())) { return null; }

        var files = Directory.GetFiles(path: site);

        return ((files.Length == 1) ? files[0] : null);
    }
    private static async Task<int> RunAsync(string root, string output, string? site) {
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
        if (site is null) {
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
                arguments: DocfxArguments
            );
            site = Path.Combine(
                path1: root,
                path2: "docs/api/_site"
            );
        }
        var reference = Path.Combine(
            path1: output,
            path2: "reference"
        );

        if (File.Exists(path: Path.Combine(
            path1: site,
            path2: "index.html"
        ))) {
            CliFiles.CopyDirectory(
                destination: reference,
                source: site
            );
        } else if (Archive(site: site) is { } archive) {
            try {
                ZipFile.ExtractToDirectory(
                    destinationDirectoryName: reference,
                    sourceArchiveFileName: archive
                );
            } catch (InvalidDataException exception) {
                return CliExit.Refuse(
                    verb: "docs build",
                    what: CliPaths.ToDisplay(fullPath: archive),
                    why: $"is not a zip archive of a DocFX site: {exception.Message}"
                );
            }
        }
        if (!File.Exists(path: Path.Combine(
            path1: reference,
            path2: "index.html"
        ))) {
            return CliExit.Refuse(
                verb: "docs build",
                what: CliPaths.ToDisplay(fullPath: site),
                why: "holds no DocFX entry point (index.html)."
            );
        }
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
        var siteOption = new Option<string?>(name: "--site") { Description = "A DocFX site already generated from this checkout, staged instead of running DocFX: its directory, a zip archive of it, or a directory holding only that archive (a workflow artifact downloaded without extraction). Resolved against the working directory." };
        var command = new Command(
            description: "Generate the DocFX reference and stage it beside the site theme.",
            name: "build"
        ) { outputOption, siteOption };

        command.SetAction(action: (parseResult, _) => {
            if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) {
                return Task.FromResult(result: CliExit.Refused);
            }

            return RunAsync(
                output: Path.GetFullPath(path: (parseResult.GetValue(option: outputOption) ?? Path.Combine(
                    path1: root,
                    path2: "artifacts/docs"
                ))),
                root: root,
                site: ((parseResult.GetValue(option: siteOption) is { Length: > 0 } site) ? Path.GetFullPath(path: site) : null)
            );
        });

        return command;
    }
}
