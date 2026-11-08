using Puck.Hosting;

namespace Puck.SdfVm;

// The glass passes of a view (glass-clear, glass, glass-shade) have work only while a glass draw's box meets the view's
// frustum, which is the glass pass's own selection (SdfMeshCull.Keeps). One decision per instance and frame is shared by
// the three recorders, so they agree: while no glass is in view the clear and the raster skip and the shading pass draws
// nothing, leaving its lit image standing for the opaque one. The sums and the glass target are never read on such a
// frame, and a frame that sees glass records all three, the clear and the raster ahead of the shading that reads them.
public sealed partial class SdfWorldPasses {
    /// <summary>Returns whether the instance's glass passes have a glass draw to rasterize this frame, deciding it when
    /// no glass pass has yet. The three glass recorders read this one answer.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="view">The view the glass recorders follow, whose residency has begun this frame.</param>
    /// <param name="context">The frame being recorded.</param>
    /// <returns><see langword="false"/> when the glass pass keeps no glass draw of the frame's staged list.</returns>
    internal bool GlassSeen(string instance, SdfWorldView view, in FrameContext context) {
        var entry = Refresh(instance: instance);

        if (entry.GlassDecided != m_frame) {
            entry.GlassDecided = m_frame;
            entry.GlassSeen = DecideGlass(entry: entry, view: view, context: in context);
        }

        return entry.GlassSeen;
    }
    /// <summary>Records the extent of the render grid a glass pass of the instance records at, which the glass cull's
    /// margin is measured against (<see cref="SdfMeshBox.MarginOf"/>).</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="width">The pixels across the render grid.</param>
    /// <param name="height">The pixels down the render grid.</param>
    internal void NoteGlassExtent(string instance, uint width, uint height) {
        var entry = Refresh(instance: instance);

        entry.GlassWidth = width;
        entry.GlassHeight = height;
    }
    // A pass that has not recorded yet knows no extent, so its frame keeps every glass draw.
    private static bool DecideGlass(Entry entry, SdfWorldView view, in FrameContext context) {
        var residency = view.Residency;
        var frame = residency.Frame;

        if ((frame is null) || (frame.Views.Count == 0) || (entry.GlassWidth == 0) || (entry.GlassHeight == 0)) {
            return true;
        }

        var cull = ((frame.MeshCull == SdfMeshCullLevel.Off)
            ? SdfMeshCull.None
            : SdfMeshCull.Of(camera: frame.Views[Math.Min(val1: view.View, val2: (frame.Views.Count - 1))].Camera, far: frame.FarDistance, height: entry.GlassHeight, width: entry.GlassWidth));

        return residency.Staged(context: in context).GlassMaySee(cull: in cull);
    }

    private sealed partial class Entry {
        public long GlassDecided;
        public bool GlassSeen;
        public uint GlassWidth;
        public uint GlassHeight;
    }
}
