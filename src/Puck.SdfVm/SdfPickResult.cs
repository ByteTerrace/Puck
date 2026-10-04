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
/// <param name="Identity">The visibility identity, packed as <see cref="SdfVisibility"/> states; zero when the pixel's
/// record was not current in the frame that rendered it.</param>
/// <param name="Distance">The ray parameter along the normalized camera ray.</param>
/// <param name="Material">The winning material index.</param>
/// <param name="Program">The program rendered by this request.</param>
/// <param name="MeshRevision">The mesh draw revision rendered by this request.</param>
/// <param name="Map">The immutable host identity table captured with the frame.</param>
public readonly record struct SdfPickResult(long Request, uint X, uint Y, uint Width, uint Height, uint Identity, float Distance, int Material, SdfProgram Program, long MeshRevision, ISdfPickMap? Map = null) {
    /// <summary>Gets the visibility row's packed primary-traversal counts: steps in bits 0..7 and queries in bits 8..30.</summary>
    public uint Flags { get; init; }
    /// <summary>Gets the selected primary march's step count, saturated at 255.</summary>
    public uint Steps => Flags & 0xFF;
    /// <summary>Gets the primary traversal's total field-query count, saturated at 2^23 - 1.</summary>
    public uint Queries => (Flags >> 8) & 0x7FFFFF;
    /// <summary>Gets the host identity from the table captured by this request.</summary>
    public object? Target => Map?.Resolve(identity: Identity);
    /// <summary>Gets the host material name captured beside the rendered program.</summary>
    public string? MaterialName => Map?.MaterialName(identity: Identity, material: Material);
    /// <summary>Gets the hit kind.</summary>
    public SdfVisibilityKind Kind => SdfVisibility.KindOf(identity: Identity);
    /// <summary>Gets the source: an SDF instance ordinal plus one, or a mesh draw ordinal.</summary>
    public uint Source => SdfVisibility.SourceOf(identity: Identity);
    /// <summary>Gets whether the pixel hits geometry.</summary>
    public bool Hit => (Kind != SdfVisibilityKind.Background);
    /// <summary>Gets the winning SDF shape's dynamic-transform slot, the record's L.x, which can differ from its
    /// instance's bound slot in an articulated group; null for static geometry, a mesh hit or a miss.</summary>
    public int? TransformSlot { get; init; }
    /// <summary>Gets <see cref="TransformSlot"/>'s row in the transform table of the frame the record was rendered from,
    /// captured when its copy recorded, so a later frame's motion never answers for it; null without a slot.</summary>
    public DynamicTransform? Transform { get; init; }
    /// <summary>Gets the camera and ray sample captured by an inspector request, or null for an ordinary identity pick.</summary>
    public SdfReprojectionView? Sample { get; init; }
    /// <summary>Gets the captured camera cut revision, used to discard answers from a superseded view epoch.</summary>
    public long CutRevision { get; init; }
    /// <summary>Gets the rendered unit surface normal, or zero when no normal was requested or the pixel missed.</summary>
    public Vector3 Normal { get; init; }
    /// <summary>Gets the indirect receiver answer and actual cache census captured by a surface inspection,
    /// or null when that request did not read indirect diagnostics.</summary>
    public SdfIndirectPick? Indirect { get; init; }
    /// <summary>Gets the hit point reconstructed from the captured ray, never a later camera; null for a miss or an
    /// ordinary identity pick.</summary>
    public Vector3? Point {
        get {
            if (!Hit || (Sample is not { } sample)) { return null; }
            var camera = sample.Camera;
            var projection = ViewProjection.Create(camera: camera, near: 1,
                jitter: new Vector2(x: ((2 * sample.Jitter.X) / sample.Width), y: ((-2 * sample.Jitter.Y) / sample.Height)));
            var ndc = ViewProjection.NdcOf(uv: new Vector2(x: ((X + 0.5f) / Width), y: ((Y + 0.5f) / Height)));
            var direction = Vector3.Normalize(value: (projection.Unproject(depth: 1, ndc: ndc) - camera.Position));

            return (camera.Position + (direction * Distance));
        }
    }
}
