using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Puck.Analyzers.Tests;

/// <summary>One <c>AdditionalFiles</c> entry handed to the analyzer, including the deliberately empty-text case.</summary>
internal sealed class HarnessAdditionalText : AdditionalText {
    private readonly SourceText? m_text;

    public HarnessAdditionalText(string path, string? text, Encoding? encoding = null) {
        m_text = ((text is null) ? null : SourceText.From(text: text, encoding: (encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))));
        Path = path;
    }

    public override string Path { get; }

    public override SourceText? GetText(CancellationToken cancellationToken = default) =>
        m_text;
}

/// <summary>A named source file in a harness compilation.</summary>
/// <param name="Name">The file name; a <c>.g.cs</c> suffix is what makes Roslyn treat the tree as generated.</param>
/// <param name="Text">The C# source.</param>
internal readonly record struct SourceFile(string Name, string Text);

/// <summary>Everything one analyzer run produced, split so a case can assert on compiler and analyzer output separately.</summary>
/// <param name="Analyzer">Diagnostics the analyzer reported, including any AD0001 analyzer crash.</param>
/// <param name="Compiler">Diagnostics the compiler itself reported for the same compilation.</param>
internal sealed record AnalysisResult(ImmutableArray<Diagnostic> Analyzer, ImmutableArray<Diagnostic> Compiler) {
    /// <summary>The analyzer diagnostic ids, sorted, so an assertion reads as a set rather than an ordering.</summary>
    public string[] Ids =>
        Analyzer.Select(selector: diagnostic => diagnostic.Id).OrderBy(keySelector: id => id, comparer: StringComparer.Ordinal).ToArray();

    /// <summary>The compiler errors, formatted for an assertion message.</summary>
    public string CompilerErrorText =>
        string.Join(separator: "; ", values: Compiler.Where(predicate: diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)).Select(selector: diagnostic => diagnostic.ToString()));

    /// <summary>Whether the compiler accepted the source outright — the precondition for calling a silent analyzer a bypass.</summary>
    public bool CompilesCleanly =>
        !Compiler.Any(predicate: diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

    /// <summary>The analyzer diagnostics carrying <paramref name="id"/>.</summary>
    public ImmutableArray<Diagnostic> WithId(string id) =>
        Analyzer.Where(predicate: diagnostic => string.Equals(a: diagnostic.Id, b: id, comparisonType: StringComparison.Ordinal)).ToImmutableArray();

    /// <summary>The one analyzer diagnostic carrying <paramref name="id"/>; throws when there is not exactly one.</summary>
    public Diagnostic Single(string id) =>
        WithId(id: id).Single();
}

/// <summary>
/// Compiles C# source strings in memory, runs <see cref="VerifiedCodeAnalyzer"/> over them against a chosen set of
/// <c>AdditionalFiles</c>, and returns both the analyzer's and the compiler's diagnostics.
/// </summary>
/// <remarks>
/// The suite drives the analyzer directly rather than through
/// <c>Microsoft.CodeAnalysis.CSharp.Analyzer.Testing</c>: that package is not in this repository's package set, and
/// several cases here — two <c>AdditionalFiles</c> sharing one file name, an <c>AdditionalText</c> whose
/// <c>GetText</c> returns nothing, a chosen assembly name, cancellation, and repeated concurrent execution of one
/// compilation — need control over the analysis inputs that a markup-driven test framework does not expose.
/// </remarks>
internal static class Harness {
    /// <summary>The assembly name harness compilations use unless a case needs a specific one for the manifest sweep.</summary>
    public const string DefaultAssemblyName = "Subject.Assembly";

    /// <summary>The file name the analyzer looks for among <c>AdditionalFiles</c>.</summary>
    public const string ManifestFileName = "VerifiedCode.json";

    /// <summary>The namespace harness sources declare by default, matching <see cref="DefaultAssemblyName"/> so the manifest sweep's convention holds.</summary>
    public const string DefaultNamespace = "Subject.Assembly";

    private static readonly ImmutableArray<MetadataReference> References = CreateReferences();

    private static readonly string BrandAttributeSource = ReadBrandAttributeSource();

    /// <summary>
    /// The repository's real <c>Puck.VerifiedCodeAttribute</c> source, embedded at build time. Compiled into every
    /// harness compilation so the analyzer resolves the same attribute type it resolves in a product build.
    /// </summary>
    public static string BrandAttribute =>
        BrandAttributeSource;

