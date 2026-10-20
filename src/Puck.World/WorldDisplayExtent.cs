using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The display extent the render root, the screen binder and the frame presenter share. It follows the extent
/// the host asks each frame to fill (<see cref="FrameContext.TargetWidth"/>, <see cref="FrameContext.TargetHeight"/>):
/// a windowed host's client area, so the display follows a resize or a maximize of the window, and an offscreen host's
/// extent, which <c>world.resize</c> sets.</summary>
/// <param name="binder">The binder whose camera and session views fit their declared extents to the display.</param>
/// <param name="presenter">The presenter that places panes and composes cameras over the display.</param>
/// <param name="width">The display's width the root, binder and presenter were built at, in pixels.</param>
/// <param name="height">The display's height the root, binder and presenter were built at, in pixels.</param>
internal sealed class WorldDisplayExtent(WorldScreenBinder binder, WorldFramePresenter presenter, uint width, uint height) {
    private uint m_height = height;
    private uint m_width = width;

    /// <summary>Resizes the display to the frame's target extent when it differs, before the frame is prepared, so the
    /// frame that reports a new extent schedules, places and composes at it. A target with no area (a minimized window)
    /// keeps the display as it is, and one past <see cref="WorldHostDefaults.MaxDisplayExtent"/> is clamped to it.</summary>
    /// <param name="root">The render root whose runtime schedules every instance against the display.</param>
    /// <param name="context">The frame's context.</param>
    public void Follow(RenderGraphRuntimeNode root, in FrameContext context) {
        var targetWidth = Math.Min(
            val1: context.TargetWidth,
            val2: ((uint)WorldHostDefaults.MaxDisplayExtent)
        );
        var targetHeight = Math.Min(
            val1: context.TargetHeight,
            val2: ((uint)WorldHostDefaults.MaxDisplayExtent)
        );

        if (
            (targetWidth == 0u) ||
            (targetHeight == 0u) ||
            ((targetWidth == m_width) && (targetHeight == m_height))
        ) {
            return;
        }

        m_width = targetWidth;
        m_height = targetHeight;
        root.Resize(
            height: targetHeight,
            width: targetWidth
        );
        binder.ResizeDisplay(
            displayHeight: ((int)targetHeight),
            displayWidth: ((int)targetWidth)
        );
        presenter.ResizeDisplay(
            height: targetHeight,
            width: targetWidth
        );
    }
}
