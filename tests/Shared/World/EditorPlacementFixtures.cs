using System.Numerics;

namespace Puck.World.Testing;

/// <summary>The editor placement world: one crate prototype and a seat to place from.</summary>
internal static class EditorPlacementFixtures {
    // One crate, standing a tenth off the half-unit lattice on x.
    internal static HostRow Build() {
        var document = Fixtures.BuildDocument();

        return HostRow.Build(
            definition: (document with {
                CreationsRaw = [Crate],
                EditorRaw = Editor,
                PlacementsRaw = (document.PlacementsRaw! with {
                    Rows = [new WorldPlacement(
                        Id: "crate1",
                        Position: new Vector3(x: 1.1f, y: 3f, z: -1f),
                        PrototypeId: Crate.Id,
                        Scale: 1f,
                        YawDegrees: 0f
                    )],
                }),
            }).WithRestingPage(),
            name: "boot"
        );
    }

    internal static WorldPrototype Crate { get; } = CreationFixtures.UnitSphere(id: "crate");
    // The standard editor's values (worlds/standard/editor.puck), with the one-bank bar the validator requires.
    internal static WorldEditorDefaults Editor { get; } = new(
        Bar: EditorBarFixtures.Bar,
        Camera: new WorldEditorCamera(FlyRate: 6f, MaxDistance: 14f, MaxPitch: 1.35f, MinDistance: 0.5f, MinPitch: 0.05f, OrbitRate: 2.4f, PanRate: 3f, ZoomRate: 1.2f),
        Grid: new WorldEditorGrid(LineWidth: 1.5f, Mode: WorldEditorGridMode.Surface, Pitch: new Vector3(value: 0.5f), PlaneY: 0f, Visible: false),
        ReadOnlyLabel: "Build (read-only)",
        Snap: new WorldEditorSnap(AngleStepDegrees: 15f, Enabled: false, ObjectPatchRadius: 2.5f, Surface: true),
        Toggle: new Puck.Commands.BindingPageEntryDefinition(Command: "player.build", Id: "editor.toggle", Sources: ["keyboard.f2", "gamepad.back"])
    );
}
