using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Commands;
using Puck.Input;
using Xunit;

namespace Puck.Platform.Windows.Tests;

// A hidden window whose procedure records every keyboard and mouse message stands in for a captured window, with a
// visible child over its right half, so a law reads back exactly the messages a passthrough source delivers and which
// window each reaches. Messages sent to a window of this thread run its procedure at once.
public sealed partial class Win32PassthroughMessageTests : IDisposable {
    private const uint MkControl = 0x0008;
    private const uint MkLeftButton = 0x0001;
    private const uint WmChar = 0x0102;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseMove = 0x0200;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;

    private readonly string m_className = $"PuckPassthroughProbe{Guid.NewGuid():N}";

    private readonly nint m_child;
    private readonly nint m_parent;
    private readonly WindowProcedure m_procedure;

    private readonly List<Message> m_messages = [];

    private readonly record struct Message(nint Window, uint Kind, long WParam, long LParam) {
        public int X => unchecked((short)(LParam & 0xFFFF));
        public int Y => unchecked((short)((LParam >> 16) & 0xFFFF));
    }

    public Win32PassthroughMessageTests() {
        m_procedure = (window, message, wParam, lParam) => {
            // A recorded message is handled here, never by the default procedure, so Alt+F4 closes nothing and a bare Alt
            // opens no menu.
            if (message is (>= WmKeyDown and <= WmSysKeyUp) or (>= WmMouseMove and <= 0x020E)) {
                m_messages.Add(item: new Message(Kind: message, LParam: lParam, WParam: wParam, Window: window));

                return 0;
            }

            return DefWindowProc(lParam: lParam, message: message, wParam: wParam, window: window);
        };

        if (!OperatingSystem.IsWindows()) {
            return;
        }

        var windowClass = new WindowClass {
            ClassName = m_className,
            Procedure = m_procedure,
            Size = ((uint)Marshal.SizeOf<WindowClass>()),
        };

        Assert.NotEqual(expected: ((ushort)0), actual: RegisterClassEx(windowClass: ref windowClass));
        // A hidden popup, 320x240, and a visible child covering its right half from x = 160.
        m_parent = CreateWindowEx(className: m_className, extendedStyle: 0, height: 240, instance: 0, menu: 0, parameter: 0, parent: 0, style: 0x80000000u, title: "Puck passthrough probe", width: 320, x: 0, y: 0);
        m_child = CreateWindowEx(className: m_className, extendedStyle: 0, height: 240, instance: 0, menu: 0, parameter: 0, parent: m_parent, style: 0x40000000u | 0x10000000u, title: "child", width: 160, x: 160, y: 0);
        Assert.NotEqual(actual: m_parent, expected: 0);
        Assert.NotEqual(actual: m_child, expected: 0);
    }

    private static SourcePassthroughPoint At(float x, float y) => new(
        InClient: true,
        Logical: new Vector2(x: x, y: y),
        Physical: new Vector2(x: x, y: y)
    );

