using System.Text.Json.Nodes;

using Xunit;

using Puck.World.Authoring.Sculpting;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="SculptPatch"/> upserts/removes rows by key (never by array index), preserves every untouched
/// member and the document's key order, and its member operations (<see cref="SculptPatch.SetMember"/>/
/// <see cref="SculptPatch.RemoveMember"/>) navigate a dotted path with optional <c>[field=value]</c> selectors —
/// including through an array it must itself descend into by key, never by position.
/// </summary>
public sealed class SculptPatchLawTests {
    private static JsonObject Document() => (JsonNode.Parse(json: """
        {
          "kits": [{"name":"a","value":1},{"name":"b","value":2}],
          "render": {"shadows": true, "sky": {"layers": []}},
          "looks": {"rows": [{"name":"moth","motion":{"poses":{"walk":"x"},"cues":["old"]}}]}
        }
        """)!.AsObject());

    /// <summary>Upserting a row sharing an existing key REPLACES it in place, leaving sibling rows and their order
    /// untouched.</summary>
    [Fact]
    public void UpsertRowReplacesInPlacePreservingOrderAndSiblings() {
        var document = Document();
        var patch = new SculptPatch().UpsertRow(
            keyField: "name",
            keyValue: "a",
            row: JsonNode.Parse(json: """{"name":"a","value":99}""")!,
            sectionPath: "kits"
        );
        var results = patch.Apply(document: document);

        Assert.Single(collection: results);
        Assert.Equal(expected: "replaced", actual: results[0].Verdict);

        var kits = document["kits"]!.AsArray();

        Assert.Equal(expected: 2, actual: kits.Count);
        Assert.Equal(expected: 99, actual: kits[0]!["value"]!.GetValue<int>());
        Assert.Equal(expected: "b", actual: kits[1]!["name"]!.GetValue<string>());
        Assert.Equal(expected: 2, actual: kits[1]!["value"]!.GetValue<int>());
    }
    /// <summary>Upserting a row with a NEW key appends it, keeping every existing row untouched.</summary>
    [Fact]
    public void UpsertRowWithNewKeyAppends() {
        var document = Document();
        var patch = new SculptPatch().UpsertRow(
            keyField: "name",
            keyValue: "c",
            row: JsonNode.Parse(json: """{"name":"c","value":3}""")!,
            sectionPath: "kits"
        );
        var results = patch.Apply(document: document);

        Assert.Equal(expected: "inserted", actual: results[0].Verdict);

        var kits = document["kits"]!.AsArray();

        Assert.Equal(expected: 3, actual: kits.Count);
        Assert.Equal(expected: "c", actual: kits[2]!["name"]!.GetValue<string>());
        // The first two rows are exactly what they started as — REPLACE would have caught a byte diff here.
        Assert.Equal(expected: 1, actual: kits[0]!["value"]!.GetValue<int>());
        Assert.Equal(expected: 2, actual: kits[1]!["value"]!.GetValue<int>());
    }
    /// <summary>Removing a row by key drops only that row.</summary>
    [Fact]
    public void RemoveRowDropsOnlyTheKeyedRow() {
        var document = Document();
        var results = new SculptPatch().RemoveRow(
            keyField: "name",
            keyValue: "a",
            sectionPath: "kits"
        ).Apply(document: document);

        Assert.Equal(expected: "removed", actual: results[0].Verdict);

        var kits = document["kits"]!.AsArray();

        Assert.Single(collection: kits);
        Assert.Equal(expected: "b", actual: kits[0]!["name"]!.GetValue<string>());
    }
    /// <summary>Removing a row whose key is absent is a no-op, reported as "unset" — never an error.</summary>
    [Fact]
    public void RemoveRowOnAbsentKeyIsUnset() {
        var document = Document();
        var results = new SculptPatch().RemoveRow(
            keyField: "name",
            keyValue: "nope",
            sectionPath: "kits"
        ).Apply(document: document);

        Assert.Equal(expected: "unset", actual: results[0].Verdict);
        Assert.Equal(expected: 2, actual: document["kits"]!.AsArray().Count);
    }
    /// <summary>Setting a keyless sub-member preserves every sibling member of the SAME parent object.</summary>
    [Fact]
    public void SetMemberOnKeylessSectionPreservesSiblings() {
        var document = Document();
        var results = new SculptPatch().SetMember(
            path: "render.lighting",
            value: JsonNode.Parse(json: """{"lights":[]}""")
        ).Apply(document: document);

        Assert.Equal(expected: "set", actual: results[0].Verdict);
        Assert.Equal(expected: "render", actual: results[0].Section);
        Assert.Null(@object: results[0].KeyField);
        Assert.True(condition: document["render"]!["shadows"]!.GetValue<bool>());
        Assert.NotNull(@object: document["render"]!["lighting"]);
        // sky.layers, a member two levels deeper, is untouched by an edit to a DIFFERENT sibling.
        Assert.NotNull(@object: document["render"]!["sky"]);
    }
    /// <summary>Setting a member through a <c>[field=value]</c> selector descends the keyed array by KEY, never by
    /// position, and preserves every other member of the selected row.</summary>
    [Fact]
    public void SetMemberThroughSelectorDescendsByKeyNotIndex() {
        var document = Document();
        var results = new SculptPatch().SetMember(
            path: "looks.rows[name=moth].motion.poses",
            value: JsonNode.Parse(json: """{"blink":"y"}""")
        ).Apply(document: document);

        Assert.Equal(expected: "looks.rows", actual: results[0].Section);
        Assert.Equal(expected: "name", actual: results[0].KeyField);
        Assert.Equal(expected: "moth", actual: results[0].KeyValue);

        var moth = document["looks"]!["rows"]![0]!;

        Assert.Equal(expected: "y", actual: moth["motion"]!["poses"]!["blink"]!.GetValue<string>());
        // "cues" — a DIFFERENT member of the same motion object — is untouched.
        Assert.NotNull(@object: moth["motion"]!["cues"]);
    }
    /// <summary>Removing a member through the same selector grammar removes only that member.</summary>
    [Fact]
    public void RemoveMemberThroughSelectorRemovesOnlyThatMember() {
        var document = Document();
        var results = new SculptPatch().RemoveMember(path: "looks.rows[name=moth].motion.cues").Apply(document: document);

        Assert.Equal(expected: "removed", actual: results[0].Verdict);

        var moth = document["looks"]!["rows"]![0]!;

        Assert.Null(@object: moth["motion"]!["cues"]);
        Assert.NotNull(@object: moth["motion"]!["poses"]);
    }
    /// <summary>Removing an already-absent member is reported "unset", not an error, even when an ancestor object
    /// is entirely missing.</summary>
    [Fact]
    public void RemoveMemberOnAbsentAncestorIsUnset() {
        var document = Document();
        var results = new SculptPatch().RemoveMember(path: "render.missingSection.nested").Apply(document: document);

        Assert.Equal(expected: "unset", actual: results[0].Verdict);
    }
    /// <summary>Every distinct row several operations touch is reported once, in first-touched order.</summary>
    [Fact]
    public void TouchedRowsDeduplicatesAndPreservesFirstTouchedOrder() {
        var document = Document();
        var patch = new SculptPatch()
            .SetMember(path: "render.lighting", value: JsonNode.Parse(json: "{}"))
            .UpsertRow(sectionPath: "kits", keyField: "name", keyValue: "a", row: JsonNode.Parse(json: """{"name":"a","value":5}""")!)
            .SetMember(path: "render.sky", value: JsonNode.Parse(json: "{}"));
        var results = patch.Apply(document: document);
        var touched = SculptPatch.TouchedRows(results: results);

        Assert.Equal(expected: 2, actual: touched.Count);
        Assert.Equal(expected: "render", actual: touched[0].Section);
        Assert.Equal(expected: "kits", actual: touched[1].Section);
        Assert.False(condition: touched[0].Removed);
    }
    /// <summary>A member removal modifies the row it descends into — the row is still there, one member fewer — so
    /// the touched row reads as re-upsert, never as removed; only a <see cref="SculptPatch.RemoveRow"/> that found
    /// its row retires it (the control).</summary>
    [Fact]
    public void RemoveMemberOnKeyedRowIsAModificationNotARowRemoval() {
        var document = Document();
        var touched = SculptPatch.TouchedRows(results: new SculptPatch()
            .RemoveMember(path: "looks.rows[name=moth].motion.cues")
            .Apply(document: document));

        Assert.Single(collection: touched);
        Assert.Equal(expected: "looks.rows", actual: touched[0].Section);
        Assert.Equal(expected: "moth", actual: touched[0].KeyValue);
        Assert.False(condition: touched[0].Removed);
        Assert.NotNull(@object: document["looks"]!["rows"]![0]);

        var removed = SculptPatch.TouchedRows(results: new SculptPatch()
            .RemoveRow(sectionPath: "kits", keyField: "name", keyValue: "a")
            .Apply(document: Document()));

        Assert.True(condition: removed[0].Removed);
    }
    /// <summary>Removing a member of a keyless section touches that section for a re-set of its remaining members,
    /// never for a whole-section removal (a keyless section has no remove).</summary>
    [Fact]
    public void RemoveMemberOnKeylessSectionTouchesItForReSet() {
        var document = Document();
        var touched = SculptPatch.TouchedRows(results: new SculptPatch()
            .RemoveMember(path: "render.shadows")
            .Apply(document: document));

        Assert.Single(collection: touched);
        Assert.Equal(expected: "render", actual: touched[0].Section);
        Assert.Null(@object: touched[0].KeyField);
        Assert.False(condition: touched[0].Removed);
        Assert.Null(@object: document["render"]!["shadows"]);
        Assert.NotNull(@object: document["render"]!["sky"]);
    }
    /// <summary>A row upserted into a section whose object does not exist yet materializes every intermediate as an
    /// object and only the final segment as the array — <c>state.world</c> on <c>{}</c> yields
    /// <c>{"state":{"world":[row]}}</c>, and a second upsert lands beside the first.</summary>
    [Fact]
    public void UpsertRowIntoAbsentSectionCreatesObjectIntermediates() {
        var document = new JsonObject();
        var results = new SculptPatch()
            .UpsertRow(sectionPath: "state.world", keyField: "name", keyValue: "a", row: JsonNode.Parse(json: """{"name":"a"}""")!)
            .UpsertRow(sectionPath: "state.world", keyField: "name", keyValue: "b", row: JsonNode.Parse(json: """{"name":"b"}""")!)
            .Apply(document: document);

        Assert.Equal(expected: "inserted", actual: results[0].Verdict);
        Assert.Equal(expected: "inserted", actual: results[1].Verdict);
        Assert.IsType<JsonObject>(@object: document["state"]);
        Assert.Equal(expected: 2, actual: document["state"]!["world"]!.AsArray().Count);
        Assert.Equal(expected: """{"state":{"world":[{"name":"a"},{"name":"b"}]}}""", actual: document.ToJsonString());
    }
    /// <summary>A section path that descends through a non-object (an array where an object belongs) faults by name
    /// rather than silently landing the row somewhere else.</summary>
    [Fact]
    public void UpsertRowThroughNonObjectIntermediateFaultsByName() {
        var document = JsonNode.Parse(json: """{"state":[1]}""")!.AsObject();
        var patch = new SculptPatch().UpsertRow(sectionPath: "state.world", keyField: "name", keyValue: "a", row: new JsonObject());
        var fault = Assert.Throws<InvalidOperationException>(testCode: () => patch.Apply(document: document));

        Assert.Contains(expectedSubstring: "state.world", actualString: fault.Message, comparisonType: StringComparison.Ordinal);
    }
}
