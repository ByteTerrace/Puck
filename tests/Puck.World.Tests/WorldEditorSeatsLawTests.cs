using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a seat's editor state reads through to the document and folds back into it. A value no verb moved is the
/// document's; <see cref="WorldEditorSeats.Fold"/> hands back the authored section itself while seat 1 moved nothing,
/// and the section with exactly seat 1's moved values once it has, into an absent section too; a vacated seat forgets
/// what it moved. The engine's build layer rides every group's resting page with the toggle, adds the build group and
/// its context row, and stands aside for a world that binds nothing.
/// </summary>
public sealed class WorldEditorSeatsLawTests {
    private static readonly WorldEditorDefaults Authored = new(
        Grid: new WorldEditorGrid(Pitch: new Vector3(value: 2f), Visible: false),
        Snap: new WorldEditorSnap(AngleStepDegrees: 30f)
    );

    [Fact]
    public void AnUnmovedSeatFoldsTheAuthoredSectionItself() {
        var seats = new WorldEditorSeats();

        Assert.Same(actual: seats.Fold(authored: Authored), expected: Authored);
        Assert.Null(@object: seats.Fold(authored: null));
        Assert.Equal(actual: seats.GridOf(document: Authored, slot: 0), expected: Authored.ResolvedGrid);

        // A second seat's moves have no document home and never fold.
        seats.SetGridVisible(slot: 1, visible: true);
        Assert.Same(actual: seats.Fold(authored: Authored), expected: Authored);
    }
    [Fact]
    public void AMovedSeatFoldsExactlyItsMovedValues() {
        var seats = new WorldEditorSeats();

        seats.SetGridVisible(slot: 0, visible: true);
        seats.SetAngleStep(degrees: 45f, slot: 0);

        var folded = seats.Fold(authored: Authored)!;

        Assert.True(condition: folded.ResolvedGrid.Visible);
        Assert.Equal(actual: folded.ResolvedGrid.ResolvedPitch, expected: new Vector3(value: 2f));
        Assert.Equal(actual: folded.ResolvedSnap.AngleStepDegrees, expected: 45f);
        Assert.Equal(actual: folded.ResolvedCamera, expected: Authored.ResolvedCamera);

        // An absent section takes the moved values over the defaults.
        var fromAbsent = seats.Fold(authored: null)!;

        Assert.True(condition: fromAbsent.ResolvedGrid.Visible);
        Assert.Equal(actual: fromAbsent.ResolvedGrid.ResolvedPitch, expected: WorldEditorDefaults.Default.ResolvedGrid.ResolvedPitch);

        // A vacated seat forgets what it moved.
        seats.Reset(slot: 0);
        Assert.Same(actual: seats.Fold(authored: Authored), expected: Authored);
    }
    [Fact]
    public void TheBuildLayerRidesEveryRestingPageAndStandsAsideForAnUnboundWorld() {
        Assert.Null(@object: WorldEditorBindings.Layer(worldLayers: [null]));

        var world = new BindingProfileDocument(
            Chords: [new BindingChordDefinition(
                Chord: [],
                Group: "play",
                Page: new BindingPageDefinition(Entries: [], Id: "walk")
            )],
            Modifiers: [],
            Version: BindingProfileDocument.CurrentVersion
        );
        var layer = WorldEditorBindings.Layer(worldLayers: [world])!;

        Assert.Collection(
            collection: layer.Chords,
            elementInspectors: [
                play => {
                    Assert.Equal(actual: play.Group.Value, expected: "play");
                    Assert.Equal(actual: play.Page!.Id, expected: "walk");
                    Assert.Equal(actual: Assert.Single(collection: play.Page.Entries).Command, expected: WorldEditorBindings.ToggleCommand);
                },
                build => {
                    Assert.Equal(actual: build.Group.Value, expected: WorldEditorBindings.BuildGroup);
                    Assert.Same(actual: build.Page, expected: WorldEditorBindings.BuildPageDefinition);
                },
            ]
        );
        Assert.Equal(
            actual: Assert.Single(collection: layer.Contexts!),
            expected: new BindingContextDefinition(Family: WorldContextFamilies.Editor, Group: WorldEditorBindings.BuildGroup, State: WorldContextFamilies.EditorBuild)
        );
    }
    [Fact]
    public void TheBuildBarShowsEveryBuildControl() {
        var sources = WorldEditorBindings.BuildPageDefinition.Entries.SelectMany(selector: static entry => (entry.Sources ?? [])).ToHashSet(comparer: StringComparer.Ordinal);
        var bar = WorldEditorBindings.Bar;
        var placed = bar.LayoutNamed(name: null).Compile();

        Assert.Equal(actual: bar.SlotSet.ToHashSet(comparer: StringComparer.Ordinal), expected: sources);
        Assert.True(condition: (bar.SlotSet.Count <= WorldBindingBarCapacity.MaxSlots));
        Assert.Equal(actual: Assert.Single(collection: bar.Banks).PageId, expected: WorldEditorBindings.BuildPage);
        Assert.NotNull(@object: placed);
    }
}
