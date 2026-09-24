using Puck.Commands;
using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: a placement id a channel could not name — one carrying <c>:</c>, which separates a
/// reserved channel's arguments (<c>$region:&lt;placementId&gt;</c>, <c>$influence:&lt;channel&gt;:&lt;placementId&gt;</c>),
/// or one exactly <c>$each</c>, the token <c>placement:$each</c> binds to a rule's <c>forEach</c> key — is refused by
/// name at the JSON load door and at the live mutation door a console <c>world.row.set placements</c> reaches. Each
/// denial is paired with the same row spelled with <c>-</c>, which is admitted.</summary>
public sealed class PlacementIdLawTests {
    private const string Creation = "rock";

    private static WorldPlacement Row(string id) => new(
        Id: id,
        PrototypeId: Creation,
        Position: new DocumentVector3(value: Vector3.Zero),
        YawDegrees: 0f,
        Scale: 1f
    );
    private static WorldDefinition Document(params string[] ids) => (Fixtures.BuildDocument() with {
        CreationsRaw = [CreationFixtures.UnitSphere(id: Creation)],
        PlacementRowsRaw = [.. ids.Select(selector: Row)],
    });

    [InlineData("a:b", "carries ':'")]
    [InlineData("$each", "is the token 'placement:$each'")]
    [Theory]
    public void AnIdAChannelCannotNameIsRefusedByNameAtLoad(string id, string rule) {
        Assert.False(condition: WorldDefinitionLoader.TryLoad(
            WorldDefinitionSerialization.Serialize(definition: Document(ids: id)),
            "placement-id",
            out var refused,
            out var reason
        ));
        Assert.Null(@object: refused);
        Assert.Contains(actualString: reason, expectedSubstring: $"placement id '{id}' {rule}");
        Assert.True(
            condition: WorldDefinitionLoader.TryLoad(
                WorldDefinitionSerialization.Serialize(definition: Document(ids: "a-b")),
                "placement-id",
                out _,
                out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [InlineData("a:b")]
    [InlineData("$each")]
    [Theory]
    public void AnIdAChannelCannotNameIsRefusedAtTheMutationDoor(string id) {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var before = fixture.DefinitionBytes();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertPlacement(
            Placement: Row(id: id),
            Principal: Principal.Console
        ));
        fixture.Step();

        Assert.Equal(actual: fixture.DefinitionBytes(), expected: before);
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: Document(ids: id), reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: $"placement id '{id}'");

        // CONTROL: the same row spelled with '-' is installed through the same door.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertPlacement(
            Placement: Row(id: id.Replace(newChar: '-', oldChar: ':').Replace(newValue: "each-", oldValue: "$")),
            Principal: Principal.Console
        ));
        fixture.Step();

        Assert.NotEqual(expected: before, actual: fixture.DefinitionBytes());
    }
}
