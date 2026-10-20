using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>A completed presentation-only pick of one rendered pixel. The program and mesh revision identify the
/// rendered source; the captured immutable map keeps delayed identity lookup independent of later scene changes.</summary>
/// <param name="Request">The request identity.</param>
/// <param name="X">The sampled pixel column: the coordinate this answer belongs to, whatever the pointer did since.</param>
/// <param name="Y">The sampled pixel row.</param>
/// <param name="Width">The rendered width.</param>
/// <param name="Height">The rendered height.</param>
/// <param name="Identity">The visibility identity, packed as <see cref="SdfVisibility"/> states; zero for the sky.</param>
/// <param name="Distance">The ray parameter along the normalized camera ray.</param>
/// <param name="Material">The winning material index.</param>
/// <param name="Program">The program rendered by this request.</param>
/// <param name="MeshRevision">The mesh draw revision rendered by this request.</param>
/// <param name="Map">The immutable host identity table captured with the frame.</param>
public readonly record struct SdfPickResult(long Request, uint X, uint Y, uint Width, uint Height, uint Identity, float Distance, int Material, SdfProgram Program, long MeshRevision, ISdfPickMap? Map = null) {
    /// <summary>Gets the host identity from the table captured by this request.</summary>
    public object? Target => Map?.Resolve(identity: Identity);
    /// <summary>Gets the host material name captured beside the rendered program.</summary>
    public string? MaterialName => Map?.MaterialName(identity: Identity, material: Material);
    /// <summary>Gets the hit kind.</summary>
    public SdfVisibilityKind Kind => SdfVisibility.KindOf(identity: Identity);
    /// <summary>Gets the source: the mesh draw ordinal of a hit.</summary>
    public uint Source => SdfVisibility.SourceOf(identity: Identity);
    /// <summary>Gets whether the pixel hits geometry.</summary>
    public bool Hit => (Kind != SdfVisibilityKind.Background);
    /// <summary>Gets the camera and ray sample captured by an inspector request, or null for an ordinary identity pick.</summary>
    public SdfReprojectionView? Sample { get; init; }
    /// <summary>Gets the captured camera cut revision, used to discard answers from a superseded view epoch.</summary>
    public long CutRevision { get; init; }
    /// <summary>Gets the rendered unit surface normal, or zero when no normal was requested or the pixel missed.</summary>
    public Vector3 Normal { get; init; }
    /// <summary>Gets the hit point reconstructed from the captured ray, never a later camera; null for a miss or an
    /// ordinary identity pick.</summary>
    public Vector3? Point {
        get {
            if (!Hit || (Sample is not { } sample)) { return null; }
            var camera = sample.Camera;
            // The march's ray through the pixel centre, shifted by the frame's sub-pixel jitter as ViewProjection.Create
            // shifts it (ViewProjection.RayParameter reads the same sum).
            var ndc = (ViewProjection.NdcOf(uv: new Vector2(x: ((X + 0.5f) / Width), y: ((Y + 0.5f) / Height))) +
                new Vector2(x: ((2 * sample.Jitter.X) / sample.Width), y: ((-2 * sample.Jitter.Y) / sample.Height)));
            var direction = Vector3.Normalize(value: camera.RayThrough(ndc: ndc));

            return (camera.Position + (direction * Distance));
        }
    }
}
