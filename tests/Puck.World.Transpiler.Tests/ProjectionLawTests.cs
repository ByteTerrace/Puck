using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The projection law: source and document are two views of one thing, so printing a document and
/// compiling the print returns the same document, and formatting a source never moves what it compiles to.</summary>
/// <remarks>It runs over every shipped <c>.puck</c> source and over one generated source per construct
/// <see cref="ConstructRegistry"/> registers, so a construct the printer cannot reproduce fails under its own
/// name.</remarks>
public class ProjectionLawTests {
    private static string Canonical(JsonNode node) =>
        Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: node));
    private static JsonObject Compile(string source, string label, string? sourcePath = null) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourcePath: sourcePath
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: $"{label} does not compile:{Environment.NewLine}{compilation.Diagnostics.FormatReport(source)}{Environment.NewLine}{source}"
        );

        return compilation.RequireJson();
    }
    // The verdict names the construct, so the caller passes `label` rather than letting a mismatch report a bare
    // JSON pointer.
    private static void AssertPrintsBackToItself(JsonObject document, string label) {
        var printed = WorldDecompiler.Decompile(root: document);
        var recompiled = Compile(
            label: $"{label}: the printed source",
            source: printed
        );
        var mismatch = JsonMismatch.Find(
            actual: JsonNode.Parse(json: Canonical(node: recompiled)),
            expected: JsonNode.Parse(json: Canonical(node: document)),
            path: label
        );

        Assert.True(
            condition: (mismatch is null),
            userMessage: $"{mismatch}{Environment.NewLine}{printed}"
        );
    }
    // `document` is what `source` itself compiles to, which the formatted source must compile to as well.
    private static void AssertFormattingKeepsTheDocument(string source, JsonObject document, string label, string? sourcePath = null) {
        var formatted = PuckFormat.Format(source: source);
        var mismatch = JsonMismatch.Find(
            actual: JsonNode.Parse(json: Canonical(node: Compile(
                label: $"{label}: formatted",
                source: formatted,
                sourcePath: sourcePath
            ))),
            expected: JsonNode.Parse(json: Canonical(node: document)),
            path: label
        );

        Assert.True(
            condition: (mismatch is null),
            userMessage: $"{mismatch}{Environment.NewLine}{formatted}"
        );
    }
    private static bool Holds(JsonNode? node, Func<JsonObject, bool> predicate) => node switch {
        JsonObject o => (predicate(arg: o) || o.Any(predicate: pair => Holds(
            node: pair.Value,
            predicate: predicate
        ))),
        JsonArray a => a.Any(predicate: item => Holds(
            node: item,
            predicate: predicate
        )),
        _ => false,
    };
    // What it means for a generated source to COVER its construct: the document it compiles to actually carries a
    // node of that construct. Without this a snippet that quietly stopped exercising its arm would still pass.
    private static bool Carries(JsonObject document, string construct) {
        var (family, name) = (construct[..construct.IndexOf(value: '/')], construct[(construct.IndexOf(value: '/') + 1)..]);

        return family switch {
            "comparison" => Holds(
                node: document,
                predicate: o => (o["comparison"]?.GetValue<string>() == name)
            ),
            "comparisonKind" => Holds(
                node: document,
                predicate: o => ((o["$type"]?.GetValue<string>() == "compareValue") && (o["kind"]?.GetValue<string>() == name))
            ),
            _ => Holds(
                node: document,
                predicate: o => (o["$type"]?.GetValue<string>() == name)
            ),
        };
    }

    /// <summary>Sources whose document prints back longer than <c>PUCK047</c>'s source limit, so the print half of
    /// the law cannot close on them. They stay under the format half and under the regeneration gate.
    /// <see cref="APrintExcludedSourceStillOutgrowsTheParser"/> fails when an exclusion stops being
    /// necessary.</summary>
    private static readonly string[] PrintExclusions = ["moth-courtyard.puck"];

    public static TheoryData<string> Constructs() => new(values: ConstructRegistry.Names);
    public static TheoryData<string> PrintExcludedSources() => new(values: PrintExclusions);
    public static TheoryData<string> ShippedSources() => ShippedWorlds.Sources();
    [Fact]
    public void TheGeneratedSetCoversEveryRegisteredConstruct() {
        var registered = ConstructRegistry.Names.ToHashSet(comparer: StringComparer.Ordinal);
        var generated = ConstructCorpus.Sources.Keys.ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: generated.Except(second: registered).Order(comparer: StringComparer.Ordinal)
            ),
            expected: ""
        );
        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: registered.Except(second: generated).Order(comparer: StringComparer.Ordinal)
            ),
            expected: ""
        );
    }
    [MemberData(nameof(Constructs))]
    [Theory]
    public void AGeneratedConstructProjects(string construct) {
        Assert.True(
            condition: ConstructCorpus.Sources.TryGetValue(
                key: construct,
                value: out var source
            ),
            userMessage: $"{construct} has no generated source"
        );

        var document = Compile(
            label: construct,
            source: source!
        );

        Assert.True(
            condition: Carries(
                construct: construct,
                document: document
            ),
            userMessage: $"{construct} is not in the document its own source compiles to:{Environment.NewLine}{Canonical(node: document)}"
        );
        AssertPrintsBackToItself(
            document: document,
            label: construct
        );
        AssertFormattingKeepsTheDocument(
            document: document,
            label: construct,
            source: source!
        );
    }
    [MemberData(nameof(ShippedSources))]
    [Theory]
    public void AShippedSourceProjects(string relativePath) {
        var sourcePath = ShippedWorlds.PathOf(relativePath: relativePath);
        var document = ShippedWorlds.Compile(relativePath: relativePath).RequireJson();

        if (!PrintExclusions.Contains(value: relativePath, comparer: StringComparer.Ordinal)) {
            AssertPrintsBackToItself(
                document: document,
                label: relativePath
            );
        }
        AssertFormattingKeepsTheDocument(
            document: document,
            label: relativePath,
            source: File.ReadAllText(path: sourcePath),
            sourcePath: sourcePath
        );
    }
    [MemberData(nameof(PrintExcludedSources))]
    [Theory]
    public void APrintExcludedSourceStillOutgrowsTheParser(string relativePath) {
        var printed = WorldDecompiler.Decompile(root: ShippedWorlds.Compile(relativePath: relativePath).RequireJson());
        var diagnostics = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: printed
        ).Diagnostics;

        Assert.Contains(
            collection: diagnostics,
            filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.EvaluationLimit)
        );
    }
}
