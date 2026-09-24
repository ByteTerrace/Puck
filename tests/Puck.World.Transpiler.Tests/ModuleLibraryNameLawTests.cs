using System.Text;
using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: a module library emits no document (<see cref="WorldCompilation.EmitsDocument"/>), so
/// it carries no document name anywhere, and the composer resolves a document name only against the files that do
/// carry it. A reference to <c>hub</c> reads <c>hub.world.json</c> past a <c>hub.puck</c> that only declares modules, a
/// library alone answers the reference with no document, and a library that starts emitting a document takes its name
/// back. Two files that do carry the name in different letter case are refused by
/// <see cref="Puck.Assets.Documents.DocumentName.Collision"/>'s wording, while a library spelled in another case
/// collides with nothing.</summary>
public sealed class ModuleLibraryNameLawTests {
    private const string CaseRefusal = "whose names differ only in letter case; a document name is unique ignoring case";
    private const string Document = """{ "schema": "puck.world.definition.v1", "documentId": "hub", "authored": true }""";
    private const string Emitting = (WorldSources.Header + "documentId: \"fromSource\"\n");
    private const string Library = "module hub(value) { state { world { slot charge = value } } }\n";

    private static bool TryCompose(TemporaryDirectory directory, out JsonObject? composed, out string reason) => PuckDocumentComposer.TryComposeWorldDocument(
        chainBytes: out _,
        composed: out composed,
        reason: out reason,
        rootBytes: """{ "schema": "puck.world.definition.v1", "basis": "hub" }"""u8.ToArray(),
        rootResolvedPath: directory.PathOf(name: "root.world.json")
    );
    private static JsonObject Compose(TemporaryDirectory directory) {
        Assert.True(
            condition: TryCompose(
                composed: out var composed,
                directory: directory,
                reason: out var reason
            ),
            userMessage: reason
        );

        return composed!;
    }

    /// <summary>A library spelled like the document, in either case, carries no name, so the reference reads the
    /// document file.</summary>
    [Theory]
    [InlineData("hub.puck")]
    [InlineData("Hub.puck")]
    public void AReferenceResolvesPastAModuleLibraryToTheDocumentNamedLikeIt(string library) {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(name: library, text: Library);
        _ = directory.WriteText(name: "hub.world.json", text: Document);

        Assert.True(condition: Compose(directory: directory).ContainsKey(propertyName: "authored"));
    }
    [Fact]
    public void AModuleLibraryAloneAnswersTheReferenceWithNoDocument() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(name: "hub.puck", text: Library);

        Assert.False(condition: TryCompose(
            composed: out _,
            directory: directory,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "is a module library, which emits no document"
        );
    }
    /// <summary>The library's source changing to emit a document hands the name back to it: a composition that read
    /// the document file no longer stands, and the next one reads the source.</summary>
    [Fact]
    public void ALibraryThatStartsEmittingADocumentTakesItsNameBack() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(name: "hub.puck", text: Library);

        _ = directory.WriteText(name: "hub.world.json", text: Document);

        Assert.True(condition: Compose(directory: directory).ContainsKey(propertyName: "authored"));

        File.WriteAllText(
            contents: Emitting,
            path: source
        );

        var composed = Compose(directory: directory);

        Assert.False(condition: composed.ContainsKey(propertyName: "authored"));
        Assert.Equal(
            actual: composed["documentId"]?.GetValue<string>(),
            expected: "fromSource"
        );
    }
    /// <summary>A source that emits a document and a document file carrying the same name in another letter case are
    /// two documents under one name, which a case-insensitive file system would read as one; the reference is refused
    /// rather than resolved.</summary>
    [Theory]
    [InlineData("Hub.puck", "hub.world.json")]
    [InlineData("hub.puck", "Hub.world.json")]
    public void TwoFilesCarryingTheNameInDifferentCaseAreRefused(string source, string document) {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteText(name: source, text: Emitting);
        _ = directory.WriteText(name: document, text: Document);

        Assert.False(condition: TryCompose(
            composed: out _,
            directory: directory,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: CaseRefusal
        );
    }
    /// <summary>A world source whose basis is named like a module library compiles against the document file and
    /// validates as a composed world: the enum only that document declares resolves in the child.</summary>
    [Fact]
    public void AWorldWhoseBasisIsNamedLikeAModuleLibraryComposesTheDocument() {
        using var directory = new TemporaryDirectory();
        var basis = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: $"{WorldSources.Header}state {{\nenum Element {{\n    Nothing\n    Air\n}}\n    world {{\n    }}\n}}\n"
        );

        Assert.False(
            condition: basis.Diagnostics.HasErrors,
            userMessage: basis.Diagnostics.FormatReport(filePath: "basis")
        );

        _ = directory.WriteText(name: "hub.puck", text: Library);
        _ = directory.WriteText(name: "hub.world.json", text: basis.RequireJson().ToJsonString());

        var child = directory.WriteText(name: "child.puck", text: "basis: \"hub\"\n\nstate {\n    world {\n        slot left: Element = Air\n    }\n}\n");
        var sourceMap = new SourceMap();
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: File.ReadAllText(path: child),
            sourceMap: sourceMap,
            sourcePath: child
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(filePath: child)
        );

        _ = WorldSemanticValidator.ValidateComposedWorld(
            diagnostics: compilation.Diagnostics,
            loweredJson: compilation.RequireJson(),
            sourceMap: sourceMap,
            sourcePath: child
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(filePath: child)
        );
        Assert.True(condition: PuckDocumentComposer.TryComposeWorldDocument(
            chainBytes: out _,
            composed: out var composed,
            reason: out var reason,
            rootBytes: Encoding.UTF8.GetBytes(s: compilation.RequireJson().ToJsonString()),
            rootResolvedPath: child
        ), userMessage: reason);
        Assert.NotNull(@object: composed!["state"]?["enums"]);
    }
    /// <summary>The composer's directory enumeration holds the same rule: a library is listed as a library and not as
    /// its name's carrier, so the document file named like it carries the name.</summary>
    [Fact]
    public void TheComposersCarriersListALibraryApartFromTheDocumentNamedLikeIt() {
        using var directory = new TemporaryDirectory();
        var library = directory.WriteText(name: "hub.puck", text: Library);
        var document = directory.WriteText(name: "hub.world.json", text: Document);

        Assert.True(condition: PuckDocumentComposer.TryCarriers(
            carriers: out var carriers,
            directory: directory.RootPath,
            libraries: out var libraries,
            option: SearchOption.TopDirectoryOnly,
            reason: out var reason
        ), userMessage: reason);
        Assert.Equal(
            actual: carriers,
            expected: [new WorldDocumentCarrier(Name: "hub", Path: document.Replace(newChar: '/', oldChar: '\\'))]
        );
        Assert.Equal(
            actual: libraries,
            expected: [library.Replace(newChar: '/', oldChar: '\\')]
        );
    }
}
