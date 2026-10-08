using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class WorldViewPaneMappingLawTests {
    // A pane publishes the presentation destination under the document opener until the local user's door opens its
    // instance as a passthrough source; then the router hands a click on it to the window it shows at the mapped client
    // point, and closing it returns the pane, and the keyboard, to the game. Opening allocates nothing per steady frame.
    [Fact]
    public void APaneTheLocalUserOpenedTakesThePassthroughDestination() {
        Frame(pane: Left);
        Frame(pane: Left);

        var shown = Assert.Single(collection: m_host.Panes);

        Assert.Equal(
            actual: (shown.Destination, shown.Opener),
            expected: (SourceDestination.Presentation, SourceOpener.Document)
        );

        var window = new PassthroughWindow();
        var router = new SourcePassthroughRouter(windows: new PassthroughWindows(
            instance: Pane,
            shown: window
        ));

        // Before it is opened the pane is the game's, even with a window standing behind its name.
        Assert.False(condition: Press(
            router: router,
            x: 8.5f,
            y: 40.5f
        ));
        Assert.Empty(collection: window.Presses);

        m_host.OpenPassthrough(instance: Pane);
        Prepare(pane: Left);

        var opened = Assert.Single(collection: m_host.Panes);

        Assert.Equal(
            actual: (opened.Destination, opened.Opener),
            expected: (SourceDestination.Passthrough, SourceOpener.LocalUser)
        );
        Assert.True(condition: opened.TryValidate(refusal: out _));
        Assert.True(condition: Press(
            router: router,
            x: 8.5f,
            y: 40.5f
        ));
        Assert.Equal(
            actual: router.Focus.Focused,
            expected: opened.Source
        );
        Assert.Equal(
            actual: Assert.Single(collection: window.Presses),
            expected: new Vector2(
                x: 8.5f,
                y: 40.5f
            )
        );

        // A steady frame republishes the opened mapping without allocating.
        Prepare(pane: Left);

        var before = GC.GetAllocatedBytesForCurrentThread();

        Prepare(pane: Left);
        Assert.Equal(
            actual: (GC.GetAllocatedBytesForCurrentThread() - before),
            expected: 0L
        );
        Assert.Same(
            actual: Assert.Single(collection: m_host.Panes),
            expected: opened
        );

        Assert.True(condition: m_host.ClosePassthrough(instance: Pane));
        Prepare(pane: Left);
        Assert.Equal(
            actual: Assert.Single(collection: m_host.Panes).Destination,
            expected: SourceDestination.Presentation
        );
    }
    // THE LAW: a passthrough grant follows its pane's instance, not one frame's publication. A pane left unpublished
    // for a frame takes no input while it is unshown and keeps its grant, so its republished pane reaches the window
    // again; the local user's explicit close ends the grant, and so does the instance really going away.
    [Fact]
    public void AGrantHoldsAcrossAFrameItsPaneIsNotPublishedAndAnExplicitCloseEndsIt() {
        Frame(pane: Left);
        Frame(pane: Left);

        var window = new PassthroughWindow();
        var windows = new InstanceWindows(
            name: Pane,
            window: window
        );
        var passthrough = new WorldSourcePassthrough(
            graphs: m_host,
            viewports: new WorldSeatViewports(),
            windows: windows
        );

        Assert.True(
            condition: passthrough.TryOpen(
                instance: Pane,
                message: out var message,
                windowTitle: window.Title
            ),
            userMessage: message
        );
        Prepare(pane: Left);
        Assert.True(condition: Click(passthrough: passthrough));
        Assert.Single(collection: window.Presses);

        // One frame with the pane unpublished: nothing reaches the window, and the grant is still held.
        Prepare(pane: null);
        Assert.False(condition: Click(passthrough: passthrough));
        Assert.Single(collection: window.Presses);
        Assert.Null(@object: passthrough.Router.Focus.Focused);
        Assert.Contains(
            expectedSubstring: "(pane not shown)",
            actualString: passthrough.Describe()
        );

        // Republished: the same instance's pane is a passthrough source again, and a click reaches the window.
        Prepare(pane: Left);
        Assert.Equal(
            actual: Assert.Single(collection: m_host.Panes).Destination,
            expected: SourceDestination.Passthrough
        );
        Assert.True(condition: Click(passthrough: passthrough));
        Assert.Equal(
            actual: window.Presses.Count,
            expected: 2
        );

        // The explicit close ends the grant: the pane is the game's again.
        Assert.True(condition: passthrough.Close(instance: Pane));
        Prepare(pane: Left);
        Assert.Equal(
            actual: Assert.Single(collection: m_host.Panes).Destination,
            expected: SourceDestination.Presentation
        );
        Assert.False(condition: Click(passthrough: passthrough));
        Assert.Equal(
            actual: window.Presses.Count,
            expected: 2
        );

        // A real removal ends a grant too: once the instance stops, the next event closes its source.
        Assert.True(condition: passthrough.TryOpen(
            instance: Pane,
            message: out _,
            windowTitle: window.Title
        ));
        windows.Window = null;
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: Vector2.Zero));
        Assert.EndsWith(
            actualString: passthrough.Describe(),
            expectedEndString: "none opened"
        );
    }

    // A press and release on the left pane's window area, through the host's filter as the window pump offers them.
    private static bool Click(WorldSourcePassthrough passthrough) {
        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(
            x: 8.5f,
            y: 40.5f
        )));

        var pressed = passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Started
        ));

        _ = passthrough.Intercept(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Completed
        ));

        return pressed;
    }
    private bool Press(SourcePassthroughRouter router, float x, float y) {
        router.Publish(
            displayHeight: m_host.DisplayHeight,
            displayWidth: m_host.DisplayWidth,
            panes: m_host.Panes
        );
        _ = router.Route(inputEvent: WindowInputEvent.PointerAbsolute(position: new Vector2(
            x: x,
            y: y
        )));

        return router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: 0,
            phase: CommandPhase.Started
        ));
    }

    // The window a 32x64 pane's capture shows: a frame that is all client area, at a DPI scale of 1.
    private sealed class PassthroughWindow : ISourcePassthroughWindow {
        public List<Vector2> Presses { get; } = [];
        public string Title => "pane window";

        public void DeliverKey(in WindowInputEvent inputEvent) {
        }
        public void DeliverPointer(in WindowInputEvent inputEvent, SourcePassthroughPoint point) {
            // Presses only: a release follows its press to the same window.
            if (
                (inputEvent.Kind == WindowInputKind.PointerButton) &&
                (inputEvent.Phase == CommandPhase.Started)
            ) {
                Presses.Add(item: point.Logical);
            }
        }
        public bool TryDescribe(out SourcePassthroughWindow window) {
            window = new SourcePassthroughWindow(
                Client: SourcePixelRect.Whole(
                    height: Display,
                    width: (Display / 2)
                ),
                DpiScale: 1f,
                FrameHeight: Display,
                FrameWidth: (Display / 2)
            );

            return true;
        }
    }
    // The window one running instance captures, until the law stops the instance by clearing it.
    private sealed class InstanceWindows(string name, PassthroughWindow window) : IWorldPassthroughWindows {
        public PassthroughWindow? Window { get; set; } = window;

        public ISourcePassthroughWindow? PassthroughWindowOf(string instance, out string? fault) {
            var running = (string.Equals(
                a: instance,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
                ? Window
                : null);

            fault = ((running is null)
                ? $"no source instance '{instance}' is running"
                : null);

            return running;
        }
    }
    private sealed class PassthroughWindows(string instance, PassthroughWindow shown) : ISourcePassthroughWindows {
        public bool TryGet(SourceHandle source, [NotNullWhen(returnValue: true)] out ISourcePassthroughWindow? window) {
            window = (string.Equals(
                a: source.Name,
                b: instance,
                comparisonType: StringComparison.Ordinal
            )
                ? shown
                : null);

            return (window is not null);
        }
    }
}
