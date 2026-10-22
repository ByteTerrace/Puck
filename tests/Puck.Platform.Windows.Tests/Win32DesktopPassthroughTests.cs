using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Commands;
using Puck.Input;
using Puck.Platform.Windows.Interop;
using Xunit;

namespace Puck.Platform.Windows.Tests;

// The desktop's input is recorded rather than sent, so the law moves no cursor and presses nothing on the machine.
public sealed partial class Win32DesktopPassthroughTests {
    private const uint MouseAbsoluteMove = 0x0001 | 0x8000 | 0x4000;

    private static SourcePassthroughPoint At(float x, float y) => new(
        InClient: true,
        Logical: new Vector2(
            x: x,
            y: y
        ),
        Physical: new Vector2(
            x: x,
            y: y
        )
    );
    // The parameter a construction refuses, by name.
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.14393")]
    private static string? Refusal(nint monitorHandle, string? title) {
        try {
            _ = new Win32DesktopPassthrough(
                monitorHandle: monitorHandle,
                title: title!
            );

            return null;
        } catch (ArgumentException refused) {
            return refused.ParamName;
        }
    }
    private static nint Primary() => Native.MonitorFromPoint(
        flags: 1,
        point: 0L
    );

    // THE LAW: a press on the picture is replayed at its release as one batch (move, press, move to the release, release,
    // move back); a wheel turn moves there, turns and moves back; a move reaches nothing and a key nothing; the pad's
    // moves, presses, keys and wheel each send their one event; the desktop is the whole monitor at a scale of 1.
    [Fact]
    public void ThePictureAndThePadReachTheDesktopAsTheSystemsOwnInput() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 14393)) { Assert.Skip(reason: "Requires Win32 with per-thread DPI contexts."); return; }

        List<SystemInput[]> sent = [];
        var desktop = new Win32DesktopPassthrough(
            monitorHandle: Primary(),
            send: sent.Add,
            title: "Desktop"
        );

        Assert.Equal(expected: "Desktop", actual: desktop.Title);
        Assert.True(condition: desktop.TryDescribe(window: out var described));
        Assert.Equal(expected: 1f, actual: described.DpiScale);
        Assert.Equal(expected: described.FrameHeight, actual: desktop.Height);
        Assert.Equal(expected: described.FrameWidth, actual: described.Client.Width);

        desktop.DeliverKey(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.Space));
        desktop.DeliverPointer(inputEvent: WindowInputEvent.PointerAbsolute(position: Vector2.Zero), point: At(x: 4f, y: 4f));
        desktop.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started), point: At(x: 4f, y: 4f));
        Assert.Empty(collection: sent);

        desktop.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed), point: At(x: 6f, y: 4f));
        var replay = Assert.Single(collection: sent);

        Assert.Equal(expected: [MouseAbsoluteMove, 0x0002u, MouseAbsoluteMove, 0x0004u, MouseAbsoluteMove], actual: replay.Select(selector: x => x.Union.Mouse.Flags));
        Assert.All(collection: replay, action: x => Assert.Equal(expected: SystemInput.Mouse, actual: x.Type));
        Assert.True(condition: (replay[2].Union.Mouse.X > replay[0].Union.Mouse.X));

        // A release with no press, and a button the mouse has none of, replay nothing.
        desktop.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 1, phase: CommandPhase.Completed), point: At(x: 6f, y: 4f));
        desktop.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 7, phase: CommandPhase.Started), point: At(x: 6f, y: 4f));
        desktop.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 7, phase: CommandPhase.Completed), point: At(x: 6f, y: 4f));
        Assert.Single(collection: sent);

        desktop.DeliverPointer(inputEvent: WindowInputEvent.PointerWheel(notches: new Vector2(x: 1f, y: -0.5f)), point: At(x: 4f, y: 4f));
        Assert.Equal(expected: [MouseAbsoluteMove, 0x0800u, 0x1000u, MouseAbsoluteMove], actual: sent[^1].Select(selector: x => x.Union.Mouse.Flags));
        Assert.Equal(expected: [-60, 120], actual: sent[^1][1..3].Select(selector: x => x.Union.Mouse.Data));

        // The pad: a move, a press and release of the back button, a key, and a wheel turn, one event each.
        sent.Clear();
        desktop.MovePointer(distance: new Vector2(x: 3.5f, y: 0f));
        desktop.PressPointer(button: 3, pressed: true);
        desktop.PressPointer(button: 3, pressed: false);
        desktop.PressPointer(button: 9, pressed: true);
        desktop.PressKey(key: KeyCode.ArrowUp, pressed: true);
        desktop.PressKey(key: KeyCode.ArrowUp, pressed: false);
        desktop.PressKey(key: KeyCode.None, pressed: true);
        desktop.TurnWheel(notches: Vector2.Zero);
        desktop.TurnWheel(notches: Vector2.UnitY);
        Assert.Equal(expected: 6, actual: sent.Count);
        Assert.Equal(expected: MouseAbsoluteMove, actual: Assert.Single(collection: sent[0]).Union.Mouse.Flags);
        Assert.Equal(expected: (0x0080u, 1), actual: (sent[1][0].Union.Mouse.Flags, sent[1][0].Union.Mouse.Data));
        Assert.Equal(expected: 0x0100u, actual: sent[2][0].Union.Mouse.Flags);
        Assert.Equal(expected: SystemInput.Keyboard, actual: sent[3][0].Type);
        Assert.Equal(expected: 0x0002u, actual: (sent[4][0].Union.Keyboard.Flags & 0x0002u));
        Assert.Equal(expected: (0x0800u, 120), actual: (sent[5][0].Union.Mouse.Flags, sent[5][0].Union.Mouse.Data));

        // No window of the law's thread shows, so there is nothing to bring forward and nothing is sent.
        desktop.ReturnForeground();
        Assert.Equal(expected: 6, actual: sent.Count);
    }
    // THE LAW: a click on the host's own frame runs its caption button's command, the maximize button restoring a
    // maximized window, and any other part of the frame runs none.
    [Fact]
    public void TheHostsOwnFrameRunsItsCaptionButtonsCommands() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 14393)) { Assert.Skip(reason: "Requires Win32 with per-thread DPI contexts."); return; }

        Assert.Equal(expected: 0xF020u, actual: Win32DesktopPassthrough.FrameCommandOf(hit: 8, zoomed: false));
        Assert.Equal(expected: 0xF030u, actual: Win32DesktopPassthrough.FrameCommandOf(hit: 9, zoomed: false));
        Assert.Equal(expected: 0xF120u, actual: Win32DesktopPassthrough.FrameCommandOf(hit: 9, zoomed: true));
        Assert.Equal(expected: 0xF060u, actual: Win32DesktopPassthrough.FrameCommandOf(hit: 20, zoomed: true));
        Assert.Null(@object: Win32DesktopPassthrough.FrameCommandOf(hit: 2, zoomed: false));
    }
    // THE LAW: a monitor that is gone describes nothing and has no height, and refuses a zero handle by name.
    [Fact]
    public void AMonitorThatIsGoneDescribesNothing() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 14393)) { Assert.Skip(reason: "Requires Win32 with per-thread DPI contexts."); return; }

        List<SystemInput[]> sent = [];
        var gone = new Win32DesktopPassthrough(
            monitorHandle: 1,
            send: sent.Add,
            title: "Desktop"
        );

        Assert.False(condition: gone.TryDescribe(window: out _));
        Assert.Equal(expected: 0, actual: gone.Height);
        gone.MovePointer(distance: Vector2.One);
        gone.DeliverPointer(inputEvent: WindowInputEvent.PointerWheel(notches: Vector2.UnitY), point: At(x: 0f, y: 0f));
        gone.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started), point: At(x: 0f, y: 0f));
        gone.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed), point: At(x: 0f, y: 0f));
        Assert.Empty(collection: sent);
        Assert.Equal(expected: "monitorHandle", actual: Refusal(monitorHandle: 0, title: "Desktop"));
        Assert.Equal(expected: "title", actual: Refusal(monitorHandle: Primary(), title: null));
    }
    // THE LAW: claiming the chord registers Control+Alt+Escape system-wide on a message-only window, each hotkey it hears
    // is taken once, and releasing it gives the chord back. Another process holding the chord leaves it unclaimed.
    [Fact]
    public void TheClaimedChordIsHeardOnceAndGivenBack() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 14393)) { Assert.Skip(reason: "Requires Win32 with per-thread DPI contexts."); return; }

        var desktop = new Win32DesktopPassthrough(
            monitorHandle: Primary(),
            send: static _ => { },
            title: "Desktop"
        );

        Assert.False(condition: desktop.TakeChord());
        desktop.ClaimsChord = true;
        desktop.ClaimsChord = true;

        if (!desktop.ClaimsChord) {
            Assert.Skip(reason: "Another process holds Control+Alt+Escape.");
            return;
        }

        var window = Native.FindWindowEx(
            childAfter: 0,
            className: "Puck.DesktopChord",
            parent: -3,
            windowName: null
        );

        Assert.NotEqual(expected: 0, actual: window);
        _ = Native.SendMessage(lParam: 0, message: 0x0000, wParam: 0, window: window);
        _ = Native.SendMessage(lParam: 0, message: 0x0312, wParam: 0x5043, window: window);
        Assert.True(condition: desktop.TakeChord());
        Assert.False(condition: desktop.TakeChord());
        desktop.ClaimsChord = false;
        Assert.False(condition: desktop.ClaimsChord);
        Assert.Equal(expected: 0, actual: Native.FindWindowEx(childAfter: 0, className: "Puck.DesktopChord", parent: -3, windowName: null));
    }

    private static partial class Native {
        [LibraryImport("user32.dll")]
        public static partial nint MonitorFromPoint(long point, uint flags);
        [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);
        [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
        public static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    }
}
