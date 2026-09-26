using System.Numerics;
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
public readonly record struct WorldSeatView(
    bool Present,
    NormalizedRect Region,
    CameraSnapshot Camera,
    uint Width,
    uint Height
);
/// <summary>Where a client-pixel pointer position lies relative to one seat's view.</summary>
public enum WorldSeatPointerPlace : byte {
    /// <summary>Inside the seat's viewport rect.</summary>
    Inside,
    /// <summary>The seat resolved no view this frame, or its viewport covers less than a pixel.</summary>
    NoView,
    /// <summary>Outside the seat's viewport rect.</summary>
    OutsideViewport,
}
/// <summary>
/// Every local seat's resolved viewport + camera, published by <c>WorldFramePresenter</c> once per dressed frame
/// — the read seam a pointer consumer needs to turn a cursor pixel into a world ray without re-deriving the layout
/// or the camera (which would fork the frame source's own resolution). Session-only presentation state.
/// </summary>
/// <remarks>Single-threaded by the same contract the overlay stores document: the frame source writes during frame
/// produce, the cursor feed reads during the overlay's <c>FeedTick</c>, which the unified overlay invokes AFTER
/// the inner producer's frame (so it sees THIS frame's cameras), and the pointer-ray capture reads before the next
/// frame's ticks (so it sees the cameras of the frame on screen under the pointer), all on the launcher's window-pump
/// thread.</remarks>
public sealed class WorldSeatViewports {
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
    /// <summary>Locates a pointer position against one seat's view: the one mapping from CLIENT pixels, where pointer
    /// positions arrive, to FRAME pixels, where the viewports and the drawn overlay live, and on to the seat-local
    /// point. The presenter stretches the fixed frame over the whole client area, so the inverse of that scale, per
    /// axis frame over client, is the mapping; before a client extent is published, or while it is zero, the two
    /// spaces are taken as coincident, the boot configuration.</summary>
    /// <param name="view">The seat's view for the frame just dressed.</param>
    /// <param name="position">The pointer position, in client pixels.</param>
    /// <param name="framePosition">The position in frame pixels; the client position unchanged when the seat has no
    /// view.</param>
    /// <param name="local">The position within the seat's viewport rect, <c>x</c> right and <c>y</c> down, each in
    /// <c>[0, 1]</c> across the rect while <see cref="WorldSeatPointerPlace.Inside"/>; zero when the seat has no
    /// view.</param>
    /// <returns>Where the position lies.</returns>
    public WorldSeatPointerPlace Locate(in WorldSeatView view, Vector2 position, out Vector2 framePosition, out Vector2 local) {
        framePosition = position;
        local = Vector2.Zero;

        if (!view.Present) {
            return WorldSeatPointerPlace.NoView;
        }
        if (
            (ClientWidth > 0) &&
            (ClientHeight > 0)
        ) {
            framePosition = new Vector2(
                x: (position.X * (view.Width / ((float)ClientWidth))),
                y: (position.Y * (view.Height / ((float)ClientHeight)))
            );
        }

        var regionWidthPx = (view.Region.Width * view.Width);
        var regionHeightPx = (view.Region.Height * view.Height);

        if (
            (regionWidthPx < 1f) ||
            (regionHeightPx < 1f)
        ) {
            return WorldSeatPointerPlace.NoView;
        }

        local = new Vector2(
            x: ((framePosition.X - (view.Region.X * view.Width)) / regionWidthPx),
            y: ((framePosition.Y - (view.Region.Y * view.Height)) / regionHeightPx)
        );

        return (((local.X < 0f) || (local.X > 1f) || (local.Y < 0f) || (local.Y > 1f))
            ? WorldSeatPointerPlace.OutsideViewport
            : WorldSeatPointerPlace.Inside
        );
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
