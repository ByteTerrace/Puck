using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Embeddings;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A source the world vocabulary refuses, and where it says so.</summary>
/// <param name="Body">The source after the world schema header.</param>
/// <param name="Code">The diagnostic the source draws for its fault, exactly once.</param>
/// <param name="Needle">Text whose last occurrence in the source sits on the line the diagnostic names.</param>
internal sealed record Refusal(string Body, string Code, string Needle) {
    /// <summary>Gets the embedding lock the compile resolves authored text against, when the case needs one.</summary>
    public EmbeddingLock? Embeddings { get; init; }
    /// <summary>Gets text the diagnostic's message must carry, when the case pins one.</summary>
    public string? Mentions { get; init; }
    /// <summary>Gets whether the refusal must be the only error the source draws.</summary>
    public bool Alone { get; init; }
}
/// <summary>The suite's one way to compile world source through <see cref="WorldCompiler"/>, to hold a document to its
/// decompile-and-recompile round trip, and to hold a refusal to its code and line.</summary>
/// <remarks>Every compile here runs under the calling test's cancellation token and with no source path, so no
/// import graph or lock beside a file is read unless the caller passes one.</remarks>
internal static class WorldSources {
    /// <summary>The world schema header a body is compiled under.</summary>
    public const string Header = "schema: \"puck.world.definition.v1\"\n\n";
    /// <summary>An eight-component vector, base64: 127 and then seven zeros.</summary>
    public const string SampleVector = "fwAAAAAAAAA";

