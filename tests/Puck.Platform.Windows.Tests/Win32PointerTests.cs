using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Input;
using Xunit;

namespace Puck.Platform.Windows.Tests;

// Hidden HWNDs exercise the installed window procedure without moving the user's cursor or taking focus.
public sealed partial class Win32PointerTests {
    [Fact]
    public void Cursor_coordinates_precede_button_transitions() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Requires Win32."); return; }
        using var services = new ServiceCollection().AddWindowsPlatformWindowing().BuildServiceProvider();
        using var window = Create(services: services);
        var handle = window.CreateSurfaceBinding().Win32!.Value.WindowHandle;

        SendMessage(handle, 0x0200, 0, Position(x: 105, y: 203));
        SendMessage(handle, 0x0201, 1, Position(x: 109, y: 207));
        SendMessage(handle, 0x0202, 0, Position(x: -12, y: 311));
        window.PollEvents();
        var events = Drain(window: window);
        var presses = events.Where(predicate: x => (x.Kind == WindowInputKind.PointerButton)).ToArray();

        Assert.Equal([CommandPhase.Started, CommandPhase.Completed], presses.Select(selector: x => x.Phase));
        Assert.All(events.Where(predicate: x => (x.Kind is WindowInputKind.PointerButton or WindowInputKind.PointerPosition)),
            x => Assert.Equal(default, x.DeviceId));
        var down = events.FindIndex(match: x => ((x.Kind == WindowInputKind.PointerButton) && (x.Phase == CommandPhase.Started)));
        var up = events.FindIndex(match: x => ((x.Kind == WindowInputKind.PointerButton) && (x.Phase == CommandPhase.Completed)));

        Assert.Equal(new Vector2(x: 109, y: 207), events[(down - 1)].Vector);
        Assert.Equal(new Vector2(x: -12, y: 311), events[(up - 1)].Vector);
        Assert.DoesNotContain(collection: events, filter: x => (x.Kind == WindowInputKind.PointerMove));
    }
    [Fact]
    public void Capture_survives_one_button_release_and_cancels_on_transfer() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Requires Win32."); return; }
        using var services = new ServiceCollection().AddWindowsPlatformWindowing().BuildServiceProvider();
        using var window = Create(services: services);
        using var other = Create(services: services);
        var handle = window.CreateSurfaceBinding().Win32!.Value.WindowHandle;
        var otherHandle = other.CreateSurfaceBinding().Win32!.Value.WindowHandle;

        SendMessage(handle, 0x0201, 1, Position(x: 10, y: 20));
        SendMessage(handle, 0x0204, 3, Position(x: 10, y: 20));
        SendMessage(handle, 0x0202, 2, Position(x: 10, y: 20));
        Assert.Equal(handle, GetCapture());
        SetCapture(window: otherHandle);
        Assert.Equal(otherHandle, GetCapture());
        SendMessage(handle, 0x0205, 0, Position(x: 10, y: 20));
        var transitions = Drain(window: window).Where(predicate: x => (x.Kind == WindowInputKind.PointerButton)).ToArray();

        Assert.Equal(4, transitions.Length);
        Assert.Equal(CommandPhase.Completed, transitions[^1].Phase);
        Assert.Equal(1, transitions[^1].ButtonIndex);
        window.Dispose();
        Assert.Equal(otherHandle, GetCapture());
    }
    [Fact]
    public void Cancel_mode_releases_drag_and_fractional_wheel_keeps_its_sign() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Requires Win32."); return; }
        using var services = new ServiceCollection().AddWindowsPlatformWindowing().BuildServiceProvider();
        using var window = Create(services: services);
        var handle = window.CreateSurfaceBinding().Win32!.Value.WindowHandle;

        SendMessage(handle, 0x0201, 1, Position(x: 10, y: 20));
        SendMessage(lParam: 0, message: 0x001F, wParam: 0, window: handle);
        SendMessage(handle, 0x0202, 0, Position(x: 10, y: 20));
        SendMessage(lParam: 0, message: 0x020A, wParam: unchecked((nint)(-60 << 16)), window: handle);
        Assert.Equal(0, GetCapture());
        var events = Drain(window: window);

        Assert.Equal(2, events.Count(predicate: x => (x.Kind == WindowInputKind.PointerButton)));
        Assert.Equal(new Vector2(x: 0, y: -.5f), Assert.Single(collection: events, predicate: x => (x.Kind == WindowInputKind.PointerWheel)).Vector);
    }

    private static INativeWindow Create(ServiceProvider services) => services.GetServices<INativeWindowBackend>()
        .Single(predicate: x => (x.Kind == NativeDisplayKind.Win32)).Create(options: new NativeWindowOptions { Height = 240, Title = "Puck pointer verification", Width = 320 });
    private static nint Position(int x, int y) => unchecked((nint)(((ushort)x) | (((uint)((ushort)y)) << 16)));
    private static List<WindowInputEvent> Drain(INativeWindow window) {
        List<WindowInputEvent> events = [];
        var input = ((IWindowInputSource)window);

        while (input.TryDequeueInput(inputEvent: out var item)) { events.Add(item: item); }
        return events;
    }
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    private static partial nint SetCapture(nint window);
    [LibraryImport("user32.dll")]
    private static partial nint GetCapture();
}
