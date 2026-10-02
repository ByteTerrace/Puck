using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Each converter-backed keyed value follows its module's clock declaration; a host clock stays unqualified.</summary>
public sealed class KeyedClockNamespaceLawTests {
    [Fact]
    public void Every_keyed_value_qualifies_its_clock_when_its_module_is_instanced() {
        var module = JsonNode.Parse(json: """
            {
              "timeline": { "clocks": [ { "name": "day", "periodSeconds": 100 } ] },
              "render": {
                "lighting": { "lights": [
                  { "$type": "directional",
                    "color": { "clock": "day", "keys": [ { "at": 0, "value": "#FFFFFF" } ] },
                    "direction": { "clock": "day", "keys": [ { "at": 0, "value": [0, 1, 0] } ] },
                    "angularRadius": { "clock": "day", "keys": [ { "at": 0, "value": 0.01 } ] }
                  },
                  { "$type": "point", "position": { "clock": "day", "keys": [ { "at": 0, "value": [1, 2, 3] } ] } }
                ] },
                "sky": { "layers": [
                  { "$type": "fog", "density": { "clock": "day", "keys": [ { "at": 0, "value": 0.01 } ] } },
                  { "$type": "clouds", "drift": { "clock": "day", "keys": [ { "at": 0, "value": [1, 2] } ] },
                    "coverage": { "clock": "host", "keys": [ { "at": 0, "value": 0.5 } ] } }
                ] }
              }
            }
            """)!.AsObject();

        Assert.True(condition: WorldModuleNamespace.TryApply(alias: "room", module: module, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: "room$day", actual: module["timeline"]!["clocks"]![0]!["name"]!.GetValue<string>());

        var lights = module["render"]!["lighting"]!["lights"]!;

        foreach (var field in new[] { "color", "direction", "angularRadius" }) {
            Assert.Equal(expected: "room$day", actual: lights[0]![field]!["clock"]!.GetValue<string>());
        }

        Assert.Equal(expected: "room$day", actual: lights[1]!["position"]!["clock"]!.GetValue<string>());
        var layers = module["render"]!["sky"]!["layers"]!;

        Assert.Equal(expected: "room$day", actual: layers[0]!["density"]!["clock"]!.GetValue<string>());
        Assert.Equal(expected: "room$day", actual: layers[1]!["drift"]!["clock"]!.GetValue<string>());
        Assert.Equal(expected: "host", actual: layers[1]!["coverage"]!["clock"]!.GetValue<string>());
    }
}
