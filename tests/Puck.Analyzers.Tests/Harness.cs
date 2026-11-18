using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Puck.Analyzers.Tests;

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

    public static ImmutableArray<MetadataReference> RuntimeReferences => FixtureCompiler.RuntimeReferences;

    /// <summary>Runs <paramref name="analyzer"/> over <paramref name="compilation"/> with concurrent execution enabled.</summary>
    /// <param name="compilation">The compilation to analyze.</param>
    /// <param name="analyzer">The analyzer to run.</param>
    /// <returns>The analyzer's and the compiler's diagnostics.</returns>
    public static AnalysisResult Analyze(CSharpCompilation compilation, DiagnosticAnalyzer analyzer) =>
        Analyze(analyzer: analyzer, compilation: compilation, globalOptions: ImmutableDictionary<string, string>.Empty);
    /// <summary>Runs <paramref name="analyzer"/> over <paramref name="compilation"/> with the global analyzer config
    /// options an MSBuild build would surface (<c>build_property.*</c> keys).</summary>
    /// <param name="compilation">The compilation to analyze.</param>
    /// <param name="analyzer">The analyzer to run.</param>
    /// <param name="globalOptions">The global analyzer config options.</param>
    /// <returns>The analyzer's and the compiler's diagnostics.</returns>
    public static AnalysisResult Analyze(CSharpCompilation compilation, DiagnosticAnalyzer analyzer, ImmutableDictionary<string, string> globalOptions) {
        var options = new CompilationWithAnalyzersOptions(
            options: new AnalyzerOptions(additionalFiles: ImmutableArray<AdditionalText>.Empty, optionsProvider: new GlobalOptionsProvider(options: globalOptions)),
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
    private sealed class GlobalOptionsProvider(ImmutableDictionary<string, string> options) : AnalyzerConfigOptionsProvider {
        private static readonly AnalyzerConfigOptions None = new Options(values: ImmutableDictionary<string, string>.Empty);

        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(values: options);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => None;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => None;

        private sealed class Options(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions {
            public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) => values.TryGetValue(key: key, value: out value);
        }
    }
    public static CSharpCompilation Compile(string assemblyName, params SourceFile[] sources) => FixtureCompiler.Compile(assemblyName, sources);
}
