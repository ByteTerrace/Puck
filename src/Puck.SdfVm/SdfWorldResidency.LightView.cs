using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    private SdfFrame? m_lightFrame;
    private long m_lightFrameNumber = -1;
    private IrradianceLightProjection? m_lightProjection;
    /// <summary>Gets the sole depth camera's schedule and the held/fading maps it publishes.</summary>
    public SdfIndirectLightViews IndirectLightViews { get; } = new();

    internal void PlanLightView(in FrameContext context) {
        if (!Prepare(context: in context) || (Tables?.Indirect is not { } cache) || (Frame is not { } frame) || IndirectFrozen) { return; }
        if (IndirectLightViews.Frame == PackageFrame) { return; }
        Tables.PlanIndirect(frame: frame);
        var inputs = SdfWorldTables.IndirectInputs(frame: frame);
        // Finite mesh bounds do not certify conservative raster coverage: a subtexel triangle can miss every sample.
        // A baked draw's emitting seam certifies the same SDF remains available to the swept primary traversal.
        var casters = (frame.MeshDraws.Any(predicate: static draw => !draw.FieldBacked && draw.Indirect is SdfIndirectParticipation.Default or SdfIndirectParticipation.Cast) || inputs.Bounds.Any(predicate: static sphere => !double.IsFinite(d: sphere.Radius))
            ? ((SdfLightRegion?)null) : new SdfLightRegion(Min: inputs.WorldMin, Max: inputs.WorldMax));
        var lights = (cache.HasLightingCycle ? cache.Lighting!.Frame.Lights : frame.Lights);
        IndirectLightViews.Plan(frame: PackageFrame, geometryOwner: Tables, geometry: Tables.LightGeometry,
            lights: lights, regions: [cache.AllocatedRegion(level: 0), cache.AllocatedRegion(level: (cache.Layout.Levels.Count - 1))],
            casters: casters, forceGeometry: Tables.LightGeometryMutable);
    }

    internal SdfFrame LightFrame() {
        var frame = (Frame ?? throw new InvalidOperationException(message: "The light camera has no residency frame."));
        if (IndirectLightViews.Projection is not { } projection) { return frame; }
        if ((m_lightFrameNumber == PackageFrame) && (m_lightProjection == projection) && (m_lightFrame is { } cached)) { return cached; }
        var camera = new CameraSnapshot(Position: Point(projection.Origin), Right: Point(projection.Right), Up: Point(projection.Up),
            Forward: Point(-projection.TowardLight), TanHalfFieldOfView: ((float)projection.HalfWidth), AspectRatio: 1f) { Near = ((float)projection.Near) };
        m_lightFrameNumber = PackageFrame;
        m_lightProjection = projection;
        return m_lightFrame = frame with {
            Views = [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            FarDistance = ((float)projection.Far), IndirectTier = SdfIndirectTier.Off,
            IndirectBodies = SdfIndirectPolicy.Resolve(SdfIndirectParticipation.Default, dynamic: true, IndirectTier, frame.IndirectBodies),
        };
    }
    internal void SubmitLightView() {
        if (IndirectLightViews.Pending < 0) { return; }
        IndirectLightViews.Submitted();
        m_indirectWork.Add(kind: SdfIndirectWork.LightRegions, amount: 1);
    }
    private static Vector3 Point(Double3 point) => new(x: ((float)point.X), y: ((float)point.Y), z: ((float)point.Z));
}

public sealed partial class SdfWorldTables {
    /// <summary>Gets the exact uploaded revisions that the conservative light camera consumes.</summary>
    public SdfLightGeometry LightGeometry => new(Program: m_programRevision, Poses: m_indirectTransformRevision, Mesh: m_meshRevision, Decals: m_decalRevision, Bodies: m_indirectBodies);
    /// <summary>Gets whether a carve bake can change the field without an uploaded revision.</summary>
    public bool LightGeometryMutable => AnyBrickBaking();
}
