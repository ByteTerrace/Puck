using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;

namespace Puck.World.Client;

/// <summary>One seat's resolved view for the frame just dressed: the viewport rect it rendered into and the exact
/// camera it rendered with (basis + FOV — enough to unproject a viewport pixel back into a world ray).</summary>
/// <param name="Present">Whether the seat resolved a view this frame (joined and bound to a layout slot).</param>
/// <param name="Region">The seat's viewport rect in normalized frame space.</param>
/// <param name="Camera">The render camera's exact snapshot (editor rig included — the same one the view drew with).</param>
/// <param name="Width">The full frame width, px.</param>
/// <param name="Height">The full frame height, px.</param>
internal readonly record struct WorldSeatView(
    bool Present,
    NormalizedRect Region,
    CameraSnapshot Camera,
    uint Width,
    uint Height
);
/// <summary>
/// Every local seat's resolved viewport + camera, published by <see cref="WorldFrameSource"/> once per dressed frame
/// — the read seam a pointer consumer needs to turn a cursor pixel into a world ray without re-deriving the layout
/// or the camera (which would fork the frame source's own resolution). Session-only presentation state.
/// </summary>
/// <remarks>Single-threaded by the same contract the overlay stores document: the frame source writes during frame
/// produce and the cursor feed reads during the overlay's <c>FeedTick</c>, which the unified overlay invokes AFTER
/// the inner producer's frame (so a read always sees THIS frame's cameras), all on the launcher's window-pump
/// thread.</remarks>
internal sealed class WorldSeatViewports {
    private readonly WorldSeatView[] m_seats = new WorldSeatView[PlayerRoster.MaxSlots];

    /// <summary>Gets the live OS client-area height, px — 0 until the first frame publishes it.</summary>
    public uint ClientHeight { get; private set; }
    /// <summary>Gets the live OS client-area width, px — 0 until the first frame publishes it.</summary>
    public uint ClientWidth { get; private set; }

    /// <summary>Clears every seat's view — the start of a dress; a seat that resolves no view this frame stays
    /// absent. The client extent is NOT cleared: it is a window fact, not a per-seat one, and the freshest
    /// publication stays valid until the next one lands.</summary>
    public void BeginFrame() => Array.Clear(array: m_seats);
    /// <summary>Publishes one seat's resolved view for this frame.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="region">The seat's viewport rect in normalized frame space.</param>
    /// <param name="camera">The render camera's snapshot.</param>
    /// <param name="width">The full frame width, px.</param>
    /// <param name="height">The full frame height, px.</param>
    public void Publish(int slot, NormalizedRect region, in CameraSnapshot camera, uint width, uint height) {
        if (((uint)slot) < ((uint)m_seats.Length)) {
            m_seats[slot] = new WorldSeatView(
                Camera: camera,
                Height: height,
                Present: true,
                Region: region,
                Width: width
            );
        }
    }
    /// <summary>Publishes the live OS client-area extent — the space pointer positions arrive in, distinct from the
    /// FIXED frame extent each <see cref="WorldSeatView"/> carries (the presenter stretches the frame over the
    /// client area, so the two diverge the moment the window is resized).</summary>
    /// <param name="width">The client-area width, px.</param>
    /// <param name="height">The client-area height, px.</param>
    public void PublishClientExtent(uint width, uint height) {
        ClientWidth = width;
        ClientHeight = height;
    }
    /// <summary>The seat's view for the frame just dressed (absent = <c>Present: false</c>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public WorldSeatView Seat(int slot) {
        return ((((uint)slot) < ((uint)m_seats.Length))
            ? m_seats[slot]
            : default
        );
    }
}
