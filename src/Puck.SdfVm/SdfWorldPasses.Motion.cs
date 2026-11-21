using Puck.Shaders;

namespace Puck.SdfVm;

// Each instance's motion scopes (SdfMotionScope): one for each raster pass whose draws its passes show. A pass signs with
// the revision of its scope (MotionOf), so its image stands while nothing its scope's cull keeps has changed, whatever
// else of the residency moves:
//   - the mesh pass's   : the mesh pass and every pass that shades what it drew (the pyramid, the surface pass, shading,
//                         the resolve, the sky and the composite, the optical tail). The instance's temporal history
//                         follows this scope's poses.
//   - the glass pass's  : the glass clear, raster and shading, judged by glass draws alone.
//   - each cascade's    : the near, far and second cascades, each under its own caster cull, judged by draws alone: a
//                         cascade holds depths, which no light, medium or paint changes. The distant map holds the
//                         static scene alone and signs with the static revision instead (SdfWorldPasses.Distant).
// One function builds a pass's cull for both its recording and its scope (CullOf). The scope's is built from the
// extent the pass's latest recording noted (NoteDrawn), so while the frame's camera and that extent stand it is the
// cull the standing image was drawn under; before a pass has recorded, its scope keeps every draw.
public sealed partial class SdfWorldPasses {
    private enum DrawScope : byte {
        Mesh = 0,
        Glass = 1,
        ShadowNear = 2,
        ShadowFar = 3,
        ShadowSecond = 4,
    }

    // The scope a part signs with, or null for the distant map.
    private static DrawScope? DrawScopeOf(string? part) => part switch {
        SdfWorldPackage.Parts.ShadowDistant => null,
        SdfWorldPackage.Parts.ShadowNear => DrawScope.ShadowNear,
        SdfWorldPackage.Parts.ShadowFar => DrawScope.ShadowFar,
        SdfWorldPackage.Parts.ShadowSecond => DrawScope.ShadowSecond,
        SdfWorldPackage.Parts.GlassClear or SdfWorldPackage.Parts.Glass or SdfWorldPackage.Parts.GlassShade => DrawScope.Glass,
        _ => DrawScope.Mesh,
    };
    // The cascade a scope's pass draws (SdfMeshShadowCascades), or -1 for a pass drawn from the view's own camera.
    private static int CascadeOf(DrawScope scope) => scope switch {
        DrawScope.ShadowNear => SdfMeshShadowCascades.NearEntry,
        DrawScope.ShadowFar => SdfMeshShadowCascades.FarEntry,
        DrawScope.ShadowSecond => SdfMeshShadowCascades.SecondEntry,
        _ => -1,
    };

    /// <summary>Returns the cull a raster pass of a view draws under: the view's frustum for the mesh and glass passes,
    /// a cascade's caster cull for a shadow cascade, and every draw while the frame culls nothing. The recording and the
    /// pass's motion scope both take it from here, so the scope judges a change by the cull the image was drawn under.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="view">The view's index in the frame's views.</param>
    /// <param name="cascades">The cascades the pass draws and reads this frame; unread for a pass of the view's own camera.</param>
    /// <param name="cascade">The cascade the pass draws (<see cref="SdfMeshShadowCascades.NearEntry"/>,
    /// <see cref="SdfMeshShadowCascades.FarEntry"/>, <see cref="SdfMeshShadowCascades.SecondEntry"/>), or -1 for a pass
    /// drawn from the view's own camera.</param>
    /// <param name="width">The pixels across the extent the cull's margin is measured against.</param>
    /// <param name="height">The pixels down that extent.</param>
    /// <returns>The cull.</returns>
    internal static SdfMeshCull CullOf(SdfFrame frame, int view, in SdfMeshShadowCascades cascades, int cascade, uint width, uint height) {
        if (frame.MeshCull == SdfMeshCullLevel.Off) {
            return SdfMeshCull.None;
        }

        var camera = frame.Views[view].Camera;

        return ((cascade < 0)
            ? SdfMeshCull.Of(camera: in camera, far: frame.FarDistance, height: height, width: width)
            : SdfMeshCull.Of(camera: in camera, cascade: cascades[cascade], far: frame.FarDistance, height: height, texels: cascades.TexelsOf(index: cascade), width: width));
    }
    /// <summary>Notes the extent a raster pass of an instance records its cull at, and the cascade depths it binds, which
    /// its scope's cull is built from until it records again.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="part">The recording pass.</param>
    /// <param name="width">The pixels across the extent the recording's cull was measured against.</param>
    /// <param name="height">The pixels down that extent.</param>
    /// <param name="texels">The texels along each side of the cascade depths the pass binds, or <see langword="null"/>
    /// for the view's quality.</param>
    /// <param name="distantTexels">The texels along each side of the distant map's depth the pass binds, or
    /// <see langword="null"/> for the frame's.</param>
    internal void NoteDrawn(string instance, string part, uint width, uint height, int? texels, int? distantTexels) {
        if ((DrawScopeOf(part: part) is not { } scope) || (Refresh(instance: instance) is not { View: { Residency.Tables: { } tables } } entry)) {
            return;
        }

        var drawn = entry.DrawnOf(scope: scope, tables: tables);

        (drawn.Width, drawn.Height, drawn.Texels, drawn.DistantTexels) = (width, height, texels, distantTexels);
    }

