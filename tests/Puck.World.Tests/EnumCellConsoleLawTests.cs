using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A row naming an enum is read and written at the console by its members' names: <c>world.state</c> prints
/// a cell by the member its value stands for, and <c>world.state.cell.set</c> takes a member name as the value, as
/// well as the ordinal it stores. A name the enum does not declare is refused by name.</summary>
public sealed class EnumCellConsoleLawTests {
    private sealed class ConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }

    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        StateRaw = new WorldStateSection(
            Enums: [new StateEnum(
                Members: [.. ((string[])["Nothing", "Air", "Water", "Fire", "Earth"]).Select(selector: static member => CellName.Parse(candidate: member))],
                Name: CellName.Parse(candidate: "Element")
            )],
            World: [new WorldStateRow(
                Capacity: 2,
                Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "a"),
                    Value: CellValue.Int(value: 1L)
                )],
                Enum: CellName.Parse(candidate: "Element"),
                Kind: CellKind.Int,
                Name: CellName.Parse(candidate: "recipe")
            )]
        ),
    };

    [Fact]
    public void AnEnumCellIsWrittenAndReadByItsMembersNames() {
        using var row = HostRow.Build(
            definition: Document(),
            name: "boot"
        );
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new ConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link
        )]);
        string? refusal = null;

        row.Server.EchoTap = echo => {
            if (echo.Rejected) {
                refusal = echo.Message;
            }
        };

        string Write(string value) {
            refusal = null;
            Assert.False(condition: registry.Submit(line: $"world.state.cell.set recipe a {value}").IsError);
            row.Server.Advance(stepTicks: Fixtures.StepTicks);

            return registry.Submit(line: "world.state recipe a").Output;
        }

        Assert.Contains(
            actualString: registry.Submit(line: "world.state recipe a").Output,
            expectedSubstring: "value=Air]"
        );
        Assert.Contains(
            actualString: Write(value: "Fire"),
            expectedSubstring: "value=Fire]"
        );
        Assert.Null(@object: refusal);
        Assert.Contains(
            actualString: Write(value: "2"),
            expectedSubstring: "value=Water]"
        );
        Assert.Contains(
            actualString: Write(value: "Fyre"),
            expectedSubstring: "value=Water]"
        );
        Assert.NotNull(@object: refusal);
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "'Fyre' is not an integer or a member of enum 'Element'"
        );
    }
}
