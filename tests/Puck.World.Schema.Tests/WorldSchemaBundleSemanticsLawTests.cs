using System.Text.Json.Nodes;

using Json.Schema;

using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldSchemaBundleSemanticsLawTests {
    private static readonly Lazy<WorldSchema.SplitSchema> Split = new(valueFactory: () => WorldSchema.Export(postRenderExtensions: []));

    [Fact]
    public void EveryCommonDefinitionKeepsItsValidationAcrossJsonKinds() {
        var common = Split.Value.Common;
        var originalRoot = common.DeepClone().AsObject();
        var properties = new JsonObject();
        var sections = new List<(string Name, JsonNode Node)>();

        foreach (var (name, _) in common["$defs"]!.AsObject()) {
            properties[name] = new JsonObject { ["$ref"] = $"#/$defs/{name}" };
            sections.Add(item: (name, new JsonObject { ["$ref"] = $"./common.schema.json#/$defs/{name}" }));
        }
        originalRoot["properties"] = properties;
        var split = new WorldSchema.SplitSchema(Root: new JsonObject { ["properties"] = properties.DeepClone() }, Sections: sections, Common: common);
        var original = JsonSchema.FromText(jsonText: originalRoot.ToJsonString());
        var bundled = JsonSchema.FromText(jsonText: WorldSchema.Bundle(split: split).ToJsonString());
        var failures = new List<string>();

        foreach (var (name, _) in common["$defs"]!.AsObject()) {
            foreach (var text in new[] { "null", "0", "true", "\"\"", "[]", "{}" }) {
                var instance = new JsonObject { [name] = JsonNode.Parse(json: text) };
                if (original.Evaluate(root: instance).IsValid != bundled.Evaluate(root: instance).IsValid) {
                    failures.Add(item: $"{name}: {text}");
                }
            }
        }
        Assert.True(condition: failures.Count == 0, userMessage: string.Join(separator: "\n", values: failures));
    }

    [Fact]
    public void BareReferencesKeepTheTargetsNullAdmission() {
        var split = Split.Value;
        var bodies = (JsonObject)split.Sections.Single(predicate: section => section.Name == "bodies").Node;
        var distribution = bodies["properties"]!["distribution"]!;

        Assert.Single(collection: distribution.AsObject());
        Assert.Equal(expected: "./common.schema.json#/$defs/WorldDistribution", actual: (string?)distribution["$ref"]);
        var originalRoot = split.Common.DeepClone().AsObject();
        originalRoot["$ref"] = "#/$defs/WorldDistribution";
        var original = JsonSchema.FromText(jsonText: originalRoot.ToJsonString());
        Assert.True(condition: original.Evaluate(root: null).IsValid);

        var bundle = JsonSchema.FromText(jsonText: WorldSchema.Bundle(split: split).ToJsonString());
        Assert.True(condition: bundle.Evaluate(root: JsonNode.Parse(json: """{"bodies":{"distribution":null}}""")).IsValid);
    }

    [Fact]
    public void BundlingDoesNotRewriteObjectLiteralsInExtensionSchemas() {
        var config = JsonNode.Parse(json: """
            {"type":"object","enum":[{"title":"Night","exposure":1}],
             "default":{"title":"Night","exposure":1}}
            """)!.AsObject();
        var split = WorldSchema.Export(postRenderExtensions: [new WorldSchema.PostRenderExtensionSchema(Id: "literal-config", ConfigSchema: config)]);
        var bundle = JsonSchema.FromText(jsonText: WorldSchema.Bundle(split: split).ToJsonString());

        Assert.True(condition: bundle.Evaluate(root: JsonNode.Parse(json: """
            {"render":{"extensions":[{"id":"literal-config","config":{"title":"Night","exposure":1}}]}}
            """)).IsValid);
    }

    [Fact]
    public void ASharedTitleCannotEraseNestedValidationDifferences() {
        var split = new WorldSchema.SplitSchema(
            Root: JsonNode.Parse(json: """{"type":"object","properties":{"first":{},"second":{}}}""")!.AsObject(),
            Sections: [
                ("first", JsonNode.Parse(json: """{"title":"Example","type":"object","properties":{"value":{"type":"string"}}}""")!),
                ("second", JsonNode.Parse(json: """{"title":"Example","type":"object","properties":{"value":{"type":["string","null"]}}}""")!),
            ],
            Common: new JsonObject { ["$defs"] = new JsonObject() }
        );

        var bundle = JsonSchema.FromText(jsonText: WorldSchema.Bundle(split: split).ToJsonString());
        Assert.False(condition: bundle.Evaluate(root: JsonNode.Parse(json: """{"first":{"value":null}}""")).IsValid);
        Assert.True(condition: bundle.Evaluate(root: JsonNode.Parse(json: """{"second":{"value":null}}""")).IsValid);
    }

    [Theory]
    [InlineData("""{"title":"Choice","type":["object","null"],"anyOf":[{"type":"object"}]}""", "null", false)]
    [InlineData("""{"title":"Choice","type":"string","enum":["null","value"]}""", "null", false)]
    [InlineData("""{"title":"Choice","type":"null"}""", "42", false)]
    [InlineData("""{"title":"Choice","type":"null"}""", "null", true)]
    [InlineData("""{"title":"Choice","enum":[null]}""", "null", true)]
    [InlineData("""{"title":"Choice","enum":[null]}""", "42", false)]
    [InlineData("""{"title":"Choice","oneOf":[{"type":"null"},{}]}""", "null", false)]
    public void NullLiftingPreservesTheWholeConstraint(string schema, string instance, bool expected) {
        var split = new WorldSchema.SplitSchema(
            Root: JsonNode.Parse(json: """{"type":"object","properties":{"choice":{}}}""")!.AsObject(),
            Sections: [("choice", JsonNode.Parse(json: schema)!)],
            Common: new JsonObject { ["$defs"] = new JsonObject() }
        );
        var value = JsonNode.Parse(json: instance);
        Assert.Equal(expected: expected, actual: JsonSchema.FromText(jsonText: schema).Evaluate(root: value).IsValid);
        var bundle = JsonSchema.FromText(jsonText: WorldSchema.Bundle(split: split).ToJsonString());
        Assert.Equal(expected: expected, actual: bundle.Evaluate(root: new JsonObject { ["choice"] = value }).IsValid);
    }

    [Fact]
    public void ReferenceSiblingsStillConstrainTheTarget() {
        var split = new WorldSchema.SplitSchema(
            Root: JsonNode.Parse(json: """{"type":"object","properties":{"choice":{}}}""")!.AsObject(),
            Sections: [("choice", JsonNode.Parse(json: """{"$ref":"./common.schema.json#/$defs/Text","anyOf":[{"type":"integer"}]}""")!)],
            Common: JsonNode.Parse(json: """{"$defs":{"Text":{"type":"string"}}}""")!.AsObject()
        );
        var schema = JsonSchema.FromText(jsonText: WorldSchema.Bundle(split: split).ToJsonString());
        Assert.False(condition: schema.Evaluate(root: JsonNode.Parse(json: """{"choice":42}""")).IsValid);
        Assert.False(condition: schema.Evaluate(root: JsonNode.Parse(json: """{"choice":"text"}""")).IsValid);
    }

    [Fact]
    public void BareAndDiscriminatedStateConditionsKeepSeparateContracts() {
        var bundle = WorldSchema.Bundle(split: Split.Value);
        var bare = "WorldPlacementResponseConditionStateConditionBare";
        var arm = "WorldPlacementResponseConditionStateCondition";
        Assert.Null(@object: bundle["$defs"]![bare]!["properties"]!["$type"]);
        Assert.Equal(expected: "state", actual: (string?)bundle["$defs"]![arm]!["properties"]!["$type"]!["const"]);
        var isolated = new JsonObject { ["$defs"] = bundle["$defs"]!.DeepClone(), ["$ref"] = $"#/$defs/{bare}" };
        var schema = JsonSchema.FromText(jsonText: isolated.ToJsonString());
        Assert.True(condition: schema.Evaluate(root: JsonNode.Parse(json: """{"state":"score","comparison":"Equal","value":1}""")).IsValid);
        Assert.False(condition: schema.Evaluate(root: JsonNode.Parse(json: """{"$type":"state","state":"score","comparison":"Equal"}""")).IsValid);
        isolated["$ref"] = $"#/$defs/{arm}";
        schema = JsonSchema.FromText(jsonText: isolated.ToJsonString());
        Assert.True(condition: schema.Evaluate(root: JsonNode.Parse(json: """{"$type":"state","state":"score","comparison":"Equal"}""")).IsValid);
        Assert.False(condition: schema.Evaluate(root: JsonNode.Parse(json: """{"$type":"field","state":"score","comparison":"Equal"}""")).IsValid);
    }
}
