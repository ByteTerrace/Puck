using Puck.Abstractions.Presentation;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// A capture armed for the first frame a seat presents after its route moves: the frame a crossing shows first.
/// <c>world.screenshot &lt;path&gt; crossing [player]</c> arms it with the seat's route as it stands; every presented
/// frame, inside the window in which <see cref="WorldContinuum"/> pins the routes the frame is dressed with, asks
/// whether that seat's pinned route is another, and on the first frame it is, requests the capture of that frame from
/// the render root before any of its views record. The request waits here rather than on the render root, so an
/// ordinary <c>world.screenshot</c> may be armed and land meanwhile.
/// </summary>
/// <param name="continuum">The seat routes the presented frames are dressed with.</param>
/// <param name="renderProbe">The render root the capture is requested from.</param>
internal sealed class WorldCrossingCapture(WorldContinuum continuum, WorldRenderProbe renderProbe) {
    private Armed? m_armed;

    /// <summary>Gets the path of the capture waiting for its seat's crossing, or <see langword="null"/>.</summary>
    public string? PendingPath => m_armed?.Request.Path;

    /// <summary>Arms a capture of the first frame the seat presents after its route moves.</summary>
    /// <param name="request">The capture request.</param>
    /// <param name="slot">The seat's slot.</param>
    /// <param name="reason">Why the capture could not be armed, or empty.</param>
    /// <returns><see langword="true"/> when the capture is armed.</returns>
    public bool TryArm(FrameCaptureRequest request, int slot, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: request);

        if (m_armed is { } armed) {
            reason = $"a crossing capture of {armed.Request.Path} is still waiting for seat {(armed.Slot + 1)} to cross";

            return false;
        }

        m_armed = new Armed(
            Request: request,
            Route: continuum.Route(slot: slot),
            Slot: slot
        );
        reason = string.Empty;

        return true;
    }
    /// <summary>Requests the armed capture of this frame when the seat's route pinned for it is another than the one it
    /// was armed with. Called once a presented frame, while its routes are pinned and before its views record.</summary>
    public void Present() {
        if (
            (m_armed is not { } armed) ||
            ReferenceEquals(
                objA: continuum.Route(slot: armed.Slot),
                objB: armed.Route
            )
        ) {
            return;
        }

        m_armed = null;

        if (renderProbe.Root is not { } render) {
            _ = armed.Request.TryFail(error: new InvalidOperationException(message: "the renderer is not built"));

            return;
        }
        if (render.PendingCapturePath is { } outstanding) {
            _ = armed.Request.TryFail(error: new InvalidOperationException(message: $"seat {(armed.Slot + 1)} crossed while a capture of {outstanding} was still pending"));

            return;
        }

        render.RequestCapture(request: armed.Request);
        Console.Error.WriteLine(value: $"[capture] crossing: seat {(armed.Slot + 1)} presents its new route on this frame -> {armed.Request.Path}");
    }

    // The armed capture, the seat it waits for, and the route it waits for the seat to leave.
    private sealed record Armed(FrameCaptureRequest Request, WorldAuthorityRoute Route, int Slot);
}
