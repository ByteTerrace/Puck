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

        foreach (var source in Directory.GetFiles(
            path: worlds,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.puck"
        ).OrderBy(
            path => path,
            StringComparer.Ordinal
        )) {
            if (File.Exists(path: Path.ChangeExtension(
                extension: ".world.json",
                path: source
            ))) {
                data.Add(row: Path.GetRelativePath(
                    path: source,
                    relativeTo: worlds
                ).Replace(
                    newChar: '/',
                    oldChar: '\\'
                ));
            }
        }

        return data;
    }
    [MemberData(nameof(AuthoredWorlds))]
    [Theory]
    public void TestTheCommittedSourceCompilesToTheCommittedDocument(string relativePath) {
        var worlds = ShippedWorlds.FindDirectory();
        var sourcePath = Path.Combine(
            path1: worlds,
            path2: relativePath.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '/'
            )
        );
        var documentPath = Path.ChangeExtension(
            extension: ".world.json",
            path: sourcePath
        );
        var stem = Path.GetFileNameWithoutExtension(path: sourcePath);

        var diagnostics = new DiagnosticBag();
        var parsed = PuckParser.ParseDocumentWithDiagnostics(
            source: File.ReadAllText(path: sourcePath),
            diagnostics: diagnostics
        ).Value;

        Assert.NotNull(@object: parsed);

        ModuleResolver.ValidateImportGraph(
            diagnostics: diagnostics,
            rootDoc: parsed,
            rootPath: sourcePath
        );

        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
            basePath: worlds,
            diagnostics: diagnostics,
            document: parsed,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: string.Join(
                separator: "\n",
                values: diagnostics.Select(selector: d => $"{d.Code}: {d.Message}")
            )
        );

        var produced = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: lowered.Value!)));
        var committed = JsonNode.Parse(File.ReadAllText(path: documentPath));

        Assert.True(
            condition: JsonNode.DeepEquals(
                node1: produced,
                node2: committed
            ),
            userMessage: $"{stem}.world.json is not what {stem}.puck compiles to — recompile the source rather than editing the document."
        );
    }
}
