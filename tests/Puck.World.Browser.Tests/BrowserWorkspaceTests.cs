using System.Text.Json.Nodes;
using System.Text;
using Puck.Testing;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>The browser engine's authoring surface over a workspace rooted in a temporary directory: the same
/// <see cref="BrowserWorkspace"/> and <see cref="BrowserLanguageServer"/> the AppBundle runs over its in-memory
/// <c>/worlds</c> mount.</summary>
public sealed class BrowserWorkspaceTests : IDisposable {
    private readonly TemporaryDirectory m_directory = new(prefix: "puck-browser-workspace-");
    private readonly BrowserWorkspace m_workspace;

    public BrowserWorkspaceTests() {
        m_workspace = new BrowserWorkspace(root: Path.Combine(
            path1: m_directory.RootPath,
            path2: "worlds"
        ));
    }

    public void Dispose() => m_directory.Dispose();

    [InlineData("../escape.puck")]
    [InlineData("/worlds/absolute.puck")]
    [InlineData("games\\back.puck")]
    [InlineData("games//empty.puck")]
    [InlineData("C:/drive.puck")]
    [Theory]
    public void APathOutsideTheWorkspaceIsRefused(string path) {
        Assert.NotNull(@object: m_workspace.Write(path: path, text: ""));
        Assert.NotNull(@object: m_workspace.Mount(files: new Dictionary<string, string> { [path] = "" }));
    }
    [Fact]
    public void AnUnsavedCompilationCannotClaimTheDiskSourcesProvenance() {
        const string Saved = "schema: \"puck.world.definition.v1\"\nhost { width: 320 }";
        const string Unsaved = "schema: \"puck.world.definition.v1\"\nhost { width: 640 }";
        var path = m_directory.WriteText(name: "root.puck", text: Saved);
        var compiled = WorldCompiledSource.From(compilation: WorldCompiler.Compile(source: Unsaved, sourcePath: path, cancellationToken: TestContext.Current.CancellationToken));
        var source = new PuckDocumentComposer(cache: new WorldCompileCache(), sourceCompilation: compiled);
        var work = new WorldBootWork();

        using (WorldBootWork.Attribute(work: work)) {
            Assert.False(condition: source.RecordInputs(resolvedName: path, content: compiled.Document!));
        }
        Assert.Equal(expected: 0L, actual: work.Read(kind: WorldBootWork.Compiles));

        File.WriteAllText(contents: Unsaved, path: path);
        var saved = WorldCompiledSource.From(compilation: WorldCompiler.CompileFile(path: path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(condition: new PuckDocumentComposer(cache: new WorldCompileCache(), sourceCompilation: saved)
            .RecordInputs(resolvedName: path, content: saved.Document!));
    }
    [Fact]
    public void SuppliedRootProvenanceMustMatchTheEmittedDocumentBytes() {
        var path = m_directory.WriteText(name: "root.puck", text: "schema: \"puck.world.definition.v1\"\nhost { width: 320 }");
        var compiled = WorldCompiledSource.From(compilation: WorldCompiler.CompileFile(path: path, cancellationToken: TestContext.Current.CancellationToken));
        var source = new PuckDocumentComposer(cache: new WorldCompileCache(), sourceCompilation: compiled);
        var different = JsonNode.Parse(utf8Json: compiled.Document!)!.AsObject();

        different["host"]!["width"] = 640;
        Assert.False(condition: source.RecordInputs(resolvedName: path, content: Encoding.UTF8.GetBytes(s: different.ToJsonString())));
        Assert.True(condition: source.RecordInputs(resolvedName: path, content: compiled.Document!));
    }
}
