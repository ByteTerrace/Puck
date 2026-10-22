using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Input;

namespace Puck.World.Client;

/// <summary>
/// The windowed host's passthrough door: the panes the local user opened as passthrough sources, the window each one's
/// capture shows, and the <see cref="SourcePassthroughRouter"/> the window pump offers every raw event to
/// (<see cref="IWindowInputFilter"/>). Two acts of the local user add a source, and nothing a world document declares
/// reaches either: the <c>source.passthrough open</c> verb, which the host's own console alone may run, and a physical
/// press on the glass of a capture's screen (a window's, or a whole desktop's, <see cref="ISourcePassthroughDesktop"/>)
/// a local seat engaged and frames in the world the host booted
/// (<see cref="WorldViewGraphHost.ShowsFramed"/>): a pointer press the window pump delivers, or a local pad's bound
/// press (<see cref="PadButton"/>), so no script, <c>player.signal</c>, peer, addon or schedule makes it. Engaging the
/// screen, by any principal, only shows the window; the press is what opens it, and the source closes once the seat no
/// longer frames the screen. The reserved chord, Control+Alt+Escape, leaves: it returns the keyboard to the game, and a
/// seat framing a window closes it and leaves the engagement (<see cref="IWorldPassthroughSeats.Leave"/>).
/// </summary>
/// <remarks>
/// <para>A source stays open, its grant held, until the local user closes it or it is really removed: before each event
/// is routed, a source whose instance stopped, whose capture reopened onto another window (a row of the same name
/// replacing its row opens a new capture), or whose name a published pane now gives a different source is closed.
/// Closing a source releases every key and button its window holds and returns the keyboard to the game when the
/// source had it.</para>
/// <para>A pane that is merely not published for a while keeps its grant, since its instance and window are the ones
/// the local user opened: the router routes nothing to it while it is unshown, and an event that arrives then first
/// takes its focus and held input away (<see cref="SourcePassthroughRouter.Route"/>), so a window the local user cannot
/// see never receives input. Once the same instance's pane is published again a click on it reaches the window again;
/// a pane unshown only between two events is never seen unshown, and keeps its focus as it keeps its grant.</para>
/// <para>Window-pump-thread only: the console's immediate verbs, the pump's events and the seats' bound pad verbs run
/// there.</para>
/// </remarks>
public sealed partial class WorldSourcePassthrough : IWindowInputFilter, ISourcePassthroughWindows {
    private readonly WorldPerceptionAnchor m_anchor;
    private readonly IWorldPassthroughWindows m_windows;
    private readonly WorldViewGraphHost m_graphs;
    private readonly SortedDictionary<string, Opened> m_opened = new(comparer: StringComparer.Ordinal);
    private readonly IWorldPassthroughSeats m_seats;
    private readonly TimeProvider m_time;
    private readonly WorldSeatViewports m_viewports;

    // An opened source: the handle its pane's mapping names it by, the window the local user opened it on, and whether
    // a press on an engaged screen's framed glass opened it, which closes it once the seat stops framing the screen.
    private readonly record struct Opened(SourceHandle Source, ISourcePassthroughWindow Window, bool Engaged = false);

    // The pointer's last position, in display pixels, which a press opening a framed screen's window lands at.
    private Vector2? m_pointer;

    /// <summary>Initializes a new instance of the <see cref="WorldSourcePassthrough"/> class.</summary>
    /// <param name="windows">Finds the window each source instance's capture shows.</param>
    /// <param name="graphs">The render graph host whose published panes the router reads.</param>
    /// <param name="viewports">The viewports whose client extent scales a pointer position into display pixels.</param>
    /// <param name="anchor">The perception anchor naming the screen each local seat is captured on.</param>
    /// <param name="seats">The local seats' pointer feel and leaving.</param>
    /// <param name="time">The clock a pad's pointer integrates over.</param>
    public WorldSourcePassthrough(IWorldPassthroughWindows windows, WorldViewGraphHost graphs, WorldSeatViewports viewports, WorldPerceptionAnchor anchor, IWorldPassthroughSeats seats, TimeProvider time) {
        ArgumentNullException.ThrowIfNull(argument: windows);
        ArgumentNullException.ThrowIfNull(argument: graphs);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: anchor);
        ArgumentNullException.ThrowIfNull(argument: seats);
        ArgumentNullException.ThrowIfNull(argument: time);