    public void Dispose() {
        if (!OperatingSystem.IsWindows()) {
            return;
        }

        _ = DestroyWindow(window: m_parent);
        _ = UnregisterClass(className: m_className, instance: 0);
        GC.KeepAlive(obj: m_procedure);
    }
    [Fact]
    public void AKeyArrivesAsKeyDownThenItsTextThenKeyUp() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 14393)) { Assert.Skip(reason: "Requires Win32 with per-thread DPI contexts."); return; }
        var target = new Win32PassthroughWindow(windowHandle: m_parent);

        target.DeliverKey(inputEvent: WindowInputEvent.LetterDown(character: 'q'));
        target.DeliverKey(inputEvent: WindowInputEvent.TypedText(text: "q"));
        target.DeliverKey(inputEvent: WindowInputEvent.LetterUp(character: 'q'));

        Assert.Equal(expected: [(WmKeyDown, ((long)'Q')), (WmChar, 'q'), (WmKeyUp, 'Q')], actual: m_messages.Select(selector: x => (x.Kind, x.WParam)));
        Assert.All(m_messages, x => Assert.Equal(expected: m_parent, actual: x.Window));
        Assert.Equal(expected: 0L, actual: m_messages[0].LParam & 0x80000000L);
        Assert.NotEqual(expected: 0L, actual: m_messages[2].LParam & 0x80000000L);
    }
    [Fact]
    public void AltPressesAndReleasesAsASystemKeyWhileEitherSideIsHeld() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 14393)) { Assert.Skip(reason: "Requires Win32 with per-thread DPI contexts."); return; }
        var target = new Win32PassthroughWindow(windowHandle: m_parent);

        target.DeliverKey(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.AltLeft));
        target.DeliverKey(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.AltRight));
        target.DeliverKey(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.AltLeft));
        // The right Alt is still held, so F4 is still an Alt chord.
        target.DeliverKey(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.F4));
        target.DeliverKey(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.F4));
        target.DeliverKey(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.AltRight));
        target.DeliverKey(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.F4));

        Assert.Equal(
            expected: [
                (WmSysKeyDown, 0x12L),
                (WmSysKeyDown, 0x12L),
                (WmSysKeyUp, 0x12L),
                (WmSysKeyDown, 0x73L),
                (WmSysKeyUp, 0x73L),
                (WmSysKeyUp, 0x12L),
                (WmKeyDown, 0x73L),
            ],
            actual: m_messages.Select(selector: x => (x.Kind, x.WParam))
        );
        // Alt's own release carries the Alt context bit, as its press did; a key with no Alt held carries none.
        Assert.NotEqual(expected: 0L, actual: m_messages[5].LParam & (1L << 29));
        Assert.Equal(expected: 0L, actual: m_messages[6].LParam & (1L << 29));
    }
    [Fact]
    public void ADragStaysWithTheChildItWasPressedOnAndEachControlSideIsHeldApart() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 14393)) { Assert.Skip(reason: "Requires Win32 with per-thread DPI contexts."); return; }
        var target = new Win32PassthroughWindow(windowHandle: m_parent);

        target.DeliverKey(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.ControlLeft));
        target.DeliverKey(inputEvent: WindowInputEvent.KeyDown(key: KeyCode.ControlRight));
        target.DeliverKey(inputEvent: WindowInputEvent.KeyUp(key: KeyCode.ControlLeft));
        m_messages.Clear();

        // Pressed on the parent's left half, dragged over the child, released there: all three stay with the parent.
        target.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started), point: At(x: 40f, y: 50f));
        target.DeliverPointer(inputEvent: WindowInputEvent.PointerAbsolute(position: Vector2.Zero), point: At(x: 200f, y: 60f));
        target.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed), point: At(x: 200f, y: 60f));
        // With no button held, the same point reaches the child, in its own coordinates.
        target.DeliverPointer(inputEvent: WindowInputEvent.PointerAbsolute(position: Vector2.Zero), point: At(x: 200f, y: 60f));

        Assert.Equal(
            expected: [
                (m_parent, WmLButtonDown, 40, 50),
                (m_parent, WmMouseMove, 200, 60),
                (m_parent, WmLButtonUp, 200, 60),
                (m_child, WmMouseMove, 40, 60),
            ],
            actual: m_messages.Select(selector: x => (x.Window, x.Kind, x.X, x.Y))
        );
        // The right Control is still held after the left one's release, and the drag's move carries its button.
        Assert.All(m_messages, x => Assert.NotEqual(expected: 0L, actual: x.WParam & MkControl));
        Assert.NotEqual(expected: 0L, actual: m_messages[1].WParam & MkLeftButton);
        Assert.Equal(expected: 0L, actual: m_messages[3].WParam & MkLeftButton);
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass {
        public uint Size;
        public uint Style;
        public WindowProcedure? Procedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public string? ClassName;
        public nint SmallIcon;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
