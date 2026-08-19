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
    private const string FamilyName = "editing";
    private const string OffState = "off";
    private const string OnState = "on";
    private const string RestingGroup = "resting";
    private const string FlyingGroup = "flying";

    private static WorldSeatModeFamily Family(string defaultState = OffState) => new(
        Name: FamilyName,
        States: [new WorldSeatModeState(Name: OffState), new WorldSeatModeState(Name: OnState, Target: "camera")],
        DefaultState: defaultState
    );
    // A document authoring the family plus a views.flyRig (required whenever any state targets "camera") and a
    // contexts row mapping (editing, on) -> the flying group, with a resting-group chord page too.
    private static WorldDefinition DocumentWithFamily(WorldSeatModeFamily family) => Fixtures.BuildDocument() with {
        SeatModesRaw = [family],
        ViewsRaw = (Fixtures.BuildDocument().Views with {
            FlyRig = new WorldCameraRig(
                Motion: new WorldCameraMotion.Fly(MinSpeed: 0.5f, MaxSpeed: 64f, DefaultSpeed: 8f, LookRateRadiansPerSecond: 2.6f, MaxPitchRadians: 1.45f),
                Aim: new WorldCameraAim.Forward(FocusDistance: 1f),
                Lens: new WorldCameraLens(FieldOfViewRadians: 0.9f)
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
                        new BindingContextDefinition(Family: FamilyName, State: OnState, Group: FlyingGroup),
                    ]
                )
            ),
        ],
    };

    [Fact]
    public void SeedsEveryLocalSeat_ToTheFamilysDefaultState() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        for (var slot = 0; (slot < WorldSeatBindings.SeatCount); slot++) {
            Assert.Equal(expected: OffState, actual: bindings.ModeState(slot: slot, family: FamilyName));
        }
    }
    [Fact]
    public void SetContextState_FlipsPublishedStateAndDerivedGroup() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        Assert.Equal(expected: RestingGroup, actual: bindings.PageView(slot: 0).Group);

        bindings.SetContextState(slot: 0, family: FamilyName, state: OnState);

        Assert.Equal(expected: OnState, actual: bindings.ModeState(slot: 0, family: FamilyName));
        Assert.Equal(expected: FlyingGroup, actual: bindings.PageView(slot: 0).Group);

        bindings.SetContextState(slot: 0, family: FamilyName, state: OffState);

        Assert.Equal(expected: OffState, actual: bindings.ModeState(slot: 0, family: FamilyName));
        Assert.Equal(expected: RestingGroup, actual: bindings.PageView(slot: 0).Group);
    }
    // SetContextState's own gate admits any state text once the FAMILY is declared — validating a state token
    // against the family's own admitted list is the verb handler's job (player.mode, via TryResolveMode's returned
    // States), never this low-level publish primitive's. This mirrors the built-in families exactly: their
    // publishers are trusted callers too.
    [Fact]
    public void SetContextState_TakesAnyStateTextForAKnownFamily_ValidationIsTheCallersJob() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        bindings.SetContextState(slot: 0, family: FamilyName, state: "not-a-declared-state");

        Assert.Equal(expected: "not-a-declared-state", actual: bindings.ModeState(slot: 0, family: FamilyName));

        // Control: a state the family DOES declare takes effect identically.
        bindings.SetContextState(slot: 0, family: FamilyName, state: OnState);

        Assert.Equal(expected: OnState, actual: bindings.ModeState(slot: 0, family: FamilyName));
    }
    [Fact]
    public void TryResolveMode_StatesList_IsWhatACallerValidatesATokenAgainst() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));
        var family = bindings.TryResolveMode(slot: 0, family: FamilyName)!;

        Assert.DoesNotContain(collection: family.States, filter: state => (state.Name == "not-a-declared-state"));
        // Control: the declared states ARE present, by name.
        Assert.Contains(collection: family.States, filter: state => (state.Name == OffState));
        Assert.Contains(collection: family.States, filter: state => (state.Name == OnState));
    }
    [Fact]
    public void SetContextState_UndeclaredFamily_IsIgnored() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        bindings.SetContextState(slot: 0, family: "no-such-family", state: OnState);

        Assert.Null(@object: bindings.ModeState(slot: 0, family: "no-such-family"));
        Assert.Null(@object: bindings.TryResolveMode(slot: 0, family: "no-such-family"));

        // Control: the declared family accepts the identical state value.
        bindings.SetContextState(slot: 0, family: FamilyName, state: OnState);

        Assert.Equal(expected: OnState, actual: bindings.ModeState(slot: 0, family: FamilyName));
    }
    [Fact]
    public void TryResolveMode_ReturnsTheDeclaredFamilyAndStates() {
        var bindings = new WorldSeatBindings(definition: DocumentWithFamily(family: Family()));

        var resolved = bindings.TryResolveMode(slot: 0, family: FamilyName);

        Assert.NotNull(@object: resolved);
        Assert.Equal(expected: 2, actual: resolved!.States.Count);
        Assert.Contains(collection: resolved.States, filter: state => (state.Name == OffState) && (state.Target is null));
        Assert.Contains(collection: resolved.States, filter: state => (state.Name == OnState) && (state.Target == "camera"));
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
    public void CameraTargetingState_WithoutAnAuthoredFlyRig_RefusesByName() {
        var denied = Fixtures.BuildDocument() with { SeatModesRaw = [Family()] };
        var control = DocumentWithFamily(family: Family());

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: denied, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "views.flyRig is not authored");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: control, reason: out var controlReason), userMessage: controlReason);
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
