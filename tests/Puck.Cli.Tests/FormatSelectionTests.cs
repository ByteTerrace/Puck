using System.Text.Json.Nodes;

using Puck.Cli.Format;

using Xunit;

namespace Puck.Cli.Tests;

public sealed class FormatSelectionTests : IDisposable {
    private readonly string m_root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-format-selection-{Guid.NewGuid():N}");

    public FormatSelectionTests() {
        Directory.CreateDirectory(path: m_root);
        File.WriteAllText(path: Path.Combine(path1: m_root, path2: "One.cs"), contents: "class One {}\n");
        File.WriteAllText(path: Path.Combine(path1: m_root, path2: "Two.cs"), contents: "class Two {}\n");
    }

    [Fact]
    public void ExplicitSelectionDoesNotIncludeSiblingsAndDeduplicatesTargets() {
        var manifest = WriteManifest(paths: ["One.cs", "One.cs"]);

        Assert.Equal(expected: [Path.Combine(path1: m_root, path2: "One.cs")], actual: FormatSelection.Read(root: m_root, manifest: manifest));
    }
    [InlineData("../One.cs")]
    [InlineData("./One.cs")]
    [InlineData("missing.cs")]
    [InlineData("files.json")]
    [Theory]
    public void AnInvalidSelectionFailsBeforeAnyRewriting(string path) {
        var manifest = WriteManifest(paths: ["One.cs", path]);

        Assert.Throws<ArgumentException>(testCode: () => FormatSelection.Read(root: m_root, manifest: manifest));
        Assert.Equal(expected: "class One {}\n", actual: File.ReadAllText(path: Path.Combine(path1: m_root, path2: "One.cs")));
    }
    [Fact]
    public void EmptySelectionIsEmptyRatherThanTheDefaultSourceTree() {
        Assert.Empty(collection: FormatSelection.Read(root: m_root, manifest: WriteManifest(paths: [])));
    }
    [Fact]
    public void AnEmptyDirectoryArgumentStillReturnsAUsageError() {
        Assert.Equal(expected: 2, actual: FormatCommand.Run(args: [""]));
    }
    [InlineData("src/Puck.Cli/Format/FormatCommand.cs", true)]
    [InlineData("tests/Puck.Cli.Tests/FormatSelectionTests.cs", true)]
    [InlineData("build/Toolchain.cs", true)]
    [InlineData("experimental/Old/Program.cs", false)]
    [InlineData("src/App/obj/Generated.cs", false)]
    [InlineData("src/App/Generated.g.cs", false)]
    [InlineData(".github/workflows/format.yml", false)]
    [Theory]
    public void CiSelectionExcludesGeneratedAndQuarantinedCode(string path, bool expected) {
        Assert.Equal(expected: expected, actual: FormatCiCommand.Admits(path: path));
    }
    [Fact]
    public void BothSemanticPhasesRejectAnUnbuiltOwningProject() {
        File.WriteAllText(path: Path.Combine(path1: m_root, path2: "Sample.csproj"), contents: "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var targets = new[] { Path.Combine(path1: m_root, path2: "One.cs") };

        Assert.Equal(expected: 1, actual: NamedArgsPhase.Run(rootArgument: m_root, whatIf: false, verify: true, targets: targets));
        Assert.Equal(expected: 1, actual: NullPatternPhase.Run(rootArgument: m_root, whatIf: false, verify: true, targets: targets));
    }

    private string WriteManifest(string[] paths) {
        var array = new JsonArray();

        foreach (var path in paths) { array.Add(item: JsonValue.Create(value: path)); }
        var manifest = Path.Combine(path1: m_root, path2: "files.json");

        File.WriteAllText(path: manifest, contents: array.ToJsonString());
        return manifest;
    }

    public void Dispose() {
        Directory.Delete(path: m_root, recursive: true);
        GC.SuppressFinalize(obj: this);
    }
}
