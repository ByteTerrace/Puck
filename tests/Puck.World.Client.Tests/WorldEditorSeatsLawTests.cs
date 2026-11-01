using System.Numerics;
using Puck.Commands;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: a seat's editor state reads through to the document and folds back into it. A value no verb moved is the
/// document's; <see cref="WorldEditorSeats.Fold"/> hands back the authored section itself while seat 1 moved nothing or
/// the document authors none, and the section with exactly seat 1's moved values once it has; a vacated seat forgets
/// what it moved. The build layer rides every group's resting page with the world's authored toggle, and stands aside
/// for a world that binds nothing or authors no toggle.
/// </summary>
public sealed class WorldEditorSeatsLawTests {
    private static readonly BindingPageEntryDefinition Toggle = new(Command: WorldEditorBindings.ToggleCommand, Id: "editor.toggle", Sources: ["keyboard.f2"]);
    private static readonly WorldEditorDefaults Authored = new(
        Bar: WorldBindingBarAuthoring.Absent,
        Camera: new WorldEditorCamera(FlyRate: 6f, MaxDistance: 14f, MaxPitch: 1.35f, MinDistance: 0.5f, MinPitch: 0.05f, OrbitRate: 2.4f, PanRate: 3f, ZoomRate: 1.2f),
        Grid: new WorldEditorGrid(LineWidth: 1.5f, Mode: WorldEditorGridMode.Surface, Pitch: new Vector3(value: 2f), PlaneY: 0f, Visible: false),
        ReadOnlyLabel: "read-only",
        Snap: new WorldEditorSnap(AngleStepDegrees: 30f, Enabled: false, ObjectPatchRadius: 2.5f, Surface: true),
        Toggle: Toggle
    );

    [Fact]
    public void AnUnmovedSeatFoldsTheAuthoredSectionItself() {
        var seats = new WorldEditorSeats();

        Assert.Same(actual: seats.Fold(authored: Authored), expected: Authored);
        Assert.Null(@object: seats.Fold(authored: null));
        Assert.Equal(actual: seats.GridOf(document: Authored, slot: 0), expected: Authored.Grid);

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

        Assert.True(condition: folded.Grid.Visible);
        Assert.Equal(actual: folded.Grid.Pitch.Value, expected: new Vector3(value: 2f));
        Assert.Equal(actual: folded.Snap.AngleStepDegrees, expected: 45f);
        Assert.Equal(actual: folded.Camera, expected: Authored.Camera);

        // A document that authors no section has nothing to fold into.
        Assert.Null(@object: seats.Fold(authored: null));

        // A vacated seat forgets what it moved.
        seats.Reset(slot: 0);
        Assert.Same(actual: seats.Fold(authored: Authored), expected: Authored);
    }
    [Fact]
    public void TheBuildLayerRidesEveryRestingPageAndStandsAsideForAnUnboundWorld() {
        var world = new BindingProfileDocument(
            Chords: [new BindingChordDefinition(
                Chord: [],
                Group: "play",
                Page: new BindingPageDefinition(Entries: [], Id: "walk")
            )],
            Modifiers: [],
            Version: BindingProfileDocument.CurrentVersion
        );

        Assert.Null(@object: WorldEditorBindings.Layer(toggle: Toggle, worldLayers: [null]));
        Assert.Null(@object: WorldEditorBindings.Layer(toggle: null, worldLayers: [world]));

        var layer = WorldEditorBindings.Layer(toggle: Toggle, worldLayers: [world])!;
        var play = Assert.Single(collection: layer.Chords);

        Assert.Equal(actual: play.Group.Value, expected: "play");
        Assert.Equal(actual: play.Page!.Id, expected: "walk");
        Assert.Same(actual: Assert.Single(collection: play.Page.Entries), expected: Toggle);
    }
}
