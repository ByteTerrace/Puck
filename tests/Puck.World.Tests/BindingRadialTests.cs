using System.Numerics;
using System.Text.Json;
using Puck.Commands;
using Xunit;

namespace Puck.World.Tests;

public sealed class BindingRadialTests {
    [Fact]
    public void AuthoredExcursionDeadZoneRangesAndHysteresisSelectRings() {
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(document: ExcursionProfile()));

        _ = bindings.Resolve(slot: 0, signal: InputSignal.Press(source: "keyboard.tab"));
        var excursion = bindings.WheelFor(slot: 0)!.Excursion;

        Assert.NotNull(@object: excursion);
        Assert.Equal(expected: -1, actual: BindingWheelGeometry.ResolveExcursionRing(vector: new Vector2(x: 0.15f, y: 0f), excursion: excursion, previousRing: -1));
        Assert.Equal(expected: 0, actual: BindingWheelGeometry.ResolveExcursionRing(vector: new Vector2(x: 0.20f, y: 0f), excursion: excursion, previousRing: -1));
        Assert.Equal(expected: 1, actual: BindingWheelGeometry.ResolveExcursionRing(vector: new Vector2(x: 0.50f, y: 0f), excursion: excursion, previousRing: -1));
        Assert.Equal(expected: 2, actual: BindingWheelGeometry.ResolveExcursionRing(vector: new Vector2(x: 0.90f, y: 0f), excursion: excursion, previousRing: -1));

