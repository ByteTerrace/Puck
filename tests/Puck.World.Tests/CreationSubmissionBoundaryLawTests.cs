using System.Text.Json;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a submitted <c>UpsertCreation</c>'s embedded document is decided at the compose boundary — a document the
/// canonicalizer cannot admit (an unresolved <c>state.</c> reference included) is a loud refusal that leaves the
/// definition untouched, never an exception that kills the tick: this arm decides submissions from remote travelers
/// and admitted peers, so a throw here is a remote kill-shot on the authority. The control half pins the absent-hash
/// rule: a submission carrying no hash adopts the canonical one (self-consistent), while a carried hash must equal
/// what the pipeline computes.
/// </summary>
public sealed class CreationSubmissionBoundaryLawTests {
    // Rows built through the SAME wire grammar a routed or console submission parses (the world.row.set JSON shape),
    // so the law exercises exactly what a submitter can spell.
    private static WorldCreation Piece(string rotation) => JsonSerializer.Deserialize(
        json: ("""{"id":"boundary-piece","document":{"schema":"puck.creation.v1","name":"boundary-piece","palette":[{"color":"#D8D2C4"}],"shapes":[{"type":"Box","name":"body","position":[0,0.5,0],"scale":[0.5,0.5,0.5],"rotation":""" + rotation + "}]}}"),
        jsonTypeInfo: WorldJsonContext.Default.WorldCreation
    )!;

    [Fact]
    public void AnInadmissibleSubmittedDocumentRefusesInsteadOfKillingTheTick() {
        using var fixture = Fixtures.FreshServer();

        var before = WorldDefinitionSerialization.Serialize(definition: fixture.Server.Definition);

        // The denial: an unresolved `state.` spatial reference — exactly what a traveler's copied document carries
        // when its source world's state vocabulary did not travel with it.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertCreation(
            Principal: WorldPrincipal.Console,
            Creation: Piece(rotation: "\"state.spatial.identityQuaternion\"")
        ));
        fixture.Step();

        Assert.Equal(
            actual: WorldDefinitionSerialization.Serialize(definition: fixture.Server.Definition),
            expected: before
        );

        // The control: the identical document with the reference resolved to its literal, and NO carried hash —
        // the canonical hash is adopted rather than refused, so the row lands and self-verifies.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertCreation(
            Principal: WorldPrincipal.Console,
            Creation: Piece(rotation: "[0,0,0,1]")
        ));
        fixture.Step();

        var landed = fixture.Server.Definition.Creations.SingleOrDefault(predicate: static creation => (creation.Id.Value == "boundary-piece"));

        Assert.NotNull(@object: landed);
        Assert.False(condition: string.IsNullOrEmpty(value: landed!.Hash), userMessage: "an absent submitted hash adopts the canonical one");
    }
}