    /// <summary>Returns an embedding lock whose one space, <c>lore</c>, has eight dimensions, revision <c>1</c>, and
    /// <see cref="SampleVector"/> recorded for each of <paramref name="texts"/>.</summary>
    /// <param name="model">The model the space was embedded with.</param>
    /// <param name="texts">The texts the lock carries.</param>
    /// <returns>The lock.</returns>
    public static EmbeddingLock LoreLock(string model, params string[] texts) {
        var lockFile = new EmbeddingLock();
        var space = new EmbeddingLockSpace(identity: new EmbeddingIdentity(
            Dimensions: 8,
            Model: model,
            Revision: "1"
        ));

        foreach (var text in texts) {
            space.Entries[EmbeddingText.Hash(text: text).Hex] = new EmbeddingLockEntry(
                Text: text,
                Vector: SampleVector
            );
        }
        lockFile.Spaces["lore"] = space;

        return lockFile;
    }
    /// <summary>Asserts that <paramref name="diagnostics"/> carry <paramref name="code"/> exactly once, with a span of
    /// positive length on the line of the last occurrence of <paramref name="needle"/> in
    /// <paramref name="source"/>.</summary>
    /// <param name="label">The case's name, which every failure message leads with.</param>
    /// <param name="source">The whole source the diagnostics were drawn from.</param>
    /// <param name="diagnostics">What the parse or compile reported.</param>
    /// <param name="code">The expected diagnostic code.</param>
    /// <param name="needle">Text whose last occurrence sits on the expected line.</param>
    /// <param name="mentions">Text the diagnostic's message must carry; nothing is required when omitted.</param>
    /// <param name="alone">Whether the refusal must be the only error reported.</param>
    public static void AssertRefusedAt(string label, string source, DiagnosticBag diagnostics, string code, string needle, string? mentions = null, bool alone = false) {
        var matches = diagnostics.Where(predicate: diagnostic => (diagnostic.Code == code)).ToArray();

        Assert.True(
            condition: (matches.Length == 1),
            userMessage: $"{label}: expected {code} once, got {matches.Length}:{Environment.NewLine}{diagnostics.FormatReport(source)}"
        );

        var needleIndex = source.LastIndexOf(
            comparisonType: StringComparison.Ordinal,
            value: needle
        );

        Assert.True(
            condition: (needleIndex >= 0),
            userMessage: $"{label}: the needle '{needle}' is not in the source"
        );

        var expectedLine = (source[..needleIndex].Count(predicate: static character => (character == '\n')) + 1);

        Assert.True(
            condition: ((matches[0].Span.Length > 0) && (matches[0].Span.Line == expectedLine)),
            userMessage: $"{label}: {code} should span line {expectedLine}, and spans {matches[0].Span.Length} characters at line {matches[0].Span.Line}"
        );
        Assert.True(
            condition: ((mentions is null) || matches[0].Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: mentions
            )),
            userMessage: $"{label}: {code} should mention '{mentions}': {matches[0].Message}"
        );
        Assert.True(
            condition: (!alone || (diagnostics.Count(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error)) == 1)),
            userMessage: $"{label}: {code} should be the only error:{Environment.NewLine}{diagnostics.FormatReport(source)}"
        );
    }
    /// <summary>Compiles <see cref="Header"/> and <see cref="Refusal.Body"/> and asserts the refusal fires as the case
    /// says.</summary>
    /// <param name="label">The case's name.</param>
    /// <param name="refusal">The case.</param>
    public static void AssertRefused(string label, Refusal refusal) => AssertRefusedBy(
        diagnostics: Compile(
            embeddings: refusal.Embeddings,
            source: (Header + refusal.Body)
        ).Diagnostics,
        label: label,
        refusal: refusal
    );
    /// <summary>Asserts that <paramref name="diagnostics"/>, drawn from <see cref="Header"/> and
    /// <see cref="Refusal.Body"/> by a parse or a compile, carry the refusal as the case says.</summary>
    /// <param name="label">The case's name.</param>
    /// <param name="refusal">The case.</param>
    /// <param name="diagnostics">What the parse or compile reported.</param>
    public static void AssertRefusedBy(string label, Refusal refusal, DiagnosticBag diagnostics) => AssertRefusedAt(
        alone: refusal.Alone,
        code: refusal.Code,
        diagnostics: diagnostics,
        label: label,
        mentions: refusal.Mentions,
        needle: refusal.Needle,
        source: (Header + refusal.Body)
    );
    /// <summary>Decompiles <paramref name="original"/>, compiles the print, and asserts the result equals the
    /// original.</summary>
    /// <param name="original">A canonical document.</param>
    /// <param name="sql">Whether the decompiler projects state and rules into a <c>sql</c> block.</param>
    /// <param name="embeddings">The embedding lock both directions resolve authored text against; none when
    /// omitted.</param>
    /// <returns>The printed source.</returns>
    public static string AssertRoundTrips(JsonObject original, bool sql = false, EmbeddingLock? embeddings = null) {
        var printed = WorldDecompiler.Decompile(
            embeddings: embeddings,
            root: original,
            sql: sql
        );
        var recompiled = Compile(
            embeddings: embeddings,
            source: printed
        );

        Assert.False(
            condition: recompiled.Diagnostics.HasErrors,
            userMessage: $"{recompiled.Diagnostics.FormatReport(printed)}{Environment.NewLine}---{Environment.NewLine}{printed}"
        );

        var mismatch = JsonMismatch.Find(
            actual: recompiled.RequireJson(),
            expected: original,
            path: "$"
        );

        Assert.True(
            condition: (mismatch is null),
            userMessage: $"{mismatch}{Environment.NewLine}---{Environment.NewLine}{printed}"
        );

        return printed;
    }
    /// <summary>Returns a document written as author-facing JSON in the form a compile produces: its infix
    /// expressions and channel names lowered as the lowering lowers them.</summary>
    /// <param name="json">The document as JSON text.</param>
    /// <returns>The canonical document.</returns>
    public static JsonObject Canonical(string json) {
        var document = Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: json));

        WorldExpressionJson.Lower(node: document);
        WorldChannelNodes.Lower(
            document: document,
            type: typeof(WorldDefinition)
        );

        return document;
    }
    /// <summary>Compiles <paramref name="source"/>.</summary>
    /// <param name="source">The whole source.</param>
    /// <param name="embeddings">The embedding lock authored text resolves against; none when omitted.</param>
    /// <returns>The compilation.</returns>
    public static WorldCompilation Compile(string source, EmbeddingLock? embeddings = null) => WorldCompiler.Compile(
        cancellationToken: TestContext.Current.CancellationToken,
        embeddings: embeddings,
        source: source
    );
    /// <summary>Returns what compiling <see cref="Header"/> and <paramref name="body"/> reports.</summary>
    /// <param name="body">The source after the header.</param>
    /// <returns>Every diagnostic.</returns>
    public static DiagnosticBag Diagnose(string body) => Compile(source: (Header + body)).Diagnostics;
    /// <summary>Compiles <see cref="Header"/> and <paramref name="body"/>, requiring that a document was
    /// lowered.</summary>
    /// <param name="body">The source after the header.</param>
    /// <param name="embeddings">The embedding lock authored text resolves against; none when omitted.</param>
    /// <returns>The document, which may be partial, and every diagnostic.</returns>
    public static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string body, EmbeddingLock? embeddings = null) => LowerSource(
        embeddings: embeddings,
        source: (Header + body)
    );
    /// <summary>Compiles <see cref="Header"/> and <paramref name="body"/>, failing the test on any error.</summary>
    /// <param name="body">The source after the header.</param>
    /// <returns>The document.</returns>
    public static JsonObject LowerClean(string body) => LowerSourceClean(source: (Header + body));
    /// <summary>Compiles <paramref name="source"/>, requiring that a document was lowered.</summary>
    /// <param name="source">The whole source.</param>
    /// <param name="embeddings">The embedding lock authored text resolves against; none when omitted.</param>
    /// <returns>The document, which may be partial, and every diagnostic.</returns>
    public static (JsonObject Json, DiagnosticBag Diagnostics) LowerSource(string source, EmbeddingLock? embeddings = null) {
        var compilation = Compile(
            embeddings: embeddings,
            source: source
        );

        Assert.True(
            condition: (compilation.Json is not null),
            userMessage: $"nothing was lowered:{Environment.NewLine}{compilation.Diagnostics.FormatReport(source)}"
        );

        return (compilation.Json!, compilation.Diagnostics);
    }
    /// <summary>Parses <see cref="Header"/> and <paramref name="body"/> without lowering it.</summary>
    /// <param name="body">The source after the header.</param>
    /// <returns>The tree, or <see langword="null"/> when nothing parsed, and every diagnostic.</returns>
    public static (DocumentNode? Document, DiagnosticBag Diagnostics) Parse(string body) {
        var result = PuckParser.ParseDocumentWithDiagnostics(source: (Header + body));

        return (result.Value, result.Diagnostics);
    }
    /// <summary>Parses <see cref="Header"/> and <paramref name="body"/>, failing the test on any error.</summary>
    /// <param name="body">The source after the header.</param>
    /// <returns>The tree.</returns>
    public static DocumentNode ParseClean(string body) {
        var (document, diagnostics) = Parse(body: body);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport((Header + body))
        );

        return Assert.IsType<DocumentNode>(@object: document);
    }
    /// <summary>Compiles <paramref name="source"/>, failing the test on any error.</summary>
    /// <param name="source">The whole source.</param>
    /// <returns>The document.</returns>
    public static JsonObject LowerSourceClean(string source) {
        var compilation = Compile(source: source);

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );

        return compilation.RequireJson();
    }
}
