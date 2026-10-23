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
        var passthrough = Door(windows: new InstanceWindows(
            name: instance,
            window: window
        ));

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
    // THE LAW: a local pad drives the window its seat frames, through the door a physical mouse takes. Its stick moves
    // the pointer across the framed picture (the system cursor follows, TakeWarp), clamped to the picture, at the route's
    // feel; its press on the picture opens the window and reaches it; its stick scrolls and its arrows type only on the
    // opened window; a seat that frames nothing drives nothing. Control+Alt+Escape leaves in one step: it returns the
    // keyboard, closes the window, releases what the pad held and leaves the seat's engagement, whoever had the keyboard.
    [Fact]
    public void APadDrivesTheFramedWindowAndTheChordLeavesInOneStep() {
        var (screens, instance) = EngageableScreen();
        var window = new PassthroughWindow();
        var anchor = new WorldPerceptionAnchor();
        var seats = new Seats(feel: new WorldScreenPadPointer(
            ScrollSpeed: 12f,
            Speed: 0.5f
        ));
        var time = new ManualTime();
        var passthrough = Door(
            anchor: anchor,
            seats: seats,
            time: time,
            windows: new InstanceWindows(
                name: instance,
                window: window
            )
        );

        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: WholeDisplay));
        FramedFrame(framed: true);
        FramedFrame(framed: true);

        // No seat is captured on the screen: the pad drives nothing.
        Assert.False(condition: passthrough.PadButton(button: 0, pressed: true, slot: 0));
        Assert.False(condition: passthrough.PadPointer(slot: 0, value: Vector2.UnitX));
        Assert.False(condition: passthrough.PadScroll(slot: 0, value: Vector2.UnitY));
        Assert.False(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: true, slot: 0));
        Assert.True(condition: passthrough.PadPointer(slot: 0, value: Vector2.Zero));
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.EndsWith(expectedEndString: "; pad idle", actualString: passthrough.Describe());
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => passthrough.PadButton(button: 5, pressed: true, slot: 0));

        // Seat 1 is captured on it. The pointer resting on the framed picture shows the system arrow before the window
        // is opened; on the fill it is the game's.
        anchor.PublishEngagedScreen(screenIndex: 0, slot: 0);
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 32.5f, y: 32.5f)));
        Assert.True(condition: passthrough.HoldsPointer);
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 4.5f, y: 4.5f)));
        Assert.False(condition: passthrough.HoldsPointer);

        // A full right tilt held half a second at half a picture height a second travels a quarter of the 32-pixel
        // picture's height, from its centre since the pointer lies on the fill. A long tilt stops at the picture's edge.
        Assert.True(condition: passthrough.PadPointer(slot: 0, value: Vector2.UnitX));
        time.Advance(seconds: 0.5);
        Assert.Equal(expected: (40, 32), actual: passthrough.TakeWarp());
        time.Advance(seconds: 10.0);
        Assert.Equal(expected: (47, 32), actual: passthrough.TakeWarp());
        Assert.Contains(expectedSubstring: $"; pad p1 screen 0 '{instance}' stick (1, 0) scroll (0, 0) buttons none keys none feel speed 0.5 curve 1 boost 1 ramp 0s scroll 12/s", actualString: passthrough.Describe());
        Assert.Empty(collection: window.Moves);

        // Its arrows type on no window until the local user opens it.
        Assert.False(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: true, slot: 0));
        Assert.False(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: false, slot: 0));

        // The pad's press on the picture opens the window and reaches it at the pointer, taking the keyboard; its
        // release goes where the press went.
        Assert.True(condition: passthrough.PadButton(button: 0, pressed: true, slot: 0));
        Assert.Single(collection: window.Presses);
        Assert.Contains(expectedSubstring: $"{instance} engaged window", actualString: passthrough.Describe());
        Assert.Contains(expectedSubstring: "buttons left", actualString: passthrough.Describe());
        Assert.Equal(expected: passthrough.Router.Focus.Focused?.Name, actual: instance);
        Assert.True(condition: passthrough.PadButton(button: 0, pressed: false, slot: 0));
        Assert.Single(collection: window.Releases);
        Assert.False(condition: passthrough.PadButton(button: 0, pressed: false, slot: 0));

        // Now the window hears the pointer move.
        Assert.True(condition: passthrough.PadPointer(slot: 0, value: -Vector2.UnitX));
        time.Advance(seconds: 0.5);
        Assert.Equal(expected: (39, 32), actual: passthrough.TakeWarp());
        Assert.NotEmpty(collection: window.Moves);
        Assert.True(condition: passthrough.PadPointer(slot: 0, value: Vector2.Zero));

        // The cursor's own echo of the pad's move keeps the pad's exact pointer; the mouse moving elsewhere moves it
        // with it, and a pointer the mouse left on the fill is put back on the picture before the wheel turns there.
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 39f, y: 32f)));
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 4.5f, y: 4.5f)));
        Assert.True(condition: passthrough.PadScroll(slot: 0, value: Vector2.UnitY));
        time.Advance(seconds: 0.25);
        Assert.Equal(expected: (32, 32), actual: passthrough.TakeWarp());
        Assert.Equal(expected: new Vector2(x: 0f, y: 3f), actual: Assert.Single(collection: window.Wheels));
        Assert.True(condition: passthrough.PadScroll(slot: 0, value: Vector2.Zero));

        // An arrow types on the opened window, once per press.
        Assert.True(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: true, slot: 0));
        Assert.False(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: true, slot: 0));
        Assert.Contains(expectedSubstring: "keys ArrowUp", actualString: passthrough.Describe());
        Assert.Equal(expected: [(KeyCode.ArrowUp, CommandPhase.Started)], actual: window.Keys);

        // Control+Alt+Escape while the window holds the keyboard: the Escape reaches nobody, the keyboard returns, the
        // window closes and hears the held arrow's release, and the seat leaves.
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.ControlLeft));
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.AltLeft));
        Assert.True(condition: passthrough.Intercept(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape)));
        Assert.True(condition: passthrough.Intercept(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Escape)));
        Assert.Equal(expected: [0], actual: seats.Left);
        Assert.Null(@object: passthrough.Router.Focus.Focused);
        Assert.Contains(expectedSubstring: "none opened", actualString: passthrough.Describe());
        Assert.EndsWith(expectedEndString: "; pad idle", actualString: passthrough.Describe());
        Assert.Equal(expected: (KeyCode.ArrowUp, CommandPhase.Completed), actual: window.Keys[^1]);
        Assert.False(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: false, slot: 0));

        // While the seat still frames the window, the chord is the host's although the game has the keyboard.
        Assert.True(condition: passthrough.Intercept(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape)));
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.Escape));
        Assert.Equal(expected: [0, 0], actual: seats.Left);

        // A seat that stops framing rests the pad, and the chord's Escape is the game's again.
        Assert.True(condition: passthrough.PadPointer(slot: 0, value: Vector2.UnitX));
        anchor.PublishEngagedScreen(screenIndex: -1, slot: 0);
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.Equal(expected: -1, actual: passthrough.Pad.Slot);
        Assert.False(condition: passthrough.Intercept(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Escape)));
    }
    // THE LAW: a route that authors no pointer feel moves and scrolls nothing from a stick, and says so.
    [Fact]
    public void ARouteWithNoPointerFeelMovesNothingFromAStick() {
        var (screens, instance) = EngageableScreen();
        var anchor = new WorldPerceptionAnchor();
        var time = new ManualTime();
        var passthrough = Door(
            anchor: anchor,
            time: time,
            windows: new InstanceWindows(
                name: instance,
                window: new PassthroughWindow()
            )
        );

        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: WholeDisplay));
        FramedFrame(framed: true);
        FramedFrame(framed: true);
        anchor.PublishEngagedScreen(screenIndex: 0, slot: 0);

        Assert.True(condition: passthrough.PadPointer(slot: 0, value: Vector2.UnitX));
        Assert.True(condition: passthrough.PadScroll(slot: 0, value: Vector2.UnitY));
        Assert.Null(@object: passthrough.TakeWarp());
        time.Advance(seconds: 1.0);
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.EndsWith(expectedEndString: "feel none (the route authors no padPointer)", actualString: passthrough.Describe());
    }

    // The passthrough door over this law's graph host, with no seat captured unless the law publishes one.
    private WorldSourcePassthrough Door(InstanceWindows windows, WorldPerceptionAnchor? anchor = null, Seats? seats = null, ManualTime? time = null) => new(
        anchor: (anchor ?? new WorldPerceptionAnchor()),
        graphs: m_host,
        seats: (seats ?? new Seats(feel: null)),
        time: (time ?? new ManualTime()),
        viewports: new WorldSeatViewports(),
        windows: windows
    );

    // The seats' feel for screen 0, and the seats the chord made leave.
    private sealed class Seats(WorldScreenPadPointer? feel) : IWorldPassthroughSeats {
        public List<int> Left { get; } = [];

        public WorldScreenPadPointer? PadPointerOf(int screen) => ((screen == 0)
            ? feel
            : null);
        public void Leave(int slot) => Left.Add(item: slot);
    }
    // A clock the law advances by hand, in whole ticks of a second's ten millionth.
    private sealed class ManualTime : TimeProvider {
        private long m_ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(double seconds) => m_ticks += ((long)(seconds * TimeSpan.TicksPerSecond));
        public override long GetTimestamp() => m_ticks;
    }
}
