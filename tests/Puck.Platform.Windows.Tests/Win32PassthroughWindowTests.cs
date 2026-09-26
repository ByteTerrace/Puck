using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Input;
using Xunit;

namespace Puck.Platform.Windows.Tests;

// A hidden Puck window stands in for a captured window: what the passthrough posts, its own window procedure reads back.
public sealed class Win32PassthroughWindowTests {
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
    private static INativeWindow Create(ServiceProvider services) => services.GetServices<INativeWindowBackend>()
        .Single(predicate: x => (x.Kind == NativeDisplayKind.Win32)).Create(options: new NativeWindowOptions { Height = 240, Title = "Puck passthrough verification", Width = 320 });
    private static List<WindowInputEvent> Drain(INativeWindow window) {
        List<WindowInputEvent> events = [];
        var input = ((IWindowInputSource)window);

        while (input.TryDequeueInput(inputEvent: out var item)) { events.Add(item: item); }
        return events;
    }

    [Fact]
    public void PointerEventsReachTheWindowAtTheirClientPoint() {
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 14393)) { Assert.Skip(reason: "Requires Win32 with per-thread DPI contexts."); return; }
        using var services = new ServiceCollection().AddWindowsPlatformWindowing().BuildServiceProvider();
        using var window = Create(services: services);
        var target = new Win32PassthroughWindow(windowHandle: window.CreateSurfaceBinding().Win32!.Value.WindowHandle);

        // The window reports one position per poll, so the move is read back on its own.
        target.DeliverPointer(inputEvent: WindowInputEvent.PointerAbsolute(position: Vector2.Zero), point: At(x: 40.75f, y: 50.25f));
        window.PollEvents();
        Assert.Equal(new Vector2(x: 40, y: 50), Assert.Single(collection: Drain(window: window), predicate: x => (x.Kind == WindowInputKind.PointerPosition)).Vector);

        target.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Started), point: At(x: 109f, y: 207f));
        target.DeliverPointer(inputEvent: WindowInputEvent.PointerButton(button: 0, phase: CommandPhase.Completed), point: At(x: 111f, y: 209f));
        target.DeliverPointer(inputEvent: WindowInputEvent.PointerWheel(notches: new Vector2(x: 0f, y: -0.5f)), point: At(x: 111f, y: 209f));
        window.PollEvents();
        var events = Drain(window: window);
        var positions = events.Where(predicate: x => (x.Kind == WindowInputKind.PointerPosition)).Select(selector: x => x.Vector).ToArray();
        var presses = events.Where(predicate: x => (x.Kind == WindowInputKind.PointerButton)).ToArray();

        Assert.Contains(expected: new Vector2(x: 109, y: 207), collection: positions);
        Assert.Contains(expected: new Vector2(x: 111, y: 209), collection: positions);
        Assert.Equal([CommandPhase.Started, CommandPhase.Completed], presses.Select(selector: x => x.Phase));
        Assert.All(presses, x => Assert.Equal(0, x.ButtonIndex));
        Assert.Equal(new Vector2(x: 0, y: -.5f), Assert.Single(collection: events, predicate: x => (x.Kind == WindowInputKind.PointerWheel)).Vector);
    }
    [Fact]
    public void EveryKeyRoundTripsThroughTheOneVirtualKeyTable() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Requires Win32."); return; }

        foreach (var key in Enum.GetValues<KeyCode>()) {
            if (key is KeyCode.None or KeyCode.Letter) {
                Assert.False(condition: ((key == KeyCode.None) && Win32VirtualKeys.TryVirtualKeyOf(character: '\0', isExtended: out _, key: key, scanCode: out _, virtualKey: out _)));
                continue;
            }

            Assert.True(condition: Win32VirtualKeys.TryVirtualKeyOf(character: '\0', isExtended: out var extended, key: key, scanCode: out var scanCode, virtualKey: out var virtualKey), userMessage: $"{key} has no virtual key");
            Assert.True(condition: Win32VirtualKeys.TryKeyOf(isExtended: extended, key: out var read, scanCode: scanCode, virtualKey: virtualKey), userMessage: $"{key}'s virtual key 0x{virtualKey:X2} reads back as no key");
            Assert.Equal(actual: read, expected: key);
        }
        for (var letter = 'a'; (letter <= 'z'); letter++) {
            foreach (var typed in ((ReadOnlySpan<char>)[letter, char.ToUpperInvariant(c: letter)])) {
                Assert.True(condition: Win32VirtualKeys.TryVirtualKeyOf(character: typed, isExtended: out _, key: KeyCode.Letter, scanCode: out _, virtualKey: out var virtualKey));
                Assert.Equal(expected: letter, actual: Win32VirtualKeys.LetterOf(virtualKey: virtualKey));
            }
        }
        Assert.False(condition: Win32VirtualKeys.TryVirtualKeyOf(character: '1', isExtended: out _, key: KeyCode.Letter, scanCode: out _, virtualKey: out _));
    }
}
