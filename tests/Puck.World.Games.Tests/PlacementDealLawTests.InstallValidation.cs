using Xunit;

namespace Puck.World.Games.Tests;

public sealed partial class PlacementDealLawTests {
    /// <summary>The gate the deal sweep installs through: the children the sweep deals admit, and a child the
    /// document validator would refuse (a scale outside the envelope, a slot the region does not have, a creation the
    /// document does not declare) is refused by name, so one template's bad child never reaches the installed
    /// document.</summary>
    [Fact]
    public void ADealtChildTheDocumentValidatorWouldRefuseIsRefusedByName() {
        using var fixture = Fixtures.FreshServer(definition: Dealt(
            "a",
            "b"
        ));

        fixture.Step();

        var definition = fixture.Server.Definition;
        var template = WorldDefinitionRows.FindPlacement(
            id: TemplateId,
            placements: definition.Placements
        )!;
        var child = Child(
            key: "a",
            server: fixture.Server
        );

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateDealtChildren(
                children: Children(server: fixture.Server),
                definition: definition,
                reason: out var admitted,
                template: template
            ),
            userMessage: admitted
        );

        foreach (var (needle, bad) in new (string, WorldPlacement)[] {
            ("scale", (child with { Scale = 1000f })),
            ("dealSlot", (child with { DealSlot = 99 })),
            ("prototypeId", (child with { PrototypeId = "undeclared" })),
        }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidateDealtChildren(
                children: [child, bad],
                definition: definition,
                reason: out var reason,
                template: template
            ));
            Assert.Contains(
                actualString: reason,
                expectedSubstring: needle
            );
            Assert.Contains(
                actualString: reason,
                expectedSubstring: child.Id
            );
        }
    }
}
