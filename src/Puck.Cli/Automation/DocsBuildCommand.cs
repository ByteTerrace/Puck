
using System.CommandLine;

namespace Puck.Cli.Automation;

internal static class DocsBuildCommand {
    public static Command Create() {
        var outputArgument = new Argument<string>(name: "output-directory") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => "artifacts/docs", Description = "Where the reference site and its theme are staged." };
        var build = new Command(description: "Generate the DocFX reference and stage it beside the site theme.", name: "build") { outputArgument };
        var command = new Command(description: "Build and stage the website documentation.", name: "docs") { build };

        build.SetAction(action: (parseResult, _) => RunAsync(output: Path.GetFullPath(path: parseResult.GetRequiredValue(argument: outputArgument))));
        return command;
    }

    private static async Task<int> RunAsync(string output) {
        var root = (RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException(message: "Run within the Puck checkout."));

        foreach (var prefix in new[] { "reference", "_theme" }) {
            if (Directory.Exists(path: Path.Combine(path1: output, path2: prefix))) {
                throw new IOException(message: $"Use an output directory without existing {prefix} content.");
            }
        }
        await CliProcess.RunCheckedAsync(root: root, executable: "dotnet", arguments: ["tool", "restore", "--configfile", Path.Combine(root, "nuget.config")]);
        await CliProcess.RunCheckedAsync(root: root, executable: "dotnet", arguments: ["tool", "run", "docfx", "--", "docs/api/docfx.json", "--warningsAsErrors"]);
        if (!File.Exists(path: Path.Combine(root, "docs/api/_site/index.html"))) { throw new IOException(message: "DocFX omitted its entry point."); }
        CliFiles.CopyDirectory(destination: Path.Combine(path1: output, path2: "reference"), source: Path.Combine(path1: root, path2: "docs/api/_site"));
        File.Copy(destFileName: Path.Combine(path1: output, path2: "reference/overview.html"), sourceFileName: Path.Combine(path1: root, path2: "docs/site/index.html"));
        CliFiles.CopyDirectory(destination: Path.Combine(path1: output, path2: "_theme"), source: Path.Combine(path1: root, path2: "docs/site/_theme"));
        return 0;
    }
}
