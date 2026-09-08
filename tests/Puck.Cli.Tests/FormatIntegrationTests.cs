using System.Text.Json.Nodes;

using Puck.Cli.Format;

using Xunit;

namespace Puck.Cli.Tests;

public sealed class FormatIntegrationTests {
    [Fact]
    public void SourceReplacementPreservesAnExistingReadersMappedView() {
        var root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-format-mapped-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: root);
        var path = Path.Combine(path1: root, path2: "Source.cs");

        try {
            File.WriteAllText(contents: "old source", path: path);
            using var stream = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: path, share: FileShare.ReadWrite | FileShare.Delete);
            using var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(stream, null, 0,
                System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            using var view = mapping.CreateViewStream(0, stream.Length, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);

            RewriteIo.WriteText(path, "new source\n");
            Assert.Equal("new source\n", File.ReadAllText(path: path));
            using var reader = new StreamReader(stream: view);

            Assert.Equal("old source", reader.ReadToEnd());
            Assert.Single(collection: Directory.EnumerateFiles(path: root));
        } finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void FormattingASelectedFileLeavesItsSiblingAndLinkedSourceUntouched() {
        var root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-format-integration-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: root);
        try {
            var project = Path.Combine(path1: root, path2: "Sample.csproj");
            var selected = Path.Combine(path1: root, path2: "One.cs");
            var sibling = Path.Combine(path1: root, path2: "Two.cs");
            var linkedDirectory = Path.Combine(path1: root, path2: "linked");

            Directory.CreateDirectory(path: linkedDirectory);
            var linked = Path.Combine(path1: linkedDirectory, path2: "Three.cs");
            const string Other = "internal class Other{ public int Read( ) {return 3;} }\n";
            const string Linked = "internal class Linked{ public int Read( ) {return 4;} }\n";

            File.WriteAllText(contents: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Compile Remove=\"linked/*.cs\"/><Compile Include=\"linked/Three.cs\" Link=\"Three.cs\"/></ItemGroup></Project>", path: project);
            File.WriteAllText(contents: "internal class One{ public void Read( ) {Console.WriteLine(\"hello\");} }\n", path: selected);
            File.WriteAllText(contents: Other, path: sibling);
            File.WriteAllText(contents: Linked, path: linked);
            var manifest = Path.Combine(path1: root, path2: "files.json");

            File.WriteAllText(path: manifest, contents: new JsonArray("One.cs").ToJsonString());
            Assert.Equal(expected: 0, actual: CliProcess.RunStreamed(fileName: "dotnet", arguments: ["build", project, "-c", "Release"]));
            Assert.Equal(expected: 0, actual: FormatSelection.Run(root: root, manifest: manifest, selected: FormatPasses.DefaultSelection(), whatIf: false, verify: false));
            Assert.Contains(expectedSubstring: "value:", actualString: File.ReadAllText(path: selected), comparisonType: StringComparison.Ordinal);
            Assert.Equal(expected: Other, actual: File.ReadAllText(path: sibling));
            Assert.Equal(expected: Linked, actual: File.ReadAllText(path: linked));
            Assert.Equal(expected: 0, actual: FormatSelection.Run(root: root, manifest: manifest, selected: FormatPasses.DefaultSelection(), whatIf: false, verify: true));
        } finally { Directory.Delete(path: root, recursive: true); }
    }
    [Fact]
    public void AStandaloneAppIsFormattedAndCompiledWithoutExecutingItsBody() {
        var root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-format-file-app-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path: root);
        try {
            var source = Path.Combine(path1: root, path2: "Probe.cs");
            const string Directives = "#!/usr/bin/env dotnet\n#:property PublishAot=false\n";

            File.WriteAllText(contents: (Directives + "throw new System.InvalidOperationException(\"The formatter must never execute me.\");\n"), path: source);
            Assert.Equal(expected: 0, actual: FormatFileProject.Run(file: source, selected: FormatPasses.DefaultSelection(), whatIf: false, verify: false));
            var result = File.ReadAllText(path: source);

            Assert.StartsWith(actualString: result, comparisonType: StringComparison.Ordinal, expectedStartString: Directives);
            Assert.Contains(actualString: result, comparisonType: StringComparison.Ordinal, expectedSubstring: "message:");
            Assert.Equal(expected: 0, actual: FormatFileProject.Run(file: source, selected: FormatPasses.DefaultSelection(), whatIf: false, verify: true));
            Assert.Equal(expected: result, actual: File.ReadAllText(path: source));
        } finally { Directory.Delete(path: root, recursive: true); }
    }
}
