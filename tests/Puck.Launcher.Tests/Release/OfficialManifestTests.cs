using Puck.Assets.Documents;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Covers <see cref="OfficialCanonicalizer"/>'s round-trip identity (canonical bytes → hash → parse →
/// re-canonicalize → identical bytes and hash), its sort-based normalization, and its structural refusals.</summary>
public sealed class OfficialManifestTests {
    private static readonly string ContentHashA = $"sha256/{new string(c: '0', count: 64)}";
    private static readonly string ContentHashB = $"sha256/{new string(c: '1', count: 64)}";
    private static readonly string ContentHashC = $"sha256/{new string(c: '2', count: 64)}";
    private static readonly string ShortPin = $"sha256-64/{new string(c: 'a', count: 16)}";

    private static OfficialManifest ValidDocument() => new(
        Assets: [],
        Build: new OfficialBuildInfo(Commit: "0123456789abcdef0123456789abcdef01234567", Dirty: false, Generator: "Puck.World.WorldSchema", WorldSchema: "puck.world.def.v1"),
        Channel: "dev",
        Composed: [
            new OfficialComposedEntry(ContentType: "application/json", DocumentId: "puck", Hash: ContentHashA, Identity: null, Name: "puck.world.json", Path: "objects/sha256/00/aaa", Pin: ShortPin, Size: 10),
        ],
        Documents: [
            new OfficialDocumentEntry(ContentType: "application/json", DocumentId: "puck", Exports: [], Hash: ContentHashA, Imports: [new OfficialImportRef(As: null, Document: "games/chess.world.json")], Name: "puck.world.json", Path: "objects/sha256/00/aaa", Pin: null, Role: OfficialDocumentRoles.World, Size: 10),
            new OfficialDocumentEntry(ContentType: "application/json", DocumentId: null, Exports: [], Hash: ContentHashB, Imports: [], Name: "standard.basis.json", Path: "objects/sha256/01/bbb", Pin: ShortPin, Role: OfficialDocumentRoles.Basis, Size: 20),
        ],
        Engine: new OfficialEngine(Entry: "main.mjs", Files: [
            new OfficialEngineFile(ContentType: "text/javascript", Hash: ContentHashC, Name: "main.mjs", Path: "objects/sha256/02/ccc", Size: 5),
        ]),
        Schema: OfficialManifest.CurrentSchema,
        Signature: null,
        WorldSchemaBundle: new OfficialObjectRef(ContentType: "application/json", Hash: ContentHashC, Path: "objects/sha256/02/ccc", Size: 5)
    );

