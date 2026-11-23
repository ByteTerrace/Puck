using Puck.Shaders;

namespace Puck.SdfVm;

// Each instance's motion scopes (SdfMotionScope): one for each raster pass whose draws its passes show. A pass signs with
// the revision of its scope (MotionOf), so its image stands while nothing its scope's cull keeps has changed, whatever
// else of the residency moves:
//   - the mesh pass's   : the mesh pass and every pass that shades what it drew (the pyramid, the surface pass, shading,
//                         the resolve, the sky and the composite, the optical tail). The instance's temporal history
//                         follows this scope's poses.
//   - the glass pass's  : the glass clear, raster and shading, judged by glass draws alone.
// The shadows are the residency's (SdfWorldShadows), whose entries hold scopes of their own under their light boxes; the
// surface pass reads what they hold through the shadows' content revision.
// One function builds a pass's cull for both its recording and its scope (CullOf). The scope's is built from the
// extent the pass's latest recording noted (NoteDrawn), so while the frame's camera and that extent stand it is the
// cull the standing image was drawn under; before a pass has recorded, its scope keeps every draw.
public sealed partial class SdfWorldPasses {
    private enum DrawScope : byte {
        Mesh = 0,
        Glass = 1,
    }

    // The scope a part signs with.
    private static DrawScope DrawScopeOf(string? part) => part switch {
        SdfWorldPackage.Parts.GlassClear or SdfWorldPackage.Parts.Glass or SdfWorldPackage.Parts.GlassShade => DrawScope.Glass,
        _ => DrawScope.Mesh,
    };

    /// <summary>Returns the cull a raster pass of a view draws under: the view's frustum, and every draw while the frame
    /// culls nothing. The recording and the pass's motion scope both take it from here, so the scope judges a change by the
    /// cull the image was drawn under.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <param name="width">The pixels across the extent the cull's margin is measured against.</param>
    /// <param name="height">The pixels down that extent.</param>
    /// <returns>The cull.</returns>
    internal static SdfMeshCull CullOf(SdfFrame frame, int view, uint width, uint height) {
        if (frame.MeshCull == SdfMeshCullLevel.Off) {
            return SdfMeshCull.None;
        }

        var camera = frame.Views[view].Camera;

        return SdfMeshCull.Of(camera: in camera, far: frame.FarDistance, height: height, width: width);
    }
    /// <summary>Notes the extent a raster pass of an instance records its cull at, which its scope's cull is built from
    /// until it records again.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="part">The recording pass.</param>
    /// <param name="width">The pixels across the extent the recording's cull was measured against.</param>
    /// <param name="height">The pixels down that extent.</param>
    internal void NoteDrawn(string instance, string part, uint width, uint height) {
        if (Refresh(instance: instance) is not { View: { Residency.Tables: { } tables } } entry) {
            return;
        }

        var drawn = entry.DrawnOf(scope: DrawScopeOf(part: part), tables: tables);

        (drawn.Width, drawn.Height) = (width, height);
    }

    // The revision a part of an instance signs with for the frame its tables hold: its scope's, taken once for each
    // staging of each frame so every pass of the frame that shares a scope signs with one value.
    private long MotionOf(Entry entry, string? part, SdfWorldTables tables, SdfWorldView view) {
        if (view.Residency.Frame is not { } frame) {
            return 0L;
        }

        var drawn = entry.DrawnOf(scope: DrawScopeOf(part: part), tables: tables);

        if ((drawn.Frame == m_frame) && (drawn.Staging == tables.MotionStaging)) {
            return drawn.Revision;
        }

        var index = Math.Min(val1: view.View, val2: (frame.Views.Count - 1));
        var cull = (((drawn.Width != 0) && (drawn.Height != 0))
            ? CullOf(frame: frame, height: drawn.Height, view: index, width: drawn.Width)
            : SdfMeshCull.None);

        drawn.Revision = tables.ObserveMotion(cull: in cull, scope: drawn.Motion, view: index);
        drawn.Frame = m_frame;
        drawn.Staging = tables.MotionStaging;

        return drawn.Revision;
    }
    // The lighting revision a part of an instance signs with: its scope's (SdfMotionScope.Lighting). Asked after the part's
    // revision (MotionOf), which holds the scope.
    private static long LightingOf(Entry entry, string? part, SdfWorldTables tables) =>
        entry.DrawnOf(scope: DrawScopeOf(part: part), tables: tables).Motion.Lighting;
    // The scope whose poses an instance's temporal history follows: its mesh pass's, kept held by the asking.
    private static SdfMotionScope PosesOf(Entry entry, SdfWorldTables tables) {
        var motion = entry.DrawnOf(scope: DrawScope.Mesh, tables: tables).Motion;

        tables.ObserveMotion(scope: motion);

        return motion;
    }

    // What an instance knows of one of its raster passes: the scope its passes sign with, bound to the tables it judges,
    // the extent the pass last recorded at, and the revision taken for a frame's staging.
    private sealed class Drawn {
        public WeakReference<SdfWorldTables>? Tables;
        public SdfMotionScope Motion = null!;
        public uint Width;
        public uint Height;
        public long Frame = -1;
        public long Staging = -1;
        public long Revision;
    }

    private sealed partial class Entry {
        private readonly Drawn[] m_drawn = [new(), new()];

        // The instance's record of a raster pass under given tables. A scope belongs to the tables that judge it, so
        // other tables (another residency followed, or tables replaced after a device loss) start a new one.
        public Drawn DrawnOf(DrawScope scope, SdfWorldTables tables) {
            var drawn = m_drawn[((int)scope)];

            if ((drawn.Tables is null) || !drawn.Tables.TryGetTarget(target: out var held) || !ReferenceEquals(objA: held, objB: tables)) {
                drawn.Tables = new WeakReference<SdfWorldTables>(target: tables);
                drawn.Motion = new SdfMotionScope(reader: ((scope == DrawScope.Mesh) ? SdfMotionReader.Shading : SdfMotionReader.Glass));
                drawn.Frame = -1;
            }

            return drawn;
        }
        // Keeps every scope the instance holds under these tables held through a frame its view stands whole.
        public void ObserveDrawn(SdfWorldTables tables) {
            foreach (var drawn in m_drawn) {
                if ((drawn.Tables is not null) && drawn.Tables.TryGetTarget(target: out var held) && ReferenceEquals(objA: held, objB: tables)) {
                    tables.ObserveMotion(scope: drawn.Motion);
                }
            }
        }
    }
}
