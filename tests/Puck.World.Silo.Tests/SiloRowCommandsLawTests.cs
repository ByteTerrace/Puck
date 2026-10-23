using Puck.Commands;
using Puck.Launcher;
using Xunit;

namespace Puck.World.Silo.Tests;

/// <summary>Pins whose authored commands a silo line hears: the row its session's slot is bound to answers with its own
/// live document's <c>commands</c>, so two rows never share a command and the administrative session hears none.</summary>
public sealed class SiloRowCommandsLawTests {
    [Fact]
    public void EachRowHearsItsOwnDocumentsCommands() {
        using var output = new BufferedConsoleOutput();
        var registry = new CommandRegistry(modules: []);
        var source = new TextCommandSource(registry: registry);
        var routing = new SiloConsoleRouting(
            source: () => source,
            tagging: new SiloConsoleTagging(output: output)
        );
        var documents = new Dictionary<string, WorldDefinition>(comparer: StringComparer.Ordinal) {
            ["east"] = new(CommandsRaw: [new WorldConsoleCommand(Name: "east.hello", Lines: ["help"])]),
            ["west"] = new(CommandsRaw: [new WorldConsoleCommand(Name: "west.hello", Lines: ["help"])]),
        };
        using var east = routing.Register(worldId: "east");
        using var west = routing.Register(worldId: "west");
        using var gone = routing.Register(worldId: "gone");

        registry.UseAuthoredCommands(source: WorldSiloApplication.RowCommands(
            definitionOf: worldId => documents.GetValueOrDefault(key: worldId),
            routing: routing
        ));

        Assert.True(condition: registry.AuthoredCommandsFor(slot: east.Slot).TryGet(command: out _, name: "east.hello"));
        Assert.False(condition: registry.AuthoredCommandsFor(slot: east.Slot).TryGet(command: out _, name: "west.hello"));
        Assert.True(condition: registry.AuthoredCommandsFor(slot: west.Slot).TryGet(command: out _, name: "west.hello"));
        Assert.Empty(collection: registry.AuthoredCommandsFor(slot: gone.Slot).Commands);
        Assert.Empty(collection: registry.AuthoredCommands.Commands);
    }
}
