using Xunit;

using Puck.Commands;

namespace Puck.World.Tests;

/// <summary>
/// Laws for the authored per-seat mode family (<see cref="WorldSeatModeFamily"/>) — the generic replacement for the
/// engine's former hard-coded editor context family: a document-declared family seeds its default state on every
/// seat, <see cref="WorldSeatBindings.SetContextState"/> admits any state text once the FAMILY is declared (state
/// validation is the caller's job, against <see cref="WorldSeatBindings.TryResolveMode"/>'s own returned list — the
/// same trust built-in families' own publishers already get), and a <c>contexts</c> row maps a (family, state) pair
/// to a binding group exactly as a built-in family does. The validator laws pair every denied document with a
/// one-value-different passing control.
/// </summary>
public sealed class WorldSeatModeLawTests {
    private const string FamilyName = "camera";
    private const string FlyingGroup = "freeCam";
    private const string OffState = "seat";
    private const string OnState = "free";
    private const string RestingGroup = "resting";

    private static WorldSeatModeFamily Family(string defaultState = OffState) => new(
        Name: FamilyName,
        States: [new WorldSeatModeState(Name: OffState), new WorldSeatModeState(Name: OnState, Target: "camera")],
        DefaultState: defaultState
    );
    // A document authoring the family plus the two things a camera-targeting state couples to — a views.cameraRig
    // program and an inhabited camera-seat-0 body — and a contexts row mapping (camera, free) -> the free-cam group,
    // with a resting-group chord page too.
    private static WorldDefinition DocumentWithFamily(WorldSeatModeFamily family) => Fixtures.BuildCameraBodyDocument() with {
        SeatModesRaw = [family],
        ViewsRaw = (Fixtures.BuildDocument().Views with {
            CameraRig = new WorldCameraProgram(
                Name: "seatFreeCam",
                Version: WorldCameraProgram.CurrentVersion,
                Operations: [
                    new WorldCameraProgramOp.LookAt(
                        Subject: null,
                        FocusDistance: 1f
                    ),
                    new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 0.9f)),
                ]
            ),
        }),
        BindingOverlaysRaw = [
            new WorldBindingOverlay(
                Id: "seat-mode-law",
                Document: new BindingProfileDocument(
                    Version: BindingProfileDocument.CurrentVersion,
                    Modifiers: [],
                    Chords: [
                        new BindingChordDefinition(Group: RestingGroup, Page: new BindingPageDefinition(Id: "resting-base", Entries: [])),
                        new BindingChordDefinition(Group: FlyingGroup, Page: new BindingPageDefinition(Id: "flying-base", Entries: [])),
                    ],
                    Contexts: [
                        new BindingContextDefinition(Family: FamilyName, Group: FlyingGroup, State: OnState),
                    ]
                )
            ),
        ],
    };

    [Fact]
    public void SeedsEveryLocalSeat_ToTheFamilysDefaultState() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            Assert.Equal(expected: OffState, actual: bindings.ModeState(family: FamilyName, slot: slot));
        }
    }
    [Fact]
    public void SetContextState_FlipsPublishedStateAndDerivedGroup() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        Assert.Equal(expected: RestingGroup, actual: bindings.PageView(slot: 0).Group);

        bindings.SetContextState(family: FamilyName, slot: 0, state: OnState);

        Assert.Equal(expected: OnState, actual: bindings.ModeState(family: FamilyName, slot: 0));
        Assert.Equal(expected: FlyingGroup, actual: bindings.PageView(slot: 0).Group);

        bindings.SetContextState(family: FamilyName, slot: 0, state: OffState);

        Assert.Equal(expected: OffState, actual: bindings.ModeState(family: FamilyName, slot: 0));
        Assert.Equal(expected: RestingGroup, actual: bindings.PageView(slot: 0).Group);
    }
    // SetContextState's own gate admits any state text once the FAMILY is declared — validating a state token
    // against the family's own admitted list is the verb handler's job (player.mode, via TryResolveMode's returned
    // States), never this low-level publish primitive's. This mirrors the built-in families exactly: their
    // publishers are trusted callers too.
    [Fact]
    public void SetContextState_TakesAnyStateTextForAKnownFamily_ValidationIsTheCallersJob() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        bindings.SetContextState(family: FamilyName, slot: 0, state: "not-a-declared-state");

        Assert.Equal(expected: "not-a-declared-state", actual: bindings.ModeState(family: FamilyName, slot: 0));

        // Control: a state the family DOES declare takes effect identically.
        bindings.SetContextState(family: FamilyName, slot: 0, state: OnState);

        Assert.Equal(expected: OnState, actual: bindings.ModeState(family: FamilyName, slot: 0));
    }
    [Fact]
    public void TryResolveMode_StatesList_IsWhatACallerValidatesATokenAgainst() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));
        var family = bindings.TryResolveMode(family: FamilyName, slot: 0)!;

        Assert.DoesNotContain(collection: family.States, filter: state => (state.Name == "not-a-declared-state"));
        // Control: the declared states ARE present, by name.
        Assert.Contains(collection: family.States, filter: state => (state.Name == OffState));
        Assert.Contains(collection: family.States, filter: state => (state.Name == OnState));
    }
    [Fact]
    public void SetContextState_UndeclaredFamily_IsIgnored() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        bindings.SetContextState(family: "no-such-family", slot: 0, state: OnState);

        Assert.Null(@object: bindings.ModeState(family: "no-such-family", slot: 0));
        Assert.Null(@object: bindings.TryResolveMode(family: "no-such-family", slot: 0));

        // Control: the declared family accepts the identical state value.
        bindings.SetContextState(family: FamilyName, slot: 0, state: OnState);

        Assert.Equal(expected: OnState, actual: bindings.ModeState(family: FamilyName, slot: 0));
    }
    [Fact]
    public void TryResolveMode_ReturnsTheDeclaredFamilyAndStates() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        var resolved = bindings.TryResolveMode(family: FamilyName, slot: 0);

        Assert.NotNull(@object: resolved);
        Assert.Equal(expected: 2, actual: resolved!.States.Count);
        Assert.Contains(collection: resolved.States, filter: state => ((state.Name == OffState) && (state.Target is null)));
        Assert.Contains(collection: resolved.States, filter: state => ((state.Name == OnState) && (state.Target == "camera")));
    }
    // Validator laws: seatModes name/state rules, each a denied document beside a one-value-different control.
    [Fact]
    public void FamilyName_CollidingWithABuiltInFamily_RefusesByName() {
        var denied = Fixtures.BuildDocument() with { SeatModesRaw = [new WorldSeatModeFamily(Name: "roster", States: [new WorldSeatModeState(Name: OffState)], DefaultState: OffState)] };
        var control = Fixtures.BuildDocument() with { SeatModesRaw = [new WorldSeatModeFamily(Name: "not-roster", States: [new WorldSeatModeState(Name: OffState)], DefaultState: OffState)] };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "collides with a built-in context family");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void DuplicateStateName_RefusesByName() {
        var denied = Fixtures.BuildDocument() with { SeatModesRaw = [new WorldSeatModeFamily(Name: FamilyName, States: [new WorldSeatModeState(Name: OffState), new WorldSeatModeState(Name: OffState)], DefaultState: OffState)] };
        var control = Fixtures.BuildDocument() with { SeatModesRaw = [new WorldSeatModeFamily(Name: FamilyName, States: [new WorldSeatModeState(Name: OffState), new WorldSeatModeState(Name: OnState)], DefaultState: OffState)] };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "is duplicated");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void UnknownDefaultState_RefusesByName() {
        var denied = Fixtures.BuildDocument() with { SeatModesRaw = [new WorldSeatModeFamily(Name: FamilyName, States: [new WorldSeatModeState(Name: OffState)], DefaultState: "not-declared")] };
        var control = Fixtures.BuildDocument() with { SeatModesRaw = [new WorldSeatModeFamily(Name: FamilyName, States: [new WorldSeatModeState(Name: OffState)], DefaultState: OffState)] };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "names no state");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void CameraTargetingState_WithoutAnAuthoredCameraRig_RefusesByName() {
        var denied = Fixtures.BuildDocument() with { SeatModesRaw = [Family()] };
        var control = DocumentWithFamily(family: Family());

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "views.cameraRig is not authored");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
    [Fact]
    public void CameraTargetingState_WithoutAnInhabitedCameraBody_RefusesByName() {
        var denied = (Fixtures.BuildDocument() with {
            SeatModesRaw = [Family()],
            ViewsRaw = DocumentWithFamily(family: Family()).Views,
        });
        var control = DocumentWithFamily(family: Family());

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: WorldSeatModeState.CameraPlacementIdPrefix);
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
    // The one lookup a no-token Free Cam binding (player.camera) resolves through: the seat's own routed document
    // decides which family and state compose the application, so a wheel sector needs no hard-coded family name.
    [Fact]
    public void TryResolveCameraMode_ResolvesTheCameraTargetingFamilyAndState() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));
        var resolved = bindings.TryResolveCameraMode(slot: 0);

        Assert.NotNull(@object: resolved);
        Assert.Equal(expected: FamilyName, actual: resolved!.Value.Family.Name);
        Assert.Equal(expected: OnState, actual: resolved.Value.State.Name);
    }
    [Fact]
    public void TryResolveCameraMode_WithNoCameraTargetingState_ResolvesNothing() {
        var bindings = new WorldSeatBindings(definition: (Fixtures.BuildDocument() with {
            SeatModesRaw = [new WorldSeatModeFamily(
                Name: FamilyName,
                States: [new WorldSeatModeState(Name: OffState)],
                DefaultState: OffState
            )],
        }));

        Assert.Null(@object: bindings.TryResolveCameraMode(slot: 0));
    }
    [Fact]
    public void UnknownTarget_RefusesByName() {
        var denied = Fixtures.BuildDocument() with { SeatModesRaw = [new WorldSeatModeFamily(Name: FamilyName, States: [new WorldSeatModeState(Name: OnState, Target: "not-a-real-target")], DefaultState: OnState)] };
        var control = DocumentWithFamily(family: Family());

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "is not admitted");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
    }
}
