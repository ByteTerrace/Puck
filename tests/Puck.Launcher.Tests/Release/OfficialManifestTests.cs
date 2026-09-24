using Puck.Assets.Documents;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Covers <see cref="OfficialCanonicalizer"/>'s round-trip identity (canonical bytes → hash → parse →
/// re-canonicalize → identical bytes and hash), its sort-based normalization, and its structural refusals.</summary>
public sealed class OfficialManifestTests {
    private static readonly string ContentHashA = $"sha256/{new string(
        c: '0',
        count: 64
    )}";
    private static readonly string ContentHashB = $"sha256/{new string(
        c: '1',
        count: 64
    )}";
    private static readonly string ContentHashC = $"sha256/{new string(
        c: '2',
        count: 64
    )}";
    private static readonly string ShortPin = $"sha256-64/{new string(
        c: 'a',
        count: 16
    )}";

    private static OfficialManifest ValidDocument() => new(
        Assets: [],
        Build: new OfficialBuildInfo(
            Commit: "0123456789abcdef0123456789abcdef01234567",
            Dirty: false,
            Generator: "Puck.World.WorldSchema",
            WorldSchema: "puck.world.definition.v1"
        ),
        Channel: "dev",
        Composed: [
            new OfficialComposedEntry(
                ContentType: "application/json",
                DocumentId: "puck",
                Hash: ContentHashA,
                Identity: null,
                Name: "puck",
                Path: "objects/sha256/00/aaa",
                Pin: ShortPin,
                Size: 10
            ),
        ],
        Documents: [
            new OfficialDocumentEntry(
                ContentType: "application/json",
                DocumentId: "puck",
                Exports: [],
                Hash: ContentHashA,
                Imports: [new OfficialImportRef(
                        As: null,
                        Document: "games/example"
                    )],
                Name: "puck",
                Path: "objects/sha256/00/aaa",
                Pin: null,
                Role: OfficialDocumentRoles.World,
                Size: 10,
                Source: "puck.world.json"
            ),
            new OfficialDocumentEntry(
                ContentType: "application/json",
                DocumentId: null,
                Exports: [],
                Hash: ContentHashB,
                Imports: [],
                Name: "standard",
                Path: "objects/sha256/01/bbb",
                Pin: ShortPin,
                Role: OfficialDocumentRoles.Basis,
                Size: 20,
                Source: "standard.puck"
            ),
        ],
        Engine: new OfficialEngine(
            Entry: "main.mjs",
            Files: [
            new OfficialEngineFile(
                    ContentType: "text/javascript",
                    Hash: ContentHashC,
                    Name: "main.mjs",
                    Path: "objects/sha256/02/ccc",
                    Size: 5
                ),
        ]
        ),
        Schema: OfficialManifest.CurrentSchema,
        Signature: null,
        Sources: [
            new OfficialSourceEntry(
                ContentType: OfficialSourceContentTypes.Json,
                Hash: ContentHashA,
                Name: "puck.world.json",
                Path: "objects/sha256/00/aaa",
                Size: 10
            ),
            new OfficialSourceEntry(
                ContentType: OfficialSourceContentTypes.Puck,
                Hash: ContentHashC,
                Name: "standard.puck",
                Path: "objects/sha256/02/ccc",
                Size: 5
            ),
        ],
        WorldSchemaBundle: new OfficialObjectRef(
            ContentType: "application/json",
            Hash: ContentHashC,
            Path: "objects/sha256/02/ccc",
            Size: 5
        )
    );

    [Fact]
    public void Canonicalize_RoundTrips_ByteIdentically() {
        var first = OfficialCanonicalizer.Canonicalize(document: ValidDocument());
        var reparsed = System.Text.Json.JsonSerializer.Deserialize<OfficialManifest>(
            utf8Json: first.Bytes,
            options: DocumentJsonOptions.Shared
        )!;
        var second = OfficialCanonicalizer.Canonicalize(document: reparsed);

        Assert.Equal(
            expected: first.Hash,
            actual: second.Hash
        );
        Assert.Equal(
            expected: first.Bytes,
            actual: second.Bytes
        );
    }
    [Fact]
    public void Canonicalize_Throws_OnStructuralViolation() =>
        Assert.Throws<DocumentValidationException>(testCode: () => OfficialCanonicalizer.Canonicalize(document: (ValidDocument() with { Channel = "" })));
    [Fact]
    public void Normalize_IsIdempotent() {
        var once = OfficialCanonicalizer.Normalize(document: ValidDocument());
        var twice = OfficialCanonicalizer.Normalize(document: once);

        Assert.Equal(
            expected: OfficialCanonicalizer.Canonicalize(document: once).Bytes,
            actual: OfficialCanonicalizer.Canonicalize(document: twice).Bytes
        );
    }
    [Fact]
    public void Normalize_SortsDocumentsComposedAndAssets() {
        var unordered = ValidDocument() with {
            Assets = [
                new OfficialAssetEntry(
                ContentType: "application/json",
                Family: AssetRowFamilies.Tune,
                Hash: ContentHashA,
                Name: "z",
                Path: "objects/sha256/00/aaa",
                Pin: null,
                Size: 1,
                Source: "tunes/z.audio.json"
            ),
                new OfficialAssetEntry(
                ContentType: "application/json",
                Family: AssetRowFamilies.Music,
                Hash: ContentHashB,
                Name: "a",
                Path: "objects/sha256/01/bbb",
                Pin: null,
                Size: 1,
                Source: "music/a.music.json"
            ),
            ],
            Composed = [
                new OfficialComposedEntry(
                ContentType: "application/json",
                DocumentId: "b",
                Hash: ContentHashA,
                Identity: null,
                Name: "b",
                Path: "objects/sha256/00/aaa",
                Pin: null,
                Size: 1
            ),
                new OfficialComposedEntry(
                ContentType: "application/json",
                DocumentId: "a",
                Hash: ContentHashB,
                Identity: null,
                Name: "a",
                Path: "objects/sha256/01/bbb",
                Pin: null,
                Size: 1
            ),
            ],
            Documents = [
                new OfficialDocumentEntry(
                ContentType: "application/json",
                DocumentId: null,
                Exports: ["b", "a", "a"],
                Hash: ContentHashA,
                Imports: [],
                Name: "z",
                Path: "objects/sha256/00/aaa",
                Pin: null,
                Role: OfficialDocumentRoles.Fragment,
                Size: 1,
                Source: "puck.world.json"
            ),
                new OfficialDocumentEntry(
                ContentType: "application/json",
                DocumentId: null,
                Exports: [],
                Hash: ContentHashB,
                Imports: [],
                Name: "a",
                Path: "objects/sha256/01/bbb",
                Pin: null,
                Role: OfficialDocumentRoles.Fragment,
                Size: 1,
                Source: "puck.world.json"
            ),
            ],
        };
        var normalized = OfficialCanonicalizer.Normalize(document: unordered);

        Assert.Equal(
            expected: "a",
            actual: normalized.Documents[0].Name
        );
        Assert.Equal(
            expected: "z",
            actual: normalized.Documents[1].Name
        );
        Assert.Equal(
            expected: ["a", "b"],
            actual: normalized.Documents[1].Exports
        );
        Assert.Equal(
            expected: "a",
            actual: normalized.Composed[0].Name
        );
        Assert.Equal(
            expected: "b",
            actual: normalized.Composed[1].Name
        );
        Assert.Equal(
            expected: AssetRowFamilies.Music,
            actual: normalized.Assets[0].Family
        );
        Assert.Equal(
            expected: AssetRowFamilies.Tune,
            actual: normalized.Assets[1].Family
        );
    }
    [Fact]
    public void Normalize_SortsSources() {
        var unordered = ValidDocument() with { Sources = [.. ValidDocument().Sources.Reverse()] };
        var normalized = OfficialCanonicalizer.Normalize(document: unordered);

        Assert.Equal(
            expected: ["puck.world.json", "standard.puck"],
            actual: normalized.Sources.Select(selector: static entry => entry.Name)
        );
    }
    [InlineData("")]
    [InlineData("standard.world.json")]
    [Theory]
    public void Validate_RefusesADocumentSourceThatNamesNoSourceFile(string source) {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0], (ValidDocument().Documents[1] with { Source = source })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: static error => (error.Path == "documents[1].source")
        );
    }
    [Fact]
    public void Validate_RefusesAbsentSchema() {
        var errors = OfficialCanonicalizer.Validate(document: (ValidDocument() with { Schema = null }));

        Assert.Single(collection: errors);
        Assert.Equal(
            expected: "schema",
            actual: errors[0].Path
        );
    }
    /// <summary>A document name is unique ignoring case (<see cref="DocumentName"/>): two <c>documents[]</c> entries
    /// naming one document, exactly or in another letter case, are refused with the refusal every door that admits
    /// document names words, naming both carrying sources.</summary>
    [Theory]
    [InlineData("puck")]
    [InlineData("Puck")]
    [InlineData("PUCK")]
    public void Validate_RefusesTwoDocumentsWhoseNamesAreOneIgnoringCase(string name) {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0], (ValidDocument().Documents[1] with { Name = name })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);
        var refusal = Assert.Single(
            collection: errors,
            predicate: static error => (error.Path == "documents[1].name")
        );

        Assert.Equal(
            expected: DocumentName.Collision(
                heldFile: "puck.world.json",
                heldName: "puck",
                otherFile: "standard.puck",
                otherName: name
            ),
            actual: refusal.Message
        );
    }
    [Fact]
    public void Validate_RefusesTwoSourcesWhoseNamesDifferOnlyInCase() {
        var bad = ValidDocument() with {
            Sources = [ValidDocument().Sources[0], (ValidDocument().Sources[1] with { Name = "Puck.World.json" })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: static error => (
                (error.Path == "sources[1].name") &&
                error.Message.Contains(value: "'puck.world.json' and 'Puck.World.json' differ only in letter case")
            )
        );
    }
    [Fact]
    public void Validate_RefusesDuplicateSourceName() {
        var bad = ValidDocument() with {
            Sources = [ValidDocument().Sources[0], (ValidDocument().Sources[1] with { Name = "puck.world.json" })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: static error => (
                (error.Path == "sources[1].name") &&
                error.Message.Contains(value: "more than once")
            )
        );
    }
    [Fact]
    public void Validate_RefusesEmptyComposed() {
        var errors = OfficialCanonicalizer.Validate(document: (ValidDocument() with { Composed = [] }));

        Assert.Contains(
            collection: errors,
            filter: error => (error.Path == "composed")
        );
    }
    [Fact]
    public void Validate_RefusesEmptyDocuments() {
        var errors = OfficialCanonicalizer.Validate(document: (ValidDocument() with { Documents = [] }));

        Assert.Contains(
            collection: errors,
            filter: error => (error.Path == "documents")
        );
    }
    [Fact]
    public void Validate_RefusesEngineEntryNotInFiles() {
        var bad = ValidDocument() with {
            Engine = (ValidDocument().Engine with { Entry = "missing.mjs" }),
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: error => (error.Path == "engine.entry")
        );
    }
    [Fact]
    public void Validate_RefusesAComposedNameThatNamesNoDocument() {
        var bad = ValidDocument() with {
            Composed = [(ValidDocument().Composed[0] with { Name = "puck.world.json" })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: static error => (
                (error.Path == "composed[0].name") &&
                error.Message.Contains(value: "does not name a document")
            )
        );
    }
    [InlineData("")]
    [InlineData("../puck")]
    [InlineData("games//klondike")]
    [InlineData("/games/klondike")]
    [Theory]
    public void Validate_RefusesADocumentNameThatLeavesTheWorkspace(string name) {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0], (ValidDocument().Documents[1] with { Name = name })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: static error => (error.Path == "documents[1].name")
        );
    }
    [InlineData("")]
    [InlineData("/games/klondike.puck")]
    [InlineData("../klondike.puck")]
    [InlineData("games/../klondike.puck")]
    [InlineData("games//klondike.puck")]
    [InlineData("./klondike.puck")]
    [InlineData("games\\klondike.puck")]
    [InlineData("C:/klondike.puck")]
    [Theory]
    public void Validate_RefusesASourceNameThatLeavesTheWorkspace(string name) {
        var bad = ValidDocument() with {
            Sources = [ValidDocument().Sources[0], (ValidDocument().Sources[1] with { Name = name })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: static error => (error.Path == "sources[1].name")
        );
    }
    [Fact]
    public void Validate_RefusesMalformedContentHash() {
        var bad = ValidDocument() with {
            WorldSchemaBundle = (ValidDocument().WorldSchemaBundle with { Hash = "not-a-hash" }),
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: error => error.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".hash"
            )
        );
    }
    [Fact]
    public void Validate_RefusesUppercasePins() {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0], (ValidDocument().Documents[1] with { Pin = ShortPin.ToUpperInvariant().Replace(
                newValue: "sha256-64/",
                oldValue: "SHA256-64/"
            ) })],
            WorldSchemaBundle = (ValidDocument().WorldSchemaBundle with {
                Hash = $"sha256/{new string(
                c: 'A',
                count: 64
            )}",
            }),
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: error => error.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".pin"
            )
        );
        Assert.Contains(
            collection: errors,
            filter: error => error.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".hash"
            )
        );
    }
    [Fact]
    public void Validate_RefusesMalformedShortPin() {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0], (ValidDocument().Documents[1] with { Pin = "sha256/deadbeef" })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: error => error.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".pin"
            )
        );
    }
    [Fact]
    public void Validate_RefusesMalformedSourceHash() {
        var bad = ValidDocument() with {
            Sources = [ValidDocument().Sources[0], (ValidDocument().Sources[1] with { Hash = "sha256/deadbeef" })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: static error => (error.Path == "sources[1].hash")
        );
    }
    [Fact]
    public void Validate_RefusesUnknownAssetFamily() {
        var bad = ValidDocument() with {
            Assets = [new OfficialAssetEntry(
                ContentType: "application/json",
                Family: "bogus",
                Hash: ContentHashA,
                Name: "x",
                Path: "objects/sha256/00/aaa",
                Pin: null,
                Size: 1,
                Source: "x.json"
            )],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: error => error.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".family"
            )
        );
    }
    [Fact]
    public void Validate_RefusesUnknownExtensionMember() {
        var document = ValidDocument();

        document.Extensions = new Dictionary<string, System.Text.Json.JsonElement> {
            ["channel"] = System.Text.Json.JsonDocument.Parse(json: "\"shadowed\"").RootElement,
        };

        var errors = OfficialCanonicalizer.Validate(document: document);

        Assert.Contains(
            collection: errors,
            filter: error => (error.Path == "extensions.channel")
        );
    }
    [Fact]
    public void Validate_RefusesUnknownRole() {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0] with { Role = "bogus" }, ValidDocument().Documents[1]],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(
            collection: errors,
            filter: error => error.Path.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".role"
            )
        );
    }
}
