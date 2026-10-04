using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// A capture armed for the first frame a seat presents after its route moves: the frame a crossing shows first.
/// <c>world.screenshot &lt;path&gt; crossing [player]</c> arms it with the seat's route as it stands; every presented
/// frame, inside the window in which <see cref="WorldContinuum"/> pins the routes the frame is dressed with, asks
/// whether that seat's pinned route has a later epoch, and on the first visible frame it does, requests its capture from
/// the render root before any of its views record. The request waits here rather than on the render root, so an
/// ordinary <c>world.screenshot</c> may be armed and land meanwhile. A request the root cannot serve in its crossing
/// frame is withdrawn before the next frame.
/// </summary>
/// <param name="continuum">The seat routes the presented frames are dressed with.</param>
/// <param name="routes">The published routes, safe to read from the console thread when arming.</param>
/// <param name="viewports">The seats the current frame actually presents.</param>
/// <param name="captureTarget">The render root the capture is requested from, read on the frame thread.</param>
public sealed class WorldCrossingCapture(WorldContinuum continuum, WorldSeatAuthorityRouter routes, WorldSeatViewports viewports, Func<ICaptureRequestTarget?> captureTarget) : IDisposable {
    private readonly Lock m_gate = new();

    private Armed? m_armed;
    private FrameCaptureRequest? m_presenting;
    private bool m_disposed;

    /// <summary>Gets the path of the capture waiting for its seat's crossing, or <see langword="null"/>.</summary>
    public string? PendingPath {
        get {
            lock (m_gate) {
                return ((m_armed is { Request.Completion.IsCompleted: false } armed) ? armed.Request.Path : null);
            }
        }
    }

    /// <summary>Arms a capture of the first frame the seat presents after its route moves.</summary>
    /// <param name="request">The capture request.</param>
    /// <param name="slot">The seat's slot.</param>
    /// <param name="reason">Why the capture could not be armed, or empty.</param>
    /// <returns><see langword="true"/> when the capture is armed.</returns>
    public bool TryArm(FrameCaptureRequest request, int slot, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: request);

        lock (m_gate) {
            if (m_disposed || request.Completion.IsCompleted) {
                reason = "the crossing capture is disposed or the request is complete";

                return false;
            }
            if (m_armed is { Request.Completion.IsCompleted: false } armed) {
                reason = $"a crossing capture of {armed.Request.Path} is still waiting for seat {(armed.Slot + 1)} to cross";

                return false;
            }
            if ((((uint)slot) >= PlayerRoster.MaxSlots) || (routes.TryRoute(slot: slot) is not { } route)) {
                reason = $"seat {(slot + 1)} has no authority route";

                return false;
            }

            m_armed = new Armed(Request: request, Route: route, Slot: slot);
            reason = string.Empty;

            return true;
        }
    }
    /// <summary>Requests the armed capture of this frame when the seat's route pinned for it is later than the one it
    /// was armed with. Called once a presented frame, while its routes are pinned and before its views record.</summary>
    /// <param name="previousFrame">Whether the preceding attempt completed, still awaits renderable inputs, or was refused.</param>
    public void Present(FrameRender previousFrame) {
        lock (m_gate) {
            // The root serves readback within ProduceFrame. A request still pending at the next presentation missed
            // its crossing frame; withdrawing it prevents a rebuild or an unavailable input from capturing a later route.
            _ = m_presenting?.TryFail(error: new InvalidOperationException(message: "the first crossing frame did not serve the capture"));
            m_presenting = null;

            if (m_armed is not { } armed) {
                return;
            }
            if (armed.Request.Completion.IsCompleted) {
                m_armed = null;

                return;
            }
            // Arming can observe a publication newer than the route pinned for this frame. Only a later epoch of a
            // seat this frame presents can serve the request; a different, earlier pinned route cannot.
            if (!viewports.Seat(slot: armed.Slot).Present || (continuum.Route(slot: armed.Slot).Epoch <= armed.Route.Epoch)) {
                return;
            }

            m_armed = null;

            try {
                if (captureTarget() is not { } render) {
                    throw new InvalidOperationException(message: "the renderer is not built");
                }
                if (render.PendingCapturePath is { } outstanding) {
                    throw new InvalidOperationException(message: $"seat {(armed.Slot + 1)} crossed while a capture of {outstanding} was still pending");
                }

                render.RequestCapture(request: armed.Request);
                m_presenting = armed.Request;
                Console.Error.WriteLine(value: $"[capture] crossing: seat {(armed.Slot + 1)} presents its new route on this frame -> {armed.Request.Path}");
            } catch (InvalidOperationException error) {
                _ = armed.Request.TryFail(error: error);
            }
        }
    }
    /// <summary>Refuses a capture still waiting for a crossing when the presentation ends.</summary>
    public void Dispose() {
        lock (m_gate) {
            m_disposed = true;
            _ = m_armed?.Request.TryFail(error: new ObjectDisposedException(objectName: nameof(WorldCrossingCapture), message: "the presentation ended before the seat crossed"));
            m_armed = null;
            _ = m_presenting?.TryFail(error: new ObjectDisposedException(objectName: nameof(WorldCrossingCapture), message: "the crossing frame did not serve the capture before presentation ended"));
            m_presenting = null;
        }
    }

    // The armed capture, the seat it waits for, and the route it waits for the seat to leave.
    private sealed record Armed(FrameCaptureRequest Request, WorldAuthorityRoute Route, int Slot);
}
