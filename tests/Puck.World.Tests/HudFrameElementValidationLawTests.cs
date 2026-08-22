using System.Text.Json;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Authoring-time laws for a <see cref="WorldHudElementKind.Frame"/> HUD element: the shared
/// <c>ValidateFrameSource</c> gate, the kind/source pairing, and the radius/opacity ranges — plus its serialization
/// round trip through <see cref="WorldJsonContext"/>.</summary>
public sealed class HudFrameElementValidationLawTests {
    private static readonly WorldHudRect UnitRect = new(X: 0f, Y: 0f, Width: 1f, Height: 1f);

    private static WorldFrameSource CameraSource() => new WorldScreenSource.Camera(Sensor: WorldCameraSensor.Color);

    private static WorldHudElement FrameElement(WorldFrameSource? source, float radius = 0f, float opacity = 1f) => new(
        Id: "cam",
        Kind: WorldHudElementKind.Frame,
        Rect: UnitRect,
        Style: WorldHudStyleToken.Primary,
        Source: source,
        Mirror: true,
        Radius: radius,
        Opacity: opacity
    );

    private static WorldDefinition WithPanelElement(WorldHudElement element) => Fixtures.BuildDocument() with {
        HudRaw = new WorldHudSection(
            Defaults: new WorldHudDefaults(Enabled: true),
            Panels: [
                new WorldHudPanel(
                    Id: "face",
                    Rect: UnitRect,
                    Layer: WorldHudLayer.Over,
                    Style: WorldHudPanelStyle.Chip,
                    Elements: [element]
                ),
            ]
        ),
    };

    [Fact]
    public void AFrameElementWithNoSourceRefusesWhileASourcePasses() {
        Laws.RefusalWithControl(
            lawId: "hud.frame-source-required",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithPanelElement(element: FrameElement(source: null)),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithPanelElement(element: FrameElement(source: CameraSource())),
                reason: out _
            ));
    }
    [Fact]
    public void ANonFrameElementCarryingASourceRefusesWhileAFrameElementPasses() {
        var rectElement = new WorldHudElement(
            Id: "cam",
            Kind: WorldHudElementKind.Rect,
            Rect: UnitRect,
            Style: WorldHudStyleToken.Primary,
            Source: CameraSource()
        );

        Laws.RefusalWithControl(
            lawId: "hud.frame-source-not-allowed",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithPanelElement(element: rectElement),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithPanelElement(element: FrameElement(source: CameraSource())),
                reason: out _
            ));
    }
    [Fact]
    public void ANegativeFrameRadiusRefusesWhileAZeroRadiusPasses() {
        Laws.RefusalWithControl(
            lawId: "hud.frame-radius",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithPanelElement(element: FrameElement(source: CameraSource(), radius: -1f)),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithPanelElement(element: FrameElement(source: CameraSource(), radius: 0f)),
                reason: out _
            ));
    }
    [Fact]
    public void AnOutOfRangeFrameOpacityRefusesWhileOnePasses() {
        Laws.RefusalWithControl(
            lawId: "hud.frame-opacity",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithPanelElement(element: FrameElement(source: CameraSource(), opacity: 1.5f)),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithPanelElement(element: FrameElement(source: CameraSource(), opacity: 1f)),
                reason: out _
            ));
    }
    [Fact]
    public void AFrameElementViewSourceNamingNoCameraRefusesWhileADeclaredCameraPasses() {
        static WorldDefinition WithViewSource(bool declareCamera) {
            var document = WithPanelElement(element: FrameElement(source: new WorldScreenSource.View(CameraName: "gallery")));

            return (declareCamera
                ? (document with {
                    CamerasRaw = [
                        new WorldCamera(
                            Name: "gallery",
                            Anchor: null,
                            Rig: new WorldCameraProgram(
                                Name: "gallery-rig",
                                Version: WorldCameraProgram.CurrentVersion,
                                Operations: [new WorldCameraProgramOp.Fov(FieldOfViewRadians: new BindableScalar(literal: 0.9f))]
                            ),
                            RenderWidth: 320U,
                            RenderHeight: 240U
                        ),
                    ],
                })
                : document
            );
        }

        Laws.RefusalWithControl(
            lawId: "hud.frame-view-camera",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithViewSource(declareCamera: false),
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: WithViewSource(declareCamera: true),
                reason: out _
            ));
    }
    [Fact]
    public void AFrameElementRoundTripsThroughTheHudElementAccessor() {
        var element = FrameElement(
            source: new WorldScreenSource.Camera(Sensor: WorldCameraSensor.Color),
            radius: 12f,
            opacity: 0.75f
        ) with {
            Fit = WorldHudFrameFit.Contain,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            jsonTypeInfo: WorldJsonContext.Default.WorldHudElement,
            value: element
        );
        var roundTripped = JsonSerializer.Deserialize(
            jsonTypeInfo: WorldJsonContext.Default.WorldHudElement,
            utf8Json: bytes
        )!;

        Assert.Equal(expected: WorldHudElementKind.Frame, actual: roundTripped.Kind);
        Assert.Equal(expected: WorldHudFrameFit.Contain, actual: roundTripped.Fit);
        Assert.True(condition: roundTripped.Mirror);
        Assert.Equal(expected: 12f, actual: roundTripped.Radius);
        Assert.Equal(expected: 0.75f, actual: roundTripped.Opacity);

        var camera = Assert.IsType<WorldScreenSource.Camera>(roundTripped.Source);

        Assert.Equal(expected: WorldCameraSensor.Color, actual: camera.Sensor);
    }
}
