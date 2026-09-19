using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class MachineAuthoringTests {
    [Fact]
    public void NamedDevicesUseOrdinaryBlocksArraysAndConstants() {
        const string Source = """
            schema: "puck.world.definition.v1"
            let deviceModel = "cgb"
            let cartridge = "content/demo.cartridge.json"
            machines [
                {
                    name: "brook"
                    engine: "gaming-brick"
                    configuration {
                        schema: "puck.gaming-brick.configuration.v1"
                        model: deviceModel
                        content { path: cartridge }
                    }
                    running: false
                }
            ]
            """;
        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: Source
        ).RequireJson();
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: lowered.ToJsonString()));
        var machine = Assert.Single(collection: definition.Machines);

        Assert.Equal(
            "brook",
            machine.Name
        );
        Assert.False(condition: machine.Running);
        Assert.Equal(
            "cgb",
            machine.Configuration.GetProperty(propertyName: "model").GetString()
        );
        Assert.Equal(
            "content/demo.cartridge.json",
            machine.Configuration.GetProperty(propertyName: "content").GetProperty(propertyName: "path").GetString()
        );
    }
    [Fact]
    public void TwoAliasesMintIndependentMachineIdentities() {
        var source = JsonNode.Parse("""
            {"machines":[{"name":"brook","engine":"gaming-brick","configuration":{"schema":"puck.gaming-brick.configuration.v1"}}]}
            """)!.AsObject();
        var left = source.DeepClone().AsObject();
        var right = source.DeepClone().AsObject();

        Assert.True(
            condition: WorldModuleNamespace.TryApply(
                alias: "left",
                module: left,
                reason: out var leftReason
            ),
            userMessage: leftReason
        );
        Assert.True(
            condition: WorldModuleNamespace.TryApply(
                alias: "right",
                module: right,
                reason: out var rightReason
            ),
            userMessage: rightReason
        );
        Assert.Equal(
            "left_brook",
            left["machines"]![0]!["name"]!.GetValue<string>()
        );
        Assert.Equal(
            "right_brook",
            right["machines"]![0]!["name"]!.GetValue<string>()
        );
        Assert.Equal(
            "gaming-brick",
            left["machines"]![0]!["engine"]!.GetValue<string>()
        );
    }
}
