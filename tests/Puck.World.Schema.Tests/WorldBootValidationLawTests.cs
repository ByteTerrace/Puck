using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldBootValidationLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawCannotHideAnInvalidSourceOrReplaceAnInvalidAuthoredCell(bool invalidCell) {
        var definition = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240))
            .WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "roll"), Kind: CellKind.Int,
                Capacity: (invalidCell ? 1 : null), Min: 1, Max: 6,
                Cells: (invalidCell ? [new StateCell(CellName.Parse(candidate: "die"), CellValue.Int(value: 0))] : null),
                Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.UniformRange,
                    RangeMin: 1, RangeMax: (invalidCell ? 6 : 7)), Timing: DrawTiming.Boot))]);
        Assert.False(WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(definition: definition),
            "invalid-draw", out var admission, out var reason));
        Assert.Null(admission);
        Assert.Contains("state[0]", reason);
        Assert.Contains((invalidCell ? "min" : "admissible domain"), reason);
    }

    [Fact]
    public void MalformedInlineDrawSourceRefusesBeforeMaskTraversal() {
        var definition = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240))
            .WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "roll"), Kind: CellKind.Int,
                Draw: new Draw(Generator: new StateGenerator(Source: GeneratorSource.WeightedNumeric,
                    Mode: GeneratorMode.WithoutReplacement, Weighted: [null!]), Timing: DrawTiming.Boot),
                DrawnMasks: [default])]);
        Assert.False(WorldDefinitionValidator.TryAdmit(definition, null, null, out _, out _));
        Assert.False(WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(definition: definition),
            "malformed-source", out var admission, out var reason));
        Assert.Null(admission);
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackendRowSettlesToOneLiteralAndRejectsAnAuthoredConflict(bool conflict) {
        var definition = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240),
            HostRaw: WorldHostDefaults.Absent with {
                Width = 320,
                Height = 200,
                BackendRow = "renderer",
                Backend = (conflict ? WorldBackendPreference.Vulkan : null),
            })
            .WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "renderer"), Kind: CellKind.Text,
                Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Text(value: "auto"))])]);
        var accepted = WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(definition: definition),
            "backend", out var admission, out var reason);
        Assert.Equal(!conflict, accepted);
        if (conflict) {
            Assert.Null(admission);
            Assert.Contains("both 'backend' and 'backendRow'", reason);
            return;
        }
        Assert.NotNull(admission);
        Assert.Null(admission.Definition.Host.BackendRow);
        Assert.Equal(WorldHostTokens.ParseBackend(token: "auto"), admission.Definition.Host.Backend);
        Assert.Equal("auto", admission.Definition.AuthoredState[0].Cells![0].Value.AsText);
        Assert.True(WorldDefinitionLoader.TryLoadForAdmission(WorldDefinitionSerialization.Serialize(admission.Definition),
            "backend-reloaded", out var reloaded, out reason), reason);
        Assert.Equal(admission.Definition.Host, reloaded!.Definition.Host);
    }

    [Fact]
    public void DrawResolverAllocatesNothingWhenAllRowsAreAlreadyFilled() {
        var literal = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240))
            .WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "value"), Kind: CellKind.Int,
                Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: 3))])]);
        for (var warm = 0; (warm < 100); warm++) {
            WorldDrawBootResolver.TryResolve(definition: literal, instanceIdentity: "boot", reason: out _, resolved: out _);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var unchanged = true;
        for (var iteration = 0; (iteration < 1000); iteration++) {
            unchanged &= (WorldDrawBootResolver.TryResolve(definition: literal, instanceIdentity: "boot", reason: out _, resolved: out var resolved) &&
                ReferenceEquals(objA: literal, objB: resolved));
        }
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(condition: unchanged);
        Assert.Equal(actual: allocated, expected: 0);
    }

    [Fact]
    public void DrawResolverPreservesIdentityOnlyWhenNoBootValueChanges() {
        var literal = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240));

        Assert.True(condition: WorldDrawBootResolver.TryResolve(definition: literal, instanceIdentity: "boot", reason: out var reason, resolved: out var unchanged), userMessage: reason);
        Assert.Same(actual: unchanged, expected: literal);

        var authored = literal.WithWorldState(rows: [new WorldStateRow(
            Name: CellName.Parse(candidate: "roll"),
            Kind: CellKind.Int,
            Draw: new Draw(Generator: new StateGenerator(
                Source: GeneratorSource.UniformRange,
                RangeMin: 1,
                RangeMax: 6
            ), Timing: DrawTiming.Boot)
        )]);

        Assert.True(condition: WorldDrawBootResolver.TryResolve(definition: authored, instanceIdentity: "boot", reason: out reason, resolved: out var drawn), userMessage: reason);
        Assert.NotSame(actual: drawn, expected: authored);
        Assert.Null(authored.AuthoredState[0].Cells);
        Assert.InRange(drawn.AuthoredState[0].Cells![0].Value.Raw, 1L, 6L);
        Assert.True(condition: WorldDrawBootResolver.TryResolve(definition: drawn, instanceIdentity: "boot", reason: out reason, resolved: out var reloaded), userMessage: reason);
        Assert.Same(actual: reloaded, expected: drawn);
    }
    [Fact]
    public void LiteralLoadStillValidatesBeforeTakingTheUnchangedPath() {
        var valid = new WorldDefinition(Simulation: new WorldSimulationDefaults(RateHz: 240));

        Assert.True(condition: WorldDefinitionLoader.TryLoad(
            WorldDefinitionSerialization.Serialize(definition: valid), "valid", out var loaded, out var reason
        ), userMessage: reason);
        Assert.Equal(240, loaded!.Simulation!.RateHz);
        var invalid = valid with { Simulation = new WorldSimulationDefaults(RateHz: -1) };

        Assert.False(condition: WorldDefinitionLoader.TryLoad(
            WorldDefinitionSerialization.Serialize(definition: invalid), "invalid", out loaded, out reason
        ));
        Assert.Null(@object: loaded);
        Assert.Contains(actualString: reason, expectedSubstring: "simulation");
    }
}
