using System.Xml.Linq;

namespace Puck.Cli.Format;

// The SDK owns file-app directives. Convert a copy to a disposable owning project so all existing
// formatter phases see its actual package references and implicit usings. Never execute the app.
internal static class FormatFileProject {
    internal static int Run(string file, HashSet<string> selected, bool whatIf, bool verify) {
        var repository = (RepositoryPaths.FindRoot() ?? throw new InvalidOperationException(message: "Standalone formatting requires a Puck checkout."));
        var scratch = Path.Combine(path1: repository, path2: ".tmp", path3: $"format-{Guid.NewGuid():N}");
        var original = File.ReadAllText(path: file);
        var directives = original.Split('\n').Where(predicate: static line => (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "#!") || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "#:"))).ToArray();

        try {
            string source;
            string project;

            if (directives.Length > 0) {
                Run(arguments: ["project", "convert", file, "--output", scratch]);
                source = Path.Combine(path1: scratch, path2: Path.GetFileName(path: file));
                var converted = Directory.GetFiles(path: scratch, searchPattern: "*.csproj").Single();

                project = Path.Combine(path1: scratch, path2: (Path.GetFileName(path: file) + ".csproj"));
                File.Move(destFileName: project, sourceFileName: converted);
            } else {
                Directory.CreateDirectory(path: scratch);
                source = Path.Combine(path1: scratch, path2: Path.GetFileName(path: file));
                File.WriteAllText(contents: original, path: source);
                project = Path.Combine(path1: scratch, path2: "Format.cs.csproj");
                File.WriteAllText(contents: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>", path: project);
            }
            var document = XDocument.Load(uri: project);
            var items = new XElement(name: "ItemGroup");

            // Conversion copies linked sources locally. Drop inherited links so each source appears
            // once in the project, regardless of which helpers a particular file app imports.
            items.Add(content: new XElement(name: "Compile", content: new XAttribute(name: "Remove", value: "@(Compile->HasMetadata('Link'))")));

            // The same linked source the repository gives file apps, copied locally so the semantic
            // phases see it without expanding the selected write set.
            const string Helper = "RepositoryPaths.cs";

            if (Path.GetFileName(path: source) != Helper) {
                File.Copy(destFileName: Path.Combine(path1: scratch, path2: Helper), overwrite: true, sourceFileName: Path.Combine(path1: repository, path2: "build", path3: Helper));
            }
            document.Root!.Add(content: items);
            document.Root.Add(content: new XElement(name: "PropertyGroup", content: new XElement(content: "false", name: "PublishAot")));
            document.Save(fileName: project);
            Run(arguments: ["build", project, "-c", "Release", "-p:RestoreLockedMode=false"]);
            var result = FormatCommand.RunPhases(root: scratch, selected: selected, targets: [source], verify: verify, whatIf: whatIf);

            if ((result == 0) && !whatIf && !verify) {
                // Semantic rewrites must still compile before their result can replace the original.
                Run(arguments: ["build", project, "-c", "Release", "--no-restore"]);
                var body = File.ReadAllText(path: source);
                var rewritten = ((directives.Length == 0) ? body : ((string.Join(separator: "\n", values: directives) + "\n") + body));

                if (!RewriteIo.ContentEquals(a: original, b: rewritten)) { RewriteIo.WriteText(file: file, text: rewritten); }
            }
            return result;
        } finally {
            if (Directory.Exists(path: scratch)) { Directory.Delete(path: scratch, recursive: true); }
        }
    }

    private static void Run(string[] arguments) {
        if (CliProcess.RunStreamed(fileName: "dotnet", arguments: arguments) != 0) {
            throw new InvalidOperationException(message: "The standalone formatter project failed; no result was copied back.");
        }
    }
}
