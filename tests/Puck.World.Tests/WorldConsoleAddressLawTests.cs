using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a console row door writes to the instance the console addresses, through that instance's own link
/// (<see cref="WorldInstance.SubmissionLink"/>), never through the boot instance's. With the console addressing a
/// non-boot instance, <c>world.row.set</c> applies there and the boot instance's document never moves.
/// </summary>
public sealed class WorldConsoleAddressLawTests {
    private sealed class AddressedAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;

            return true;
        }
    }

    private static Vector3 CrateIn(HostRow row) => ((Vector3)WorldDefinitionRows.FindPlacement(id: "crate1", placements: row.Server.Definition.Placements)!.Position);

    [Fact]
    public void ARowSetAppliesInTheAddressedInstanceAndNeverInBoot() {
        using var boot = WorldEditorPlacementLawTests.Build();
        using var north = HostRow.Build(definition: boot.Server.Definition, name: "north");
        var registry = new CommandRegistry(modules: [
            new WorldRowCommandModule(authority: new AddressedAuthority(instance: north.Instance), echoes: new WorldDeferredVerbEchoes()),
        ]);
        var moved = (WorldDefinitionRows.FindPlacement(id: "crate1", placements: north.Server.Definition.Placements)! with { Position = new Vector3(x: 5f, y: 3f, z: -1f) });
        var before = CrateIn(row: boot);
        var result = registry.Submit(line: $"world.row.set placements {JsonSerializer.Serialize(value: moved, jsonTypeInfo: WorldJsonContext.Default.WorldPlacement)}");

        Assert.False(condition: result.IsError, userMessage: result.Output);
        north.Server.Advance(stepTicks: Fixtures.StepTicks);
        boot.Server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.Equal(actual: CrateIn(row: north), expected: new Vector3(x: 5f, y: 3f, z: -1f));

        // Red leg: the boot instance, whose link the row doors once held, never sees the edit.
        Assert.Equal(actual: CrateIn(row: boot), expected: before);
        Assert.NotSame(actual: north.Instance.SubmissionLink, expected: boot.Instance.SubmissionLink);
    }
}
