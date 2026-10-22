using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.Input;

namespace Puck.World.Client;

// A local pad driving the pointer over an engaged window, Steam desktop-mode style. The pad's pointer is the system
// cursor: each frame's travel moves it (TakeWarp, which the window pump hands to the native window) and routes the new
// position through the router at once, so a press in the same frame lands where the pointer went. Its presses take the
// same door a physical press does: OpenEngagedUnderPointer, then the router. Its arrow keys reach only a window the local
// user has opened. Over an engaged desktop the pad is the desktop's mouse instead (ISourcePassthroughDesktop): it moves
// the system cursor across the whole monitor and presses, scrolls and types at it, whichever window is in front, and a
// press on the host's own picture reaches the host as its mouse's press would.
public sealed partial class WorldSourcePassthrough {
    // Wheel turns one Win32 wheel report divides a notch into; the pad's fractional turns are delivered in these steps.
    private const float WheelSteps = 120f;
    private static readonly string[] PadButtonNames = ["left", "right", "middle", "back", "forward"];

    private readonly WorldPassthroughPad m_pad = new();
    private readonly HashSet<int> m_padButtons = [];
    private readonly Dictionary<int, ISourcePassthroughDesktop> m_padDesktopButtons = [];
    private readonly Dictionary<KeyCode, ISourcePassthroughDesktop> m_padDesktopKeys = [];
    private readonly Dictionary<KeyCode, Opened> m_padKeys = [];
    // The desktop the reserved chord is claimed for while a local seat frames it.
    private ISourcePassthroughDesktop? m_chordDesktop;
    private long? m_lastAdvance;
    // The pad's exact pointer, in display pixels, while the pointer is where the pad last put it.
    private Vector2? m_padPosition;
    private Vector2 m_padWheel;
    private (int X, int Y)? m_warp;

    /// <summary>Gets the pad driving the pointer over an engaged window.</summary>
    public WorldPassthroughPad Pad => m_pad;

    // A position the window reported: the pad keeps its exact pointer while the report is the pad's own move echoed
    // back in whole client pixels, and starts from wherever the mouse put the pointer otherwise.
    private void NotePointer(Vector2 display) {
        if (m_padPosition is not { } pad) {
            return;
        }

        var tolerance = (MathF.Max(
            x: (((m_viewports.ClientWidth > 0) ? (m_graphs.DisplayWidth / ((float)m_viewports.ClientWidth)) : 1f)),
            y: (((m_viewports.ClientHeight > 0) ? (m_graphs.DisplayHeight / ((float)m_viewports.ClientHeight)) : 1f))
        ) + 0.5f);

        if (Vector2.Distance(
            value1: display,
            value2: pad
        ) > tolerance) {
            m_padPosition = null;
        }
    }
    // Moves the pointer for the pad: routes the position at once and asks the pump to move the cursor there.
    private void MovePadPointer(Vector2 display) {
        m_padPosition = display;
        m_pointer = display;
        _ = Router.Route(inputEvent: WindowInputEvent.PointerAbsolute(position: display));
        m_warp = (((m_viewports.ClientWidth > 0) && (m_viewports.ClientHeight > 0))
            ? (((int)MathF.Floor(x: ((display.X * m_viewports.ClientWidth) / m_graphs.DisplayWidth))), ((int)MathF.Floor(x: ((display.Y * m_viewports.ClientHeight) / m_graphs.DisplayHeight))))
            : (((int)MathF.Floor(x: display.X)), ((int)MathF.Floor(x: display.Y))));
    }
    // Puts the pointer on the framed picture for the pad when it lies elsewhere: where the pad left it, or the centre.
    private void PlacePadPointer(PictureRect picture) {
        if (
            (m_pointer is { } pointer) &&
            picture.Contains(point: pointer)
        ) {
            return;
        }

        MovePadPointer(display: picture.Clamp(point: (m_padPosition ?? picture.Centre)));
    }
    private void ReleasePadKeys(SourceHandle source) {
        foreach (var (key, held) in m_padKeys) {
            if (held.Source == source) {
                held.Window.DeliverKey(inputEvent: WindowInputEvent.KeyUp(key: key));
                _ = m_padKeys.Remove(key: key);
            }
        }
    }
    // Releases every button and key a pad holds on a desktop, as closing its source releases a window's.
    private void ReleasePadDesktop(ISourcePassthroughWindow window) {
        foreach (var (button, held) in m_padDesktopButtons) {
            if (ReferenceEquals(objA: held, objB: window)) {
                held.PressPointer(
                    button: button,
                    pressed: false
                );
                _ = m_padDesktopButtons.Remove(key: button);
            }
        }
        foreach (var (key, held) in m_padDesktopKeys) {
            if (ReferenceEquals(objA: held, objB: window)) {
                held.PressKey(
                    key: key,
                    pressed: false
                );
                _ = m_padDesktopKeys.Remove(key: key);
            }
        }
    }
    // The desktop a local seat frames, when its screen shows one.
    private bool TryFramedDesktop(int slot, [NotNullWhen(returnValue: true)] out string? instance, [NotNullWhen(returnValue: true)] out ISourcePassthroughDesktop? desktop) {
        if (
            TryFramedWindow(
                instance: out instance,
                region: out _,
                screen: out _,
                slot: slot
            ) &&
            (m_windows.PassthroughWindowOf(
                fault: out _,
                instance: instance
            ) is ISourcePassthroughDesktop framed)
        ) {
            desktop = framed;

            return true;
        }

        instance = null;
        desktop = null;

        return false;
    }
    // Claims the reserved chord system-wide for the desktop a local seat frames, gives it back from one no seat frames,
    // and leaves when the chord was pressed, whichever window had the keyboard.
    private void ClaimChord() {
        ISourcePassthroughDesktop? framed = null;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (TryFramedDesktop(
                desktop: out var desktop,
                instance: out _,
                slot: slot
            )) {
                framed = desktop;

                break;
            }
        }
        if (!ReferenceEquals(
            objA: framed,
            objB: m_chordDesktop
        )) {
            if (m_chordDesktop is { } released) {
                released.ClaimsChord = false;
            }

            m_chordDesktop = framed;
        }
        if (framed is null) {
            return;
        }

