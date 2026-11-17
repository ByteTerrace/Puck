using Puck.SignedDistance;
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

    /// <summary>The gate runs the shape laws the placement validator runs on a solid row: a dealt child whose
    /// creation carries a shape a solid row refuses (a revolved lift) is refused by the creation and the shape, while
    /// the same child on an admitted creation, and a child with no solid facet, pass.</summary>
    [Fact]
    public void ADealtSolidChildWhoseCreationHasARefusedShapeIsRefusedByName() {
        var document = Dealt(
            "a",
            "b"
        );
        var revolved = CreationFixtures.Prototype(
            document: CreationFixtures.Document(
                name: "revolved",
                shapes: [(CreationFixtures.Shape(type: SdfSolidPrimitive.Prism) with { Lift = SdfLift.Revolve })],
                palette: CreationFixtures.Grey
            ),
            id: "revolved"
        );

        using var fixture = Fixtures.FreshServer(definition: (document with {
            CreationsRaw = [.. document.CreationsRaw!, revolved],
        }));

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

        Assert.True(condition: WorldDefinitionValidator.TryValidateDealtChildren(
            children: [child],
            definition: definition,
            reason: out _,
            template: template
        ));
        Assert.False(condition: WorldDefinitionValidator.TryValidateDealtChildren(
            children: [(child with { PrototypeId = "revolved" })],
            definition: definition,
            reason: out var reason,
            template: template
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "creation 'revolved'"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "lifts by revolve"
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateDealtChildren(
                children: [(child with { PrototypeId = "revolved", Solid = null })],
                definition: definition,
                reason: out reason,
                template: template
            ),
            userMessage: reason
        );
    }
}
