using Puck.Assets.Documents;
using Puck.Commands;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>CONTRACT UNDER TEST: an invalid <c>editor</c> value is refused by its document path, and a valid section
/// passes.</summary>
public sealed class WorldEditorSectionLawTests {
    private static readonly WorldEditorDefaults Valid = new(
        Bar: WorldBindingBarAuthoring.Absent,
        Camera: new WorldEditorCamera(FlyRate: 6f, MaxDistance: 14f, MaxPitch: 1.35f, MinDistance: 0.5f, MinPitch: 0.05f, OrbitRate: 2.4f, PanRate: 3f, ZoomRate: 1.2f),
        Grid: new WorldEditorGrid(LineWidth: 1.5f, Mode: WorldEditorGridMode.Surface, Pitch: new DocumentVector3(x: 1f, y: 1f, z: 1f), PlaneY: 0f, Visible: false),
        ReadOnlyLabel: "read-only",
        Snap: new WorldEditorSnap(AngleStepDegrees: 15f, Enabled: false, ObjectPatchRadius: 2.5f, Surface: true),
        Toggle: new BindingPageEntryDefinition(Command: "player.build", Id: "editor.toggle", Sources: ["keyboard.f2"])
    );

    private static List<string> Errors(WorldEditorDefaults editor) {
        var errors = new List<string>();

        _ = WorldDefinitionValidator.TryValidateLocally(
            compilation: out _,
            definition: (Fixtures.BuildDocument() with { EditorRaw = editor }),
            errors: errors
        );

        return errors;
    }

    [Fact]
    public void AnInvalidEditorValueIsRefusedByItsPath() {
        Assert.DoesNotContain(
            collection: Errors(editor: Valid),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.")
        );
        Assert.Contains(
            collection: Errors(editor: (Valid with { Grid = (Valid.Grid with { Pitch = new DocumentVector3(x: 0.5f, y: 0f, z: 0.5f) }) })),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.grid.pitch[1]")
        );
        Assert.Contains(
            collection: Errors(editor: (Valid with { Snap = (Valid.Snap with { AngleStepDegrees = 0f }) })),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.snap.angleStepDegrees")
        );
        Assert.Contains(
            collection: Errors(editor: (Valid with { Camera = (Valid.Camera with { MaxDistance = 0.25f }) })),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.camera.minDistance")
        );
        Assert.Contains(
            collection: Errors(editor: (Valid with { Toggle = (Valid.Toggle with { Id = null }) })),
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "editor.toggle")
        );
    }
}
