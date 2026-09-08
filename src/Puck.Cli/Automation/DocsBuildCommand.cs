
namespace Puck.Cli.Automation;

internal static class DocsBuildCommand {
    public static async Task<int> RunAsync(string[] args) {
        if (args is ["-h" or "--help"]) { Console.WriteLine(value: "puck docs build [output-directory]"); return 0; }
        if (args.Length > 1) { throw new ArgumentException(message: "Usage: puck docs build [output-directory]"); }
        var output = Path.GetFullPath(path: ((args.Length == 0) ? "artifacts/docs" : args[0]));
        var root = (RepositoryPaths.FindRoot() ?? throw new DirectoryNotFoundException(message: "Run within the Puck checkout."));

        foreach (var prefix in new[] { "reference", "_theme" }) {
            if (Directory.Exists(path: Path.Combine(path1: output, path2: prefix))) {
                throw new IOException(message: $"Use an output directory without existing {prefix} content.");
            }
        }
        await CliProcess.RunCheckedAsync(root: root, executable: "dotnet", arguments: ["tool", "restore", "--configfile", Path.Combine(root, "nuget.config")]);
        await CliProcess.RunCheckedAsync(root: root, executable: "dotnet", arguments: ["tool", "run", "docfx", "--", "docs/api/docfx.json", "--warningsAsErrors"]);
        if (!File.Exists(path: Path.Combine(root, "docs/api/_site/index.html"))) { throw new IOException(message: "DocFX omitted its entry point."); }
        CopyDirectory(source: Path.Combine(root, "docs/api/_site"), destination: Path.Combine(path1: output, path2: "reference"));
        File.Copy(sourceFileName: Path.Combine(root, "docs/site/index.html"), destFileName: Path.Combine(path1: output, path2: "reference/overview.html"));
        CopyDirectory(source: Path.Combine(root, "docs/site/_theme"), destination: Path.Combine(path1: output, path2: "_theme"));
        return 0;
    }

    private static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(path: destination);
        foreach (var file in Directory.EnumerateFiles(path: source, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var target = Path.Combine(path1: destination, path2: Path.GetRelativePath(path: file, relativeTo: source));

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
            File.Copy(destFileName: target, sourceFileName: file);
        }
    }
}
