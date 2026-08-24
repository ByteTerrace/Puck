using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the exactly-one-of-response-or-dynamics rule a kit's planar shaping obeys, against the exact
/// validator message, each paired with an admitting control. The sibling row-level refusals (range, uniqueness,
/// dangling references) are pinned in <see cref="DynamicsAuthoringValidationLawTests"/>; this file is the
/// consumer-side "which mechanism" gate.</summary>
/// <remarks>The merged plan names this file for <c>tests/Puck.World.Schema.Tests</c>; it sits here instead, beside
/// <see cref="DynamicsAuthoringValidationLawTests"/>, which already established the precedent — that project carries
/// no <c>WorldDefinitionValidator.TryValidate</c>/<c>Fixtures.BuildDocument</c> whole-document law pattern, and a
/// second one here would be a parallel mechanism, not a sibling.</remarks>
public sealed class MotionShapingValidationLawTests {
    private static WorldDynamicsRow Chase => new(Name: "chase", Frequency: 1f, Damping: 1f, Response: 0f);
    private static WorldDefinition WithDynamics(IReadOnlyList<WorldDynamicsRow> rows) => Fixtures.BuildDocument() with {
        DynamicsRaw = rows,
    };
    private static bool TryValidate(WorldDefinition definition, out string reason) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out reason
    );

    [Fact]
    public void BothResponseAndDynamicsRefusesWhileEitherAlonePasses() {
        var document = WithDynamics([Chase]);
        var kit = document.Kits[0];
        var grounded = (WorldMotionModel.Grounded)kit.Motion;

        var denied = document with { KitRowsRaw = [kit with { Motion = grounded with { Dynamics = "chase" } }] }; // Response already authored by Fixtures.BuildKits
        var responseOnly = document with { KitRowsRaw = [kit with { Motion = grounded with { Dynamics = null } }] };
        var dynamicsOnly = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "chase" } }] };

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "authors both response and dynamics 'chase' — a kit shapes planar velocity through exactly one.");
        Assert.True(condition: TryValidate(definition: responseOnly, reason: out var responseReason), userMessage: responseReason);
        Assert.True(condition: TryValidate(definition: dynamicsOnly, reason: out var dynamicsReason), userMessage: dynamicsReason);
    }

    [Fact]
    public void NeitherResponseNorDynamicsRefusesWhileEitherAlonePasses() {
        var document = WithDynamics([Chase]);
        var kit = document.Kits[0];
        var grounded = (WorldMotionModel.Grounded)kit.Motion;

        var denied = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = null } }] };
        var responseOnly = document with { KitRowsRaw = [kit with { Motion = grounded with { Dynamics = null } }] };
        var dynamicsOnly = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "chase" } }] };

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "requires exactly one of response or dynamics (neither is authored).");
        Assert.True(condition: TryValidate(definition: responseOnly, reason: out var responseReason), userMessage: responseReason);
        Assert.True(condition: TryValidate(definition: dynamicsOnly, reason: out var dynamicsReason), userMessage: dynamicsReason);
    }

    [Fact]
    public void EmptyDynamicsNameRefusesWhileANamedRowPasses() {
        var document = WithDynamics([Chase]);
        var kit = document.Kits[0];
        var grounded = (WorldMotionModel.Grounded)kit.Motion;

        var denied = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "" } }] };
        var admitted = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "chase" } }] };

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: ".dynamics is empty — name a dynamics row or omit it.");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void DanglingDynamicsNameRefusesWhileResolvingPasses() {
        var document = WithDynamics([Chase]);
        var kit = document.Kits[0];
        var grounded = (WorldMotionModel.Grounded)kit.Motion;

        var denied = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "missing" } }] };
        var admitted = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "chase" } }] };

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "'missing' names no dynamics row.");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void DynamicsAtResidentSimulationRateRefusesWhileASteppingRatePasses() {
        var document = WithDynamics([Chase]);
        var kit = document.Kits[0];
        var grounded = (WorldMotionModel.Grounded)kit.Motion;
        var withDynamics = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "chase" } }] };

        var denied = withDynamics with { Simulation = null }; // rate-0, resident, non-stepping
        var admitted = withDynamics;

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot compile — the world authors no simulation rate (simulation.rateHz)");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var controlReason), userMessage: controlReason);
    }
}
