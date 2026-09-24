using System.Xml.Linq;

namespace Puck.Cli.Format;

// The SDK owns file-app directives. Convert a copy to a disposable owning project so all existing
// formatter phases see its actual package references and implicit usings. Never execute the app.
internal static class FormatFileProject {
    // The disposable project is built here and nowhere else, so the semantic passes resolve against the
    // configuration this build just produced.
    private const string Configuration = "Release";
    // The item the injected target copies @(Compile) into once the reference set is resolved. The build adds its own
    // generated sources (assembly info and attributes) to @(Compile) just before compiling, so the copy is what a
    // design-time closure evaluation reports as the project's sources.
    private const string SourceItem = "PuckFormatClosureSource";

    // An MSBuild inline task declares its task base: it reads MSBuild's own task API.
    private static bool IsInlineTask(string source) =>
        source.Contains(
            comparisonType: StringComparison.Ordinal,
            value: "using Microsoft.Build.Utilities;"
        );

    internal static int Run(string file, HashSet<string> selected, bool check) {
        var repository = RepositoryPaths.RequireRoot();
        var scratch = Path.Combine(
            path1: repository,
            path2: ".tmp",
            path3: $"format-{Guid.NewGuid():N}"
        );
        var original = File.ReadAllText(path: file);
        var directives = original.Split('\n').Where(predicate: static line => (line.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "#!"
        ) || line.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "#:"
        ))).ToArray();

        try {
            string source;
            string project;

            if (directives.Length > 0) {
                Run(arguments: ["project", "convert", file, "--output", scratch]);
                source = Path.Combine(
                    path1: scratch,
                    path2: Path.GetFileName(path: file)
                );
                var converted = Directory.GetFiles(
                    path: scratch,
                    searchPattern: "*.csproj"
                ).Single();

                project = Path.Combine(
                    path1: scratch,
                    path2: (Path.GetFileName(path: file) + ".csproj")
                );
                File.Move(
                    destFileName: project,
                    sourceFileName: converted
                );
            } else {
                Directory.CreateDirectory(path: scratch);
                source = Path.Combine(
                    path1: scratch,
                    path2: Path.GetFileName(path: file)
                );
                File.WriteAllText(
                    contents: original,
                    path: source
                );
                project = Path.Combine(
                    path1: scratch,
                    path2: "Format.cs.csproj"
                );
                // An MSBuild inline task (a RoslynCodeTaskFactory <Code Source=...> under build/) is compiled by no
                // project: the factory compiles it against the running SDK's MSBuild assemblies, with no implicit usings
                // and none of this repository's analyzers, and its disposable project does the same.
                File.WriteAllText(
                    contents: (IsInlineTask(source: original)
                        ? "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable><RunAnalyzers>false</RunAnalyzers><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup><ItemGroup><Reference Include=\"$(MSBuildToolsPath)/Microsoft.Build.Framework.dll\" /><Reference Include=\"$(MSBuildToolsPath)/Microsoft.Build.Utilities.Core.dll\" /></ItemGroup></Project>"
                        : "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>"
                    ),
                    path: project
                );
            }
            var document = XDocument.Load(uri: project);
            var items = new XElement(name: "ItemGroup");

            // Conversion copies linked sources locally. Drop inherited links so each source appears
            // once in the project, regardless of which helpers a particular file app imports.
            items.Add(content: new XElement(
                name: "Compile",
                content: new XAttribute(
                    name: "Remove",
                    value: "@(Compile->HasMetadata('Link'))"
                )
            ));

            // The same linked source the repository gives file apps, copied locally so the semantic
            // phases see it without expanding the selected write set. An inline task is given nothing.
            const string Helper = "RepositoryPaths.cs";

            if (
                !IsInlineTask(source: original) &&
                (Path.GetFileName(path: source) != Helper)
            ) {
                File.Copy(
                    destFileName: Path.Combine(
                        path1: scratch,
                        path2: Helper
                    ),
                    overwrite: true,
                    sourceFileName: Path.Combine(
                        path1: repository,
                        path2: "build",
                        path3: Helper
                    )
                );
            }
            document.Root!.Add(content: items);
            document.Root.Add(content: new XElement(
                name: "PropertyGroup",
                content: new XElement(
                    content: "false",
                    name: "PublishAot"
                )
            ));
            document.Root.Add(content: new XElement(
                name: "Target",
                content: new object[] {
                    new XAttribute(
                        name: "Name",
                        value: "PuckFormatClosure"
                    ),
                    new XAttribute(
                        name: "AfterTargets",
                        value: "FindReferenceAssembliesForReferences"
                    ),
                    new XElement(
                        name: "ItemGroup",
                        content: new XElement(
                            name: SourceItem,
                            content: new XAttribute(
                                name: "Include",
                                value: "@(Compile)"
                            )
                        )
                    ),
                }
            ));
            document.Save(fileName: project);
            var report = Path.Combine(
                path1: scratch,
                path2: "closure.json"
            );

            // An explicit target makes MSBuild report items as the build left them rather than as evaluated, so this
            // one build is also the closure evaluation the semantic passes would otherwise run.
            Run(arguments: ["build", project, "-t:Build", "-c", Configuration, "-p:RestoreLockedMode=false", "-getItem:ReferencePathWithRefAssemblies", $"-getItem:{SourceItem}", "-getProperty:TargetPath", $"-getResultOutputFile:{report}"]);
            var closures = new CompileClosures();

            closures.Record(
                closure: CompileClosure.Read(
                    configuration: Configuration,
                    json: File.ReadAllText(path: report),
                    referenceItem: "ReferencePathWithRefAssemblies",
                    sourceItem: SourceItem
                ),
                configuration: Configuration,
                project: project
            );
            var result = FormatCommand.RunPhases(
                check: check,
                closures: closures,
                configuration: Configuration,
                root: scratch,
                selected: selected,
                targets: [source]
            );

            if (
                (result == 0) &&
                !check
            ) {
                var body = File.ReadAllText(path: source);
                var rewritten = ((directives.Length == 0)
                    ? body
                    : ((string.Join(
                        separator: "\n",
                        values: directives
                    ) + "\n") + body)
                );

                // Unchanged source already compiled in the build above. Rewritten source must still compile
                // before it can replace the original.
                if (!RewriteIo.ContentEquals(
                    a: original,
                    b: rewritten
                )) {
                    Run(arguments: ["build", project, "-c", Configuration, "--no-restore"]);
                    RewriteIo.WriteText(
                        file: file,
                        text: rewritten
                    );
                }
            }
            return result;
        } finally {
            if (Directory.Exists(path: scratch)) {
                Directory.Delete(
                    path: scratch,
                    recursive: true
                );
            }
        }
    }

    private static void Run(string[] arguments) {
        if (CliProcess.RunAsync(
            arguments: arguments,
            capture: false,
            fileName: "dotnet"
        ).GetAwaiter().GetResult().ExitCode != 0) {
            throw new InvalidOperationException(message: "The standalone formatter project failed; no result was copied back.");
        }
    }
}
