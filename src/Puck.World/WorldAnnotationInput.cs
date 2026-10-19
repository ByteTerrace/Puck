using System.Numerics;
using Puck.Commands;
using Puck.Input;
using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The window's annotate-mode pointer: while <see cref="WorldAnnotations.Active"/> holds, a left drag keeps the
/// ellipse fitting its box (a click without a drag keeps a small dot ring), a right click clears every outline, and
/// every pointer button and relative motion is consumed, so no click reaches a binding or the pointer store and no drag
/// orbits or steers the camera. The absolute position always passes on, so the cursor keeps moving. A release whose
/// press this filter consumed is consumed too, even after annotate mode ends, and one it did not see pass on, so no
/// button is left held on either side.
/// </summary>
/// <param name="annotations">The annotation state the drag draws into.</param>
/// <param name="viewports">The client extent a pointer position normalizes against.</param>
/// <remarks>Window-pump-thread only, through <see cref="WorldWindowInputFilters"/>.</remarks>
public sealed class WorldAnnotationInput(WorldAnnotations annotations, WorldSeatViewports viewports) : IWindowInputFilter {
    // A drag shorter than this on both axes, in client pixels, is a click.
    private const float ClickExtentPx = 6f;
    // The radius of a click's dot ring, in client pixels.
    private const float DotRadiusPx = 18f;

    // The buttons whose press this filter consumed, one bit per zero-based index below 32.
    private uint m_consumed;
    private bool m_dragging;
    private Vector2 m_dragStart;
    private bool m_hasPosition;
    private Vector2 m_position;

    private static uint BitOf(int buttonIndex) => (((uint)buttonIndex < 32u)
        ? (1u << buttonIndex)
        : 0u);
    // The outline a drag from start to end keeps, in normalized frame space, or null before the client extent is known.
    private OverlayAnnotationCircle? Fit(Vector2 start, Vector2 end) {
        var width = ((float)viewports.ClientWidth);
        var height = ((float)viewports.ClientHeight);

        if ((width <= 0f) || (height <= 0f)) {
            return null;
        }

        var extent = Vector2.Abs(value: (end - start));

        if ((extent.X < ClickExtentPx) && (extent.Y < ClickExtentPx)) {
            return new OverlayAnnotationCircle(
                CenterX: (start.X / width),
                CenterY: (start.Y / height),
                RadiusX: (DotRadiusPx / width),
                RadiusY: (DotRadiusPx / height)
            );
        }

        var center = ((start + end) * 0.5f);

        return new OverlayAnnotationCircle(
            CenterX: (center.X / width),
            CenterY: (center.Y / height),
            RadiusX: ((extent.X * 0.5f) / width),
            RadiusY: ((extent.Y * 0.5f) / height)
        );
    }
    // A client-pixel position in normalized frame space, or null before the client extent is known.
    private Vector2? Normalize(Vector2 position) {
        var width = ((float)viewports.ClientWidth);
        var height = ((float)viewports.ClientHeight);

        return (((width <= 0f) || (height <= 0f))
            ? null
            : new Vector2(x: (position.X / width), y: (position.Y / height))
        );
    }
    private bool Button(in WindowInputEvent inputEvent) {
        var bit = BitOf(buttonIndex: inputEvent.ButtonIndex);

        if (inputEvent.Phase == CommandPhase.Started) {
            if (!annotations.Active) {
                return false;
            }

            m_consumed |= bit;

            if ((inputEvent.ButtonIndex == 0) && m_hasPosition) {
                m_dragging = true;
                m_dragStart = m_position;
                annotations.SetDrag(drag: Fit(end: m_position, start: m_dragStart));
            } else if (inputEvent.ButtonIndex == 1) {
                _ = annotations.Clear();
            }

            return true;
        }

        if ((m_consumed & bit) == 0u) {
            return false;
        }

        m_consumed &= ~bit;

        if ((inputEvent.ButtonIndex == 0) && m_dragging) {
            m_dragging = false;
            annotations.SetDrag(drag: null);

            if (Fit(end: m_position, start: m_dragStart) is { } circle) {
                _ = annotations.TryAdd(circle: in circle, index: out _);
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public bool Intercept(in WindowInputEvent inputEvent) {
        switch (inputEvent.Kind) {
            case WindowInputKind.PointerPosition:
                m_position = inputEvent.Vector;
                m_hasPosition = true;
                annotations.SetPointer(pointer: Normalize(position: m_position));

                if (m_dragging) {
                    annotations.SetDrag(drag: Fit(end: m_position, start: m_dragStart));
                }

                return false;
            case WindowInputKind.PointerLeft:
                m_hasPosition = false;
                annotations.SetPointer(pointer: null);

                return false;
            case WindowInputKind.FocusLost:
                // The pump releases every held input on focus loss, so no release follows.
                m_consumed = 0u;
                m_dragging = false;
                annotations.SetDrag(drag: null);

                return false;
            case WindowInputKind.PointerButton:
                return Button(inputEvent: in inputEvent);
            case WindowInputKind.PointerMove:
                return (annotations.Active || m_dragging);
            default:
                return false;
        }
    }
}
