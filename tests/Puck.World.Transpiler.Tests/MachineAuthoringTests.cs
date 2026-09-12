using System.Text.Json.Nodes;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class MachineAuthoringTests {
    [Fact]
    public void NamedDevicesUseOrdinaryBlocksArraysAndConstants() {
        const string source = """
            schema: "puck.world.def.v1"
            let deviceModel = "cgb"
            let cartridge = "content/demo.cartridge.json"
            machines [
                {
                    name: "brook"
                    engine: "gaming-brick"
                    configuration {
                        schema: "puck.gaming-brick.config.v1"
                        model: deviceModel
                        content { path: cartridge }
                    }
                    running: false
                }
            ]
            """;
        var lowered = WorldDocumentEmitter.Lower(PuckParser.ParseDocument(source));
        var definition = WorldDefinitionSerialization.Deserialize(System.Text.Encoding.UTF8.GetBytes(lowered.ToJsonString()));
        var machine = Assert.Single(definition.Machines);
        Assert.Equal("brook", machine.Name);
        Assert.False(machine.Running);
        Assert.Equal("cgb", machine.Configuration.GetProperty("model").GetString());
        Assert.Equal("content/demo.cartridge.json", machine.Configuration.GetProperty("content").GetProperty("path").GetString());
    }

    [Fact]
    public void TwoAliasesMintIndependentMachineIdentities() {
        var source = JsonNode.Parse("""
            {"machines":[{"name":"brook","engine":"gaming-brick","configuration":{"schema":"puck.gaming-brick.config.v1"}}]}
            """)!.AsObject();
        var left = source.DeepClone().AsObject();
        var right = source.DeepClone().AsObject();
        Assert.True(WorldModuleNamespace.TryApply(left, "left", out var leftReason), leftReason);
        Assert.True(WorldModuleNamespace.TryApply(right, "right", out var rightReason), rightReason);
        Assert.Equal("left_brook", left["machines"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("right_brook", right["machines"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("gaming-brick", left["machines"]![0]!["engine"]!.GetValue<string>());
    }
}
