using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Hosting;
using Puck.Input;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class WorldViewPaneMappingLawTests {
    private static readonly NormalizedRect WholeDisplay = new(Height: 1f, Width: 1f, X: 0f, Y: 0f);

    // An engageable screen showing a test pattern, cropped 8 rows top and bottom at 8:7 pixels, its source scheduled at
    // the pattern's extent.
    private (WorldScreenMappingSet Screens, string Instance) EngageableScreen() {
        var pattern = WorldImageProducerSettings.SourceOf(
            id: WorldImageProducerSettings.TestPatternId,
            settings: new WorldTestPatternSettings(
                Height: ScreenSourceHeight,
                Width: ScreenSourceWidth
            )
        );
        var screens = new WorldScreenMappingSet(world: WorldDefinitionLoader.BootInstanceName);

        screens.Reconcile(
            cameras: [],
            screens: [new WorldScreen(
                HalfDepth: 0.1f,
                HalfHeight: 0.9f,
                HalfWidth: 1.2f,
                Index: 0,
                Origin: new Puck.Assets.Documents.DocumentVector3(value: new Vector3(x: 0f, y: 1f, z: 0f)),
                Right: new Puck.Assets.Documents.DocumentVector3(value: Vector3.UnitX),
                Round: 0f,
                Route: new WorldScreenRoute(
                    Display: new WorldScreenDisplay(
                        Crop: new WorldScreenDisplayCrop(Bottom: 8, Top: 8),
                        PixelAspect: (8f / 7f)
                    ),
                    Engageable: true,
                    EngageRadius: 3f
                ),
                Source: pattern,
                Up: new Puck.Assets.Documents.DocumentVector3(value: Vector3.UnitY)
            )]
        );
        screens.Publish(images: new PatternImages(Height: ScreenSourceHeight, Width: ScreenSourceWidth));

        var instance = screens.InstanceOf(screen: 0)!;

        m_sources = [new RenderGraphSourceState(
            Cadence: ImageSourceCadence.Static,
            Height: ScreenSourceHeight,
            Instance: instance,
            Width: ScreenSourceWidth
        )];

        return (screens, instance);
    }
    // One host frame with a lone whole-display view and, when `engaged`, the engaged screen's emulator view over it.
    private void EngagedFrame(bool engaged) {
        m_host.BeginFrame(
            tonemap: m_tonemap,
            views: m_document
        );
        m_host.PlaceViews(
            panesCover: false,
            rendered: static _ => true,
            sharpness: 0f,
            views: m_views
        );

        if (engaged) {
            _ = m_host.PlaceEngaged(
                displayHeight: Display,
                displayWidth: Display,
                frame: WholeDisplay,
                screen: 0
            );
        }

        m_host.PublishPanes(
            displayHeight: Display,
            displayWidth: Display
        );
        Schedule();
    }

    // THE LAW: the synthesized root places every engageable screen's source after the document's panes, and a seat whose
    // camera has framed that screen shows its source as an emulator view: its display's crop, at its pixel aspect,
    // centred and whole inside the seat's view, published as a pane the picker answers, never outlined as a pane beside
    // the view. A frame that places none publishes none.
    [Fact]
    public void AnEngagedScreensEmulatorViewShowsItsCropAtItsAspectOverTheSeatsView() {
        var (screens, instance) = EngageableScreen();

        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: WholeDisplay));
        EngagedFrame(engaged: true);
        EngagedFrame(engaged: true);
        EngagedFrame(engaged: true);

        Assert.Equal(expected: [Pane, instance], actual: m_host.Synthesized!.Panes);

        var shown = Assert.Single(collection: m_host.Panes);

        Assert.Equal(expected: instance, actual: shown.Source.Name);
        Assert.True(condition: m_host.ShowsEngaged(instance: instance));
        Assert.Equal(
            expected: new SourcePixelRect(Height: (shown.SourceHeight - 16), Width: shown.SourceWidth, X: 0, Y: 8),
            actual: shown.Crop
        );
        Assert.True(condition: WorldEmulatorView.TryFit(
            crop: out _,
            display: screens.Screens[0].Route.Display,
            displayHeight: Display,
            displayWidth: Display,
            drawn: out var drawn,
            frame: WholeDisplay,
            sourceHeight: shown.SourceHeight,
            sourceWidth: shown.SourceWidth
        ));
        Assert.Equal(expected: new SourcePlacement.Pane(Region: drawn), actual: shown.Placement);
        Assert.Equal(expected: instance, actual: Picked(x: 32.5, y: 32.5)?.Source);

        // The root places the instance once a frame, however many seats frame it.
        Assert.False(condition: m_host.PlaceEngaged(displayHeight: Display, displayWidth: Display, frame: WholeDisplay, screen: 0));
        // A screen that shows no source instance places nothing.
        Assert.False(condition: m_host.PlaceEngaged(displayHeight: Display, displayWidth: Display, frame: WholeDisplay, screen: 7));

        EngagedFrame(engaged: false);
        Assert.Empty(collection: m_host.Panes);
        Assert.False(condition: m_host.ShowsEngaged(instance: instance));
    }
    // THE LAW: engaging a window capture's screen only shows the window. The local user's own press on the emulator
    // view, which only the window pump delivers to the filter, opens it as a passthrough source and reaches the window
    // at once; the source closes once the view stops showing it, as when the seat leaves.
    [Fact]
    public void APressOnAnEngagedWindowsEmulatorViewOpensItUntilTheViewEnds() {
        var (screens, instance) = EngageableScreen();
        var window = new PassthroughWindow();
        var passthrough = new WorldSourcePassthrough(
            graphs: m_host,
            viewports: new WorldSeatViewports(),
            windows: new InstanceWindows(
                name: instance,
                window: window
            )
        );

        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: WholeDisplay));
        EngagedFrame(engaged: true);
        EngagedFrame(engaged: true);
        EngagedFrame(engaged: true);
        Assert.Contains(expectedSubstring: "none opened", actualString: passthrough.Describe());

        // A press off the view (on the fill) opens nothing.
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 32.5f, y: 0.5f)));
        Assert.False(condition: passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started)));
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed));
        Assert.Contains(expectedSubstring: "none opened", actualString: passthrough.Describe());

        // A press on it opens the window and reaches it, taking the keyboard.
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 32.5f, y: 32.5f)));
        Assert.True(condition: passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started)));
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed));
        Assert.Single(collection: window.Presses);
        Assert.Equal(expected: SourceDestination.Passthrough, actual: Assert.Single(collection: m_host.Panes).Destination);
        Assert.Contains(expectedSubstring: $"{instance} engaged window", actualString: passthrough.Describe());

        // The seat leaves: the view ends, and so does the source.
        EngagedFrame(engaged: false);
        Assert.Contains(expectedSubstring: "none opened", actualString: passthrough.Describe());
        Assert.Null(@object: passthrough.Router.Focus.Focused);
    }
}
