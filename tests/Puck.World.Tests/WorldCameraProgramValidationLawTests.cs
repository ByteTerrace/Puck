using System.Numerics;

using Xunit;

using Puck.Assets.Documents;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the authored camera-program vocabulary (<see cref="WorldCameraProgram"/>) — the ordered op list that
/// replaced the closed motion/aim/lens union. Every refusal is stated beside a one-value-different control that
/// validates, so a law can never pass because the whole document was malformed for some other reason.
/// </summary>
public sealed class WorldCameraProgramValidationLawTests {
    private static WorldCameraProgramOp Fov(float radians = 0.9f) => new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: radians));
    private static WorldCameraProgram Program(string name, params WorldCameraProgramOp[] operations) => new(
        Name: name,
        Version: WorldCameraProgram.CurrentVersion,
        Operations: operations
    );
    // A document carrying ONE authored camera whose rig is the program under test. A camera row imposes no
    // interactivity rule of its own, so it is the honest place to state an op-level refusal.
    private static WorldDefinition DocumentWithCamera(WorldCameraProgram rig) => (Fixtures.BuildDocument() with {
        CamerasRaw = [
            new WorldCamera(
                Name: "probe",
                Anchor: null,
                Rig: rig,
                RenderWidth: 320u,
                RenderHeight: 240u
            ),
        ],
    });
    private static void Refuses(WorldCameraProgram denied, WorldCameraProgram control, string expected) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: DocumentWithCamera(rig: denied), reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: expected);
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: DocumentWithCamera(rig: control), reason: out var controlReason),
            userMessage: controlReason
        );
    }

    [Fact]
    public void AProgramRoundTripsThroughTheDocumentWire() {
        var document = DocumentWithCamera(rig: Program(
            "probe-rig",
            new WorldCameraProgramOp.Anchor(Subject: new WorldCameraSubject.WorldPoint(Point: new DocumentVector3(x: 1f, y: 2f, z: 3f))),
            new WorldCameraProgramOp.ClampPitch(MinPitch: -1f, MaxPitch: 1f),
            new WorldCameraProgramOp.Orbit(
                Distance: 4f,
                Yaw: new BindableScalar(literal: 0.25f),
                Pitch: new BindableScalar(literal: 0.5f),
                PivotOffset: new DocumentVector3(x: 0f, y: 1f, z: 0f)
            ),
            new WorldCameraProgramOp.LookAt(
                Subject: new WorldCameraSubject.Reference(),
                TargetOffset: new DocumentVector3(x: 0f, y: 1f, z: 0f),
                WorldAxes: true
            ),
            Fov(radians: 0.6f),
            new WorldCameraProgramOp.Dynamics(Row: "probe")
        ));
        var round = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: document));

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: round, reason: out var reason),
            userMessage: reason
        );

        var rig = round.Cameras[0].Rig;

        Assert.Equal(expected: "probe-rig", actual: rig.Name);
        Assert.Equal(expected: 6, actual: rig.Operations.Count);
        Assert.Equal(
            actual: rig.Operations.Select(selector: static op => op.Opcode),
            expected: ["anchor", "clampPitch", "orbit", "lookAt", "fov", "dynamics"]
        );
        Assert.Equal(expected: 0.5f, actual: rig.OrbitOp!.Pitch.Literal);
        Assert.Equal(expected: "probe", actual: rig.DynamicsOp!.Row);
        Assert.Equal(expected: new Vector3(x: 1f, y: 2f, z: 3f), actual: ((WorldCameraSubject.WorldPoint)rig.AnchorOp!.Subject).Point.Value);
    }
    [Fact]
    public void AnUnmappedMemberIsRefusedByName() {
        var document = DocumentWithCamera(rig: Program("probe-rig", Fov()));
        var json = System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: document))
            .Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: "\"$type\": \"fov\", \"smoothRate\": 6",
                oldValue: "\"$type\": \"fov\""
            );

        var thrown = Assert.ThrowsAny<Exception>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: json)));

        Assert.Contains(actualString: thrown.Message, comparisonType: StringComparison.Ordinal, expectedSubstring: "smoothRate");

        // Control: the same document without the extra member parses.
        _ = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: document));
    }
    [Fact]
    public void AnUnknownOpcodeIsRefusedByName() {
        var document = DocumentWithCamera(rig: Program("probe-rig", Fov()));
        var json = System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: document))
            .Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: "\"$type\": \"dolly\"",
                oldValue: "\"$type\": \"fov\""
            );

        _ = Assert.ThrowsAny<Exception>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: json)));
    }
    [Fact]
    public void AnUnsupportedVersionIsRefusedByName() => Refuses(
        control: Program("probe-rig", Fov()),
        denied: (Program("probe-rig", Fov()) with { Version = "puck.camera.v0" }),
        expected: "must be 'puck.camera.v1'"
    );
    [Fact]
    public void AMissingNameIsRefusedByName() => Refuses(
        control: Program("probe-rig", Fov()),
        denied: (Program("probe-rig", Fov()) with { Name = "  " }),
        expected: ".name is required"
    );
    [Fact]
    public void AnEmptyOperationListIsRefusedByName() => Refuses(
        control: Program("probe-rig", Fov()),
        denied: Program("probe-rig"),
        expected: $"operations count must be within 1..{WorldCameraProgram.MaxOperations}"
    );
    [Fact]
    public void MoreOperationsThanTheCeilingAreRefusedByName() {
        var operations = new List<WorldCameraProgramOp> { Fov() };

        while (operations.Count <= WorldCameraProgram.MaxOperations) {
            operations.Add(item: new WorldCameraProgramOp.Offset(Value: new DocumentVector3(value: Vector3.Zero)));
        }

        Refuses(
            control: Program("probe-rig", [.. operations.Take(count: WorldCameraProgram.MaxOperations)]),
            denied: Program("probe-rig", [.. operations]),
            expected: $"operations count must be within 1..{WorldCameraProgram.MaxOperations}"
        );
    }
    [Fact]
    public void AnAnchorOpAwayFromTheFrontIsRefusedByName() => Refuses(
        control: Program("probe-rig", new WorldCameraProgramOp.Anchor(Subject: new WorldCameraSubject.Reference()), Fov()),
        denied: Program("probe-rig", Fov(), new WorldCameraProgramOp.Anchor(Subject: new WorldCameraSubject.Reference())),
        expected: "'anchor' must be the first operation"
    );
    [Fact]
    public void ASecondAnchorOpIsRefusedByName() => Refuses(
        control: Program("probe-rig", new WorldCameraProgramOp.Anchor(Subject: new WorldCameraSubject.Reference()), Fov()),
        denied: Program(
            "probe-rig",
            new WorldCameraProgramOp.Anchor(Subject: new WorldCameraSubject.Reference()),
            new WorldCameraProgramOp.Anchor(Subject: new WorldCameraSubject.Reference()),
            Fov()
        ),
        expected: "second 'anchor' op"
    );
    [Fact]
    public void AClampPitchAfterTheOrbitItGovernsIsRefusedByName() => Refuses(
        control: Program(
            "probe-rig",
            new WorldCameraProgramOp.ClampPitch(MinPitch: -1f, MaxPitch: 1f),
            new WorldCameraProgramOp.Orbit(Distance: 4f, Yaw: new BindableScalar(literal: 0f), Pitch: new BindableScalar(literal: 0f)),
            Fov()
        ),
        denied: Program(
            "probe-rig",
            new WorldCameraProgramOp.Orbit(Distance: 4f, Yaw: new BindableScalar(literal: 0f), Pitch: new BindableScalar(literal: 0f)),
            new WorldCameraProgramOp.ClampPitch(MinPitch: -1f, MaxPitch: 1f),
            Fov()
        ),
        expected: "'clampPitch' must precede the 'orbit' op it governs"
    );
    [Fact]
    public void AnInvertedClampPitchBandIsRefusedByName() => Refuses(
        control: Program("probe-rig", new WorldCameraProgramOp.ClampPitch(MinPitch: -1f, MaxPitch: 1f), Fov()),
        denied: Program("probe-rig", new WorldCameraProgramOp.ClampPitch(MinPitch: 1f, MaxPitch: -1f), Fov()),
        expected: "minPitch must be strictly less than maxPitch"
    );
    [Fact]
    public void ANonPositiveOrbitDistanceIsRefusedByName() => Refuses(
        control: Program("probe-rig", new WorldCameraProgramOp.Orbit(Distance: 4f, Yaw: new BindableScalar(literal: 0f), Pitch: new BindableScalar(literal: 0f)), Fov()),
        denied: Program("probe-rig", new WorldCameraProgramOp.Orbit(Distance: 0f, Yaw: new BindableScalar(literal: 0f), Pitch: new BindableScalar(literal: 0f)), Fov()),
        expected: ".distance must be finite and positive"
    );
    [Fact]
    public void ANegativeFocusDistanceIsRefusedByName() => Refuses(
        control: Program("probe-rig", new WorldCameraProgramOp.LookAt(Subject: null, FocusDistance: 1f), Fov()),
        denied: Program("probe-rig", new WorldCameraProgramOp.LookAt(Subject: null, FocusDistance: -1f), Fov()),
        expected: ".focusDistance must be finite and non-negative"
    );
    [Fact]
    public void ASubjectNamingAnUndeclaredPlacementIsRefusedByName() => Refuses(
        control: Program("probe-rig", new WorldCameraProgramOp.Anchor(Subject: new WorldCameraSubject.Reference()), Fov()),
        denied: Program("probe-rig", new WorldCameraProgramOp.Anchor(Subject: new WorldCameraSubject.Placement(PlacementId: "no-such-placement")), Fov()),
        expected: "references undeclared placement 'no-such-placement'"
    );
    [Fact]
    public void AProgramWithNoRenderedFieldOfViewIsRefusedByName() => Refuses(
        control: Program("probe-rig", Fov()),
        denied: Program("probe-rig", new WorldCameraProgramOp.Offset(Value: new DocumentVector3(value: Vector3.Zero))),
        expected: "must include a 'fov' op"
    );
    // The blend namespace is the whole document's program table — every cameras[].rig plus views.seatRig and
    // views.cameraRig — so a dangling name and a cycle are both cross-program facts.
    [Fact]
    public void ABlendNamingAnUndeclaredProgramIsRefusedByName() => Refuses(
        control: Program(
            "probe-rig",
            new WorldCameraProgramOp.Blend(
                A: Fixtures.StandardSeatRig.Name,
                B: Fixtures.StandardSeatRig.Name,
                Weight: new BindableScalar(literal: 0.5f)
            ),
            Fov()
        ),
        denied: Program(
            "probe-rig",
            new WorldCameraProgramOp.Blend(
                A: "no-such-program",
                B: Fixtures.StandardSeatRig.Name,
                Weight: new BindableScalar(literal: 0.5f)
            ),
            Fov()
        ),
        expected: "names undeclared camera program 'no-such-program'"
    );
    [Fact]
    public void ABlendCycleIsRefusedByNameWithItsTrail() {
        var denied = (Fixtures.BuildDocument() with {
            CamerasRaw = [
                new WorldCamera(
                    Name: "left",
                    Anchor: null,
                    RenderWidth: 320u,
                    RenderHeight: 240u,
                    Rig: Program(
                        "left-rig",
                        new WorldCameraProgramOp.Blend(A: "right-rig", B: "right-rig", Weight: new BindableScalar(literal: 0.5f)),
                        Fov()
                    )
                ),
                new WorldCamera(
                    Name: "right",
                    Anchor: null,
                    RenderWidth: 320u,
                    RenderHeight: 240u,
                    Rig: Program(
                        "right-rig",
                        new WorldCameraProgramOp.Blend(A: "left-rig", B: "left-rig", Weight: new BindableScalar(literal: 0.5f)),
                        Fov()
                    )
                ),
            ],
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "cycles back to a program already being blended");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "-> 'right-rig' -> 'left-rig'");

        // Control: the identical pair with one blend removed resolves and validates.
        var control = (denied with {
            CamerasRaw = [
                denied.Cameras[0],
                (denied.Cameras[1] with { Rig = Program("right-rig", Fov()) }),
            ],
        });

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
    // A blend resolves a program by name alone, so two programs under one name resolve nothing honestly.
    [Fact]
    public void ADuplicatedProgramNameIsRefusedByName() {
        var denied = (Fixtures.BuildDocument() with {
            CamerasRaw = [
                new WorldCamera(
                    Name: "left",
                    Anchor: null,
                    RenderWidth: 320u,
                    RenderHeight: 240u,
                    Rig: Program("shared-rig", Fov())
                ),
                new WorldCamera(
                    Name: "right",
                    Anchor: null,
                    RenderWidth: 320u,
                    RenderHeight: 240u,
                    Rig: Program("shared-rig", Fov())
                ),
            ],
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "'shared-rig' is duplicated across the document's camera programs");

        // Control: the identical pair under distinct program names validates.
        var control = (denied with {
            CamerasRaw = [
                denied.Cameras[0],
                (denied.Cameras[1] with { Rig = Program("right-rig", Fov()) }),
            ],
        });

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
    // views.seatRig is the ONE interactive program: seatControl declares a live yaw/pitch band, and only an orbit op
    // can express it.
    [Fact]
    public void ASeatRigWithoutAnOrbitOpIsRefusedByName() {
        var denied = (Fixtures.BuildDocument() with {
            ViewsRaw = (Fixtures.BuildDocument().Views with { SeatRig = Program("seatChase", Fov()) }),
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "views.seatRig must contain an 'orbit' op");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: Fixtures.BuildDocument(), reason: out var controlReason), userMessage: controlReason);
    }
    // views.cameraRig is the first-person framing a possessed camera body resolves through: it sits AT that body's
    // own pose, so an orbit or offset op would move it off the thing the seat is perceiving from.
    [Fact]
    public void ACameraRigWithAnOffsetOpIsRefusedByName() {
        var baseline = Fixtures.BuildCameraBodyDocument();
        var family = new WorldSeatModeFamily(
            Name: "camera",
            States: [new WorldSeatModeState(Name: "seat"), new WorldSeatModeState(Name: "free", Target: WorldSeatModeState.CameraTarget)],
            DefaultState: "seat"
        );
        var freeCam = Program("seatFreeCam", new WorldCameraProgramOp.LookAt(Subject: null, FocusDistance: 1f), Fov());
        var control = (baseline with {
            SeatModesRaw = [family],
            ViewsRaw = (baseline.Views with { CameraRig = freeCam }),
        });
        var denied = (control with {
            ViewsRaw = (baseline.Views with {
                CameraRig = Program(
                    "seatFreeCam",
                    new WorldCameraProgramOp.Offset(Value: new DocumentVector3(x: 0f, y: 0f, z: 3f)),
                    new WorldCameraProgramOp.LookAt(Subject: null, FocusDistance: 1f),
                    Fov()
                ),
            }),
        });

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "views.cameraRig must author no 'orbit', 'offset', or 'path' op");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
}
