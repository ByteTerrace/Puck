using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Proves the <c>metadata</c> section: it round-trips byte-identically, its <c>custom</c> bag preserves nested
/// JSON structure and observes the SAME compose-time carve-outs (<c>$drop</c>/<c>$replace</c> refused,
/// authored-null deletes) every other nested object in a basis chain observes, every cap/shape rule refuses by
/// name with a passing control, its <c>authors</c> list follows the document's generic row-key merge rule, it never
/// enters a simulation-state hash while it DOES move the file-identity content hash, and it never crosses the
/// disclosure boundary except its <c>title</c>/<c>description</c>.
/// </summary>
public sealed class WorldMetadataSectionLawTests {
    private static IReadOnlyList<WorldMetadataAuthor> AuthorRows(int count) {
        var rows = new WorldMetadataAuthor[count];

        for (var index = 0; (index < count); index++) {
            rows[index] = new WorldMetadataAuthor(Name: $"Author{index}");
        }

        return rows;
    }
    private static IDictionary<string, JsonElement> ParseCustomBag(string json) {
        using var document = JsonDocument.Parse(json: json);
        var bag = new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject()) {
            bag[property.Name] = property.Value.Clone();
        }

        return bag;
    }
    private static WorldDefinition RichDocument() => (Fixtures.BuildDocument() with {
        Metadata = new WorldMetadataSection(
        Title: "Play",
        Description: "The overworld hub.",
        Authors: [
                new WorldMetadataAuthor(
                Name: "Jane",
                Oid: "11111111-1111-1111-1111-111111111111"
            ),
                new WorldMetadataAuthor(Name: "Bob"),
            ],
        Tags: ["overworld", "hub", "city"],
        Custom: ParseCustomBag(json: /*lang=json*/ """
                { "a": { "b": [1, 2, "x"] }, "count": 3.5e2, "label": "café ★" }
                """)
    ),
    });
    private static IDictionary<string, JsonElement> SingleEntryBag(string key) => ParseCustomBag(json: $$"""{ "{{key}}": true }""");
    // "k" (1 UTF-8 byte) plus a quoted ASCII string of valueLength characters (valueLength + 2 UTF-8 bytes) —
    // total bytes = valueLength + 3, so callers can hit an exact byte-cap boundary.
    private static IDictionary<string, JsonElement> SingleStringCustomBag(int valueLength) => new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal) {
        ["k"] = JsonDocument.Parse(json: JsonSerializer.Serialize(value: new string(
        c: 'x',
        count: valueLength
    ))).RootElement.Clone(),
    };
    private static IReadOnlyList<string> TagRows(int count) {
        var rows = new string[count];

        for (var index = 0; (index < count); index++) {
            rows[index] = $"tag{index}";
        }

        return rows;
    }
    private static bool TryValidateLocal(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out _
    );

    [Fact]
    public void AuthorOid_InvalidRefusesByName_ControlValidOidClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.author-oid",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Authors: [new WorldMetadataAuthor(
                    Name: "Jane",
                    Oid: "not-a-guid"
                )]),
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Authors: [new WorldMetadataAuthor(
                    Name: "Jane",
                    Oid: "11111111-1111-1111-1111-111111111111"
                )]),
            }))
        );
    }
    [Fact]
    public void ClosingBracketInTitle_RefusesByName_ControlPlainTitleClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.title-closing-bracket",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Title: "Play]") })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Title: "Play") }))
        );
    }
    [Fact]
    public void Compose_AuthorsMergeByName_AndFlipsToWholesaleWithoutName() {
        var basis = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "authors": [ { "name": "Jane" }, { "name": "Bob" } ] } }
            """)!);
        var overlay = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "authors": [ { "name": "Jane", "oid": "11111111-1111-1111-1111-111111111111" }, { "name": "Carol" } ] } }
            """)!);

        Assert.True(
            condition: WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var composed,
                overlay: overlay,
                reason: out var reason
            ),
            userMessage: reason
        );

        var authors = ((JsonArray)((JsonObject)composed![propertyName: "metadata"]!)[propertyName: "authors"]!);

        // Basis order preserved; the same-name row refines in place, a new name appends — merge by row key.
        Assert.Equal(
            expected: 3,
            actual: authors.Count
        );
        Assert.Equal(
            expected: "Jane",
            actual: ((JsonObject)authors[0]!)[propertyName: "name"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "11111111-1111-1111-1111-111111111111",
            actual: ((JsonObject)authors[0]!)[propertyName: "oid"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "Bob",
            actual: ((JsonObject)authors[1]!)[propertyName: "name"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "Carol",
            actual: ((JsonObject)authors[2]!)[propertyName: "name"]!.GetValue<string>()
        );

        // The flip: one basis row without "name" makes the whole list ineligible for keyed merge, so it replaces
        // wholesale rather than merging the surviving rows in place.
        var basisWithGhostRow = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "authors": [ { "name": "Jane" }, { "note": "ghost" } ] } }
            """)!);
        var overlaySingleAuthor = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "authors": [ { "name": "Carol" } ] } }
            """)!);

        Assert.True(
            condition: WorldDocumentBasis.TryMerge(
                basis: basisWithGhostRow,
                composed: out var wholesale,
                overlay: overlaySingleAuthor,
                reason: out var wholesaleReason
            ),
            userMessage: wholesaleReason
        );

        var wholesaleAuthors = ((JsonArray)((JsonObject)wholesale![propertyName: "metadata"]!)[propertyName: "authors"]!);

        Assert.Single(collection: wholesaleAuthors);
        Assert.Equal(
            expected: "Carol",
            actual: ((JsonObject)wholesaleAuthors[0]!)[propertyName: "name"]!.GetValue<string>()
        );
    }
    [Fact]
    public void Compose_CustomAuthoredNullClearsInheritedKey_ControlOmittedKeyInherits() {
        var basis = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "custom": { "a": "kept" } } }
            """)!);
        var overlayClears = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "custom": { "a": null } } }
            """)!);

        Assert.True(
            condition: WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var composed,
                overlay: overlayClears,
                reason: out var reason
            ),
            userMessage: reason
        );

        var custom = ((JsonObject)((JsonObject)composed![propertyName: "metadata"]!)[propertyName: "custom"]!);

        Assert.False(condition: custom.ContainsKey(propertyName: "a"));

        var overlayOmits = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "custom": { "b": "new" } } }
            """)!);

        Assert.True(
            condition: WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var inherited,
                overlay: overlayOmits,
                reason: out var inheritReason
            ),
            userMessage: inheritReason
        );

        var inheritedCustom = ((JsonObject)((JsonObject)inherited![propertyName: "metadata"]!)[propertyName: "custom"]!);

        Assert.Equal(
            expected: "kept",
            actual: inheritedCustom[propertyName: "a"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "new",
            actual: inheritedCustom[propertyName: "b"]!.GetValue<string>()
        );
    }
    [Fact]
    public void Compose_MemberWiseMerge_AndAuthoredNullClears() {
        var basis = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "title": "Base Title" } }
            """)!);
        var overlayDescriptionOnly = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "description": "Derived description" } }
            """)!);

        Assert.True(
            condition: WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var composed,
                overlay: overlayDescriptionOnly,
                reason: out var reason
            ),
            userMessage: reason
        );

        var metadata = ((JsonObject)composed![propertyName: "metadata"]!);

        Assert.Equal(
            expected: "Base Title",
            actual: metadata[propertyName: "title"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "Derived description",
            actual: metadata[propertyName: "description"]!.GetValue<string>()
        );

        var overlayClearsTitle = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "metadata": { "title": null } }
            """)!);

        Assert.True(
            condition: WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var cleared,
                overlay: overlayClearsTitle,
                reason: out var clearReason
            ),
            userMessage: clearReason
        );

        var clearedMetadata = ((JsonObject)cleared![propertyName: "metadata"]!);

        Assert.False(condition: clearedMetadata.ContainsKey(propertyName: "title"));
    }
    [Fact]
    public void ControlCharacterInDescription_RefusesByName_ControlPlainTextClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.description-control-character",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Description: "line one\nline two") })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Description: "line one line two") }))
        );
    }
    [Fact]
    public void CustomBag_RejectsComposeVocabularyKeys_ByName_ControlPlainKeyClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.custom-compose-vocabulary",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Custom: SingleEntryBag(key: "$drop")) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Custom: SingleEntryBag(key: "drop")) }))
        );
    }
    [Fact]
    public void CustomOverCap_RefusesByName_ControlAtCapClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.custom-byte-cap",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Custom: SingleStringCustomBag(valueLength: ((WorldMetadataCapacity.MaxCustomBytes - 3) + 1))) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Custom: SingleStringCustomBag(valueLength: (WorldMetadataCapacity.MaxCustomBytes - 3))) }))
        );
    }
    [Fact]
    public void DoesNotAffectReplayHashes_WithDiscriminatingControl() {
        var baseDocument = Fixtures.BuildDocument();
        var metadataA = (baseDocument with { Metadata = new WorldMetadataSection(Title: "Play") });
        var metadataB = (baseDocument with {
            Metadata = new WorldMetadataSection(
            Title: "A Completely Different Title",
            Description: "different",
            Tags: ["x"]
        ),
        });

        var populationA = new WorldPopulation(definition: metadataA);
        var populationB = new WorldPopulation(definition: metadataB);

        populationA.ActivateSeat(
            profile: null,
            slot: 0
        );
        populationB.ActivateSeat(
            profile: null,
            slot: 0
        );

        Assert.Equal(
            expected: WorldReplaySnapshot.HashState(population: populationA),
            actual: WorldReplaySnapshot.HashState(population: populationB)
        );

        // Discriminating control: a document differing in a SIMULATION-affecting field (the seat spawn position)
        // DOES hash differently through the identical harness — the equality above is not because HashState can
        // never differ.
        var movedSpawn = (baseDocument.SpawnPoints[0] with {
            Position = (baseDocument.SpawnPoints[0].Position + new Vector3(
            x: 5f,
            y: 0f,
            z: 0f
        )),
        });
        var spawnMovedDocument = (baseDocument with { SpawnPointsRaw = [movedSpawn, .. baseDocument.SpawnPoints.Skip(count: 1)] });

        var populationC = new WorldPopulation(definition: baseDocument);
        var populationD = new WorldPopulation(definition: spawnMovedDocument);

        populationC.ActivateSeat(
            profile: null,
            slot: 0
        );
        populationD.ActivateSeat(
            profile: null,
            slot: 0
        );

        Assert.NotEqual(
            expected: WorldReplaySnapshot.HashState(population: populationC),
            actual: WorldReplaySnapshot.HashState(population: populationD)
        );
    }
    [Fact]
    public void DuplicateTag_RefusesByName_ControlDistinctTagsClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.duplicate-tag",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Tags: ["hub", "hub"]) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Tags: ["hub", "city"]) }))
        );
    }
    [Fact]
    public void MetadataEditOnTheBasis_MovesTheDerivedChainContentHash() {
        using var files = new TempWorldDirectory();

        var basisPath = files.WriteFlatDocument(name: "basis.world.json");
        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """
            { "basis": "basis.world.json", "motion": { "moveSpeed": 6.5 } }
            """
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: deltaPath,
                definition: out _,
                contentHash: out var before,
                reason: out var reason
            ),
            userMessage: reason
        );

        var basisNode = ((JsonObject)JsonNode.Parse(json: File.ReadAllText(path: basisPath))!);

        basisNode["metadata"] = JsonNode.Parse(json: /*lang=json*/ """{ "title": "Edited" }""");

        File.WriteAllText(
            path: basisPath,
            contents: basisNode.ToJsonString()
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: deltaPath,
                definition: out _,
                contentHash: out var after,
                reason: out var reloadReason
            ),
            userMessage: reloadReason
        );
        Assert.NotEqual(
            actual: after,
            expected: before
        );
    }
    [Fact]
    public void MetadataOnlyEdit_MovesContentHash() {
        var baseDocument = Fixtures.BuildDocument();
        var withMetadata = (baseDocument with { Metadata = new WorldMetadataSection(Title: "Play") });

        var bytesA = WorldDefinitionSerialization.Serialize(definition: baseDocument);
        var bytesB = WorldDefinitionSerialization.Serialize(definition: withMetadata);

        Assert.NotEqual(
            expected: WorldDefinitionFileSource.ComputeContentHash(content: bytesA),
            actual: WorldDefinitionFileSource.ComputeContentHash(content: bytesB)
        );
    }
    [Fact]
    public void Metadata_AuthorNameOverLengthCap_RefusesByName_ControlAtCapClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.author-name-length-cap",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Authors: [new WorldMetadataAuthor(Name: new string(
                    c: 'x',
                    count: (WorldMetadataCapacity.MaxAuthorNameLength + 1)
                ))]),
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Authors: [new WorldMetadataAuthor(Name: new string(
                    c: 'x',
                    count: WorldMetadataCapacity.MaxAuthorNameLength
                ))]),
            }))
        );
    }
    [Fact]
    public void Metadata_AuthorsOverRowCap_RefusesByName_ControlAtCapClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.authors-row-cap",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Authors: AuthorRows(count: (WorldMetadataCapacity.MaxAuthors + 1))) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Authors: AuthorRows(count: WorldMetadataCapacity.MaxAuthors)) }))
        );
    }
    [Fact]
    public void Metadata_CustomNestedComposeVocabulary_RefusesByName() {
        Laws.RefusalWithControl(
            lawId: "metadata.custom-compose-vocabulary-nested",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Custom: ParseCustomBag(json: /*lang=json*/ """{ "a": { "$replace": true } }""")) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Custom: ParseCustomBag(json: /*lang=json*/ """{ "a": { "replace": true } }""")) }))
        );
    }
    [Fact]
    public void Metadata_DescriptionOverLengthCap_RefusesByName_ControlAtCapClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.description-length-cap",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Description: new string(
                c: 'x',
                count: (WorldMetadataCapacity.MaxDescriptionLength + 1)
            )),
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Description: new string(
                c: 'x',
                count: WorldMetadataCapacity.MaxDescriptionLength
            )),
            }))
        );
    }
    [Fact]
    public void Metadata_EmptyAuthorName_RefusesByName_ControlNonEmptyClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.author-name-empty",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Authors: [new WorldMetadataAuthor(Name: "")]) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Authors: [new WorldMetadataAuthor(Name: "Jane")]) }))
        );
    }
    [Fact]
    public void Metadata_EmptyTag_RefusesByName_ControlNonEmptyClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.tag-empty",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Tags: [""]) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Tags: ["hub"]) }))
        );
    }
    [Fact]
    public void Metadata_NullAuthorRow_RefusesByName_ControlNonNullClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.author-row-null",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Authors: [null!]) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Authors: [new WorldMetadataAuthor(Name: "Jane")]) }))
        );
    }
    [Fact]
    public void Metadata_TagOverLengthCap_RefusesByName_ControlAtCapClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.tag-length-cap",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Tags: [new string(
                    c: 'x',
                    count: (WorldMetadataCapacity.MaxTagLength + 1)
                )]),
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Tags: [new string(
                    c: 'x',
                    count: WorldMetadataCapacity.MaxTagLength
                )]),
            }))
        );
    }
    [Fact]
    public void Metadata_TagsOverRowCap_RefusesByName_ControlAtCapClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.tags-row-cap",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Tags: TagRows(count: (WorldMetadataCapacity.MaxTags + 1))) })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { Metadata = new WorldMetadataSection(Tags: TagRows(count: WorldMetadataCapacity.MaxTags)) }))
        );
    }
    [Fact]
    public void Metadata_TitleOverLengthCap_RefusesByName_ControlAtCapClean() {
        Laws.RefusalWithControl(
            lawId: "metadata.title-length-cap",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Title: new string(
                c: 'x',
                count: (WorldMetadataCapacity.MaxTitleLength + 1)
            )),
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                Metadata = new WorldMetadataSection(Title: new string(
                c: 'x',
                count: WorldMetadataCapacity.MaxTitleLength
            )),
            }))
        );
    }
    [Fact]
    public void RoundTrip_PreservesEveryField_CanonicalTextEqual() {
        var document = RichDocument();
        var bytes = WorldDefinitionSerialization.Serialize(definition: document);
        var reloaded = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

        Assert.Equal(
            expected: Encoding.UTF8.GetString(bytes: bytes),
            actual: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: reloaded))
        );

        // custom's nested shapes survive structurally, not merely as matching canonical text.
        var nested = reloaded.Metadata!.Custom!["a"];

        Assert.Equal(
            expected: JsonValueKind.Object,
            actual: nested.ValueKind
        );

        var array = nested.GetProperty(propertyName: "b");

        Assert.Equal(
            expected: JsonValueKind.Array,
            actual: array.ValueKind
        );
        Assert.Equal(
            expected: 3,
            actual: array.GetArrayLength()
        );
        Assert.Equal(
            expected: 1,
            actual: array[0].GetInt32()
        );
        Assert.Equal(
            expected: "x",
            actual: array[2].GetString()
        );
    }
    [Fact]
    public void SaveIsIdempotent_SerializeDeserializeSerialize_ByteIdentical() {
        var document = RichDocument();
        var first = WorldDefinitionSerialization.Serialize(definition: document);
        var second = WorldDefinitionSerialization.Serialize(definition: WorldDefinitionSerialization.Deserialize(utf8Json: first));

        Assert.Equal(
            expected: Encoding.UTF8.GetString(bytes: first),
            actual: Encoding.UTF8.GetString(bytes: second)
        );
    }
    [Fact]
    public void UnknownMemberInsideMetadata_RefusesByName() {
        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()))!.AsObject();

        node["metadata"] = JsonNode.Parse(json: /*lang=json*/ """{ "nickname": "x" }""");

        var bytes = Encoding.UTF8.GetBytes(s: node.ToJsonString());
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: bytes));

        Assert.IsType<JsonException>(@object: exception.InnerException);
        Assert.Contains(
            expectedSubstring: "nickname",
            actualString: exception.InnerException!.Message,
            comparisonType: StringComparison.Ordinal
        );
    }

    /// <summary>A per-test directory so relative <c>basis</c> spellings resolve exactly the way the shipped assets'
    /// do (against the referring file's own directory), cleaned up whole. Not the hoisted shared fixture other law
    /// files use — this project has no such shared fixture yet — so this is a self-contained copy.</summary>
    private sealed class TempWorldDirectory : IDisposable {
        private readonly string m_root = Directory.CreateDirectory(path: Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-world-tests-metadata-{Guid.NewGuid():N}"
        )).FullName;

        public void Dispose() {
            try {
                Directory.Delete(
                    path: m_root,
                    recursive: true
                );
            } catch (IOException) {
            }
        }
        public string WriteFlatDocument(string name) {
            var path = Path.Combine(
                path1: m_root,
                path2: name
            );

            File.WriteAllBytes(
                path: path,
                bytes: Fixtures.DefaultWorldBytes()
            );

            return path;
        }
        public string WriteText(string name, string text) {
            var path = Path.Combine(
                path1: m_root,
                path2: name
            );

            File.WriteAllText(
                contents: text,
                path: path
            );

            return path;
        }
    }
}
