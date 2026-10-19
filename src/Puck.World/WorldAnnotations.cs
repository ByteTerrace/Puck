using Puck.Overlays;

namespace Puck.World;

/// <summary>
/// The window's on-screen annotations: the outlines a person circles to point something out, whether annotate mode is
/// on, and the outline a drag in progress would keep. Presentation only: nothing here reaches the simulation, the
/// journal or a replay. The <c>view.annotate</c> verb edits and lists it in every boot shape;
/// <see cref="WorldAnnotationInput"/> draws into it from the window's pointer, and <see cref="AnnotationWriter"/> draws
/// it on the overlay. Leaving annotate mode keeps the outlines until they are cleared.
/// </summary>
/// <remarks>Thread-safe: the window pump, the console and the render thread each reach it.</remarks>
public sealed class WorldAnnotations : IAnnotationSource {
    private readonly OverlayAnnotationCircle[] m_circles = new OverlayAnnotationCircle[AnnotationWriter.MaxCircles];
    private readonly Lock m_gate = new();
    // The render thread's copy, valid until its next snapshot.
    private readonly OverlayAnnotationCircle[] m_snapshot = new OverlayAnnotationCircle[AnnotationWriter.MaxCircles];

    private bool m_active;
    private int m_count;
    private OverlayAnnotationCircle? m_drag;
    private System.Numerics.Vector2? m_pointer;

    /// <summary>Gets whether annotate mode is on.</summary>
    public bool Active {
        get {
            lock (m_gate) {
                return m_active;
            }
        }
    }
    /// <summary>Gets the number of kept outlines.</summary>
    public int Count {
        get {
            lock (m_gate) {
                return m_count;
            }
        }
    }

    /// <summary>Removes every kept outline and the drag in progress.</summary>
    /// <returns>The number of outlines removed.</returns>
    public int Clear() {
        lock (m_gate) {
            var removed = m_count;

            m_count = 0;
            m_drag = null;

            return removed;
        }
    }
    /// <summary>Copies the kept outlines, oldest first.</summary>
    /// <returns>A new array of the outlines.</returns>
    public OverlayAnnotationCircle[] List() {
        lock (m_gate) {
            return m_circles.AsSpan(start: 0, length: m_count).ToArray();
        }
    }
    /// <summary>Removes the newest kept outline.</summary>
    /// <param name="index">The removed outline's one-based index, or 0 when there was none.</param>
    /// <returns><see langword="true"/> when an outline was removed.</returns>
    public bool RemoveLast(out int index) {
        lock (m_gate) {
            index = m_count;

            if (m_count == 0) {
                return false;
            }

            m_count--;

            return true;
        }
    }
    /// <summary>Turns annotate mode on or off. Turning it off ends the preview of a drag in progress and keeps every
    /// outline.</summary>
    /// <param name="active">Whether annotate mode is on.</param>
    public void SetActive(bool active) {
        lock (m_gate) {
            m_active = active;

            if (!active) {
                m_drag = null;
            }
        }
    }
    /// <summary>Sets the outline a drag in progress would keep.</summary>
    /// <param name="drag">The outline, or <see langword="null"/> for none.</param>
    public void SetDrag(OverlayAnnotationCircle? drag) {
        lock (m_gate) {
            m_drag = drag;
        }
    }
    /// <summary>Sets where the pointer is, which annotate mode draws as a reticle.</summary>
    /// <param name="pointer">The pointer in normalized frame space (origin top-left, Y down), or <see langword="null"/>
    /// when it is not over the window.</param>
    public void SetPointer(System.Numerics.Vector2? pointer) {
        lock (m_gate) {
            m_pointer = pointer;
        }
    }
    /// <summary>Keeps one outline after the others.</summary>
    /// <param name="circle">The outline, in normalized frame space.</param>
    /// <param name="index">The kept outline's one-based index, or 0 when none was kept.</param>
    /// <returns><see langword="true"/> when it was kept; <see langword="false"/> when <see cref="AnnotationWriter.MaxCircles"/>
    /// outlines are already kept.</returns>
    public bool TryAdd(in OverlayAnnotationCircle circle, out int index) {
        lock (m_gate) {
            if (m_count == m_circles.Length) {
                index = 0;

                return false;
            }

            m_circles[m_count++] = circle;
            index = m_count;

            return true;
        }
    }
    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayAnnotationFrame frame) {
        lock (m_gate) {
            m_circles.AsSpan(start: 0, length: m_count).CopyTo(destination: m_snapshot);
            frame = new OverlayAnnotationFrame(
                Active: m_active,
                Circles: m_snapshot.AsMemory(start: 0, length: m_count),
                Drag: m_drag,
                Pointer: m_pointer
            );

            return (m_active || (m_count > 0) || m_drag.HasValue);
        }
    }
}