    // The revision a part of an instance signs with for the frame its tables hold: its scope's, taken once for each
    // staging of each frame so every pass of the frame that shares a scope signs with one value.
    private long MotionOf(Entry entry, string instance, string? part, SdfWorldTables tables, SdfWorldView view) {
        if ((DrawScopeOf(part: part) is not { } scope) || (view.Residency.Frame is not { } frame)) {
            return 0L;
        }

        var drawn = entry.DrawnOf(scope: scope, tables: tables);

        if ((drawn.Frame == m_frame) && (drawn.Staging == tables.MotionStaging)) {
            return drawn.Revision;
        }

        var index = Math.Min(val1: view.View, val2: (frame.Views.Count - 1));
        var cascade = CascadeOf(scope: scope);
        var cull = SdfMeshCull.None;

        if ((drawn.Width != 0) && (drawn.Height != 0)) {
            if (cascade < 0) {
                cull = CullOf(cascade: cascade, cascades: default, frame: frame, height: drawn.Height, view: index, width: drawn.Width);
            } else if (CascadesOf(distantTexels: drawn.DistantTexels, frame: frame, instance: instance, tables: tables, texels: drawn.Texels, view: index) is var cascades && cascades.Holds(index: cascade)) {
                cull = CullOf(cascade: cascade, cascades: in cascades, frame: frame, height: drawn.Height, view: index, width: drawn.Width);
            }
        }

        drawn.Revision = tables.ObserveMotion(cull: in cull, scope: drawn.Motion, view: index);
        drawn.Frame = m_frame;
        drawn.Staging = tables.MotionStaging;

        return drawn.Revision;
    }
    // The lighting revision a part of an instance signs with: its scope's (SdfMotionScope.Lighting), or zero for the
    // distant map. Asked after the part's revision (MotionOf), which holds the scope.
    private static long LightingOf(Entry entry, string? part, SdfWorldTables tables) =>
        ((DrawScopeOf(part: part) is { } scope) ? entry.DrawnOf(scope: scope, tables: tables).Motion.Lighting : 0L);
    // The scope whose poses an instance's temporal history follows: its mesh pass's, kept held by the asking.
    private static SdfMotionScope PosesOf(Entry entry, SdfWorldTables tables) {
        var motion = entry.DrawnOf(scope: DrawScope.Mesh, tables: tables).Motion;

        tables.ObserveMotion(scope: motion);

        return motion;
    }

    // What an instance knows of one of its raster passes: the scope its passes sign with, bound to the tables it judges,
    // the extent and depths the pass last recorded at, and the revision taken for a frame's staging.
    private sealed class Drawn {
        public WeakReference<SdfWorldTables>? Tables;
        public SdfMotionScope Motion = null!;
        public uint Width;
        public uint Height;
        public int? Texels;
        public int? DistantTexels;
        public long Frame = -1;
        public long Staging = -1;
        public long Revision;
    }

    private sealed partial class Entry {
        private readonly Drawn[] m_drawn = [new(), new(), new(), new(), new()];

        // The instance's record of a raster pass under given tables. A scope belongs to the tables that judge it, so
        // other tables (another residency followed, or tables replaced after a device loss) start a new one.
        public Drawn DrawnOf(DrawScope scope, SdfWorldTables tables) {
            var drawn = m_drawn[((int)scope)];

            if ((drawn.Tables is null) || !drawn.Tables.TryGetTarget(target: out var held) || !ReferenceEquals(objA: held, objB: tables)) {
                drawn.Tables = new WeakReference<SdfWorldTables>(target: tables);
                drawn.Motion = new SdfMotionScope(reader: (scope switch {
                    DrawScope.Mesh => SdfMotionReader.Shading,
                    DrawScope.Glass => SdfMotionReader.Glass,
                    _ => SdfMotionReader.Caster,
                }));
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
