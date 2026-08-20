using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the seat-routed row write (<c>player.row.set</c>) composes through the SAME section table
/// <c>world.row.set</c> owns (<see cref="WorldRowCommandModule.TryComposeRoutedSet"/>), so the two grammars can
/// never drift — and the entries whose mutation composes against the addressed world's own live document are refused
/// by name rather than composed against the WRONG world's rows.
/// </summary>
public sealed class RoutedRowComposerLawTests {
    [Fact]
    public void ComposesTheSameMutationTheLocalGrammarWould_AndRefusesTheServerReadingPaths() {
        // Control: a placements row composes to the identical UpsertPlacement the local verb submits.
        Assert.True(condition: WorldRowCommandModule.TryComposeRoutedSet(
            error: out var composeError,
            json: """{"id":"probe","creationId":"ball","position":[1,0,2],"yawDegrees":0,"scale":1}""",
            mutation: out var composed,
            path: "placements",
            principal: WorldPrincipal.Console
        ), userMessage: composeError);

        var upsert = Assert.IsType<WorldMutation.UpsertPlacement>(@object: composed);

        Assert.Equal(expected: "probe", actual: upsert.Placement.Id);

        // Denial 1: a section whose mutation reads the addressed world's own live document is refused by name.
        Assert.False(condition: WorldRowCommandModule.TryComposeRoutedSet(
            error: out var liveDocumentError,
            json: "{}",
            mutation: out _,
            path: "views.seatRig",
            principal: WorldPrincipal.Console
        ));
        Assert.Contains(expectedSubstring: "addressed world's own live document", actualString: liveDocumentError, comparisonType: StringComparison.Ordinal);

        // Denial 2: an unknown path is refused naming the admissible siblings, never a silent no-op.
        Assert.False(condition: WorldRowCommandModule.TryComposeRoutedSet(
            error: out var unknownError,
            json: "{}",
            mutation: out _,
            path: "nonsense",
            principal: WorldPrincipal.Console
        ));
        Assert.Contains(expectedSubstring: "placements", actualString: unknownError, comparisonType: StringComparison.Ordinal);
    }
}
