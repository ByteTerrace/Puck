using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the <c>dynamics</c> section's own row refusals (range/uniqueness) and every consumer's dangling-
/// reference refusal (looks, kits, camera programs, state cells) against the exact validator message, each paired
/// with an admitting control so the assertion is discriminating.</summary>
public sealed class DynamicsAuthoringValidationLawTests {
    private static DynamicsRow Chase => new(
        Damping: 1f,
        Frequency: 1f,
        Name: "chase",
        Response: 0f
    );

    private static WorldCamera ProbeCamera(WorldCameraProgram rig) => new(
        Name: "probe",
        Anchor: null,
        Rig: rig,
        RenderWidth: 320,
        RenderHeight: 240
    );
    private static WorldCameraProgram ProgramWithOps(IReadOnlyList<WorldCameraProgramOp> operations) => new(
        Name: "probe-cam",
        Operations: operations,
        Version: WorldCameraProgram.CurrentVersion
    );
    private static WorldDefinition WithDynamics(IReadOnlyList<DynamicsRow> rows) => Fixtures.BuildDocument() with {
        DynamicsRaw = rows,
    };

    [Fact]
    public void CameraDynamicsOpDanglingReferenceRefusesWhileResolvingPasses() {
        var document = WithDynamics(rows: [Chase]);
        var dangling = document with {
            CamerasRaw = [ProbeCamera(rig: ProgramWithOps(operations: [
                new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                new WorldCameraProgramOp.Dynamics(Row: "missing"),
            ]))],
        };
        var resolving = document with {
            CamerasRaw = [ProbeCamera(rig: ProgramWithOps(operations: [
                new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                new WorldCameraProgramOp.Dynamics(Row: "chase"),
            ]))],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: dangling,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "cameras[0].rig.operations[1].row 'missing' names no dynamics row."
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: resolving,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    // Each lane's admitted range: zeta [0, 16], f (0, 100], r [-4, 4]; the control sits on the admitted edge.
    [InlineData("zeta", 16.5f, 16f, "dynamics[0].zeta 16.5 must be finite and within [0, 16].")]
    [InlineData("zeta", -0.1f, 0f, "dynamics[0].zeta -0.1 must be finite and within [0, 16].")]
    [InlineData("f", 101f, 100f, "dynamics[0].f 101 must be finite and within (0, 100].")]
    [InlineData("f", 0f, 1f, "dynamics[0].f 0 must be finite and within (0, 100].")]
    [InlineData("r", 4.1f, 4f, "dynamics[0].r 4.1 must be finite and within [-4, 4].")]
    [Theory]
    public void ALaneOutsideItsRangeRefusesWhileTheEdgePasses(string lane, float denied, float control, string refusal) {
        DynamicsRow With(float value) => lane switch {
            "zeta" => (Chase with { Damping = value }),
            "f" => (Chase with { Frequency = value }),
            _ => (Chase with { Response = value }),
        };

        Laws.RefusalWithControl(
            control: WithDynamics(rows: [With(value: control)]),
            denied: WithDynamics(rows: [With(value: denied)]),
            needle: refusal
        );
    }
    [Fact]
    public void DuplicateNameRefusesWhileUniquePasses() {
        var denied = WithDynamics(rows: [Chase, Chase]);
        var admitted = WithDynamics(rows: [Chase, Chase with { Name = "probe" }]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: denied,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "dynamics[1] 'chase' is duplicated."
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: admitted,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void EmptyNameRefuses() {
        var denied = WithDynamics(rows: [Chase with { Name = "" }]);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: denied,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "dynamics[0] is required."
        );
    }
    [Fact]
    public void KitDynamicsDanglingReferenceRefusesWhileResolvingPasses() {
        var document = WithDynamics(rows: [Chase]);
        var kit = document.Kits[0];
        var motion = kit.Motion;

        var row = motion.Shaping![0];
        var dangling = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "missing" }] } }] };
        var resolving = document with { KitRowsRaw = [kit with { Motion = motion with { Shaping = [row with { Along = null, Dynamics = "chase" }] } }] };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: dangling,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "kits[0].motion.shaping[0].dynamics 'missing' names no dynamics row."
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: resolving,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void LookRootDynamicsDanglingReferenceRefusesWhileResolvingPasses() {
        var document = WithDynamics(rows: [Chase]);
        var dangling = document with {
            LookRowsRaw = [new WorldLook(
                Name: "avatar",
                Source: new WorldLookSource.Catalog(Index: null),
                Scale: 1f,
                Motion: WorldLookMotion.Default with { Dynamics = "missing" }
            )],
        };
        var resolving = document with {
            LookRowsRaw = [new WorldLook(
                Name: "avatar",
                Source: new WorldLookSource.Catalog(Index: null),
                Scale: 1f,
                Motion: WorldLookMotion.Default with { Dynamics = "chase" }
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: dangling,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "looks[0].motion.dynamics 'missing' names no dynamics row."
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: resolving,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void PartDynamicsOnCatalogSourceRefusesWhileAbsentOnCatalogPasses() {
        var document = WithDynamics(rows: [Chase]);
        var denied = document with {
            LookRowsRaw = [new WorldLook(
                Name: "avatar",
                Source: new WorldLookSource.Catalog(Index: null),
                Scale: 1f,
                Motion: WorldLookMotion.Default with {
                PartDynamics = new Dictionary<string, string> { ["head"] = "chase" },
            }
            )],
        };
        var admitted = document with {
            LookRowsRaw = [new WorldLook(
                Name: "avatar",
                Source: new WorldLookSource.Catalog(Index: null),
                Scale: 1f,
                Motion: WorldLookMotion.Default
            )],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: denied,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "looks[0].motion.partDynamics cannot be set on a catalog source"
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: admitted,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void SecondCameraDynamicsOpRefusesWhileOnePasses() {
        var document = WithDynamics(rows: [Chase]);
        var denied = document with {
            CamerasRaw = [ProbeCamera(rig: ProgramWithOps(operations: [
                new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                new WorldCameraProgramOp.Dynamics(Row: "chase"),
                new WorldCameraProgramOp.Dynamics(Row: "chase"),
            ]))],
        };
        var admitted = document with {
            CamerasRaw = [ProbeCamera(rig: ProgramWithOps(operations: [
                new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                new WorldCameraProgramOp.Dynamics(Row: "chase"),
            ]))],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: denied,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is a second 'dynamics' op — at most one is admitted."
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: admitted,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void StateRowDynamicsBesideAdvanceRefusesWhileDynamicsAlonePasses() {
        var document = WithDynamics(rows: [Chase]);
        var denied = document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: CellName.Parse(candidate: "gauge"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0)
                    )],
                Advance: new StateAdvance(
                    PerSecondDenominator: 1,
                    PerSecondNumerator: 1
                ),
                Dynamics: new StateDynamics(
                    Row: "chase"
                )
            ),
            ]),
        };
        var admitted = document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: CellName.Parse(candidate: "gauge"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0)
                    )],
                Dynamics: new StateDynamics(
                    Row: "chase"
                )
            ),
            ]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: denied,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "declares both advance and dynamics"
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: admitted,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void StateRowDynamicsDanglingReferenceRefusesWhileResolvingPasses() {
        var document = WithDynamics(rows: [Chase]);
        var dangling = document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: CellName.Parse(candidate: "gauge"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0)
                    )],
                Dynamics: new StateDynamics(
                    Row: "missing"
                )
            ),
            ]),
        };
        var resolving = document with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: CellName.Parse(candidate: "gauge"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0)
                    )],
                Dynamics: new StateDynamics(
                    Row: "chase"
                )
            ),
            ]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: dangling,
            neighbours: null,
            reason: out var deniedReason
        ));
        Assert.Contains(
            actualString: deniedReason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "state.world[0].dynamics.row 'missing' names no dynamics row."
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: resolving,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
}
