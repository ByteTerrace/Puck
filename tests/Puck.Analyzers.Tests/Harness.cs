using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Puck.Analyzers.Tests;

/// <summary>A named source file in a harness compilation.</summary>
/// <param name="Name">The file name; a <c>.g.cs</c> suffix is what makes Roslyn treat the tree as generated.</param>
/// <param name="Text">The C# source.</param>
internal readonly record struct SourceFile(string Name, string Text);
/// <summary>Everything one analyzer run produced, split so a case can assert on compiler and analyzer output separately.</summary>
/// <param name="Analyzer">Diagnostics the analyzer reported, including any AD0001 analyzer crash.</param>
/// <param name="Compiler">Diagnostics the compiler itself reported for the same compilation.</param>
internal sealed record AnalysisResult(ImmutableArray<Diagnostic> Analyzer, ImmutableArray<Diagnostic> Compiler) {
    /// <summary>The compiler errors, formatted for an assertion message.</summary>
    public string CompilerErrorText =>
        string.Join(
            separator: "; ",
            values: Compiler.Where(predicate: diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)).Select(selector: diagnostic => diagnostic.ToString())
        );
    /// <summary>Whether the compiler accepted the source outright — the precondition for calling a silent analyzer a bypass.</summary>
    public bool CompilesCleanly =>
        !Compiler.Any(predicate: diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));
    /// <summary>The analyzer diagnostic ids, sorted, so an assertion reads as a set rather than an ordering.</summary>
    public string[] Ids =>
        Analyzer.Select(selector: diagnostic => diagnostic.Id).OrderBy(
            keySelector: id => id,
            comparer: StringComparer.Ordinal
        ).ToArray();

    /// <summary>The one analyzer diagnostic carrying <paramref name="id"/>; throws when there is not exactly one.</summary>
    public Diagnostic Single(string id) =>
        WithId(id: id).Single();
    /// <summary>The analyzer diagnostics carrying <paramref name="id"/>.</summary>
    public ImmutableArray<Diagnostic> WithId(string id) =>
        Analyzer.Where(predicate: diagnostic => string.Equals(
            a: diagnostic.Id,
            b: id,
            comparisonType: StringComparison.Ordinal
        )).ToImmutableArray();
}
/// <summary>
/// Compiles C# source strings in memory, runs one analyzer over them, and returns both the analyzer's and the
/// compiler's diagnostics.
/// </summary>
/// <remarks>
/// The suite drives each analyzer directly rather than through
/// <c>Microsoft.CodeAnalysis.CSharp.Analyzer.Testing</c>: that package is not in this repository's package set, and a
/// case here needs control over the assembly name and the compilation options that a markup-driven test framework
/// does not expose.
/// </remarks>
internal static class Harness {
    /// <summary>The assembly name harness compilations use unless a case needs a specific one.</summary>
    public const string DefaultAssemblyName = "Subject.Assembly";

    private static readonly ImmutableArray<MetadataReference> References = CreateReferences();

    /// <summary>The metadata references every harness compilation gets: the running runtime's own reference set.</summary>
    public static ImmutableArray<MetadataReference> RuntimeReferences =>
        References;

    private static ImmutableArray<MetadataReference> CreateReferences() {
        var trusted = ((AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty);

        return trusted
            .Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: System.IO.Path.PathSeparator
        )
            .Where(predicate: path => path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".dll"
        ))
            .Select(selector: path => ((MetadataReference)MetadataReference.CreateFromFile(path: path)))
            .ToImmutableArray();
    }
    private static SyntaxTree Parse(SourceFile file) =>
        CSharpSyntaxTree.ParseText(
            text: SourceText.From(
                text: file.Text,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            ),
            options: new CSharpParseOptions(languageVersion: LanguageVersion.Latest),
            path: file.Name
        );

    /// <summary>Runs <paramref name="analyzer"/> over <paramref name="compilation"/> with concurrent execution enabled.</summary>
    /// <param name="compilation">The compilation to analyze.</param>
    /// <param name="analyzer">The analyzer to run.</param>
    /// <returns>The analyzer's and the compiler's diagnostics.</returns>
    public static AnalysisResult Analyze(CSharpCompilation compilation, DiagnosticAnalyzer analyzer) {
        var options = new CompilationWithAnalyzersOptions(
            options: new AnalyzerOptions(additionalFiles: ImmutableArray<AdditionalText>.Empty),
            onAnalyzerException: null,
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false
        );

        var withAnalyzers = compilation.WithAnalyzers(
            analyzers: ImmutableArray.Create(item: analyzer),
            analysisOptions: options
        );

        var analyzerDiagnostics = withAnalyzers
            .GetAnalyzerDiagnosticsAsync(cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return new AnalysisResult(
            Analyzer: analyzerDiagnostics,
            Compiler: compilation.GetDiagnostics(cancellationToken: CancellationToken.None)
        );
    }
    /// <summary>Builds a compilation from <paramref name="sources"/> plus the global usings the SDK would supply.</summary>
    /// <param name="assemblyName">The compilation's assembly name.</param>
    /// <param name="sources">The source files to compile.</param>
    /// <returns>The compilation.</returns>
    public static CSharpCompilation Compile(string assemblyName, params SourceFile[] sources) {
        var trees = new List<SyntaxTree> {
            Parse(file: new SourceFile(
            Name: "GlobalUsings.cs",
            Text: "global using System;\r\nglobal using System.Collections.Generic;\r\nglobal using System.Linq;\r\nglobal using System.Threading;\r\nglobal using System.Threading.Tasks;\r\n"
        )),
        };

        trees.AddRange(collection: sources.Select(selector: Parse));

        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: trees,
            references: References,
            options: new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
    }
}