        framed.ClaimsChord = true;

        if (framed.TakeChord()) {
            LeaveFramed();
        }
    }
    // The wheel's whole steps the pad turned, in notches, the remainder carried to the next frame.
    private Vector2 TakeWheel(Vector2 wheel) {
        m_padWheel += wheel;

        var steps = new Vector2(
            x: MathF.Truncate(x: (m_padWheel.X * WheelSteps)),
            y: MathF.Truncate(x: (m_padWheel.Y * WheelSteps))
        );

        m_padWheel -= (steps / WheelSteps);

        return (steps / WheelSteps);
    }
    private void ResetPad(int slot) {
        if (m_pad.Slot == slot) {
            m_pad.Reset();
            m_padPosition = null;
            m_padWheel = Vector2.Zero;
        }
    }

    /// <summary>Holds a local seat's pointer stick, which moves the pointer over the window the seat frames from the next
    /// frame on (<see cref="TakeWarp"/>). A deflection while the seat frames no window moves nothing; a rest always
    /// applies.</summary>
    /// <param name="slot">The 0-based local seat whose pad reported it.</param>
    /// <param name="value">The deflection, +X right and +Y up.</param>
    /// <returns><see langword="true"/> when the seat frames a window, or the stick rests.</returns>
    public bool PadPointer(int slot, Vector2 value) {
        if (
            (value != Vector2.Zero) &&
            !TryFramedWindow(
                instance: out _,
                region: out _,
                screen: out _,
                slot: slot
            )
        ) {
            return false;
        }

        m_pad.SetStick(
            slot: slot,
            value: value
        );

        return true;
    }
    /// <summary>Holds a local seat's scrolling stick, which turns the wheel at the pointer over the window the seat
    /// frames from the next frame on. A deflection while the seat frames no window turns nothing; a rest always
    /// applies.</summary>
    /// <param name="slot">The 0-based local seat whose pad reported it.</param>
    /// <param name="value">The deflection, +X right and +Y up (the wheel turning away from the user).</param>
    /// <returns><see langword="true"/> when the seat frames a window, or the stick rests.</returns>
    public bool PadScroll(int slot, Vector2 value) {
        if (
            (value != Vector2.Zero) &&
            !TryFramedWindow(
                instance: out _,
                region: out _,
                screen: out _,
                slot: slot
            )
        ) {
            return false;
        }

        m_pad.SetScroll(
            slot: slot,
            value: value
        );

        return true;
    }
    /// <summary>Presses or releases a pointer button for a local seat's pad, through the door a physical press takes: a
    /// press on the framed picture of a window the seat frames opens it as a passthrough source when it is not open yet,
    /// and reaches the window at the pointer, moving the pointer onto the picture first when it lies elsewhere. A
    /// release goes where its press went.</summary>
    /// <param name="slot">The 0-based local seat whose pad reported it.</param>
    /// <param name="button">The pointer button: 0 left, 1 right, 2 middle, 3 back, 4 forward.</param>
    /// <param name="pressed">Whether the button went down.</param>
    /// <returns><see langword="true"/> when a press reached a window or a release ended one; <see langword="false"/>
    /// for a press while the seat frames no window, or a release of a press that reached none.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="button"/> is not 0 through 4.</exception>
    public bool PadButton(int slot, int button, bool pressed) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: button);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: PadButtonNames.Length,
            value: button
        );
        CloseStale();

        if (!pressed) {
            if (m_padDesktopButtons.Remove(
                key: button,
                value: out var desktopHeld
            )) {
                desktopHeld.PressPointer(
                    button: button,
                    pressed: false
                );

                return true;
            }

            return (
                m_padButtons.Remove(item: button) &&
                Router.Route(inputEvent: WindowInputEvent.PointerButton(
                    button: button,
                    phase: CommandPhase.Completed
                ))
            );
        }
        if (TryFramedDesktop(
            desktop: out var desktop,
            instance: out var desktopInstance,
            slot: slot
        )) {
            OpenFramed(
                instance: desktopInstance,
                window: desktop
            );
            desktop.PressPointer(
                button: button,
                pressed: true
            );
            m_padDesktopButtons[button] = desktop;

            return true;
        }
        if (!TryFramedWindow(
            instance: out _,
            region: out var region,
            screen: out _,
            slot: slot
        )) {
            return false;
        }

        PlacePadPointer(picture: Picture(region: region));
        OpenEngagedUnderPointer();

        if (!Router.Route(inputEvent: WindowInputEvent.PointerButton(
            button: button,
            phase: CommandPhase.Started
        ))) {
            return false;
        }

        _ = m_padButtons.Add(item: button);

        return true;
    }
    /// <summary>Presses or releases a key for a local seat's pad on the window the seat frames, once the local user has
    /// opened it, wherever the keyboard is. A release goes to the window its press reached; closing the source releases
    /// every key its window holds.</summary>
    /// <param name="slot">The 0-based local seat whose pad reported it.</param>
    /// <param name="key">The key.</param>
    /// <param name="pressed">Whether the key went down.</param>
    /// <returns><see langword="true"/> when the key reached a window.</returns>
    public bool PadKey(int slot, KeyCode key, bool pressed) {
        CloseStale();

        if (!pressed) {
            if (m_padDesktopKeys.Remove(
                key: key,
                value: out var desktopHeld
            )) {
                desktopHeld.PressKey(
                    key: key,
                    pressed: false
                );

                return true;
            }
            if (!m_padKeys.Remove(
                key: key,
                value: out var held
            )) {
                return false;
            }

            held.Window.DeliverKey(inputEvent: WindowInputEvent.KeyUp(key: key));

            return true;
        }
        if (TryFramedDesktop(
            desktop: out var desktop,
            instance: out _,
            slot: slot
        )) {
            if (!m_padDesktopKeys.TryAdd(
                key: key,
                value: desktop
            )) {
                return false;
            }

            desktop.PressKey(
                key: key,
                pressed: true
            );

            return true;
        }
        if (
            !TryFramedWindow(
                instance: out var instance,
                region: out _,
                screen: out _,
                slot: slot
            ) ||
            !m_opened.TryGetValue(
                key: instance,
                value: out var opened
            ) ||
            !m_padKeys.TryAdd(
                key: key,
                value: opened
            )
        ) {
            return false;
        }

        opened.Window.DeliverKey(inputEvent: WindowInputEvent.KeyDown(key: key));

        return true;
    }
    /// <summary>Advances the pad over the frame's interval and returns the client point the cursor moves to: the pointer
    /// travels across the framed picture of the window its seat frames, clamped to the picture, and the wheel turns at
    /// it in whole wheel steps, the remainder carried to the next frame; over a desktop it moves the system cursor across
    /// the desktop instead and returns no point. The window pump calls it once a frame, through the cursor policy, which
    /// also closes the sources that are gone and claims the reserved chord for a desktop a local seat frames. A seat that
    /// no longer frames a window rests the pad.</summary>
    /// <returns>The client point, or <see langword="null"/> when the cursor stays where it is.</returns>
    public (int X, int Y)? TakeWarp() {
        var now = m_time.GetTimestamp();
        var seconds = ((m_lastAdvance is { } last)
            ? m_time.GetElapsedTime(
                endingTimestamp: now,
                startingTimestamp: last
            ).TotalSeconds
            : 0.0);

        m_lastAdvance = now;
        CloseStale();
        ClaimChord();

        var slot = m_pad.Slot;

        if (slot >= 0) {
            if (TryFramedWindow(
                instance: out var instance,
                region: out var region,
                screen: out var screen,
                slot: slot
            )) {
                var feel = m_seats.PadPointerOf(screen: screen);

                if (m_windows.PassthroughWindowOf(
                    fault: out _,
                    instance: instance
                ) is ISourcePassthroughDesktop desktop) {
                    AdvanceDesktopPad(
                        desktop: desktop,
                        feel: feel,
                        seconds: seconds
                    );
                } else {
                    AdvancePad(
                        feel: feel,
                        picture: Picture(region: region),
                        seconds: seconds
                    );
                }
            } else {
                ResetPad(slot: slot);
            }
        }

        var warp = m_warp;

        m_warp = null;

        return warp;
    }
    private void AdvancePad(double seconds, WorldScreenPadPointer? feel, PictureRect picture) {
        var (travel, wheel) = m_pad.Advance(
            feel: feel,
            seconds: seconds
        );

        if (travel != Vector2.Zero) {
            var from = ((m_padPosition is { } exact)
                ? exact
                : (((m_pointer is { } pointer) && picture.Contains(point: pointer))
                    ? pointer
                    : picture.Centre));

            MovePadPointer(display: picture.Clamp(point: (from + (travel * picture.Height))));
        }

        var notches = TakeWheel(wheel: wheel);

        if (notches != Vector2.Zero) {
            PlacePadPointer(picture: picture);
            _ = Router.Route(inputEvent: WindowInputEvent.PointerWheel(notches: notches));
        }
    }
    // The pad over a desktop: the travel, in desktop heights, moves the system cursor, and the wheel turns at it.
    private void AdvanceDesktopPad(ISourcePassthroughDesktop desktop, double seconds, WorldScreenPadPointer? feel) {
        var (travel, wheel) = m_pad.Advance(
            feel: feel,
            seconds: seconds
        );

        if (travel != Vector2.Zero) {
            desktop.MovePointer(distance: (travel * desktop.Height));
        }

        var notches = TakeWheel(wheel: wheel);

        if (notches != Vector2.Zero) {
            desktop.TurnWheel(notches: notches);
        }
    }
    private void DescribePad(StringBuilder builder) {
        var slot = m_pad.Slot;

        if (
            (slot < 0) ||
            !TryFramedWindow(
                instance: out var instance,
                region: out _,
                screen: out var screen,
                slot: slot
            )
        ) {
            _ = builder.Append(value: "; pad idle");

            return;
        }

        var stick = m_pad.Stick;
        var scroll = m_pad.Scroll;

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"; pad p{PlayerRoster.DisplayNumber(slot: slot)} screen {screen} '{instance}' stick ({stick.X:0.###}, {stick.Y:0.###}) scroll ({scroll.X:0.###}, {scroll.Y:0.###}) buttons ");
        AppendList(
            builder: builder,
            names: m_padButtons.Order().Select(selector: static button => PadButtonNames[button])
        );
        _ = builder.Append(value: " keys ");
        AppendList(
            builder: builder,
            names: m_padKeys.Keys.Order().Select(selector: static key => key.ToString())
        );
        _ = ((m_seats.PadPointerOf(screen: screen) is { } feel)
            ? builder.Append(provider: CultureInfo.InvariantCulture, handler: $" feel speed {feel.Speed:0.###} curve {feel.Curve:0.###} boost {feel.Boost:0.###} ramp {feel.RampSeconds:0.###}s scroll {feel.ScrollSpeed:0.###}/s")
            : builder.Append(value: " feel none (the route authors no padPointer)"));
    }
    private static void AppendList(StringBuilder builder, IEnumerable<string> names) {
        var any = false;

        foreach (var name in names) {
            _ = builder.Append(value: (any ? "+" : string.Empty)).Append(value: name);
            any = true;
        }

        if (!any) {
            _ = builder.Append(value: "none");
        }
    }
}