    [Fact]
    public void Canonicalize_RoundTrips_ByteIdentically() {
        var first = OfficialCanonicalizer.Canonicalize(document: ValidDocument());
        var reparsed = System.Text.Json.JsonSerializer.Deserialize<OfficialManifest>(utf8Json: first.Bytes, options: DocumentJsonOptions.Shared)!;
        var second = OfficialCanonicalizer.Canonicalize(document: reparsed);

        Assert.Equal(expected: first.Hash, actual: second.Hash);
        Assert.Equal(expected: first.Bytes, actual: second.Bytes);
    }
    [Fact]
    public void Normalize_IsIdempotent() {
        var once = OfficialCanonicalizer.Normalize(document: ValidDocument());
        var twice = OfficialCanonicalizer.Normalize(document: once);

        Assert.Equal(expected: OfficialCanonicalizer.Canonicalize(document: once).Bytes, actual: OfficialCanonicalizer.Canonicalize(document: twice).Bytes);
    }
    [Fact]
    public void Normalize_SortsDocumentsComposedAndAssets() {
        var unordered = ValidDocument() with {
            Assets = [
                new OfficialAssetEntry(ContentType: "application/json", Family: OfficialAssetFamilies.Tune, Hash: ContentHashA, Name: "z", Path: "objects/sha256/00/aaa", Pin: null, Size: 1, Source: "tunes/z.audio.json"),
                new OfficialAssetEntry(ContentType: "application/json", Family: OfficialAssetFamilies.Music, Hash: ContentHashB, Name: "a", Path: "objects/sha256/01/bbb", Pin: null, Size: 1, Source: "music/a.music.json"),
            ],
            Composed = [
                new OfficialComposedEntry(ContentType: "application/json", DocumentId: "b", Hash: ContentHashA, Identity: null, Name: "b.world.json", Path: "objects/sha256/00/aaa", Pin: null, Size: 1),
                new OfficialComposedEntry(ContentType: "application/json", DocumentId: "a", Hash: ContentHashB, Identity: null, Name: "a.world.json", Path: "objects/sha256/01/bbb", Pin: null, Size: 1),
            ],
            Documents = [
                new OfficialDocumentEntry(ContentType: "application/json", DocumentId: null, Exports: ["b", "a", "a"], Hash: ContentHashA, Imports: [], Name: "z.world.json", Path: "objects/sha256/00/aaa", Pin: null, Role: OfficialDocumentRoles.Fragment, Size: 1),
                new OfficialDocumentEntry(ContentType: "application/json", DocumentId: null, Exports: [], Hash: ContentHashB, Imports: [], Name: "a.world.json", Path: "objects/sha256/01/bbb", Pin: null, Role: OfficialDocumentRoles.Fragment, Size: 1),
            ],
        };
        var normalized = OfficialCanonicalizer.Normalize(document: unordered);

        Assert.Equal(expected: "a.world.json", actual: normalized.Documents[0].Name);
        Assert.Equal(expected: "z.world.json", actual: normalized.Documents[1].Name);
        Assert.Equal(expected: ["a", "b"], actual: normalized.Documents[1].Exports);
        Assert.Equal(expected: "a.world.json", actual: normalized.Composed[0].Name);
        Assert.Equal(expected: "b.world.json", actual: normalized.Composed[1].Name);
        Assert.Equal(expected: OfficialAssetFamilies.Music, actual: normalized.Assets[0].Family);
        Assert.Equal(expected: OfficialAssetFamilies.Tune, actual: normalized.Assets[1].Family);
    }
    [Fact]
    public void Validate_RefusesAbsentSchema() {
        var errors = OfficialCanonicalizer.Validate(document: (ValidDocument() with { Schema = null }));

        Assert.Single(collection: errors);
        Assert.Equal(expected: "schema", actual: errors[0].Path);
    }
    [Fact]
    public void Validate_RefusesEmptyDocuments() {
        var errors = OfficialCanonicalizer.Validate(document: (ValidDocument() with { Documents = [] }));

        Assert.Contains(collection: errors, filter: error => (error.Path == "documents"));
    }
    [Fact]
    public void Validate_RefusesEmptyComposed() {
        var errors = OfficialCanonicalizer.Validate(document: (ValidDocument() with { Composed = [] }));

        Assert.Contains(collection: errors, filter: error => (error.Path == "composed"));
    }
    [Fact]
    public void Validate_RefusesUnknownRole() {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0] with { Role = "bogus" }, ValidDocument().Documents[1]],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(collection: errors, filter: error => error.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".role"));
    }
    [Fact]
    public void Validate_RefusesUnknownAssetFamily() {
        var bad = ValidDocument() with {
            Assets = [new OfficialAssetEntry(ContentType: "application/json", Family: "bogus", Hash: ContentHashA, Name: "x", Path: "objects/sha256/00/aaa", Pin: null, Size: 1, Source: "x.json")],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(collection: errors, filter: error => error.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".family"));
    }
    [Fact]
    public void Validate_RefusesMalformedContentHash() {
        var bad = ValidDocument() with {
            WorldSchemaBundle = (ValidDocument().WorldSchemaBundle with { Hash = "not-a-hash" }),
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(collection: errors, filter: error => error.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".hash"));
    }
    [Fact]
    public void Validate_RefusesMalformedShortPin() {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0], (ValidDocument().Documents[1] with { Pin = "sha256/deadbeef" })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(collection: errors, filter: error => error.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".pin"));
    }
    [Fact]
    public void Validate_RefusesDuplicateDocumentName() {
        var bad = ValidDocument() with {
            Documents = [ValidDocument().Documents[0], (ValidDocument().Documents[1] with { Name = "puck.world.json" })],
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: "more than once"));
    }
    [Fact]
    public void Validate_RefusesEngineEntryNotInFiles() {
        var bad = ValidDocument() with {
            Engine = (ValidDocument().Engine with { Entry = "missing.mjs" }),
        };
        var errors = OfficialCanonicalizer.Validate(document: bad);

        Assert.Contains(collection: errors, filter: error => (error.Path == "engine.entry"));
    }
    [Fact]
    public void Validate_RefusesUnknownExtensionMember() {
        var document = ValidDocument();

        document.Extensions = new Dictionary<string, System.Text.Json.JsonElement> {
            ["channel"] = System.Text.Json.JsonDocument.Parse(json: "\"shadowed\"").RootElement,
        };

        var errors = OfficialCanonicalizer.Validate(document: document);

        Assert.Contains(collection: errors, filter: error => (error.Path == "extensions.channel"));
    }
    [Fact]
    public void Canonicalize_Throws_OnStructuralViolation() =>
        Assert.Throws<DocumentValidationException>(testCode: () => OfficialCanonicalizer.Canonicalize(document: (ValidDocument() with { Channel = "" })));
}
