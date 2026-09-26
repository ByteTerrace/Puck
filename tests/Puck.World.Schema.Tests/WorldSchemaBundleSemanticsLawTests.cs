using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldSchemaBundleSemanticsLawTests {
    private static readonly Lazy<WorldSchema.SplitSchema> Split = new(valueFactory: () => WorldSchema.Export(postProcessPackages: []));

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

        var bundle = SchemaVerdicts.Build(schema: WorldSchema.Bundle(split: split));

        Assert.False(condition: bundle.Admits(instance: JsonNode.Parse(json: """{"first":{"value":null}}""")));
        Assert.True(condition: bundle.Admits(instance: JsonNode.Parse(json: """{"second":{"value":null}}""")));
    }
    [Fact]
    public void BareAndDiscriminatedStateConditionsKeepSeparateContracts() {
        var bundle = WorldSchema.Bundle(split: Split.Value);
        var bare = "WorldPlacementResponseConditionStateConditionBare";
        var arm = "WorldPlacementResponseConditionStateCondition";

        Assert.Null(@object: bundle["$defs"]![bare]!["properties"]!["$type"]);
        Assert.Equal(
            expected: "state",
            actual: ((string?)bundle["$defs"]![arm]!["properties"]!["$type"]!["const"])
        );
        var isolated = new JsonObject { ["$defs"] = bundle["$defs"]!.DeepClone(), ["$ref"] = $"#/$defs/{bare}" };
        var schema = SchemaVerdicts.Build(schema: isolated);

        Assert.True(condition: schema.Admits(instance: JsonNode.Parse(json: """{"state":"score","comparison":"Equal","value":1}""")));
        Assert.False(condition: schema.Admits(instance: JsonNode.Parse(json: """{"$type":"state","state":"score","comparison":"Equal"}""")));
        isolated["$ref"] = $"#/$defs/{arm}";
        schema = SchemaVerdicts.Build(schema: isolated);
        Assert.True(condition: schema.Admits(instance: JsonNode.Parse(json: """{"$type":"state","state":"score","comparison":"Equal"}""")));
        Assert.False(condition: schema.Admits(instance: JsonNode.Parse(json: """{"$type":"field","state":"score","comparison":"Equal"}""")));
    }
    [Fact]
    public void BareReferencesKeepTheTargetsNullAdmission() {
        var split = Split.Value;
        var bodies = ((JsonObject)split.Sections.Single(predicate: section => (section.Name == "bodies")).Node);
        var distribution = bodies["properties"]!["distribution"]!;

        Assert.Single(collection: distribution.AsObject());
        Assert.Equal(
            expected: "./common.schema.json#/$defs/WorldDistribution",
            actual: ((string?)distribution["$ref"])
        );
        var originalRoot = split.Common.DeepClone().AsObject();

        originalRoot["$ref"] = "#/$defs/WorldDistribution";
        var original = SchemaVerdicts.Build(schema: originalRoot);

        Assert.True(condition: original.Admits(instance: null));

        var bundle = SchemaVerdicts.Build(schema: WorldSchema.Bundle(split: split));

        Assert.True(condition: bundle.Admits(instance: JsonNode.Parse(json: """{"bodies":{"distribution":null}}""")));
    }
    [Fact]
    public void BundlingDoesNotRewriteObjectLiteralsInPostProcessSchemas() {
        var config = JsonNode.Parse(json: """
            {"type":"object","enum":[{"title":"Night","exposure":1}],
             "default":{"title":"Night","exposure":1}}
            """)!.AsObject();
        var split = WorldSchema.Export(postProcessPackages: [new WorldSchema.PostProcessPackageSchema(
                ConfigSchema: config,
                Package: "literal-config"
            )]);
        var bundle = SchemaVerdicts.Build(schema: WorldSchema.Bundle(split: split));

        Assert.True(condition: bundle.Admits(instance: JsonNode.Parse(json: """
            {"views":{"post":[{"name":"night","package":"literal-config","config":{"title":"Night","exposure":1}}]}}
            """)));
    }
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
        var split = new WorldSchema.SplitSchema(
            Root: new JsonObject { ["properties"] = properties.DeepClone() },
            Sections: sections,
            Common: common
        );
        var original = SchemaVerdicts.Build(schema: originalRoot);
        var bundled = SchemaVerdicts.Build(schema: WorldSchema.Bundle(split: split));
        var failures = new List<string>();

        foreach (var (name, _) in common["$defs"]!.AsObject()) {
            foreach (var text in new[] { "null", "0", "true", "\"\"", "[]", "{}" }) {
                var instance = new JsonObject { [name] = JsonNode.Parse(json: text) };

                if (original.Admits(instance: instance) != bundled.Admits(instance: instance)) {
                    failures.Add(item: $"{name}: {text}");
                }
            }
        }
        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(
                separator: "\n",
                values: failures
            )
        );
    }
    [InlineData("""{"title":"Choice","type":["object","null"],"anyOf":[{"type":"object"}]}""", "null", false)]
    [InlineData("""{"title":"Choice","type":"string","enum":["null","value"]}""", "null", false)]
    [InlineData("""{"title":"Choice","type":"null"}""", "42", false)]
    [InlineData("""{"title":"Choice","type":"null"}""", "null", true)]
    [InlineData("""{"title":"Choice","enum":[null]}""", "null", true)]
    [InlineData("""{"title":"Choice","enum":[null]}""", "42", false)]
    [InlineData("""{"title":"Choice","oneOf":[{"type":"null"},{}]}""", "null", false)]
    [Theory]
    public void NullLiftingPreservesTheWholeConstraint(string schema, string instance, bool expected) {
        var split = new WorldSchema.SplitSchema(
            Root: JsonNode.Parse(json: """{"type":"object","properties":{"choice":{}}}""")!.AsObject(),
            Sections: [("choice", JsonNode.Parse(json: schema)!)],
            Common: new JsonObject { ["$defs"] = new JsonObject() }
        );
        var value = JsonNode.Parse(json: instance);

        Assert.Equal(
            expected: expected,
            actual: SchemaVerdicts.Build(schema: JsonNode.Parse(json: schema)!).Admits(instance: value)
        );
        var bundle = SchemaVerdicts.Build(schema: WorldSchema.Bundle(split: split));

        Assert.Equal(
            expected: expected,
            actual: bundle.Admits(instance: new JsonObject { ["choice"] = value })
        );
    }
    [Fact]
    public void ReferenceSiblingsStillConstrainTheTarget() {
        var split = new WorldSchema.SplitSchema(
            Root: JsonNode.Parse(json: """{"type":"object","properties":{"choice":{}}}""")!.AsObject(),
            Sections: [("choice", JsonNode.Parse(json: """{"$ref":"./common.schema.json#/$defs/Text","anyOf":[{"type":"integer"}]}""")!)],
            Common: JsonNode.Parse(json: """{"$defs":{"Text":{"type":"string"}}}""")!.AsObject()
        );
        var schema = SchemaVerdicts.Build(schema: WorldSchema.Bundle(split: split));

        Assert.False(condition: schema.Admits(instance: JsonNode.Parse(json: """{"choice":42}""")));
        Assert.False(condition: schema.Admits(instance: JsonNode.Parse(json: """{"choice":"text"}""")));
    }
}