        m_anchor = anchor;
        m_windows = windows;
        m_graphs = graphs;
        m_seats = seats;
        m_time = time;
        m_viewports = viewports;
        Router = new SourcePassthroughRouter(windows: this);
    }

    /// <summary>Gets the router that hosts the keyboard focus.</summary>
    public SourcePassthroughRouter Router { get; }
    /// <summary>Gets whether the pointer belongs to a passthrough source's window rather than the game
    /// (<see cref="SourcePassthroughRouter.HoldsPointer"/>), or lies on the framed picture of a window a local seat may
    /// open, read against the panes the display published last, so a pane hidden or moved under a resting pointer gives
    /// the pointer back without waiting for it to move.</summary>
    public bool HoldsPointer {
        get {
            PublishPanes();

            return (Router.HoldsPointer || PointerOnFramedWindow());
        }
    }

    private void PublishPanes() => Router.Publish(
        displayHeight: m_graphs.DisplayHeight,
        displayWidth: m_graphs.DisplayWidth,
        panes: m_graphs.Panes
    );

    // The first opened source that is really gone: its capture no longer shows the window it was opened on (the instance
    // stopped, or its capture reopened), a published pane gives its name a different source, or a press on a framed
    // screen opened it and the seat no longer frames it (the seat left). A console-opened pane that is only unpublished
    // is not gone; the router keeps input from it while it is unshown.
    private string? Stale() {
        foreach (var (instance, opened) in m_opened) {
            if (
                (opened.Engaged && !m_graphs.ShowsFramed(instance: instance)) ||
                !ReferenceEquals(
                    objA: m_windows.PassthroughWindowOf(
                        fault: out _,
                        instance: instance
                    ),
                    objB: opened.Window
                ) ||
                (
                    m_graphs.TryGetPane(
                        instance: instance,
                        mapping: out var mapping
                    ) &&
                    (mapping.Source != opened.Source)
                )
            ) {
                return instance;
            }
        }

        return null;
    }

    /// <summary>Opens a pane's capture as a passthrough source the local user opened.</summary>
    /// <param name="instance">The instance a shown pane draws.</param>
    /// <param name="windowTitle">The title the local user names the window by; the window the pane captures must carry
    /// it, compared without regard to case.</param>
    /// <param name="message">What was opened, or why nothing was.</param>
    /// <returns><see langword="true"/> when the source is open.</returns>
    public bool TryOpen(string instance, string windowTitle, out string message) {
        if (!m_graphs.TryGetPane(
            instance: instance,
            mapping: out var mapping
        )) {
            message = $"no shown pane draws '{instance}' (world.view.panes lists them)";

            return false;
        }
        if (m_windows.PassthroughWindowOf(
            fault: out var fault,
            instance: instance
        ) is not { } window) {
            message = fault!;

            return false;
        }

        var title = window.Title;

        if (!title.Contains(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: windowTitle
        )) {
            message = $"'{instance}' captures the window '{title}', whose title does not contain '{windowTitle}'";

            return false;
        }

        _ = Close(instance: instance);
        m_opened[instance] = new Opened(
            Source: mapping.Source,
            Window: window
        );
        m_graphs.OpenPassthrough(instance: instance);
        message = $"'{instance}' opened as a passthrough source for the window '{title}'; a click on its pane gives it the keyboard, and Control+Alt+Escape returns it to the game";

        return true;
    }
    /// <summary>Closes a passthrough source: releases every key and button its window holds, to that window, returns the
    /// keyboard to the game when the source had it, and returns its pane to the presentation destination.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns><see langword="true"/> when the source was open.</returns>
    public bool Close(string instance) {
        if (!m_opened.TryGetValue(
            key: instance,
            value: out var opened
        )) {
            return false;
        }

        // Revoked while the window still resolves, so the releases reach it.
        Router.Revoke(source: opened.Source);
        ReleasePadKeys(source: opened.Source);
        ReleasePadDesktop(window: opened.Window);
        _ = m_opened.Remove(key: instance);
        _ = m_graphs.ClosePassthrough(instance: instance);

        // A desktop may have brought another window forward: the keyboard comes back to the game.
        if (opened.Window is ISourcePassthroughDesktop desktop) {
            desktop.ReturnForeground();
        }

        return true;
    }
    /// <summary>Describes the opened sources, where the keyboard is and the pad driving the pointer, the
    /// <c>source.passthrough</c> read-back.</summary>
    /// <returns>One line.</returns>
    public string Describe() {
        CloseStale();

        var builder = new StringBuilder(value: "keyboard ");

        _ = builder.Append(value: ((Router.Focus.Focused is { } focused)
            ? focused.Name
            : "game"
        ));

        if (m_opened.Count == 0) {
            _ = builder.Append(value: "; none opened");
        }

        foreach (var (instance, opened) in m_opened) {
            var window = opened.Window;

            _ = builder.Append(value: "; ").Append(value: instance).Append(value: (opened.Engaged ? " engaged" : string.Empty)).Append(value: " window '").Append(value: window.Title).Append(value: '\'');
            _ = (window.TryDescribe(window: out var described)
                ? builder.Append(provider: CultureInfo.InvariantCulture, handler: $" frame {described.FrameWidth}x{described.FrameHeight} client {described.Client.Width}x{described.Client.Height} at {described.Client.X},{described.Client.Y} scale {described.DpiScale:0.###}")
                : builder.Append(value: " gone"));

            if (window is ISourcePassthroughDesktop { ClaimsChord: true }) {
                _ = builder.Append(value: " chord claimed");
            }

            // Held, but routed nothing until its pane is published again.
            if (!m_graphs.TryGetPane(
                instance: instance,
                mapping: out _
            )) {
                _ = builder.Append(value: " (pane not shown)");
            }
        }

        DescribePad(builder: builder);

        return builder.ToString();
    }
    /// <inheritdoc/>
    public bool Intercept(in WindowInputEvent inputEvent) {
        CloseStale();

        switch (inputEvent.Kind) {
            case WindowInputKind.PointerPosition:
                // The pointer arrives in client pixels; the panes are published in display pixels, the stretch the drawn
                // cursor's pane hover applies too.
                var display = (((m_viewports.ClientWidth > 0) && (m_viewports.ClientHeight > 0))
                    ? new Vector2(
                        x: (inputEvent.Vector.X * (m_graphs.DisplayWidth / ((float)m_viewports.ClientWidth))),
                        y: (inputEvent.Vector.Y * (m_graphs.DisplayHeight / ((float)m_viewports.ClientHeight)))
                    )
                    : inputEvent.Vector);

                m_pointer = display;
                NotePointer(display: display);

                return Router.Route(inputEvent: inputEvent with {
                    Vector = display,
                });
            case WindowInputKind.PointerLeft:
                m_pointer = null;

                break;
            case WindowInputKind.PointerButton when (inputEvent.Phase == CommandPhase.Started):
                OpenEngagedUnderPointer();

                break;
            case WindowInputKind.Key:
                // While a seat frames a window it may open, the reserved chord is the host's whoever has the keyboard:
                // it leaves the engagement.
                Router.Focus.ClaimsChord = FramesWindow();

                var consumed = Router.Route(inputEvent: in inputEvent);

                if (Router.Focus.TakeChord()) {
                    LeaveFramed();
                }

                return consumed;
        }

        return Router.Route(inputEvent: in inputEvent);
    }

    private void CloseStale() {
        PublishPanes();

        while (Stale() is { } stale) {
            _ = Close(instance: stale);
        }
    }
    // Whether any local seat frames a window it may open.
    private bool FramesWindow() {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (TryFramedWindow(
                instance: out _,
                region: out _,
                screen: out _,
                slot: slot
            )) {
                return true;
            }
        }

        return false;
    }
    // The chord's leaving: every local seat framing a window closes it and leaves its engagement, and a desktop gives
    // the keyboard back to the game whether or not a press opened it.
    private void LeaveFramed() {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (TryFramedWindow(
                instance: out var instance,
                region: out _,
                screen: out _,
                slot: slot
            )) {
                if (m_windows.PassthroughWindowOf(
                    fault: out _,
                    instance: instance
                ) is ISourcePassthroughDesktop desktop) {
                    desktop.ReturnForeground();
                }

                _ = Close(instance: instance);
                ResetPad(slot: slot);
                m_seats.Leave(slot: slot);
            }
        }
    }
    // The local user's own press on the framed glass of an engaged window capture's screen opens that window as a
    // passthrough source before the press is routed, so the press itself reaches the window and gives it the keyboard.
    // Only the window pump's events and a local pad's bound press get here.
    private void OpenEngagedUnderPointer() {
        if (
            (m_pointer is not { } point) ||
            !m_graphs.Picker.TryPick(
                pick: out var pick,
                point: point
            ) ||
            (pick.Source.Name is not { } instance) ||
            !m_graphs.ShowsFramed(instance: instance) ||
            m_opened.ContainsKey(key: instance) ||
            (m_windows.PassthroughWindowOf(
                fault: out _,
                instance: instance
            ) is not { } window)
        ) {
            return;
        }

        m_opened[instance] = new Opened(
            Engaged: true,
            Source: pick.Source,
            Window: window
        );
        m_graphs.OpenPassthrough(instance: instance);
        PublishPanes();
    }
    // A local pad's press on a desktop its seat frames opens it, as a press on the picture would, wherever the cursor
    // rests: the pad is the desktop's mouse.
    private void OpenFramed(string instance, ISourcePassthroughWindow window) {
        if (
            m_opened.ContainsKey(key: instance) ||
            !m_graphs.TryGetPane(
                instance: instance,
                mapping: out var mapping
            )
        ) {
            return;
        }

        m_opened[instance] = new Opened(
            Engaged: true,
            Source: mapping.Source,
            Window: window
        );
        m_graphs.OpenPassthrough(instance: instance);
        PublishPanes();
    }
    // Whether the pointer lies on the framed picture of a window a local seat may open.
    private bool PointerOnFramedWindow() {
        if (m_pointer is not { } point) {
            return false;
        }

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (
                TryFramedWindow(
                    instance: out _,
                    region: out var region,
                    screen: out _,
                    slot: slot
                ) &&
                Picture(region: region).Contains(point: point)
            ) {
                return true;
            }
        }

        return false;
    }
    // The rect of the display a framed picture fills, in display pixels.
    private PictureRect Picture(NormalizedRect region) => new(
        Height: (region.Height * m_graphs.DisplayHeight),
        Width: (region.Width * m_graphs.DisplayWidth),
        X: (region.X * m_graphs.DisplayWidth),
        Y: (region.Y * m_graphs.DisplayHeight)
    );
    // The screen a local seat is captured on, when a published pane frames it and its capture shows a window: the
    // window the seat may open, the source instance it shows and the rect its picture fills.
    private bool TryFramedWindow(int slot, [NotNullWhen(returnValue: true)] out string? instance, out NormalizedRect region, out int screen) {
        screen = m_anchor.EngagedScreen(slot: slot);

        if (
            (screen >= 0) &&
            m_graphs.TryFramedScreen(
                instance: out instance,
                region: out region,
                screen: screen
            ) &&
            (m_windows.PassthroughWindowOf(
                fault: out _,
                instance: instance
            ) is not null)
        ) {
            return true;
        }

        instance = null;
        region = default;

        return false;
    }
    /// <inheritdoc/>
    public bool TryGet(SourceHandle source, [NotNullWhen(returnValue: true)] out ISourcePassthroughWindow? window) {
        if (
            m_opened.TryGetValue(
                key: source.Name,
                value: out var opened
            ) &&
            (opened.Source == source)
        ) {
            window = opened.Window;

            return true;
        }

        window = null;

        return false;
    }

    // A framed picture's rect in display pixels; a point on its right or bottom edge lies outside it. A clamped point
    // stays half a pixel inside the far edges, so its fixed-point hit (SourcePanes) still lands on the picture.
    private readonly record struct PictureRect(float X, float Y, float Width, float Height) {
        public Vector2 Centre => new(
            x: (X + (Width / 2f)),
            y: (Y + (Height / 2f))
        );

        public Vector2 Clamp(Vector2 point) => new(
            x: Math.Clamp(
                max: MathF.Max(x: X, y: ((X + Width) - 0.5f)),
                min: X,
                value: point.X
            ),
            y: Math.Clamp(
                max: MathF.Max(x: Y, y: ((Y + Height) - 0.5f)),
                min: Y,
                value: point.Y
            )
        );
        public bool Contains(Vector2 point) => (
            (point.X >= X) &&
            (point.Y >= Y) &&
            (point.X < (X + Width)) &&
            (point.Y < (Y + Height))
        );
    }
}
