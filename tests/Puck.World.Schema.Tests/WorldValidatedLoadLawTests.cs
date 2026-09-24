using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// One load produces one whole-document validation and one rule compilation, whatever the document's rows settle
/// into and whatever the host overrides on the way in. Each law counts through its own attributed
/// <see cref="WorldBootWork"/> ledger.
/// </summary>
public sealed class WorldValidatedLoadLawTests {
    private readonly WorldBootWork m_work = new();

    private (long Validations, long Compilations) Counts() => (
        m_work.Read(kind: WorldBootWork.Validations),
        m_work.Read(kind: WorldBootWork.RuleCompilations)
    );
    // A cell that accumulates carries no clock as authored, so loading it settles one: the document a receipt is
    // earned for is a different object from the document that was parsed.
    private static WorldDefinition SettlingDocument() => new WorldDefinition(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        HostRaw: (WorldHostDefaults.Absent with { Authority = "127.0.0.1:7777", Height = 1, Listen = "127.0.0.1:7777", Width = 1 })
    ).WithWorldState(rows: [
        new WorldStateRow(Name: CellName.Parse(candidate: "clock"), Kind: CellKind.Int,
            Advance: new StateAdvance(PerSecondDenominator: 1, PerSecondNumerator: 1),
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 0L))]),
    ]);

    [Fact]
    public void RowSettlementCostsNoSecondValidationOrCompilation() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        var bytes = WorldDefinitionSerialization.Serialize(definition: SettlingDocument());
        var before = Counts();

        Assert.True(condition: WorldDefinitionLoader.TryLoadForAdmission(bytes, "settling", out var admission, out var reason), userMessage: reason);
        var after = Counts();

        Assert.NotNull(@object: admission);
        Assert.Equal(actual: (after.Validations - before.Validations), expected: 1L);
        Assert.Equal(actual: (after.Compilations - before.Compilations), expected: 1L);
        // The receipt names the settled document, and settling it again is a fixpoint, so the server installs the
        // exact object validation read.
        Assert.Same(admission.Definition, admission.Compilation.Definition);
        Assert.Same(admission.Definition, WorldStateSettlement.SettleBeforeAdmission(definition: admission.Definition));
        Assert.NotNull(@object: admission.Definition.AuthoredState[0].Cells![0].Clock);
    }
    [Fact]
    public void ABootOverrideIsAdmittedRatherThanAppliedAfterwards() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        var bytes = WorldDefinitionSerialization.Serialize(definition: SettlingDocument());
        var before = Counts();

        Assert.True(condition: WorldDefinitionLoader.TryLoadForAdmission(bytes, "overridden", out var admission, out var reason,
            overrides: static definition => (definition with { HostRaw = definition.Host with { Authority = null, Listen = null } })), userMessage: reason);
        var after = Counts();

        Assert.NotNull(@object: admission);
        Assert.Equal(actual: (after.Validations - before.Validations), expected: 1L);
        Assert.Equal(actual: (after.Compilations - before.Compilations), expected: 1L);
        Assert.Null(@object: admission.Definition.Host.Authority);
        Assert.Null(@object: admission.Definition.Host.Listen);
        Assert.Same(admission.Definition, admission.Compilation.Definition);
        Assert.True(condition: admission.AppliesTo(definition: admission.Definition, machines: null));
    }
    [Fact]
    public void AnOverrideRefusalIsTheLoadsRefusal() {
        using var attribution = WorldBootWork.Attribute(work: m_work);

        var bytes = WorldDefinitionSerialization.Serialize(definition: SettlingDocument());

        Assert.False(condition: WorldDefinitionLoader.TryLoadForAdmission(bytes, "overridden", out var admission, out var reason,
            overrides: static definition => (definition with { Schema = "puck.world.definition.v0" })));
        Assert.Null(@object: admission);
        Assert.Contains(actualString: reason, expectedSubstring: "puck.world.definition.v0");
    }
}
