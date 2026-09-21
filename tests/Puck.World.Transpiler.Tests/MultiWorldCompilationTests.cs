using System.Text.Json.Nodes;
using Puck.Transpiler.Formatting;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class MultiWorldCompilationTests {
    [Fact]
    public void ModuleFamiliesAreEmittedInsideEachWorld() {
        var result = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module room(size) {
              state { world { slot Piece[0..size] = 0 } }
            }
            world small = room(1)
            world large = room(3)
            """, allowMultiple: true);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: "\n", values: result.Diagnostics));
        Assert.Equal(2, result.Worlds[0].Json["state"]!["families"]![0]!["size"]!.GetValue<int>());
        Assert.Equal(4, result.Worlds[1].Json["state"]!["families"]![0]!["size"]!.GetValue<int>());
    }
    [Fact]
    public void AliasedModuleUseEmitsItsFamilies() {
        var result = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module pieces() {
              state { world { slot Piece[0..1] = 0 } }
            }
            use pieces as board()
            """);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: "\n", values: result.Diagnostics));
        Assert.Single(collection: result.Json!["state"]!["families"]!.AsArray());
    }
    [Fact]
    public void WorldsKeepIndependentRowsAndSharedDeclarations() {
        var source = """
            schema: "puck.world.definition.v1"
            record Traveller { tokens: Int = 0 }
            module room(value) { state { world { slot score = value } } }
            world first = room(3)
            world second = room(8)
            """;
        var result = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: source, allowMultiple: true);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: "\n", values: result.Diagnostics));
        Assert.Null(@object: result.Json);
        Assert.Equal(["first", "second"], result.Worlds.Select(static w => w.Name));
        Assert.Equal(3L, result.Worlds[0].Json["state"]!["world"]![0]!["value"]!.GetValue<long>());
        Assert.Equal(8L, result.Worlds[1].Json["state"]!["world"]![0]!["value"]!.GetValue<long>());
        Assert.Equal("Traveller", result.Worlds[1].Json["state"]!["records"]![0]!["name"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(testCode: () => result.RequireJson());
        Assert.False(condition: WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: source).Success);
        var formatted = PuckPrinter.Print(result.Document!);
        var roundTrip = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: formatted, allowMultiple: true);

        Assert.True(condition: roundTrip.Success, userMessage: string.Join(separator: "\n", values: roundTrip.Diagnostics));
        Assert.True(condition: JsonNode.DeepEquals(result.Worlds[1].Json, roundTrip.Worlds[1].Json));
    }
    [Fact]
    public void LoopWorldNamesAndArgumentsRetainLexicalValues() {
        var result = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module room(value) { state { world { slot score = value } } }
            for (name, index) in ["north", "south"] {
                world name = room(index)
            }
            """, allowMultiple: true);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: "\n", values: result.Diagnostics));
        Assert.Equal(["north", "south"], result.Worlds.Select(static w => w.Name));
        Assert.Equal(1L, result.Worlds[1].Json["state"]!["world"]![0]!["value"]!.GetValue<long>());
    }
    [InlineData("world same = room()\nworld same = room()")]
    [InlineData("world same = room()\nworld SAME = room()")]
    [InlineData("world con = room()")]
    [InlineData("world $\"../escape\" = room()")]
    [InlineData("world x = missing()")]
    [Theory]
    public void InvalidWorldDeclarationsRefuse(string declarations) {
        var result = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: ("module room() { }\n" + declarations), allowMultiple: true);

        Assert.False(condition: result.Success);
    }
    [Fact]
    public void WorldsHaveSeparateMapsAndDeclaredNamesAreOrigins() {
        var result = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module room(value) { simulation { rate: value } }
            world north = room(20)
            world south = room(30)
            """, allowMultiple: true);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: "\n", values: result.Diagnostics));
        Assert.True(result.Worlds[0].SourceMap.TryGetOrigin("/simulation/rate", out var north));
        Assert.True(result.Worlds[1].SourceMap.TryGetOrigin("/simulation/rate", out var south));
        Assert.Contains("north", north.ModuleInstancePath);
        Assert.Contains("south", south.ModuleInstancePath);
    }
}
