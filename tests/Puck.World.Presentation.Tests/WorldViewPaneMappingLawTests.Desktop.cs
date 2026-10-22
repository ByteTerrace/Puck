using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class WorldViewPaneMappingLawTests {
    // THE LAW: a framed desktop takes the pointer through its picture and the pad as its mouse, never the keyboard. A
    // press on the picture opens it and reaches it, while the keys stay the game's; the pad moves the desktop's cursor by
    // the route's feel in desktop heights, and presses, scrolls and types at it; while a seat frames it the chord is
    // claimed system-wide, and its press leaves in one step, releasing what the pad held and handing the keyboard back;
    // leaving any other way hands it back too.
    [Fact]
    public void AFramedDesktopTakesThePictureAndThePadAndTheChordLeaves() {
        var (screens, instance) = EngageableScreen();
        var desktop = new DesktopWindow();
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
                window: desktop
            )
        );

        m_host.Screens = screens;
        m_views.Add(item: new SdfViewSnapshot(Camera: Camera(x: 0f), Region: WholeDisplay));
        FramedFrame(framed: true);
        FramedFrame(framed: true);

        // No seat frames it: nothing is claimed and the pad reaches nothing.
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.False(condition: desktop.ClaimsChord);
        Assert.False(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: true, slot: 0));

        // Seat 1 frames it: the chord is claimed, and the pad's first press opens it at the cursor, wherever that is.
        anchor.PublishEngagedScreen(screenIndex: 0, slot: 0);
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.True(condition: desktop.ClaimsChord);
        Assert.True(condition: passthrough.PadButton(button: 0, pressed: true, slot: 0));
        Assert.True(condition: passthrough.PadButton(button: 0, pressed: false, slot: 0));
        Assert.False(condition: passthrough.PadButton(button: 0, pressed: false, slot: 0));
        Assert.Equal(expected: [(0, true), (0, false)], actual: desktop.Buttons);
        Assert.Contains(expectedSubstring: $"{instance} engaged window 'Desktop'", actualString: passthrough.Describe());
        Assert.Contains(expectedSubstring: "chord claimed", actualString: passthrough.Describe());

        // The pointer's move, press and release on the picture reach the desktop at its point, and take no keyboard.
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(x: 32.5f, y: 32.5f)));
        Assert.True(condition: passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started)));
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed));
        Assert.Equal(expected: 3, actual: desktop.Delivered);
        Assert.Null(@object: passthrough.Router.Focus.Focused);
        Assert.False(condition: passthrough.Intercept(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Space)));

        // A full right tilt held half a second at half a desktop height a second moves the cursor a quarter of the
        // desktop's 100-pixel height; a full up tilt held a quarter second at 12 notches a second turns 3 notches.
        Assert.True(condition: passthrough.PadPointer(slot: 0, value: Vector2.UnitX));
        time.Advance(seconds: 0.5);
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.Equal(expected: new Vector2(x: 25f, y: 0f), actual: Assert.Single(collection: desktop.Moves));
        Assert.True(condition: passthrough.PadPointer(slot: 0, value: Vector2.Zero));
        Assert.True(condition: passthrough.PadScroll(slot: 0, value: Vector2.UnitY));
        time.Advance(seconds: 0.25);
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.Equal(expected: new Vector2(x: 0f, y: 3f), actual: Assert.Single(collection: desktop.Wheels));
        Assert.True(condition: passthrough.PadScroll(slot: 0, value: Vector2.Zero));

        // An arrow types once per press; a held arrow and a held button are released when it leaves.
        Assert.True(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: true, slot: 0));
        Assert.False(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: true, slot: 0));
        Assert.True(condition: passthrough.PadKey(key: KeyCode.ArrowDown, pressed: true, slot: 0));
        Assert.True(condition: passthrough.PadKey(key: KeyCode.ArrowDown, pressed: false, slot: 0));
        Assert.True(condition: passthrough.PadButton(button: 1, pressed: true, slot: 0));

        // The chord, pressed in any window, leaves: the seat leaves, the source closes, the pad lets go and the game
        // has the keyboard again.
        desktop.Chords++;
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.Equal(expected: [0], actual: seats.Left);
        Assert.Contains(expectedSubstring: "none opened", actualString: passthrough.Describe());
        Assert.Equal(expected: (1, false), actual: desktop.Buttons[^1]);
        Assert.Equal(expected: [(KeyCode.ArrowUp, true), (KeyCode.ArrowDown, true), (KeyCode.ArrowDown, false), (KeyCode.ArrowUp, false)], actual: desktop.Keys);
        Assert.True(condition: (desktop.Returned > 0));
        Assert.False(condition: passthrough.PadKey(key: KeyCode.ArrowUp, pressed: false, slot: 0));

        // A seat that leaves the engagement any other way (the pad's context button, the console) gives the chord back
        // and brings the host's window to the front.
        var returned = desktop.Returned;

        anchor.PublishEngagedScreen(screenIndex: -1, slot: 0);
        Assert.Null(@object: passthrough.TakeWarp());
        Assert.False(condition: desktop.ClaimsChord);
        Assert.Equal(expected: (returned + 1), actual: desktop.Returned);
    }

    // A 200 by 100 pixel desktop that records what it is given.
    private sealed class DesktopWindow : ISourcePassthroughDesktop {
        public List<(int Button, bool Pressed)> Buttons { get; } = [];
        public int Chords { get; set; }
        public bool ClaimsChord { get; set; }
        public int Delivered { get; private set; }
        public int Height => 100;
        public List<(KeyCode Key, bool Pressed)> Keys { get; } = [];
        public List<Vector2> Moves { get; } = [];
        public int Returned { get; private set; }
        public string Title => "Desktop";
        public List<Vector2> Wheels { get; } = [];

        public void DeliverKey(in WindowInputEvent inputEvent) {
        }
        public void DeliverPointer(in WindowInputEvent inputEvent, SourcePassthroughPoint point) => Delivered++;
        public void MovePointer(Vector2 distance) => Moves.Add(item: distance);
        public void PressKey(KeyCode key, bool pressed) => Keys.Add(item: (key, pressed));
        public void PressPointer(int button, bool pressed) => Buttons.Add(item: (button, pressed));
        public void ReturnForeground() => Returned++;
        public bool TakeChord() {
            var pressed = (Chords > 0);

            Chords = 0;

            return pressed;
        }
        public bool TryDescribe(out SourcePassthroughWindow window) {
            window = new SourcePassthroughWindow(
                Client: SourcePixelRect.Whole(
                    height: 100,
                    width: 200
                ),
                DpiScale: 1f,
                FrameHeight: 100,
                FrameWidth: 200
            );

            return true;
        }
        public void TurnWheel(Vector2 notches) => Wheels.Add(item: notches);
    }
}
