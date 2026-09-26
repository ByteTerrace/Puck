using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Puck.Commands;
using Puck.Maths;

namespace Puck.Input;

/// <summary>The external window a <see cref="SourceDestination.Passthrough"/> source shows, as the host that opened the
/// source reaches it. Deliveries run on the window-pump thread and touch no simulation state; a delivery to a window
/// that has gone does nothing.</summary>
public interface ISourcePassthroughWindow {
    /// <summary>Gets the window's title as it reads now, or an empty string once the window has gone.</summary>
    string Title { get; }

    /// <summary>Delivers a key or text event to the window's keyboard focus: a <see cref="WindowInputKind.Key"/> event
    /// presses or releases its key, and a <see cref="WindowInputKind.Text"/> event types its text.</summary>
    /// <param name="inputEvent">The event.</param>
    void DeliverKey(in WindowInputEvent inputEvent);
    /// <summary>Delivers a pointer event at a client point: a <see cref="WindowInputKind.PointerPosition"/> event moves the
    /// pointer there, a <see cref="WindowInputKind.PointerButton"/> event presses or releases its button there, and a
    /// <see cref="WindowInputKind.PointerWheel"/> event turns the wheel there.</summary>
    /// <param name="inputEvent">The event; its <see cref="WindowInputEvent.Vector"/> is not a window position.</param>
    /// <param name="point">The client point, from <see cref="SourcePassthrough.ToClient"/>.</param>
    void DeliverPointer(in WindowInputEvent inputEvent, SourcePassthroughPoint point);
    /// <summary>Reads the window's captured frame, client area and DPI scale as they are now.</summary>
    /// <param name="window">The description when this returns <see langword="true"/>; default otherwise.</param>
    /// <returns><see langword="true"/> while the window exists and has a client area.</returns>
    bool TryDescribe(out SourcePassthroughWindow window);
}
/// <summary>Resolves a source to the external window it shows, for the sources the local user opened as passthrough
/// sources.</summary>
public interface ISourcePassthroughWindows {
    /// <summary>Finds the window a source shows.</summary>
    /// <param name="source">The source, as its published mapping names it.</param>
    /// <param name="window">The window when this returns <see langword="true"/>; <see langword="null"/> otherwise.</param>
    /// <returns><see langword="true"/> when the local user opened <paramref name="source"/> as a passthrough source and it
    /// still shows that window.</returns>
    bool TryGet(SourceHandle source, [NotNullWhen(returnValue: true)] out ISourcePassthroughWindow? window);
}
/// <summary>
/// Hosts <see cref="SourceFocus"/> for a window: it reads the pane mappings the display last published, gives a
/// permitted passthrough source's window the pointer and keyboard events meant for it at
/// <see cref="SourcePassthrough.ToClient"/>'s client points, and tells the pump which events the game must not see.
/// </summary>
/// <remarks>
/// <para>Pointer events follow the mapping under the pointer: over a pane whose mapping takes
/// <see cref="SourceDestination.Passthrough"/>, was opened by the local user and lands on its source, the pointer's
/// position, buttons and wheel reach the source's window; a button press there also focuses the source, and a press
/// anywhere else returns focus to the game. A press on the captured frame outside the client area focuses the source
/// but reaches no client. Keys and text follow <see cref="SourceFocus"/>, and the reserved chord returns focus to the
/// game. Every release, of a key or a button, goes where its press went, even after focus or the pointer moves: a key
/// released after focus moved to another source reaches the window its press did, and a button pressed on a source is
/// released there at the point its drag reached.</para>
/// <para>A mapping a world document published takes the passthrough destination only if the local user opened it, which
/// <see cref="SourceFocus.Press"/> and this router both check, so a document's source never focuses or receives
/// input.</para>
/// <para>Window-pump-thread only. Routing allocates nothing once each key and button has been seen.</para>
/// </remarks>
/// <param name="windows">Resolves a focused or pressed source to its window.</param>
public sealed class SourcePassthroughRouter(ISourcePassthroughWindows windows) {
    private readonly Dictionary<int, ButtonPress> m_buttons = [];
    // Buttons whose press reached a source that was revoked before their release.
    private readonly HashSet<int> m_suppressedButtons = [];
    private readonly Dictionary<(KeyCode Key, char Character), SourceHandle> m_keys = [];
    private readonly ISourcePassthroughWindows m_windows = (windows ?? throw new ArgumentNullException(paramName: nameof(windows)));
    private int m_displayHeight = 1;
    private int m_displayWidth = 1;
    private IReadOnlyList<SourceMapping> m_panes = [];

