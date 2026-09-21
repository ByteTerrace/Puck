using Xunit;
using Puck.World.Machines;
using Puck.World.Server;

namespace Puck.World.Tests;

public sealed class WorldCompilationAnalysisLawTests {
    [Fact]
    public void LoaderReceiptReachesServerConstructionWithoutReplacingItsPrograms() {
        using var fixture = Fixtures.FreshServer();
        var catalog = new WorldMachineCatalog(engines: []);
        Assert.True(WorldDefinitionLoader.TryLoadForAdmission(
            WorldDefinitionSerialization.Serialize(definition: fixture.Server.Definition), "boot",
            out var admission, out var reason, catalog: catalog), reason);
        Assert.NotNull(admission);
        var definition = admission.Definition;
        using var machines = new WorldMachineHost(screens: definition.Screens, catalog: catalog);
        var server = new WorldServer(definition, new WorldPopulation(definition), fixture.Server.Profiles,
            new WorldRenderEnvelope(), machines, admission: admission);
        Assert.Same(admission.Compilation.CostReport, server.CostReport);
        Assert.Same(admission.Compilation.Hazards, server.RuleHazards);
        Assert.True(WorldDefinitionValidator.TryCompleteAdmission(admission, catalog, null, out reason), reason);
        Assert.False(WorldDefinitionValidator.TryCompleteAdmission(admission, new WorldMachineCatalog(engines: []), null, out reason));
        Assert.Contains("different machine catalog", reason);
    }

    [Fact]
    public void AdmissionCarriesTheExactProgramsIntoConstructionAndRefusesAnotherOwner() {
        using var fixture = Fixtures.FreshServer();
        var definition = fixture.Server.Definition;
        var catalog = new WorldMachineCatalog(engines: []);
        Assert.True(condition: WorldDefinitionValidator.TryAdmitLocally(definition: definition, machines: catalog,
            admission: out var admission, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: admission);
        Assert.Same(expected: definition, actual: admission.Definition);
        using var machines = new WorldMachineHost(screens: definition.Screens, catalog: catalog);
        var server = new WorldServer(definition: definition, population: new WorldPopulation(definition: definition),
            profiles: fixture.Server.Profiles, envelope: new WorldRenderEnvelope(), machines: machines, admission: admission);
        Assert.Same(expected: admission.Compilation.CostReport, actual: server.CostReport);
        Assert.Same(expected: admission.Compilation.Hazards, actual: server.RuleHazards);

        var another = definition with { };
        Assert.False(condition: admission.AppliesTo(definition: another, machines: catalog));
        Assert.False(condition: machines.TryPrepare(current: definition, candidate: another, admission: admission,
            plan: out var plan, reason: out reason));
        Assert.Null(@object: plan);
        Assert.Contains(expectedSubstring: "different definition or machine catalog", actualString: reason);
        var otherCatalog = new WorldMachineCatalog(engines: []);
        Assert.Equal(expected: catalog.CompositionFingerprint, actual: otherCatalog.CompositionFingerprint);
        using var otherMachines = new WorldMachineHost(screens: definition.Screens, catalog: otherCatalog);
        Assert.False(condition: otherMachines.TryPrepare(current: null, candidate: definition, admission: admission,
            plan: out plan, reason: out reason));
        Assert.Null(@object: plan);
        Assert.Throws<ArgumentException>(testCode: () => new WorldServer(definition: definition,
            population: new WorldPopulation(definition: definition), profiles: fixture.Server.Profiles,
            envelope: new WorldRenderEnvelope(), machines: otherMachines, admission: admission));
    }

    [Fact]
    public void EmbeddedAdmissionRetainsValidationAndRefusesMalformedOrInvalidDocuments() {
        var definition = Fixtures.BuildDocument();
        var catalog = new WorldMachineCatalog(engines: []);
        var admission = WorldDefinitionSerialization.DeserializeForAdmission(
            utf8Json: WorldDefinitionSerialization.Serialize(definition: definition), machines: catalog);
        Assert.True(condition: admission.AppliesTo(definition: admission.Definition, machines: catalog));
        Assert.Equal(expected: WorldDefinitionSerialization.Serialize(definition: definition),
            actual: WorldDefinitionSerialization.Serialize(definition: admission.Definition));
        Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.DeserializeForAdmission(
            utf8Json: "{"u8.ToArray(), machines: catalog));
        var invalid = definition with {
            Rules = [new WorldRule(CellName.Parse(candidate: "bad"),
                [new ActionEffect.SetState(State: "missing", Value: 1)])],
        };
        Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.DeserializeForAdmission(
            utf8Json: WorldDefinitionSerialization.Serialize(definition: invalid), machines: catalog));
    }

    [Fact]
    public void InstalledAnalysisSurvivesTicksAndIsReplacedWithRuleOrder() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(CellName.Parse(candidate: "value"), CellKind.Int,
                    Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0))]),
                new WorldStateRow(CellName.Parse(candidate: "observed"), CellKind.Int,
                    Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 0))]),
            ]),
            Rules = [
                new WorldRule(CellName.Parse(candidate: "observe"),
                    [new ActionEffect.SetState(State: "observed", Value: 1)],
                    Gate: new ActionPredicate.CompareState(State: "value", Comparison: ActionStateComparison.Equal, Value: 0)),
                new WorldRule(CellName.Parse(candidate: "advance"),
                    [new ActionEffect.AddState(State: "value", Value: 1)]),
            ],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var server = fixture.Server;
        var hazards = server.RuleHazards;
        var cost = server.CostReport;
        Assert.Single(collection: hazards);
        fixture.Step();
        Assert.Same(expected: hazards, actual: server.RuleHazards);
        Assert.Same(expected: cost, actual: server.CostReport);

        var current = server.Definition;
        Assert.NotNull(@object: current.Rules);
        var replacement = current with { Rules = [current.Rules[1], current.Rules[0]] };
        Assert.Same(expected: current.StateCatalog, actual: replacement.StateCatalog);
        server.RecompileRules(definition: replacement);
        Assert.Empty(collection: server.RuleHazards);
        Assert.NotSame(expected: cost, actual: server.CostReport);
        Assert.Single(collection: hazards);
    }
}
