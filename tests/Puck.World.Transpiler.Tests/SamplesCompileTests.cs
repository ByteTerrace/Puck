using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Modules;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The shipped <c>Samples/*.puck</c> are the DSL's only worked examples, so each one must compile clean and
/// must exercise the sugar an author is meant to reach for.</summary>
public class SamplesCompileTests {
    private static readonly string[] SugarKeywords = [
        "rule ", "when ", "bind ", "schedule ", "transaction ", "decision ", "option ", "shape ", "placement ",
    ];

    private static string FindSamplesDirectory() {
        var dir = AppContext.BaseDirectory;

        while (dir is not null) {
            var candidate = Path.Combine(
                path1: dir,
                path2: "src",
                path3: "Puck.World.Transpiler",
                path4: "Samples"
            );

            if (Directory.Exists(path: candidate)) {
                return candidate;
            }
            dir = Path.GetDirectoryName(path: dir);
        }
        throw new DirectoryNotFoundException(message: "Could not locate src/Puck.World.Transpiler/Samples from the test runner.");
    }

    [MemberData(nameof(SampleFiles))]
    [Theory]
    public void SampleCompilesWithoutDiagnosticErrors(string fileName) {
        var path = Path.Combine(
            path1: FindSamplesDirectory(),
            path2: fileName
        );
        var source = File.ReadAllText(path: path);

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            diagnostics: diagnostics,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.NotNull(@object: parseResult.Value);

        ModuleResolver.ValidateImportGraph(
            diagnostics: diagnostics,
            rootDoc: parseResult.Value,
            rootPath: path,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path: path),
            diagnostics: diagnostics,
            embeddings: EmbeddingLock.TryLoad(rootSourcePath: path),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(
                filePath: path,
                sourceText: source
            )
        );
    }
    public static TheoryData<string> SampleFiles() {
        var data = new TheoryData<string>();

        foreach (var file in Directory.GetFiles(
            path: FindSamplesDirectory(),
            searchPattern: "*.puck"
        )) {
            data.Add(row: Path.GetFileName(path: file));
        }
        return data;
    }
    [Fact]
    public void TheComprehensiveSampleExercisesTheStatementSugar() {
        var source = File.ReadAllText(path: Path.Combine(
            path1: FindSamplesDirectory(),
            path2: "comprehensive.synthetic.world.puck"
        ));

        foreach (var keyword in SugarKeywords) {
            Assert.Contains(
                actualString: source,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: keyword
            );
        }
    }
}
