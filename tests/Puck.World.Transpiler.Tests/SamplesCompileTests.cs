using Puck.Transpiler.Diagnostics;
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
            var candidate = Path.Combine(dir, "src", "Puck.World.Transpiler", "Samples");
            if (Directory.Exists(candidate)) {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not locate src/Puck.World.Transpiler/Samples from the test runner.");
    }

    public static TheoryData<string> SampleFiles() {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(FindSamplesDirectory(), "*.puck")) {
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void SampleCompilesWithoutDiagnosticErrors(string fileName) {
        var path = Path.Combine(FindSamplesDirectory(), fileName);
        var source = File.ReadAllText(path);

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source, diagnostics: diagnostics);
        Assert.NotNull(parseResult.Value);

        ModuleResolver.ValidateImportGraph(diagnostics: diagnostics, rootDoc: parseResult.Value, rootPath: path);

        WorldDocumentEmitter.LowerWithDiagnostics(
            parseResult.Value,
            basePath: Path.GetDirectoryName(path),
            diagnostics: diagnostics
        , cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(sourceText: source, filePath: path));
    }

    [Fact]
    public void TheComprehensiveSampleExercisesTheStatementSugar() {
        var source = File.ReadAllText(Path.Combine(FindSamplesDirectory(), "comprehensive.synthetic.world.puck"));

        foreach (var keyword in SugarKeywords) {
            Assert.Contains(keyword, source, StringComparison.Ordinal);
        }
    }
}
