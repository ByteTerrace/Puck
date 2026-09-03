using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the exactly-one-of-along-or-dynamics rule a shaping row obeys, against the exact validator
/// message, each paired with an admitting control. The sibling row-level refusals (range, uniqueness, dangling
/// references) are pinned in <see cref="DynamicsAuthoringValidationLawTests"/>; this file is the consumer-side
/// "which mechanism" gate.</summary>
public sealed class MotionShapingValidationLawTests {
    private static WorldDynamicsRow Chase => new(Damping: 1f, Frequency: 1f, Name: "chase", Response: 0f);

    private static WorldDefinition WithDynamics(IReadOnlyList<WorldDynamicsRow> rows) => Fixtures.BuildDocument() with {
        DynamicsRaw = rows,
    };
    private static bool TryValidate(WorldDefinition definition, out string reason) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out reason
    );

    [Fact]
    public void BothAlongAndDynamicsRefusesWhileEitherAlonePasses() {
        var document = WithDynamics(rows: [Chase]);
        var kit = document.Kits[0];
        var motion = kit.Motion;
        var row = motion.Shaping![0];

        var denied = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Dynamics = "chase" }] } }] }; // Along already authored by Fixtures.BuildKits
        var alongOnly = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Dynamics = null }] } }] };
        var dynamicsOnly = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "chase" }] } }] };

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "authors both along and dynamics 'chase' — a shaping row selects exactly one.");
        Assert.True(condition: TryValidate(definition: alongOnly, reason: out var alongReason), userMessage: alongReason);
        Assert.True(condition: TryValidate(definition: dynamicsOnly, reason: out var dynamicsReason), userMessage: dynamicsReason);
    }
    [Fact]
    public void NeitherAlongNorDynamicsRefusesWhileEitherAlonePasses() {
        var document = WithDynamics(rows: [Chase]);
        var kit = document.Kits[0];
        var motion = kit.Motion;
        var row = motion.Shaping![0];

        var denied = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = null }] } }] };
        var alongOnly = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Dynamics = null }] } }] };
        var dynamicsOnly = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "chase" }] } }] };

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "requires exactly one of along or dynamics (neither is authored).");
        Assert.True(condition: TryValidate(definition: alongOnly, reason: out var alongReason), userMessage: alongReason);
        Assert.True(condition: TryValidate(definition: dynamicsOnly, reason: out var dynamicsReason), userMessage: dynamicsReason);
    }
    [Fact]
    public void EmptyDynamicsNameRefusesWhileANamedRowPasses() {
        var document = WithDynamics(rows: [Chase]);
        var kit = document.Kits[0];
        var motion = kit.Motion;
        var row = motion.Shaping![0];

        var denied = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "" }] } }] };
        var admitted = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "chase" }] } }] };

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: ".dynamics is empty — name a dynamics row or omit it.");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void DanglingDynamicsNameRefusesWhileResolvingPasses() {
        var document = WithDynamics(rows: [Chase]);
        var kit = document.Kits[0];
        var motion = kit.Motion;
        var row = motion.Shaping![0];

        var denied = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "missing" }] } }] };
        var admitted = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "chase" }] } }] };

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "'missing' names no dynamics row.");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void DynamicsAtResidentSimulationRateRefusesWhileASteppingRatePasses() {
        var document = WithDynamics(rows: [Chase]);
        var kit = document.Kits[0];
        var motion = kit.Motion;
        var row = motion.Shaping![0];
        var withDynamics = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "chase" }] } }] };

        var denied = withDynamics with { Simulation = null }; // rate-0, resident, non-stepping
        var admitted = withDynamics;

        Assert.False(condition: TryValidate(definition: denied, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "cannot compile — the world authors no simulation rate (simulation.rateHz)");
        Assert.True(condition: TryValidate(definition: admitted, reason: out var controlReason), userMessage: controlReason);
    }
}
