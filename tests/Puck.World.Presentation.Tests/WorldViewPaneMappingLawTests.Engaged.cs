using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Input;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class WorldViewPaneMappingLawTests {
    private static readonly NormalizedRect WholeDisplay = new(Height: 1f, Width: 1f, X: 0f, Y: 0f);
    // The matte a framed screen's picture fills: the middle of the display.
    private static readonly NormalizedRect Matted = new(Height: 0.5f, Width: 0.5f, X: 0.25f, Y: 0.25f);

    // An engageable screen showing a test pattern.
    private static (WorldScreenMappingSet Screens, string Instance) EngageableScreen() {
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
                    EngageRadius: 3f,
                    Engageable: true
                ),
                Source: pattern,
                Up: new Puck.Assets.Documents.DocumentVector3(value: Vector3.UnitY)
            )]
        );
        screens.Publish(images: new PatternImages(Height: ScreenSourceHeight, Width: ScreenSourceWidth));

        return (screens, screens.InstanceOf(screen: 0)!);
    }
    // One host frame with a lone whole-display view and, when `framed`, the screen framed on the matte.
    private void FramedFrame(bool framed) {
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

        if (framed) {
            m_host.FrameScreen(
                region: Matted,
                screen: 0
            );
            // A second seat framing the same screen publishes nothing more.
            m_host.FrameScreen(
                region: WholeDisplay,
                screen: 0
            );
            // A screen that publishes no mapping publishes nothing.
            m_host.FrameScreen(
                region: WholeDisplay,
                screen: 7
            );
        }

        m_host.PublishPanes(
            displayHeight: Display,
            displayWidth: Display
        );
        Schedule();
    }

    // THE LAW: a screen a seat frames publishes its own mapping over the matte its picture fills, the glass's bezel left
    // out since the matte is the picture, so a display point there maps to the source pixel the ray through the picture
    // meets. It is never outlined as a pane beside the view, a steady frame publishes it without allocating, and a frame
    // that frames nothing publishes none.
    [Fact]
    public void AFramedScreenPublishesItsMappingOverItsMatte() {
        var (screens, instance) = EngageableScreen();

        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: WholeDisplay));
        FramedFrame(framed: true);
        FramedFrame(framed: true);

        var shown = Assert.Single(collection: m_host.Panes);

        Assert.Equal(expected: instance, actual: shown.Source.Name);
        Assert.True(condition: m_host.ShowsFramed(instance: instance));
        Assert.Equal(expected: new SourcePlacement.Pane(Region: Matted), actual: shown.Placement);
        Assert.Null(@object: shown.Warp);
        Assert.Equal(expected: SourceDestination.Presentation, actual: shown.Destination);

        // The matte's top-left pixel is the source's top-left pixel, and its centre the source's centre.
        Assert.Equal(expected: (instance, 0L, 0L), actual: Picked(x: 16.1, y: 16.1));
        Assert.Equal(expected: (instance, ((long)(ScreenSourceWidth / 2)), ((long)(ScreenSourceHeight / 2))), actual: Picked(x: 32.0, y: 32.0));
        Assert.Null(@object: Picked(x: 8.5, y: 8.5));

        var before = GC.GetAllocatedBytesForCurrentThread();

        FramedFrame(framed: true);
        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));

        FramedFrame(framed: false);
        Assert.Empty(collection: m_host.Panes);
        Assert.False(condition: m_host.ShowsFramed(instance: instance));
    }
    // THE LAW: engaging a window capture's screen only shows the window. The local user's own press on its framed picture,
    // which only the window pump delivers to the filter, opens it as a passthrough source and reaches the window at
    // once; the source closes once the seat no longer frames the screen, as when it leaves.
    [Fact]
    public void APressOnAFramedWindowOpensItUntilTheSeatLeaves() {
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
        FramedFrame(framed: true);
        FramedFrame(framed: true);
        Assert.Contains(expectedSubstring: "none opened", actualString: passthrough.Describe());

        // A press on the matte's fill opens nothing.
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 4.5f, y: 4.5f)));
        Assert.False(condition: passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started)));
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed));
        Assert.Contains(expectedSubstring: "none opened", actualString: passthrough.Describe());

        // A press on the picture opens the window and reaches it, taking the keyboard.
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 32.5f, y: 32.5f)));
        Assert.True(condition: passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started)));
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed));
        Assert.Single(collection: window.Presses);
        Assert.Equal(expected: SourceDestination.Passthrough, actual: Assert.Single(collection: m_host.Panes).Destination);
        Assert.Contains(expectedSubstring: $"{instance} engaged window", actualString: passthrough.Describe());

        // The next frame publishes it as the passthrough source it now is.
        FramedFrame(framed: true);
        Assert.Equal(expected: SourceDestination.Passthrough, actual: Assert.Single(collection: m_host.Panes).Destination);

        // The seat leaves: nothing frames the screen, and the source closes.
        FramedFrame(framed: false);
        Assert.Contains(expectedSubstring: "none opened", actualString: passthrough.Describe());
        Assert.Null(@object: passthrough.Router.Focus.Focused);
    }
}
