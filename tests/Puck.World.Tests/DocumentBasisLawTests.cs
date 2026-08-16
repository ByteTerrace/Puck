using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves the document-composition contract behind <see cref="WorldDefinition.Basis"/>: a file naming a
/// <c>basis</c> is a delta whose load composes the chain (<see cref="WorldDocumentBasis"/>) inside
/// <see cref="WorldDefinitionFileSource"/> and crosses the same strict parse → migrate → validate gate a flat file
/// crosses; the content pin folds every file on the chain; keyed lists merge by row identity with tombstones refused
/// when stale; a basis surviving to validation refuses; and
/// <see cref="WorldDefinitionSerialization.SavePreservingBasis"/> writes a proved delta that round-trips.</summary>
public sealed class DocumentBasisLawTests {
    [Fact]
    public void BasisCycle_RefusesByName() {
        using var files = new TempWorldDirectory();

        var firstPath = files.WriteText(
            name: "first.world.json",
            text: /*lang=json*/ """{ "basis": "second.world.json" }"""
        );

        files.WriteText(
            name: "second.world.json",
            text: /*lang=json*/ """{ "basis": "first.world.json" }"""
        );

        Assert.False(condition: WorldDefinitionFileSource.TryLoad(
            path: firstPath,
            definition: out _,
            contentHash: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "cycle"
        );
    }
    [Fact]
    public void BasisSurvivingToValidation_Refuses() {
        var stray = (Fixtures.BuildDocument() with { Basis = "basis.world.json" });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: stray,
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "basis"
        );

        // The wire path (a replay embed, a replica) has no directory to resolve a basis against; the same refusal
        // arrives through Deserialize.
        var bytes = WorldDefinitionSerialization.Serialize(definition: stray);

        Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: bytes));
    }
    [Fact]
    public void ChainContentPin_MovesWhenTheBasisMoves() {
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

        // A parse-invisible byte (trailing whitespace) still moves the derived document's pin — the pin covers the
        // chain's raw bytes, not its parse.
        File.AppendAllText(
            contents: "\n",
            path: basisPath
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
    public void ComposedTree_StillCrossesTheStrictParse() {
        using var files = new TempWorldDirectory();

        files.WriteFlatDocument(name: "basis.world.json");

        // The typo lives only in the delta's authored override; it must still refuse by member name after
        // composition, proving the composed tree crosses the same strict gate a flat file does.
        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """
            { "basis": "basis.world.json", "motion": { "moveSpede": 6.5 } }
            """
        );

        Assert.False(condition: WorldDefinitionFileSource.TryLoad(
            path: deltaPath,
            definition: out _,
            contentHash: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "moveSpede"
        );
    }
    [Fact]
    public void DeltaOverBasis_InheritsOmittedAndMergesAuthored() {
        using var files = new TempWorldDirectory();

        var basisPath = files.WriteFlatDocument(name: "basis.world.json");

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: basisPath,
                definition: out var flat,
                contentHash: out var flatHash,
                reason: out var flatReason
            ),
            userMessage: flatReason
        );

        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """
            {
              "basis": "basis.world.json",
              "motion": { "moveSpeed": 6.5 },
              "spawnPoints": [ { "id": "seat-2", "position": [9, 0, 0] } ]
            }
            """
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: deltaPath,
                definition: out var composed,
                contentHash: out var composedHash,
                reason: out var reason
            ),
            userMessage: reason
        );

        // The authored member merges; the sibling member of the same object inherits.
        Assert.Equal(
            expected: 6.5f,
            actual: composed!.Motion.MoveSpeed
        );
        Assert.Equal(
            expected: flat!.Motion.TurnSpeed,
            actual: composed.Motion.TurnSpeed
        );

        // The keyed list merges in place: same row count, basis order, one row's position replaced.
        Assert.Equal(
            expected: flat.SpawnPoints.Count,
            actual: composed.SpawnPoints.Count
        );
        Assert.Equal(
            expected: flat.SpawnPoints.Select(selector: static row => row.Id),
            actual: composed.SpawnPoints.Select(selector: static row => row.Id)
        );
        Assert.Equal(
            expected: 9f,
            actual: composed.SpawnPoints.First(predicate: static row => (row.Id == "seat-2")).Position.X
        );
        Assert.Equal(
            expected: flat.SpawnPoints.First(predicate: static row => (row.Id == "seat-1")).Position,
            actual: composed.SpawnPoints.First(predicate: static row => (row.Id == "seat-1")).Position
        );

        // Wholly omitted sections inherit.
        Assert.Equal(
            expected: flat.Kits.Count,
            actual: composed.Kits.Count
        );
        Assert.Equal(
            expected: flat.DefaultSeatKit,
            actual: composed.DefaultSeatKit
        );

        // The consumed basis member never survives into the live document, and the chain pin is not the flat pin.
        Assert.Null(@object: composed.Basis);
        Assert.NotEqual(
            actual: composedHash,
            expected: flatHash
        );
    }
    [Fact]
    public void Diff_IsTheMergeInverse_EvenWhenRowsReorder() {
        var basis = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "rows": [ { "id": "a", "v": 1 }, { "id": "b", "v": 2 }, { "id": "c", "v": 3 } ] }
            """)!);
        var target = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "rows": [ { "id": "c", "v": 3 }, { "id": "a", "v": 8 }, { "id": "d", "v": 4 } ] }
            """)!);

        var delta = WorldDocumentBasis.Diff(
            basis: basis,
            target: target
        );

        Assert.True(
            condition: WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var composed,
                overlay: delta,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: JsonNode.DeepEquals(
                node1: composed,
                node2: target
            ),
            userMessage: $"delta failed to reproduce the target: {delta.ToJsonString()}"
        );

        // Reordered rows cannot merge by key in place, so the delta must have degraded to the wholesale marker.
        var rows = ((JsonArray)delta[propertyName: "rows"]!);

        Assert.True(condition: ((JsonObject)rows[index: 0]!).ContainsKey(propertyName: WorldDocumentBasis.ReplaceMemberName));
    }
    [Fact]
    public void Diff_PrefersRowGrainDeltas_WhenOrderSurvives() {
        var basis = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "rows": [ { "id": "a", "v": 1, "w": 5 }, { "id": "b", "v": 2 } ] }
            """)!);
        var target = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "rows": [ { "id": "a", "v": 6, "w": 5 }, { "id": "b", "v": 2 } ] }
            """)!);

        var delta = WorldDocumentBasis.Diff(
            basis: basis,
            target: target
        );
        var rows = ((JsonArray)delta[propertyName: "rows"]!);

        // One changed row, addressed by key, carrying only the changed member beside it.
        Assert.Single(collection: rows);

        var row = ((JsonObject)rows[index: 0]!);

        Assert.Equal(
            expected: "a",
            actual: row[propertyName: "id"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: 6,
            actual: row[propertyName: "v"]!.GetValue<int>()
        );
        Assert.False(condition: row.ContainsKey(propertyName: "w"));

        Assert.True(
            condition: WorldDocumentBasis.TryMerge(
                basis: basis,
                composed: out var composed,
                overlay: delta,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(condition: JsonNode.DeepEquals(
            node1: composed,
            node2: target
        ));
    }
    [Fact]
    public void KeyedList_AppendsNewRows_AndTombstonesThemDownChain() {
        using var files = new TempWorldDirectory();

        files.WriteFlatDocument(name: "basis.world.json");

        var appendPath = files.WriteText(
            name: "append.world.json",
            text: /*lang=json*/ """
            {
              "basis": "basis.world.json",
              "spawnPoints": [ { "id": "extra", "position": [5, 0, 5] } ]
            }
            """
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: appendPath,
                definition: out var appended,
                contentHash: out _,
                reason: out var appendReason
            ),
            userMessage: appendReason
        );
        Assert.Equal(
            expected: 5,
            actual: appended!.SpawnPoints.Count
        );
        Assert.Equal(
            expected: "extra",
            actual: appended.SpawnPoints[index: 4].Id
        );

        // A three-document chain: the tombstone drops the row its own basis appended.
        var dropPath = files.WriteText(
            name: "drop.world.json",
            text: /*lang=json*/ """
            {
              "basis": "append.world.json",
              "spawnPoints": [ { "id": "extra", "$drop": true } ]
            }
            """
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: dropPath,
                definition: out var dropped,
                contentHash: out _,
                reason: out var dropReason
            ),
            userMessage: dropReason
        );
        Assert.Equal(
            expected: 4,
            actual: dropped!.SpawnPoints.Count
        );
        Assert.DoesNotContain(
            collection: dropped.SpawnPoints,
            filter: static row => (row.Id == "extra")
        );
    }
    [Fact]
    public void MalformedJsonNamingBasis_StillRefusesFromTheStrictParse() {
        using var files = new TempWorldDirectory();

        // The literal substring `"basis"` appears inside malformed JSON that never parses at all — TryComposeChain
        // must decline to compose (composed == null) rather than throw, leaving the strict parse to own the wording.
        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """{ "basis": "x.world.json", """
        );

        Assert.False(condition: WorldDefinitionFileSource.TryLoad(
            path: deltaPath,
            definition: out _,
            contentHash: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is not a valid"
        );
        Assert.DoesNotContain(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "basis composition refused"
        );
    }
    [Fact]
    public void Merge_TypeDiscriminatorChange_ReplacesWholesale_AndNullClears() {
        var basis = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "a": { "$type": "orbit", "radius": 4, "height": 2 }, "b": { "x": 1 }, "c": 3 }
            """)!);
        var overlay = ((JsonObject)JsonNode.Parse(json: /*lang=json*/ """
            { "a": { "$type": "follow", "distance": 9 }, "b": null }
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

        // The changed discriminator replaces wholesale — no orbit member survives into the follow arm.
        var arm = ((JsonObject)composed![propertyName: "a"]!);

        Assert.False(condition: arm.ContainsKey(propertyName: "radius"));
        Assert.Equal(
            expected: 9,
            actual: arm[propertyName: "distance"]!.GetValue<int>()
        );

        // An authored null removes the inherited member; an omitted member inherits.
        Assert.False(condition: composed.ContainsKey(propertyName: "b"));
        Assert.Equal(
            expected: 3,
            actual: composed[propertyName: "c"]!.GetValue<int>()
        );
    }
    [Fact]
    public void NonObjectRootNamingBasis_FallsThroughToTheStrictParse() {
        using var files = new TempWorldDirectory();

        // A JSON array containing the literal substring `"basis"` still trips the cheap substring gate, but the
        // root itself is not a JsonObject — TryComposeChain must decline to compose, not throw.
        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """[ "basis" ]"""
        );

        Assert.False(condition: WorldDefinitionFileSource.TryLoad(
            path: deltaPath,
            definition: out _,
            contentHash: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is not a valid"
        );
        Assert.DoesNotContain(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "basis composition refused"
        );
    }
    [Fact]
    public void SavePreservingBasis_DegradesToFlatWhenTheBasisIsGone() {
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
                definition: out var loaded,
                contentHash: out _,
                reason: out var loadReason
            ),
            userMessage: loadReason
        );

        File.Delete(path: basisPath);

        WorldDefinitionSerialization.SavePreservingBasis(
            basisPath: out var preserved,
            definition: loaded!,
            note: out var note,
            path: deltaPath
        );

        Assert.Null(@object: preserved);
        Assert.NotEqual(
            actual: note,
            expected: string.Empty
        );

        // The degraded write is a self-contained flat document that loads on its own.
        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: deltaPath,
                definition: out _,
                contentHash: out _,
                reason: out var flatReason
            ),
            userMessage: flatReason
        );
    }
    [Fact]
    public void SavePreservingBasis_WritesAProvedDeltaThatRoundTrips() {
        using var files = new TempWorldDirectory();

        files.WriteFlatDocument(name: "basis.world.json");

        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """
            { "basis": "basis.world.json", "motion": { "moveSpeed": 6.5 } }
            """
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: deltaPath,
                definition: out var loaded,
                contentHash: out _,
                reason: out var loadReason
            ),
            userMessage: loadReason
        );

        var retuned = (loaded! with { Motion = (loaded.Motion with { MoveSpeed = 7.25f }) });

        WorldDefinitionSerialization.SavePreservingBasis(
            basisPath: out var basisPath,
            definition: retuned,
            note: out var note,
            path: deltaPath
        );

        Assert.NotNull(@object: basisPath);
        Assert.Equal(
            actual: note,
            expected: string.Empty
        );

        // The written file is still a delta: it names its basis and omits what it inherits.
        var written = ((JsonObject)JsonNode.Parse(json: File.ReadAllText(path: deltaPath))!);

        Assert.Equal(
            expected: "basis.world.json",
            actual: written[propertyName: WorldDocumentBasis.BasisMemberName]!.GetValue<string>()
        );
        Assert.False(condition: written.ContainsKey(propertyName: "kits"));

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: deltaPath,
                definition: out var reloaded,
                contentHash: out _,
                reason: out var reloadReason
            ),
            userMessage: reloadReason
        );
        Assert.Equal(
            expected: 7.25f,
            actual: reloaded!.Motion.MoveSpeed
        );
        Assert.Equal(
            expected: WorldDefinitionSerialization.Serialize(definition: retuned),
            actual: WorldDefinitionSerialization.Serialize(definition: reloaded)
        );
    }
    [Fact]
    public void StaleTombstone_RefusesByName() {
        using var files = new TempWorldDirectory();

        files.WriteFlatDocument(name: "basis.world.json");

        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """
            {
              "basis": "basis.world.json",
              "spawnPoints": [ { "id": "seat-9", "$drop": true } ]
            }
            """
        );

        Assert.False(condition: WorldDefinitionFileSource.TryLoad(
            path: deltaPath,
            definition: out _,
            contentHash: out _,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "tombstone"
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "seat-9"
        );
    }
    [Fact]
    public void ThreeLinkChain_ComposesInResolutionOrder_AndEditingTheDeepestLinkMovesTheHash() {
        using var files = new TempWorldDirectory();

        files.WriteFlatDocument(name: "root-basis.world.json");
        files.WriteText(
            name: "mid.world.json",
            text: /*lang=json*/ """
            { "basis": "root-basis.world.json", "motion": { "moveSpeed": 5.5 } }
            """
        );
        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """
            { "basis": "mid.world.json", "motion": { "turnSpeed": 3.25 } }
            """
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: deltaPath,
                definition: out var composed,
                contentHash: out var before,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 5.5f,
            actual: composed!.Motion.MoveSpeed
        );
        Assert.Equal(
            expected: 3.25f,
            actual: composed.Motion.TurnSpeed
        );

        // Mutation falsifier: a parse-invisible edit to the DEEPEST link (not the tip, not the middle) still moves
        // the pin — proving every chain link's own bytes participate in the fold, not just the two nearest the root.
        File.AppendAllText(
            path: Path.Combine(
                path1: files.RootPath,
                path2: "root-basis.world.json"
            ),
            contents: "\n"
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
}
