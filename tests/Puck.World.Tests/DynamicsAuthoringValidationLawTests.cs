using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the <c>dynamics</c> section's own row refusals (range/uniqueness) and every consumer's dangling-
/// reference refusal (looks, kits, camera programs, state cells) against the exact validator message, each paired
/// with an admitting control so the assertion is discriminating.</summary>
public sealed class DynamicsAuthoringValidationLawTests {
    private static WorldDynamicsRow Chase => new(Name: "chase", Frequency: 1f, Damping: 1f, Response: 0f);

    [Fact]
    public void NonPositiveFrequencyRefusesWhilePositivePasses() {
        var denied = WithDynamics([Chase with { Frequency = 0f }]);
        var admitted = WithDynamics([Chase]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "dynamics[0].f 0 must be finite and within (0, 100].");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void FrequencyPastCeilingRefusesWhileWithinPasses() {
        var denied = WithDynamics([Chase with { Frequency = 101f }]);
        var admitted = WithDynamics([Chase with { Frequency = 100f }]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "dynamics[0].f 101");
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "must be finite and within (0, 100].");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void NegativeDampingRefusesWhileZeroPasses() {
        var denied = WithDynamics([Chase with { Damping = -0.1f }]);
        var admitted = WithDynamics([Chase with { Damping = 0f }]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "dynamics[0].zeta -0.1 must be finite and within [0, 16].");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void DampingPastCeilingRefusesWhileWithinPasses() {
        var denied = WithDynamics([Chase with { Damping = 16.5f }]);
        var admitted = WithDynamics([Chase with { Damping = 16f }]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "dynamics[0].zeta 16.5 must be finite and within [0, 16].");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void ResponseOutsideRangeRefusesWhileWithinPasses() {
        var denied = WithDynamics([Chase with { Response = 4.1f }]);
        var admitted = WithDynamics([Chase with { Response = 4f }]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "dynamics[0].r 4.1 must be finite and within [-4, 4].");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void DuplicateNameRefusesWhileUniquePasses() {
        var denied = WithDynamics([Chase, Chase]);
        var admitted = WithDynamics([Chase, Chase with { Name = "probe" }]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "dynamics[1] 'chase' is duplicated.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void EmptyNameRefuses() {
        var denied = WithDynamics([Chase with { Name = "" }]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "dynamics[0] is required.");
    }

    [Fact]
    public void LookRootDynamicsDanglingReferenceRefusesWhileResolvingPasses() {
        var document = WithDynamics([Chase]);
        var dangling = document with {
            LookRowsRaw = [new WorldLook(Name: "avatar", Source: new WorldLookSource.Catalog(Index: null), Scale: 1f, Motion: WorldLookMotion.Default with { Dynamics = "missing" })],
        };
        var resolving = document with {
            LookRowsRaw = [new WorldLook(Name: "avatar", Source: new WorldLookSource.Catalog(Index: null), Scale: 1f, Motion: WorldLookMotion.Default with { Dynamics = "chase" })],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: dangling, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "looks[0].motion.dynamics 'missing' names no dynamics row.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: resolving, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void PartDynamicsOnCatalogSourceRefusesWhileAbsentOnCatalogPasses() {
        var document = WithDynamics([Chase]);
        var denied = document with {
            LookRowsRaw = [new WorldLook(Name: "avatar", Source: new WorldLookSource.Catalog(Index: null), Scale: 1f, Motion: WorldLookMotion.Default with {
                PartDynamics = new Dictionary<string, string> { ["head"] = "chase" },
            })],
        };
        var admitted = document with {
            LookRowsRaw = [new WorldLook(Name: "avatar", Source: new WorldLookSource.Catalog(Index: null), Scale: 1f, Motion: WorldLookMotion.Default)],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "looks[0].motion.partDynamics cannot be set on a catalog source");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void KitDynamicsDanglingReferenceRefusesWhileResolvingPasses() {
        var document = WithDynamics([Chase]);
        var kit = document.Kits[0];
        var grounded = (WorldMotionModel.Grounded)kit.Motion;

        var dangling = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "missing" } }] };
        var resolving = document with { KitRowsRaw = [kit with { Motion = grounded with { Response = null, Dynamics = "chase" } }] };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: dangling, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "kits[0].motion.dynamics 'missing' names no dynamics row.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: resolving, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldCameraProgram ProgramWithOps(IReadOnlyList<WorldCameraProgramOp> operations) => new(
        Name: "probe-cam",
        Version: WorldCameraProgram.CurrentVersion,
        Operations: operations
    );
    private static WorldCamera ProbeCamera(WorldCameraProgram rig) => new(
        Name: "probe",
        Anchor: null,
        Rig: rig,
        RenderWidth: 320,
        RenderHeight: 240
    );

    [Fact]
    public void CameraDynamicsOpDanglingReferenceRefusesWhileResolvingPasses() {
        var document = WithDynamics([Chase]);
        var dangling = document with {
            CamerasRaw = [ProbeCamera(rig: ProgramWithOps([
                new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                new WorldCameraProgramOp.Dynamics(Row: "missing"),
            ]))],
        };
        var resolving = document with {
            CamerasRaw = [ProbeCamera(rig: ProgramWithOps([
                new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                new WorldCameraProgramOp.Dynamics(Row: "chase"),
            ]))],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: dangling, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "cameras[0].rig.operations[1].row 'missing' names no dynamics row.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: resolving, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void SecondCameraDynamicsOpRefusesWhileOnePasses() {
        var document = WithDynamics([Chase]);
        var denied = document with {
            CamerasRaw = [ProbeCamera(rig: ProgramWithOps([
                new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                new WorldCameraProgramOp.Dynamics(Row: "chase"),
                new WorldCameraProgramOp.Dynamics(Row: "chase"),
            ]))],
        };
        var admitted = document with {
            CamerasRaw = [ProbeCamera(rig: ProgramWithOps([
                new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                new WorldCameraProgramOp.Dynamics(Row: "chase"),
            ]))],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "is a second 'dynamics' op — at most one is admitted.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void StateRowDynamicsDanglingReferenceRefusesWhileResolvingPasses() {
        var document = WithDynamics([Chase]);
        var dangling = document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: WorldCellName.Parse(candidate: "gauge"), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)], Dynamics: new WorldStateDynamics(Row: "missing", Y0: 0, V0: 0)),
            ]),
        };
        var resolving = document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: WorldCellName.Parse(candidate: "gauge"), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)], Dynamics: new WorldStateDynamics(Row: "chase", Y0: 0, V0: 0)),
            ]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: dangling, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "state[0].dynamics.row 'missing' names no dynamics row.");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: resolving, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    [Fact]
    public void StateRowDynamicsBesideAdvanceRefusesWhileDynamicsAlonePasses() {
        var document = WithDynamics([Chase]);
        var denied = document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: "gauge"),
                    Kind: CellKind.Int,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)],
                    Advance: new WorldStateAdvance(RateNumerator: 1, RateDenominator: 1),
                    Dynamics: new WorldStateDynamics(Row: "chase", Y0: 0, V0: 0)
                ),
            ]),
        };
        var admitted = document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: WorldCellName.Parse(candidate: "gauge"), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)], Dynamics: new WorldStateDynamics(Row: "chase", Y0: 0, V0: 0)),
            ]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: denied, neighbours: null, reason: out var deniedReason));
        Assert.Contains(actualString: deniedReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "declares both advance and dynamics");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: admitted, neighbours: null, reason: out var controlReason), userMessage: controlReason);
    }

    private static WorldDefinition WithDynamics(IReadOnlyList<WorldDynamicsRow> rows) => Fixtures.BuildDocument() with {
        DynamicsRaw = rows,
    };
}