        Assert.Equal(expected: 0, actual: BindingWheelGeometry.ResolveExcursionRing(vector: new Vector2(x: 0.36f, y: 0f), excursion: excursion, previousRing: 0));
        Assert.Equal(expected: 1, actual: BindingWheelGeometry.ResolveExcursionRing(vector: new Vector2(x: 0.38f, y: 0f), excursion: excursion, previousRing: 0));
        Assert.Equal(expected: 1, actual: BindingWheelGeometry.ResolveExcursionRing(vector: new Vector2(x: 0.34f, y: 0f), excursion: excursion, previousRing: 1));
        Assert.Equal(expected: 0, actual: BindingWheelGeometry.ResolveExcursionRing(vector: new Vector2(x: 0.32f, y: 0f), excursion: excursion, previousRing: 1));
    }

    [Fact]
    public void AngleUsesInputNeutralWhileHitTargetUsesTheDisplayedHub() {
        var openingInput = new Vector2(x: 700f, y: 500f);
        var centeredHub = new Vector2(x: 400f, y: 300f);

        Assert.Equal(
            expected: Vector2.Zero,
            actual: BindingWheelGeometry.ResolveSpatialTargetVector(BindingWheelSpatialSelectionMode.Angle, position: openingInput, neutral: openingInput, hub: centeredHub)
        );
        Assert.Equal(
            expected: new Vector2(x: 300f, y: 200f),
            actual: BindingWheelGeometry.ResolveSpatialTargetVector(BindingWheelSpatialSelectionMode.HitTarget, position: openingInput, neutral: openingInput, hub: centeredHub)
        );
    }

    [Fact]
    public void ExcursionPolicyRoundTripsAsAuthoredData() {
        var json = JsonSerializer.Serialize(value: ExcursionProfile(), jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument);

        Assert.Contains(expectedSubstring: "\"ringSelection\": \"Excursion\"", actualString: json);
        Assert.Contains(expectedSubstring: "\"deadZone\": 0.15", actualString: json);
        Assert.Contains(expectedSubstring: "\"thresholds\": [", actualString: json);
        Assert.Contains(expectedSubstring: "\"spatialTravelFraction\": 0.2", actualString: json);
        Assert.Contains(expectedSubstring: "\"hysteresis\": 0.02", actualString: json);

        var roundTripped = JsonSerializer.Deserialize(json: json, jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument);
        var style = roundTripped!.Wheels![0].Style!;

        Assert.Equal(expected: BindingWheelRingSelectionMode.Excursion, actual: style.RingSelection);
        Assert.Equal(expected: [0.35f, 0.70f], actual: style.Excursion!.Thresholds);
    }

    [Fact]
    public void ExcursionRequiresExactlyOneBoundaryBetweenEachRing() {
        var document = ExcursionProfile();
        var wheel = document.Wheels![0];
        var invalid = document with {
            Wheels = [wheel with {
                Style = wheel.Style! with {
                    Excursion = wheel.Style.Excursion! with { Thresholds = [0.35f] },
                },
            }],
        };

        _ = Assert.Throws<ArgumentException>(testCode: () => BindingProfile.Compile(document: invalid));
    }

    [Fact]
    public void HitTargetCanUseNeutralRelativeExcursionWithoutConflatingTheTwoOrigins() {
        var document = ExcursionProfile();
        var wheel = document.Wheels![0];
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(document: document with {
            Wheels = [wheel with {
                Style = wheel.Style! with { PointerSelection = BindingWheelSpatialSelectionMode.HitTarget },
            }],
        }));

        _ = bindings.Resolve(slot: 0, signal: InputSignal.Press(source: "keyboard.tab"));
        var view = bindings.WheelFor(slot: 0)!;
        var neutral = new Vector2(x: 100f, y: 100f);
        var hub = new Vector2(x: 200f, y: 100f);
        var position = new Vector2(x: 230f, y: 100f);
        var neutralVector = (position - neutral);
        var normalized = BindingWheelGeometry.NormalizeSpatialExcursion(vector: neutralVector, viewportUnit: 100f, excursion: view.Excursion!);
        var ring = BindingWheelGeometry.ResolveExcursionRing(vector: normalized, excursion: view.Excursion!, previousRing: -1);
        var targetingVector = BindingWheelGeometry.ResolveSpatialTargetVector(
            mode: view.Style.PointerSelection,
            position: position,
            neutral: neutral,
            hub: hub
        );
        var selection = BindingWheelGeometry.SelectSpatial(
            vector: targetingVector,
            sectorCount: view.Rings[ring].Sectors.Count,
            ringCount: view.Rings.Count,
            style: view.Style,
            mode: view.Style.PointerSelection,
            unit: 100f
        );

        Assert.Equal(expected: 2, actual: ring);
        Assert.Equal(expected: new Vector2(x: 30f, y: 0f), actual: targetingVector);
        Assert.Equal(expected: BindingWheelSelectionOutcome.Sector, actual: selection.Outcome);
    }

    [Fact]
    public void AngleSelectionIgnoresOuterDistanceWhileHitTargetRetainsIt() {
        var angle = new BindingWheelStyleDefinition(
            PointerSelection: BindingWheelSpatialSelectionMode.Angle,
            DeadZoneFraction: 0.1f,
            RingWidthFraction: 0.1f,
            OuterGraceRingFraction: 0f
        );

        var selection = BindingWheelGeometry.SelectSpatial(
            vector: new Vector2(x: 1_000f, y: 0f),
            sectorCount: 4,
            ringCount: 1,
            style: angle,
            mode: BindingWheelSpatialSelectionMode.Angle,
            unit: 100f
        );

        Assert.Equal(expected: 1, actual: selection.Sector);
        Assert.Equal(expected: BindingWheelSelectionOutcome.Sector, actual: selection.Outcome);

        var targeted = angle with { PointerSelection = BindingWheelSpatialSelectionMode.HitTarget };
        selection = BindingWheelGeometry.SelectSpatial(
            vector: new Vector2(x: 1_000f, y: 0f),
            sectorCount: 4,
            ringCount: 1,
            style: targeted,
            mode: BindingWheelSpatialSelectionMode.HitTarget,
            unit: 100f
        );

        Assert.Equal(expected: -1, actual: selection.Sector);
        Assert.Equal(expected: BindingWheelSelectionOutcome.Outside, actual: selection.Outcome);
    }

    [Fact]
    public void PlacementIsIndependentFromSelectionSource() {
        var pointer = new Vector2(x: 12f, y: 34f);
        var viewportCenter = new Vector2(x: 400f, y: 300f);

        Assert.Equal(
            expected: pointer,
            actual: BindingWheelGeometry.ResolveOpeningCenter(BindingWheelPlacement.Pointer, pointerAvailable: true, pointer: pointer, viewportCenter: viewportCenter)
        );
        Assert.Equal(
            expected: viewportCenter,
            actual: BindingWheelGeometry.ResolveOpeningCenter(BindingWheelPlacement.Pointer, pointerAvailable: false, pointer: pointer, viewportCenter: viewportCenter)
        );
        Assert.Equal(
            expected: viewportCenter,
            actual: BindingWheelGeometry.ResolveOpeningCenter(BindingWheelPlacement.ViewportCenter, pointerAvailable: true, pointer: pointer, viewportCenter: viewportCenter)
        );
    }

    [Fact]
    public void SelectionAndPlacementPoliciesRoundTripAsAuthoredTokens() {
        var json = JsonSerializer.Serialize(value: Profile(), jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument);

        Assert.Contains(expectedSubstring: "\"pointerSelection\": \"HitTarget\"", actualString: json);
        Assert.Contains(expectedSubstring: "\"placement\": \"ViewportCenter\"", actualString: json);

        var roundTripped = JsonSerializer.Deserialize(json: json, jsonTypeInfo: WorldJsonContext.Default.BindingProfileDocument);

        Assert.NotNull(@object: roundTripped);
        var style = roundTripped.Wheels![0].Style;
        Assert.NotNull(@object: style);
        Assert.Equal(expected: BindingWheelSpatialSelectionMode.HitTarget, actual: style.PointerSelection);
        Assert.Equal(expected: BindingWheelPlacement.ViewportCenter, actual: style.Placement);
    }

    [Fact]
    public void CancellationRemainsLatchedUntilTheNextGestureOpens() {
        var gesture = new BindingWheelGestureState();

        gesture.Open();

        Assert.True(condition: gesture.CanArm);

        gesture.Cancel();

        Assert.True(condition: gesture.Cancelled);
        Assert.False(condition: gesture.CanArm);

        gesture.Close();

        Assert.True(condition: gesture.Cancelled);
        Assert.False(condition: gesture.CanArm);

        gesture.Open();

        Assert.False(condition: gesture.Cancelled);
        Assert.True(condition: gesture.CanArm);
    }

    [Fact]
    public void AxisSelectionIsClearedAtBothGestureBoundaries() {
        var gesture = new BindingWheelGestureState();

        gesture.Open();
        gesture.Select(axis: new Vector2(x: 0.75f, y: -0.25f), sequence: 19L);

        Assert.True(condition: gesture.AxisKnown);
        Assert.Equal(expected: 19L, actual: gesture.AxisSequence);

        gesture.Close();

        Assert.False(condition: gesture.AxisKnown);
        Assert.Equal(expected: Vector2.Zero, actual: gesture.Axis);
        Assert.Equal(expected: 0L, actual: gesture.AxisSequence);

        gesture.Select(axis: Vector2.UnitX, sequence: 20L);
        gesture.Open();

        Assert.False(condition: gesture.AxisKnown);
        Assert.Equal(expected: Vector2.Zero, actual: gesture.Axis);
        Assert.Equal(expected: 0L, actual: gesture.AxisSequence);
    }

    [Fact]
    public void SpatialNeutralCapturesTheFirstAvailablePositionWithinEachGesture() {
        var gesture = new BindingWheelGestureState();
        var first = new Vector2(x: 120f, y: 80f);
        var later = new Vector2(x: 300f, y: 240f);

        Assert.False(condition: gesture.TryCaptureSpatialNeutral(position: first));

        gesture.Open();

        Assert.False(condition: gesture.SpatialNeutralKnown);
        Assert.True(condition: gesture.TryCaptureSpatialNeutral(position: first));
        Assert.False(condition: gesture.TryCaptureSpatialNeutral(position: later));
        Assert.Equal(expected: first, actual: gesture.SpatialNeutral);

        gesture.Close();

        Assert.False(condition: gesture.SpatialNeutralKnown);
        Assert.Equal(expected: Vector2.Zero, actual: gesture.SpatialNeutral);

        gesture.Open();

        Assert.True(condition: gesture.TryCaptureSpatialNeutral(position: later));
        Assert.Equal(expected: later, actual: gesture.SpatialNeutral);
    }

    [Fact]
    public void UnregisteredSectorDispatchRemainsADistinctCommitOutcome() {
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(document: Profile()));
        var registry = new CommandRegistry(modules: []);
        var router = new InputRouter(registry: registry, bindings: bindings, principalResolver: new FixedPrincipalResolver(CommandPrincipal.Console));

        _ = bindings.Resolve(slot: 2, signal: InputSignal.Press(source: "keyboard.tab"));
        var activation = bindings.WheelFor(slot: 2)!.Rings[0].Sectors[0].Activation;
        var outcome = BindingWheelCommitResult.Dispatch(
            router: router,
            slot: 2,
            activation: activation,
            label: "Missing",
            ring: 0,
            sector: 0
        );

        Assert.Equal(expected: BindingWheelCommitStatus.Unregistered, actual: outcome.Status);
        Assert.Equal(expected: TestModule.Command, actual: outcome.Command);
        Assert.Equal(expected: "unregistered", actual: outcome.Reason);
        Assert.Empty(collection: router.SnapshotForTick(tick: 7UL, windowEndTick: ulong.MaxValue).Lanes);
    }

    [Fact]
    public void SeveralSourcesCanHoldOneRadialAndAGroupCanCarryAnother() {
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(document: Profile()));

        _ = bindings.Resolve(slot: 2, signal: InputSignal.Press(source: "keyboard.tab"));
        var primary = bindings.WheelFor(slot: 2);

        Assert.NotNull(primary);
        Assert.Equal(expected: "primary", actual: primary.Id);
        Assert.Equal(expected: BindingWheelSpatialSelectionMode.HitTarget, actual: primary.Style.PointerSelection);
        Assert.Equal(expected: BindingWheelPlacement.ViewportCenter, actual: primary.Style.Placement);
        Assert.Equal(expected: 30f, actual: primary.Style.RotationDegrees);
        var selector = Assert.Single(collection: bindings.Resolve(slot: 2, source: "gamepad.leftStick")!);
        Assert.Equal(expected: "test.radial.select", actual: selector.Command);

        _ = bindings.Resolve(slot: 2, signal: InputSignal.Press(source: "gamepad.leftTrigger"));
        _ = bindings.Resolve(slot: 2, signal: InputSignal.Release(source: "keyboard.tab"));

        Assert.Same(expected: primary, actual: bindings.WheelFor(slot: 2));

        _ = bindings.Resolve(slot: 2, signal: InputSignal.Release(source: "gamepad.leftTrigger"));

        Assert.Null(bindings.WheelFor(slot: 2));

        _ = bindings.Resolve(slot: 2, signal: InputSignal.Press(source: "gamepad.rightTrigger"));

        Assert.Equal(expected: "secondary", actual: bindings.WheelFor(slot: 2)?.Id);
    }

    [Fact]
    public void ACompiledSectorReturnsThroughTheSeatsPrincipalDoor() {
        var bindings = new PagedInputBindings(profile: BindingProfile.Compile(document: Profile()));
        var registry = new CommandRegistry(modules: [new TestModule()]);
        var expectedPrincipal = CommandPrincipal.Peer(index: 17, generation: 4);
        var router = new InputRouter(registry: registry, bindings: bindings, principalResolver: new FixedPrincipalResolver(expectedPrincipal));

        _ = bindings.Resolve(slot: 2, signal: InputSignal.Press(source: "keyboard.tab"));
        var activation = bindings.WheelFor(slot: 2)!.Rings[0].Sectors[0].Activation;

        Assert.True(router.Activate(slot: 2, activation: activation));

        var snapshot = router.SnapshotForTick(tick: 7UL, windowEndTick: ulong.MaxValue);

        Assert.True(snapshot.TryGetLane(slot: 2, lane: out var lane));
        var entry = Assert.Single(collection: lane.Entries);
        Assert.Equal(expected: expectedPrincipal, actual: entry.Principal);
        Assert.Equal(expected: CommandPhase.Started, actual: entry.Phase);
        Assert.Equal(expected: CommandOrigin.Binding, actual: entry.Origin);
        Assert.Null(@object: entry.Source);
    }

    [Fact]
    public void ComposerKeysRadialsByIdRatherThanCollapsingAGroup() {
        var baseDocument = Profile();
        var overlay = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [Page(group: "play", chord: ["tab", "lt"], id: "third-page")],
            Wheels: [Wheel(id: "third", holdPages: ["third-page"], ringId: "third-ring")]
        );

        var composed = WorldBindingComposer.Compose(baseDocument, overlay);

        Assert.Equal(expected: 3, actual: composed.Wheels?.Count);
        _ = BindingProfile.Compile(document: composed);
    }

    private static BindingProfileDocument Profile() => new(
        Version: BindingProfileDocument.CurrentVersion,
        Modifiers: [
            new BindingModifierDefinition(Id: "tab", Source: "keyboard.tab"),
            new BindingModifierDefinition(Id: "lt", Source: "gamepad.leftTrigger"),
            new BindingModifierDefinition(Id: "rt", Source: "gamepad.rightTrigger"),
        ],
        Chords: [
            Page(group: "play", chord: [], id: "base"),
            Page(
                group: "play",
                chord: ["tab"],
                id: "tab-page",
                entries: [new BindingPageEntryDefinition(Source: "gamepad.leftStick", Command: "test.radial.select")]
            ),
            Page(group: "play", chord: ["lt"], id: "lt-page"),
            Page(group: "play", chord: ["rt"], id: "rt-page"),
        ],
        Wheels: [
            Wheel(id: "primary", holdPages: ["tab-page", "lt-page"], ringId: "primary-ring"),
            Wheel(id: "secondary", holdPages: ["rt-page"], ringId: "secondary-ring"),
        ]
    );

    private static BindingProfileDocument ExcursionProfile() {
        var profile = Profile();

        return profile with {
            Wheels = [
                new BindingWheelDefinition(
                    Id: "excursion",
                    Group: "play",
                    HoldPages: ["tab-page"],
                    Rings: [
                        Ring(id: "excursion-near"),
                        Ring(id: "excursion-middle"),
                        Ring(id: "excursion-far"),
                    ],
                    Style: new BindingWheelStyleDefinition(
                        PointerSelection: BindingWheelSpatialSelectionMode.Angle,
                        Placement: BindingWheelPlacement.ViewportCenter,
                        RingSelection: BindingWheelRingSelectionMode.Excursion,
                        Excursion: new BindingWheelExcursionDefinition(
                            DeadZone: 0.15f,
                            Thresholds: [0.35f, 0.70f],
                            SpatialTravelFraction: 0.20f,
                            Hysteresis: 0.02f
                        )
                    )
                ),
            ],
        };
    }

    private static BindingPageDefinition Ring(string id) => new(
        Id: id,
        Entries: [
            new BindingPageEntryDefinition(Source: null, Command: TestModule.Command),
            new BindingPageEntryDefinition(Source: null, Command: TestModule.Command),
        ]
    );

    private static BindingChordDefinition Page(string group, IReadOnlyList<string> chord, string id, IReadOnlyList<BindingPageEntryDefinition>? entries = null) => new(
        Group: group,
        Chord: chord,
        Page: new BindingPageDefinition(Id: id, Entries: (entries ?? []))
    );

    private static BindingWheelDefinition Wheel(string id, IReadOnlyList<string> holdPages, string ringId) => new(
        Id: id,
        Group: "play",
        HoldPages: holdPages,
        Rings: [
            new BindingPageDefinition(
                Id: ringId,
                Entries: [
                    new BindingPageEntryDefinition(Source: null, Command: TestModule.Command),
                    new BindingPageEntryDefinition(Source: null, Command: TestModule.Command),
                ]
            ),
        ],
        Style: new BindingWheelStyleDefinition(
            PointerSelection: BindingWheelSpatialSelectionMode.HitTarget,
            Placement: BindingWheelPlacement.ViewportCenter,
            DeadZoneFraction: 0.2f,
            RingWidthFraction: 0.08f,
            OuterGraceRingFraction: 0.25f,
            RotationDegrees: 30f,
            Clockwise: false,
            InitialRing: 0
        )
    );

    private sealed class FixedPrincipalResolver(CommandPrincipal principal) : ICommandPrincipalResolver {
        public CommandPrincipal PrincipalOf(int slot) => principal;
    }

    private sealed class TestModule : ICommandModule {
        public const string Command = "test.radial.act";

        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                bindability: CommandBindability.Bindable,
                name: Command,
                description: "Test radial act.",
                handler: static (context, args) => CommandResult.None
            );
        }
    }
}