    private Vector2? m_pointer;

    /// <summary>Gets the keyboard focus between the game and a passthrough source.</summary>
    public SourceFocus Focus { get; } = new();

    // A button press that went to a source: where it was delivered, or that it landed off the client area and was
    // delivered nowhere, so its release is consumed without a delivery too.
    private readonly record struct ButtonPress(SourceHandle Source, SourcePassthroughPoint Point, bool Delivered);

    private static bool IsPassthrough(SourceMapping mapping) => (
        (mapping.Destination == SourceDestination.Passthrough) &&
        SourcePassthrough.IsPermitted(opener: mapping.Opener)
    );

    /// <summary>Publishes the panes the display shows and its extent; the next event reads them.</summary>
    /// <param name="panes">The pane mappings, in drawing order.</param>
    /// <param name="displayWidth">The display's width, in pixels; positive.</param>
    /// <param name="displayHeight">The display's height, in pixels; positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="panes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="displayWidth"/> or <paramref name="displayHeight"/>
    /// is not positive.</exception>
    public void Publish(IReadOnlyList<SourceMapping> panes, int displayWidth, int displayHeight) {
        ArgumentNullException.ThrowIfNull(argument: panes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: displayHeight);

        m_panes = panes;
        m_displayWidth = displayWidth;
        m_displayHeight = displayHeight;
    }
    /// <summary>Revokes a source: releases every key and button whose press reached its window, to that window, and
    /// returns focus to the game when the source had it. The source's later physical releases reach nobody, since the game
    /// never saw their presses.</summary>
    /// <param name="source">The source.</param>
    public void Revoke(SourceHandle source) {
        ReleaseHeld(
            source: source,
            suppress: true
        );

        if (Focus.Focused == source) {
            Focus.ReturnToGame();
        }
    }
    /// <summary>Routes one window input event, delivering it to a passthrough source's window when it is meant for one.
    /// A source that holds focus or a press but no longer has a passthrough pane among the published panes is revoked
    /// first (<see cref="Revoke"/>), so a pane that stops being shown stops taking input.</summary>
    /// <param name="inputEvent">The event, in the window's order. A <see cref="WindowInputKind.PointerPosition"/> event's
    /// <see cref="WindowInputEvent.Vector"/> is in display pixels from the display's top-left corner.</param>
    /// <returns><see langword="true"/> when the game must not see the event. A pointer position is never withheld, so
    /// the game keeps drawing its cursor over a passthrough pane.</returns>
    public bool Route(in WindowInputEvent inputEvent) {
        RevokeUnshown();

        return RouteShown(inputEvent: in inputEvent);
    }

