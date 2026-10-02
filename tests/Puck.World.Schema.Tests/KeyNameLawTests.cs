using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Presentation values use the document's one name walker, with independent clock and state namespaces.</summary>
public sealed class KeyNameLawTests {
    [Fact]
    public void An_admitted_position_component_qualifies_its_state_and_clock_in_independent_namespaces() {
        var module = JsonNode.Parse("""
            {
              "schema": "puck.world.definition.v1",
              "state": { "world": [{ "name": "day", "kind": "Int", "value": 3 }] },
              "timeline": { "clocks": [{ "name": "phase", "periodSeconds": 8 }] },
              "render": { "lighting": { "lights": [{ "$type": "point", "name": "lamp", "position": [
                "state.day", {"clock":"phase","keys":[{"at":0,"value":"state.phase"},{"at":4,"value":2}]}, 0
              ] }] } }
            }
            """)!.AsObject();

        Assert.True(condition: WorldModuleNamespace.TryApply(alias: "a", module: module, reason: out var reason), userMessage: reason);
        var position = module["render"]!["lighting"]!["lights"]![0]!["position"]!;

        Assert.Equal("state.a$day", position[0]!.GetValue<string>());
        Assert.Equal("a$phase", position[1]!["clock"]!.GetValue<string>());
        Assert.Equal("state.phase", position[1]!["keys"]![0]!["value"]!.GetValue<string>());
        module["state"]!["world"]!.AsArray().Add(item: JsonNode.Parse("""{"name":"phase","kind":"Int","value":1}"""));
        module["timeline"]!["clocks"]!.AsArray().Add(item: JsonNode.Parse("""{"name":"day","periodSeconds":3}"""));
        var definition = module.Deserialize(jsonTypeInfo: WorldJsonContext.Default.WorldDefinition)!;

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out reason), userMessage: reason);
        var point = Assert.IsType<WorldRenderLight.Point>(@object: Assert.Single(collection: definition.Render.Lighting!.Lights!));

        Assert.Equal(new System.Numerics.Vector3(x: 3f, y: 1f, z: 0f), new WorldValueResolver(definition, default).Vector(point.Position!.Value, default));
    }
    [Fact]
    public void Aliasing_preserves_same_spelled_host_state_and_host_clock_references() {
        var module = JsonNode.Parse("""
            {
              "state": { "world": [{ "name": "energy", "kind": "Int", "value": 1 }] },
              "timeline": { "clocks": [
                { "name": "day", "periodSeconds": 8 },
                { "name": "linked", "state": "day" },
                { "name": "nested", "phase": { "clock": "day", "keys": [{ "at": 0, "value": 0 }, { "at": 4, "value": 1 }] } }
              ] },
              "theme": { "color": { "scrimPanel": {
                "alpha": { "clock": "day", "keys": [{ "at": 0, "value": "state.day" }, { "at": 4, "value": "state.energy" }] }
              }, "accent": { "clock": "energy", "keys": [{ "at": 0, "value": "#000000" }, { "at": 0.5, "value": "#FFFFFF" }] } } }
            }
            """)!.AsObject();

        Assert.True(condition: WorldModuleNamespace.TryApply(alias: "a", module: module, reason: out var reason), userMessage: reason);
        Assert.Equal("a$energy", module["state"]!["world"]![0]!["name"]!.GetValue<string>());
        var clocks = module["timeline"]!["clocks"]!;

        Assert.Equal("a$day", clocks[0]!["name"]!.GetValue<string>());
        Assert.Equal("day", clocks[1]!["state"]!.GetValue<string>());
        Assert.Equal("a$day", clocks[2]!["phase"]!["clock"]!.GetValue<string>());
        var alpha = module["theme"]!["color"]!["scrimPanel"]!["alpha"]!;

        Assert.Equal("a$day", alpha["clock"]!.GetValue<string>());
        Assert.Equal("state.day", alpha["keys"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("state.a$energy", alpha["keys"]![1]!["value"]!.GetValue<string>());
        Assert.Equal("energy", module["theme"]!["color"]!["accent"]!["clock"]!.GetValue<string>());
    }
    [Fact]
    public void Section_key_values_follow_the_named_rows_model_type_and_leave_local_identity_alone() {
        var module = JsonNode.Parse("""
            {
              "state": { "world": [{ "name": "tint", "kind": "Int", "value": 1 }] },
              "timeline": { "clocks": [{ "name": "day", "periodSeconds": 8 }] },
              "render": { "lighting": {
                "lights": [{ "$type": "hemisphere", "name": "tint", "color": "#000000" }],
                "keys": { "clock": "day", "keys": [
                  { "at": 0, "lights": { "tint": { "color": "state.tint" } } },
                  { "at": 4, "lights": { "tint": { "color": "#FFFFFF" } } }
                ] }
              } }
            }
            """)!.AsObject();

        Assert.True(condition: WorldModuleNamespace.TryApply(alias: "a", module: module, reason: out var reason), userMessage: reason);
        var lighting = module["render"]!["lighting"]!;

        Assert.Equal("tint", lighting["lights"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("a$day", lighting["keys"]!["clock"]!.GetValue<string>());
        Assert.Equal("state.a$tint", lighting["keys"]!["keys"]![0]!["lights"]!["tint"]!["color"]!.GetValue<string>());
    }
    [Fact]
    public void A_vector_component_and_its_key_values_report_their_original_array_and_object_slots() {
        var vector = JsonNode.Parse("""["state.x", {"clock":"day","keys":[{"at":0,"value":"state.y"},{"at":4,"value":2}]}]""")!;
        var sites = new List<string>();

        WorldModuleNamespace.Visit(node: vector, type: typeof(BindableVector2), visitor: (holder, member, value, field, _) => {
            if ((value is not JsonValue leaf) || !leaf.TryGetValue<string>(value: out var text)) { return; }
            sites.Add(item: $"{field.Kind}:{value.GetPath()}");
            var rewritten = WorldModuleNamespace.Rewrite(text, field.Role,
                new Dictionary<string, string> { ["x"] = "a$x", ["y"] = "a$y", ["day"] = "a$day" });

            WorldModuleNamespace.SetSiteValue(holder: holder, jsonName: member, value: JsonValue.Create(rewritten));
        });
        Assert.Equal(actual: sites, expected: ["State:$[0]", "Clock:$[1].clock", "State:$[1].keys[0].value"]);
        Assert.Equal("state.a$x", vector[0]!.GetValue<string>());
        Assert.Equal("a$day", vector[1]!["clock"]!.GetValue<string>());
        Assert.Equal("state.a$y", vector[1]!["keys"]![0]!["value"]!.GetValue<string>());
        var parsed = vector.Deserialize<BindableVector2>(options: WorldJsonContext.Default.Options);

        Assert.NotNull(@object: parsed.Y.Keys);
    }
    [Fact]
    public void A_compiler_qualified_clock_is_admitted_after_composition() {
        var definition = new WorldDefinition(TimelineRaw: new WorldTimelineSection(Clocks: [new WorldClock("a$day", PeriodSeconds: 8)]));

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
    }
}