    /// <summary>Builds a compilation from <paramref name="sources"/> plus the brand attribute and the global usings the SDK would supply.</summary>
    public static CSharpCompilation Compile(string assemblyName, params SourceFile[] sources) {
        var trees = new List<SyntaxTree> {
            // `global using Puck;` stands in for the namespace ancestry that resolves the brand in the product
            // tree, where every branded type already sits under a Puck.* namespace. Usings live in their own tree,
            // so this never enters a fingerprint.
            Parse(file: new SourceFile(Name: "GlobalUsings.cs", Text: "global using System;\r\nglobal using System.Collections.Generic;\r\nglobal using System.Linq;\r\nglobal using System.Threading;\r\nglobal using System.Threading.Tasks;\r\nglobal using Puck;\r\n")),
            Parse(file: new SourceFile(Name: "VerifiedCodeAttribute.cs", Text: BrandAttributeSource)),
        };

        trees.AddRange(collection: sources.Select(selector: Parse));

        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: trees,
            references: References,
            options: new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>Builds a compilation that does not declare the brand attribute at all, the one shape the analyzer opts out of.</summary>
    public static CSharpCompilation CompileWithoutBrandAttribute(string assemblyName, params SourceFile[] sources) =>
        CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: sources.Select(selector: Parse),
            references: References,
            options: new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary));

    /// <summary>Runs <paramref name="analyzer"/> (<see cref="VerifiedCodeAnalyzer"/> by default) over <paramref name="compilation"/> with the given additional files.</summary>
    public static AnalysisResult Analyze(CSharpCompilation compilation, Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer? analyzer = null, params AdditionalText[] additionalFiles) =>
        AnalyzeCore(compilation: compilation, additionalFiles: additionalFiles, concurrent: true, cancellationToken: CancellationToken.None, analyzer: analyzer);

    /// <summary>Runs the analyzer with explicit control over concurrent execution and cancellation.</summary>
    /// <param name="compilation">The compilation to analyze.</param>
    /// <param name="additionalFiles">The <c>AdditionalFiles</c> the analyzer sees.</param>
    /// <param name="concurrent">Whether the analyzer runs with concurrent execution enabled.</param>
    /// <param name="cancellationToken">The token analysis observes.</param>
    /// <param name="analyzer">The analyzer to run; defaults to a fresh <see cref="VerifiedCodeAnalyzer"/> when omitted.</param>
    public static AnalysisResult AnalyzeCore(CSharpCompilation compilation, IEnumerable<AdditionalText> additionalFiles, bool concurrent, CancellationToken cancellationToken, Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer? analyzer = null) {
        var options = new CompilationWithAnalyzersOptions(
            options: new AnalyzerOptions(additionalFiles: additionalFiles.ToImmutableArray()),
            onAnalyzerException: null,
            concurrentAnalysis: concurrent,
            logAnalyzerExecutionTime: false);

        var withAnalyzers = compilation.WithAnalyzers(
            analyzers: ImmutableArray.Create(item: (analyzer ?? new VerifiedCodeAnalyzer())),
            analysisOptions: options);

        var analyzerDiagnostics = withAnalyzers
            .GetAnalyzerDiagnosticsAsync(cancellationToken: cancellationToken)
            .GetAwaiter()
            .GetResult();

        return new AnalysisResult(Analyzer: analyzerDiagnostics, Compiler: compilation.GetDiagnostics(cancellationToken: cancellationToken));
    }

    /// <summary>The common shape: one source file, one <c>VerifiedCode.json</c>, the default assembly name.</summary>
    public static AnalysisResult Run(string source, string manifestJson, string assemblyName = DefaultAssemblyName, string sourceName = "Subject.cs") =>
        Analyze(
            compilation: Compile(assemblyName: assemblyName, sources: new SourceFile(Name: sourceName, Text: source)),
            additionalFiles: new HarnessAdditionalText(path: ManifestPath, text: manifestJson));

    /// <summary>Runs a case with no <c>VerifiedCode.json</c> among the additional files at all.</summary>
    public static AnalysisResult RunWithoutManifest(string source, string assemblyName = DefaultAssemblyName) =>
        Analyze(compilation: Compile(assemblyName: assemblyName, sources: new SourceFile(Name: "Subject.cs", Text: source)));

    /// <summary>The fingerprint the analyzer computes for the brand <paramref name="id"/> in <paramref name="source"/>.</summary>
    /// <param name="source">The compilation's one source file.</param>
    /// <param name="id">The brand id whose recomputed hash is wanted.</param>
    /// <param name="assemblyName">The assembly the source is compiled as.</param>
    /// <param name="sourceName">The source file's name.</param>
    /// <param name="symbol">The documentation-comment id to record while probing, needed only when <paramref name="dependencies"/> is non-empty.</param>
    /// <param name="dependencies">
    /// The declarations to seal alongside the branded one. An entry declaring these has to exist for the analyzer to
    /// fold them, so a probe entry recording an all-zero hash is offered — it can never match, so VER001 always
    /// carries the real one.
    /// </param>
    /// <remarks>Recovered from VER001's structured properties, the same channel the code fix reads.</remarks>
    public static string Fingerprint(
        string source,
        string id,
        string assemblyName = DefaultAssemblyName,
        string sourceName = "Subject.cs",
        string? symbol = null,
        IReadOnlyList<string>? dependencies = null
    ) {
        var manifestJson = (((dependencies is null) || (dependencies.Count == 0))
            ? Manifest.Empty
            : Manifest.Of(new ManifestEntry {
                Assembly = assemblyName,
                Dependencies = dependencies,
                Id = id,
                Sha256 = new string(c: '0', count: 64),
                Symbol = (symbol ?? throw new ArgumentNullException(paramName: nameof(symbol), message: "A probe entry has to record a symbol before its dependencies can be folded.")),
            }));

        var result = Run(source: source, manifestJson: manifestJson, assemblyName: assemblyName, sourceName: sourceName);

        if (!result.CompilesCleanly) {
            throw new InvalidOperationException(message: $"Fingerprint source did not compile: {result.CompilerErrorText}");
        }

        var mismatch = result
            .WithId(id: "VER001")
            .FirstOrDefault(predicate: diagnostic => string.Equals(a: Property(diagnostic: diagnostic, key: "VerifiedCodeId"), b: id, comparisonType: StringComparison.Ordinal))
            ?? throw new InvalidOperationException(message: $"No VER001 for brand '{id}'; analyzer reported [{string.Join(separator: ", ", values: result.Ids)}].");

        return (Property(diagnostic: mismatch, key: "VerifiedCodeHash")
            ?? throw new InvalidOperationException(message: $"VER001 for brand '{id}' carried no recomputed hash property."));
    }

    private static string? Property(Diagnostic diagnostic, string key) =>
        (diagnostic.Properties.TryGetValue(key: key, value: out var value) ? value : null);

    /// <summary>The full path harness manifests are given, so <c>Path.GetFileName</c> in the analyzer sees the real name.</summary>
    public static string ManifestPath =>
        System.IO.Path.Combine(path1: System.IO.Path.GetTempPath(), path2: ManifestFileName);

    /// <summary>The metadata references every harness compilation gets: the running runtime's own reference set.</summary>
    public static ImmutableArray<MetadataReference> RuntimeReferences =>
        References;

    private static SyntaxTree Parse(SourceFile file) =>
        CSharpSyntaxTree.ParseText(
            text: SourceText.From(text: file.Text, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
            options: new CSharpParseOptions(languageVersion: LanguageVersion.Latest),
            path: file.Name);

    private static ImmutableArray<MetadataReference> CreateReferences() {
        var trusted = (AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;

        return trusted
            .Split(separator: System.IO.Path.PathSeparator, options: StringSplitOptions.RemoveEmptyEntries)
            .Where(predicate: path => path.EndsWith(value: ".dll", comparisonType: StringComparison.OrdinalIgnoreCase))
            .Select(selector: path => (MetadataReference)MetadataReference.CreateFromFile(path: path))
            .ToImmutableArray();
    }

    private static string ReadBrandAttributeSource() {
        using var stream = typeof(Harness).Assembly.GetManifestResourceStream(name: "VerifiedCodeAttribute.cs")
            ?? throw new InvalidOperationException(message: "The embedded VerifiedCodeAttribute.cs resource is missing.");
        using var reader = new StreamReader(stream: stream);

        return reader.ReadToEnd();
    }
}
