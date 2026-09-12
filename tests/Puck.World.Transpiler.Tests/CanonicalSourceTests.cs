using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Modules;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

// A world authored in the DSL commits both artifacts: the '.puck' source and the '.world.json' it compiles to.
// This is the gate that keeps them from drifting — the source is canonical, so the document must be exactly what
// it produces.
public class CanonicalSourceTests {
    /// <summary>Every authored source under the worlds directory: a <c>*.puck</c> with a <c>*.world.json</c> of the
    /// same stem beside it. Discovered rather than listed, so a world that moves or is added is covered without
    /// anyone remembering to name it here.</summary>
    /// <returns>The relative source paths.</returns>
    public static TheoryData<string> AuthoredWorlds() {
        var worlds = ShippedWorlds.FindDirectory();
        var data = new TheoryData<string>();

        foreach (var source in Directory.GetFiles(worlds, "*.puck", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)) {
            if (File.Exists(Path.ChangeExtension(source, ".world.json"))) {
                data.Add(Path.GetRelativePath(worlds, source).Replace('\\', '/'));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AuthoredWorlds))]
    public void TestTheCommittedSourceCompilesToTheCommittedDocument(string relativePath) {
        var worlds = ShippedWorlds.FindDirectory();
        var sourcePath = Path.Combine(worlds, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var documentPath = Path.ChangeExtension(sourcePath, ".world.json");
        var stem = Path.GetFileNameWithoutExtension(sourcePath);

        var diagnostics = new DiagnosticBag();
        var parsed = PuckParser.ParseDocumentWithDiagnostics(source: File.ReadAllText(sourcePath), diagnostics: diagnostics).Value;

        Assert.NotNull(parsed);

        ModuleResolver.ValidateImportGraph(diagnostics: diagnostics, rootDoc: parsed, rootPath: sourcePath);

        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
            basePath: worlds,
            diagnostics: diagnostics,
            document: parsed, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Select(d => $"{d.Code}: {d.Message}")));

        var produced = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(CanonicalJsonDocument.Serialize(lowered.Value!)));
        var committed = JsonNode.Parse(File.ReadAllText(documentPath));

        Assert.True(
            JsonNode.DeepEquals(produced, committed),
            $"{stem}.world.json is not what {stem}.puck compiles to — recompile the source rather than editing the document.");
    }
}