    // Releases every key and button whose press reached a source's window, to that window, at the point its drag last
    // reached. A suppressed button's later physical release reaches nobody, as a revoked key's does, since the game never
    // saw its press.
    private void ReleaseHeld(SourceHandle source, bool suppress) {
        _ = m_windows.TryGet(
            source: source,
            window: out var window
        );

        while (TryTakeButton(
            button: out var button,
            press: out var press,
            source: source
        )) {
            if (suppress) {
                _ = m_suppressedButtons.Add(item: button);
            }

            if (press.Delivered) {
                window?.DeliverPointer(
                    inputEvent: WindowInputEvent.PointerButton(
                        button: button,
                        phase: CommandPhase.Completed
                    ),
                    point: press.Point
                );
            }
        }
        while (TryTakeKey(
            key: out var key,
            source: source
        )) {
            window?.DeliverKey(inputEvent: new WindowInputEvent(
                Character: key.Character,
                Key: key.Key,
                Kind: WindowInputKind.Key,
                Phase: CommandPhase.Completed
            ));
        }
    }
    // Whether a passthrough pane the local user opened shows a source among the published panes.
    private bool IsShown(SourceHandle source) {
        for (var index = 0; (index < m_panes.Count); index++) {
            if (
                (m_panes[index].Source == source) &&
                IsPassthrough(mapping: m_panes[index])
            ) {
                return true;
            }
        }

        return false;
    }
    private void RevokeUnshown() {
        if (
            (Focus.Focused is { } focused) &&
            !IsShown(source: focused)
        ) {
            Revoke(source: focused);
        }

        while (TryHeldUnshown(source: out var source)) {
            Revoke(source: source);
        }
    }
    // The first source holding a key or a button whose pane is no longer shown.
    private bool TryHeldUnshown(out SourceHandle source) {
        foreach (var press in m_buttons.Values) {
            if (!IsShown(source: press.Source)) {
                source = press.Source;

                return true;
            }
        }
        foreach (var held in m_keys.Values) {
            if (!IsShown(source: held)) {
                source = held;

                return true;
            }
        }

        source = default;

        return false;
    }
    private bool TryTakeButton(SourceHandle source, out int button, out ButtonPress press) {
        foreach (var (pressed, candidate) in m_buttons) {
            if (candidate.Source == source) {
                _ = m_buttons.Remove(key: pressed);
                button = pressed;
                press = candidate;

                return true;
            }
        }

        button = -1;
        press = default;

        return false;
    }
    private bool TryTakeKey(SourceHandle source, out (KeyCode Key, char Character) key) {
        foreach (var (pressed, candidate) in m_keys) {
            if (candidate == source) {
                _ = m_keys.Remove(key: pressed);
                key = pressed;

                return true;
            }
        }

        key = default;

        return false;
    }
    private bool RouteShown(in WindowInputEvent inputEvent) {
        switch (inputEvent.Kind) {
            case WindowInputKind.PointerPosition:
                m_pointer = inputEvent.Vector;
                RoutePointerPosition(inputEvent: in inputEvent);

                return false;
            case WindowInputKind.PointerLeft:
                m_pointer = null;

                return false;
            case WindowInputKind.PointerMove:
                return ((m_buttons.Count > 0) || TryUnderPointer(
                    hit: out _,
                    mapping: out _
                ));
            case WindowInputKind.PointerButton:
                return ((inputEvent.Phase == CommandPhase.Started)
                    ? RouteButtonPress(inputEvent: in inputEvent)
                    : RouteButtonRelease(inputEvent: in inputEvent)
                );
            case WindowInputKind.PointerWheel:
                if (
                    TryUnderPointer(
                        hit: out var wheelHit,
                        mapping: out var wheelMapping
                    ) &&
                    m_windows.TryGet(
                        source: wheelMapping.Source,
                        window: out var wheelWindow
                    ) &&
                    TryPoint(
                        coordinate: wheelHit.Coordinate,
                        mapping: wheelMapping,
                        point: out var wheelPoint,
                        window: wheelWindow
                    ) &&
                    wheelPoint.InClient
                ) {
                    wheelWindow.DeliverPointer(
                        inputEvent: in inputEvent,
                        point: wheelPoint
                    );

                    return true;
                }

                return false;
            case WindowInputKind.Key:
                return RouteKey(inputEvent: in inputEvent);
            case WindowInputKind.Text:
                if (Focus.Route(inputEvent: in inputEvent) != SourceFocusRoute.Source) {
                    return false;
                }
                if (
                    (Focus.Focused is { } focused) &&
                    m_windows.TryGet(
                        source: focused,
                        window: out var textWindow
                    )
                ) {
                    textWindow.DeliverKey(inputEvent: in inputEvent);

                    return true;
                }

                // The focused source's window has gone, so the keys go back to the game.
                Focus.ReturnToGame();

                return false;
            case WindowInputKind.FocusLost:
                ReleaseEverything();
                _ = Focus.Route(inputEvent: in inputEvent);

                return false;
            default:
                return false;
        }
    }
    // The topmost pane under the pointer, when it is a permitted passthrough pane and the pointer lies on its source.
    private bool TryUnderPointer(out SourceHit hit, [NotNullWhen(returnValue: true)] out SourceMapping? mapping) {
        if (
            (m_pointer is { } pointer) &&
            (Under(
                hit: out hit,
                point: pointer
            ) is { } under) &&
            IsPassthrough(mapping: under) &&
            hit.IsOnSource
        ) {
            mapping = under;

            return true;
        }

        hit = default;
        mapping = null;

        return false;
    }
    private SourceMapping? Under(Vector2 point, out SourceHit hit) {
        var index = SourcePanes.Topmost(
            displayHeight: m_displayHeight,
            displayWidth: m_displayWidth,
            hit: out hit,
            panes: m_panes,
            point: ToFixed(point: point)
        );

        return ((index < 0)
            ? null
            : m_panes[index]
        );
    }
    private static FixedVector2 ToFixed(Vector2 point) => new(
        X: FixedQ4816.FromDouble(value: point.X),
        Y: FixedQ4816.FromDouble(value: point.Y)
    );
    // The client point of a source coordinate on the window the source shows now.
    private static bool TryPoint(ISourcePassthroughWindow window, SourceMapping mapping, FixedVector2 coordinate, out SourcePassthroughPoint point) {
        if (!window.TryDescribe(window: out var described)) {
            point = default;

            return false;
        }

        point = SourcePassthrough.ToClient(
            coordinate: coordinate,
            sourceHeight: mapping.SourceHeight,
            sourceWidth: mapping.SourceWidth,
            window: described
        );

        return true;
    }
    // Where the pointer lies on a source's topmost published pane, inside it or not, as a drag that leaves the pane
    // reads it; false when no published pane shows the source or the pointer's position is unknown.
    private bool TryDragPoint(ISourcePassthroughWindow window, SourceHandle source, out SourcePassthroughPoint point) {
        if (m_pointer is { } pointer) {
            for (var index = (m_panes.Count - 1); (index >= 0); index--) {
                var mapping = m_panes[index];

                if (
                    (mapping.Source != source) ||
                    (mapping.Placement is not SourcePlacement.Pane)
                ) {
                    continue;
                }

                var hit = mapping.MapDisplayPoint(
                    displayHeight: m_displayHeight,
                    displayWidth: m_displayWidth,
                    point: ToFixed(point: pointer)
                );

                if (hit.Outcome is SourceHitOutcome.OnSource or SourceHitOutcome.OutsidePlacement) {
                    return TryPoint(
                        coordinate: hit.Coordinate,
                        mapping: mapping,
                        point: out point,
                        window: window
                    );
                }

                break;
            }
        }

        point = default;

        return false;
    }
    private void RoutePointerPosition(in WindowInputEvent inputEvent) {
        // A drag goes to the source its button was pressed on, wherever the pointer has moved since.
        foreach (var (button, press) in m_buttons) {
            if (
                press.Delivered &&
                m_windows.TryGet(
                    source: press.Source,
                    window: out var dragWindow
                ) &&
                TryDragPoint(
                    point: out var dragPoint,
                    source: press.Source,
                    window: dragWindow
                )
            ) {
                dragWindow.DeliverPointer(
                    inputEvent: in inputEvent,
                    point: dragPoint
                );
                // A release the router makes itself goes where the drag last reached.
                m_buttons[button] = (press with { Point = dragPoint });

                return;
            }
        }

        if (
            TryUnderPointer(
                hit: out var hit,
                mapping: out var mapping
            ) &&
            m_windows.TryGet(
                source: mapping.Source,
                window: out var window
            ) &&
            TryPoint(
                coordinate: hit.Coordinate,
                mapping: mapping,
                point: out var point,
                window: window
            ) &&
            point.InClient
        ) {
            window.DeliverPointer(
                inputEvent: in inputEvent,
                point: point
            );
        }
    }
    private bool RouteButtonPress(in WindowInputEvent inputEvent) {
        var button = inputEvent.ButtonIndex;

        // A new press means the suppressed release went missing; this press's release is its own.
        _ = m_suppressedButtons.Remove(item: button);
        SourceMapping? mapping = null;
        SourceHit hit = default;

        if (m_pointer is { } pointer) {
            mapping = Under(
                hit: out hit,
                point: pointer
            );
        }

        // A press on the source focuses it; a press anywhere else, a document's source included, returns focus.
        if (
            !Focus.Press(
                hit: hit,
                mapping: mapping
            ) ||
            (mapping is null)
        ) {
            return false;
        }
        if (!m_windows.TryGet(
            source: mapping.Source,
            window: out var window
        )) {
            // Nothing the local user opened shows there any more, so the press and the keys are the game's.
            Focus.ReturnToGame();

            return false;
        }

        var delivered = (
            TryPoint(
                coordinate: hit.Coordinate,
                mapping: mapping,
                point: out var point,
                window: window
            ) &&
            point.InClient
        );

        if (delivered) {
            window.DeliverPointer(
                inputEvent: in inputEvent,
                point: point
            );
        }

        m_buttons[button] = new ButtonPress(
            Delivered: delivered,
            Point: point,
            Source: mapping.Source
        );

        return true;
    }
    private bool RouteButtonRelease(in WindowInputEvent inputEvent) {
        if (m_suppressedButtons.Remove(item: inputEvent.ButtonIndex)) {
            return true;
        }
        if (!m_buttons.Remove(
            key: inputEvent.ButtonIndex,
            value: out var press
        )) {
            return false;
        }

        if (
            press.Delivered &&
            m_windows.TryGet(
                source: press.Source,
                window: out var window
            )
        ) {
            window.DeliverPointer(
                inputEvent: in inputEvent,
                point: (TryDragPoint(
                    point: out var released,
                    source: press.Source,
                    window: window
                )
                    ? released
                    : press.Point
                )
            );
        }

        return true;
    }
    private bool RouteKey(in WindowInputEvent inputEvent) {
        var key = (inputEvent.Key, inputEvent.Character);
        var focused = Focus.Focused;
        var route = Focus.Route(inputEvent: in inputEvent);

        if (route == SourceFocusRoute.Consumed) {
            return true;
        }
        if (route == SourceFocusRoute.Game) {
            return false;
        }

        SourceHandle target;

        if (inputEvent.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
            // The release goes to the window its press reached, even when focus has moved to another source since.
            if (!m_keys.Remove(
                key: key,
                value: out target
            )) {
                return true;
            }
        } else if (!m_keys.TryGetValue(
            key: key,
            value: out target
        )) {
            if (focused is not { } source) {
                return true;
            }

            target = source;
            m_keys[key] = target;
        }

        if (m_windows.TryGet(
            source: target,
            window: out var window
        )) {
            window.DeliverKey(inputEvent: in inputEvent);
        } else if (inputEvent.Phase == CommandPhase.Started) {
            // The window has gone: the press is withheld from the game, whose keys come back from the next event.
            Focus.ReturnToGame();
        }

        return true;
    }
    // Losing the window's focus leaves no key or button held on a source's window, as it leaves none held in the game.
    // Like SourceFocus forgetting where each key went, it forgets the suppressed buttons, whose releases the window may
    // never report now.
    private void ReleaseEverything() {
        while (TryAnyHeld(source: out var source)) {
            ReleaseHeld(
                source: source,
                suppress: false
            );
        }

        m_suppressedButtons.Clear();
    }
    private bool TryAnyHeld(out SourceHandle source) {
        foreach (var press in m_buttons.Values) {
            source = press.Source;

            return true;
        }
        foreach (var held in m_keys.Values) {
            source = held;

            return true;
        }

        source = default;

        return false;
    }
}
