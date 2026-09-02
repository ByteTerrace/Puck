using Xunit;

using Puck.Commands;

namespace Puck.World.Tests;

/// <summary>
/// The runtime modifier-ceiling law: a live session rebind (the <c>player.bind</c> path) whose composed profile would
/// carry more than <see cref="WorldBindingBarCapacity.MaxModifiers"/> modifiers is refused by name, rather than
/// silently clamped by the overlay feed's per-seat reservation. The metric is
/// <see cref="CompiledBindingProfile.Modifiers"/>.<c>Count</c> — the same one the boot-time validator uses — so the
/// preflight (<see cref="WorldSeatBindings.TryValidateSessionRebind"/>) and the install agree.
/// </summary>
public sealed class WorldSeatBindingModifierCeilingLawTests {
    private static WorldDefinition MinimalBindingDocument() => Fixtures.BuildDocument() with {
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
                Id: "modifier-ceiling-law",
                Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion,
                    Modifiers: [],
                    Chords: [
                        new BindingChordDefinition(Group: "resting", Page: new BindingPageDefinition(Id: "resting-base", Entries: [])),
                    ]
                )
            ),
        ],
    };
    // A rebind carrying `count` distinct modifiers (unique id AND unique source, so composition unions rather than
    // absorbs them). Unreferenced modifiers still count against the ceiling — the compiler seeds every declared one.
    private static BindingProfileDocument RebindWithModifiers(int count) => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [.. Enumerable.Range(count: count, start: 0).Select(selector: static index => new BindingModifierDefinition(Id: $"mod{index}", Sources: [$"source.{index}"]))],
        Chords: []
    );

    [Fact]
    public void RebindOverTheCeilingIsRefusedByName_RebindAtTheCeilingValidates() {
        var bindings = new WorldSeatBindings(definition: MinimalBindingDocument());

        Assert.False(condition: bindings.TryValidateSessionRebind(
            slot: 0,
            rebinds: RebindWithModifiers(count: (WorldBindingBarCapacity.MaxModifiers + 1)),
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: $"{WorldBindingBarCapacity.MaxModifiers}-modifier ceiling");

        // Control: the same path with one fewer modifier — exactly at the ceiling — validates.
        Assert.True(
            condition: bindings.TryValidateSessionRebind(
                slot: 0,
                rebinds: RebindWithModifiers(count: WorldBindingBarCapacity.MaxModifiers),
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
}
