using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// The <see cref="RenderGraphPackageCatalog.SdfWorld"/> package's declaration: the values and resources every one of its
/// kernels reads from its pass group, the layout facts its storages are sized by, and the fragment it runs as.
/// <para>Every compute pass of the fragment reads one interface, <see cref="Members"/>, whose pass block holds the
/// extent and the world values in ordinal name order and whose resources follow. A buffer one pass writes and a later
/// pass reads is two members over the one buffer: a read-write member named with an <c>RW</c> suffix for its writer, and a
/// read-only member for its readers.</para>
/// <para>The fragment (<see cref="Fragment"/>) is one view: the instance masks, the beam, the cull arguments, the mesh
/// pass, primary traversal, surface, ambient and shadow resolution, shading the hits into the lit image, then the sky's
/// field runs and the composite that writes the output. Its scratch is retained,
/// one allocation shared by every frame slot, and scales with the counts its host resolves: the view's extent, one
/// viewport, its tiles at <see cref="TileSize"/>, and the program's instances and instance-mask words.</para>
/// </summary>
public static partial class SdfWorldPackage {
    /// <summary>The pass-group value holding the render extent in pixels: the per-view visibility record stride.</summary>
    public const string ImageExtent = "imageExtent";
    /// <summary>The pass-group value holding the tiles per viewport, columns then rows: the cull buffer's per-viewport
    /// stride.</summary>
    public const string TileGrid = "tileGrid";
    /// <summary>The pass-group value holding every view the dispatch set renders.</summary>
    public const string ViewportCount = "viewportCount";
    /// <summary>The pass-group value holding one past the highest screen whose source is bound this frame, or zero when
    /// none is: the bound the screen-light loop runs to.</summary>
    public const string ScreenCount = "screenCount";
    /// <summary>The pass-group value holding the live program's per-tile instance-mask width.</summary>
    public const string InstanceMaskWordCount = "instanceMaskWordCount";
    /// <summary>The pass-group value naming the view the dispatch set renders, whose visibility records it indexes.</summary>
    public const string ViewBase = "viewBase";
    /// <summary>The pass-group value holding the frame's mesh draws, or zero when no mesh draws: the hit passes read the
    /// mesh visibility target only when it is non-zero, and cull-args then covers the whole extent.</summary>
    public const string MeshDraws = "meshDraws";
    /// <summary>The pass-group value holding the view's camera position in world space (<c>float3</c>).</summary>
    public const string ViewPosition = "viewPosition";
    /// <summary>The pass-group value holding the view's unit right axis in world space (<c>float3</c>).</summary>
    public const string ViewRight = "viewRight";
    /// <summary>The pass-group value holding the view's unit up axis in world space (<c>float3</c>).</summary>
    public const string ViewUp = "viewUp";
    /// <summary>The pass-group value holding the view's unit forward axis in world space (<c>float3</c>).</summary>
    public const string ViewForward = "viewForward";
    /// <summary>The pass-group value holding the tangent of half the view's vertical field of view (<c>float</c>).</summary>
    public const string TanHalfFieldOfView = "tanHalfFieldOfView";
    /// <summary>The pass-group value holding the view's width over its height (<c>float</c>).</summary>
    public const string AspectRatio = "aspectRatio";
    /// <summary>The pass-group value holding the off-axis frustum's tangent-space center offset, zero for a symmetric
    /// camera (<c>float2</c>).</summary>
    public const string FrustumOffset = "frustumOffset";
    /// <summary>The ray offset in render pixels, with positive Y down (<c>float2</c>).</summary>
    public const string Jitter = "jitter";
    /// <summary>The number of preceding rendered samples in the current history epoch (<c>uint</c>).</summary>
    public const string HistoryFrames = "historyFrames";
    /// <summary>The pass-group value set to one when the view runs <see cref="TemporalFragment"/>: the views pass
    /// then writes the reactivity buffer and the resolve reconstructs over time (<c>uint</c>).</summary>
    public const string Temporal = "temporal";
    /// <summary>The pass-group value holding the forward distance of the view camera's own near plane, in world units,
    /// zero for a camera whose image begins at its eye (<c>float</c>).</summary>
    public const string NearDistance = "nearDistance";
    /// <summary>The nearest forward distance, in world units, a view's surfaces are rendered from: the mesh pass's
    /// reversed-Z depth needs a positive near, so a camera whose own near (<see cref="NearDistance"/>) is nearer is
    /// rendered from this one. The kernels read it as <c>SDF_MINIMUM_NEAR</c>, which <c>SdfIsaHlsl</c> generates from this
    /// value.</summary>
    public const float MinimumNear = 0.02f;
    /// <summary>The pass-group value holding the depth, in world units, at which every camera march ends
    /// (<c>float</c>).</summary>
    public const string FarDistance = "farDistance";
    /// <summary>The pass-group value holding the debug view mode, an index into the debug view names; zero renders the
    /// final image (<c>uint</c>).</summary>
    public const string DebugMode = "debugMode";
    /// <summary>The pass-group value holding the lights the lights table holds this frame, at most its records
    /// (<c>uint</c>).</summary>
    public const string LightCount = "lightCount";
    /// <summary>The stable shadow slots' light indices; an empty slot holds −1 (<c>int4</c>).</summary>
    public const string ShadowSlots = "shadowSlots";
    /// <summary>The configured stable shadow slot count (<c>uint</c>).</summary>
    public const string ShadowSlotCount = "shadowSlotCount";
    /// <summary>The active incoming shadow march count (<c>uint</c>).</summary>
    public const string ShadowFadeCount = "shadowFadeCount";
    /// <summary>The pass-group value holding the curvature shading's cavity-darkening gain (<c>float</c>).</summary>
    public const string CurvatureCavity = "curvatureCavity";
    /// <summary>The pass-group value holding the curvature shading's rim gain (<c>float</c>).</summary>
    public const string CurvatureRim = "curvatureRim";
    /// <summary>The pass-group value holding the curvature shading's ink outline gain (<c>float</c>).</summary>
    public const string CurvatureInk = "curvatureInk";
    /// <summary>The pass-group value holding the curvature magnitude at which the ink outline starts (<c>float</c>).</summary>
    public const string CurvatureInkLow = "curvatureInkLow";
    /// <summary>The pass-group value holding the curvature magnitude at which the ink outline saturates
    /// (<c>float</c>).</summary>
    public const string CurvatureInkHigh = "curvatureInkHigh";
    /// <summary>The pass-group value holding the ink outline's linear color (<c>float3</c>).</summary>
    public const string CurvatureInkColor = "curvatureInkColor";
    /// <summary>The pass-group value selecting the slice debug view's plane: zero camera-locked, one to three the world
    /// X, Y or Z axis (<c>float</c>).</summary>
    public const string DebugSliceAxis = "debugSliceAxis";
    /// <summary>The pass-group value holding the slice plane's signed offset along its axis, in world units
    /// (<c>float</c>).</summary>
    public const string DebugSliceOffset = "debugSliceOffset";
    /// <summary>The pass-group value holding the view's grid flags: bit 0 the world grid on the working plane, bit 1 the
    /// object grid, bit 2 the world grid projected onto every surface (<c>uint</c>).</summary>
    public const string GridFlags = "gridFlags";
    /// <summary>The pass-group value holding the working plane's height the world grid draws on, in world units
    /// (<c>float</c>).</summary>
    public const string GridPlaneY = "gridPlaneY";
    /// <summary>The pass-group value holding the grid's line width in pixels at the surface, which the on-plane band
    /// scales with (<c>float</c>).</summary>
    public const string GridLineWidth = "gridLineWidth";
    /// <summary>The pass-group value holding the world grid's pitch on its own X, Y and Z, in world units, zero disabling an axis
    /// (<c>float3</c>).</summary>
    public const string GridWorldPitch = "gridWorldPitch";
    /// <summary>The pass-group value holding the world grid's origin in the view's world space: the origin of the world the
    /// lattice belongs to (<c>float3</c>).</summary>
    public const string GridWorldOrigin = "gridWorldOrigin";
    /// <summary>The pass-group value holding the world grid's orientation quaternion in the view's world space, xyzw, identity
    /// for the world the view draws (<c>float4</c>).</summary>
    public const string GridWorldFrame = "gridWorldFrame";
    /// <summary>The pass-group value holding the object grid's reference origin in world space (<c>float3</c>).</summary>
    public const string GridObjectOrigin = "gridObjectOrigin";
    /// <summary>The pass-group value holding the object grid's pitch on its reference X, Y and Z, zero disabling an axis
    /// (<c>float3</c>).</summary>
    public const string GridObjectPitch = "gridObjectPitch";
    /// <summary>The pass-group value holding the object grid's reference orientation quaternion, xyzw
    /// (<c>float4</c>).</summary>
    public const string GridObjectFrame = "gridObjectFrame";
    /// <summary>The pass-group value holding the object grid's patch radius in reference-local units, zero disabling it
    /// (<c>float</c>).</summary>
    public const string GridObjectPatchRadius = "gridObjectPatchRadius";
    /// <summary>The pass-group value set to one to shade normals with four finite-difference taps instead of the analytic
    /// gradient (<c>uint</c>).</summary>
    public const string FiniteDifferenceNormals = "finiteDifferenceNormals";
    /// <summary>The pass-group value set to one to march every instance for soft shadows instead of the grid-gathered
    /// set (<c>uint</c>).</summary>
    public const string DisableShadowCull = "disableShadowCull";
    /// <summary>The pass-group value set to one to skip the soft-shadow march (<c>uint</c>).</summary>
    public const string DisableSoftShadows = "disableSoftShadows";
    /// <summary>The pass-group value set to one to skip ambient occlusion (<c>uint</c>).</summary>
    public const string DisableAmbientOcclusion = "disableAmbientOcclusion";
    /// <summary>The pass-group value scaling the soft-shadow reach; zero reads as the full reach (<c>float</c>).</summary>
    public const string ShadowDistanceScale = "shadowDistanceScale";
    /// <summary>The pass-group value set to one to skip the screens' area lights (<c>uint</c>).</summary>
    public const string DisableScreenLights = "disableScreenLights";
    /// <summary>The pass-group value set to one to leave shadow-transparent carves out of the soft-shadow occluders
    /// (<c>uint</c>).</summary>
    public const string EnableShadowProxy = "enableShadowProxy";
    /// <summary>The pass-group value set to one to shadow with the camera tile's instance mask instead of the gathered
    /// one (<c>uint</c>).</summary>
    public const string CameraTileShadowMask = "cameraTileShadowMask";
    /// <summary>The pass-group value set to one to march soft shadows with the bounded-cost marcher (<c>uint</c>).</summary>
    public const string FastSoftShadowMarch = "fastSoftShadowMarch";
    /// <summary>The pass-group value set to one to take one contact sample for ambient occlusion (<c>uint</c>).</summary>
    public const string FastAmbientOcclusion = "fastAmbientOcclusion";
    /// <summary>The pass-group value set to one to march past the beam's per-tile far bound to the far distance
    /// (<c>uint</c>).</summary>
    public const string DisableFarBound = "disableFarBound";
    /// <summary>The pass-group value set to one to publish premultiplied geometry color and coverage without background sky
    /// (<c>uint</c>), for a far sky layer.</summary>
    public const string GeometryOnly = "geometryOnly";
    /// <summary>The program word stream.</summary>
    public const string ProgramWords = "sdfWords";
    /// <summary>The dynamic-transform table, three float4 rows per slot.</summary>
    public const string DynamicTransforms = "sdfDynamicTransforms";
    /// <summary>The preceding consumed frame's rigid transforms, copied entirely on the device.</summary>
    public const string PreviousDynamicTransforms = "sdfPreviousDynamicTransforms";
    /// <summary>The preceding consumed frame's mesh matrices, four float4 rows per draw.</summary>
    public const string PreviousMeshTransforms = "sdfPreviousMeshTransforms";
    /// <summary>The preceding rendered camera: position and validity, right and tangent, up and aspect, forward,
    /// extent, and near distance with jittered frustum offset.</summary>
    public const string PreviousView = "previousView";
    /// <summary>The frame-local instance grid.</summary>
    public const string FrameInstanceGrid = "sdfFrameInstanceGrid";
    /// <summary>The per-tile instance masks, read by the beam and the hit passes.</summary>
    public const string InstanceMasks = "sdfInstanceMasks";
    /// <summary>The per-tile instance masks, written by the mask pass.</summary>
    public const string InstanceMasksWritten = "sdfInstanceMasksRW";
    /// <summary>The certified per-tile live-segment masks and slab balls.</summary>
    public const string SegmentTapes = "sdfSegmentTapes";
    /// <summary>The live-segment masks written by the tape pass.</summary>
    public const string SegmentTapesWritten = "sdfSegmentTapesRW";
    /// <summary>The cull buffer, read by cull-args and the hit passes.</summary>
    public const string Tiles = "tiles";
    /// <summary>The cull buffer, written by the beam.</summary>
    public const string TilesWritten = "tilesRW";
    /// <summary>The dispatch box, read by the hit passes.</summary>
    public const string CullBounds = "cullBounds";
    /// <summary>The dispatch box, written by cull-args.</summary>
    public const string CullBoundsWritten = "cullBoundsRW";
    /// <summary>The hit passes' indirect dispatch arguments, written by cull-args.</summary>
    public const string ViewsArgsWritten = "viewsArgsRW";
    /// <summary>The visibility records, read by views.</summary>
    public const string VisibilityRecords = "sdfVisibilityRecords";
    /// <summary>The visibility records, written by primary, surface and ambient.</summary>
    public const string VisibilityRecordsWritten = "sdfVisibilityRecordsRW";
    /// <summary>The image a pass writes its color to: the lit image views shades, the resolved lit image the resolve
    /// writes, and the view's output the composite writes.</summary>
    public const string Output = "output";
    /// <summary>The screen-surface table, three float4 rows per screen slot.</summary>
    public const string ScreenSurfaces = "screenSurfaces";
    /// <summary>The screen-mapping table, seven float4 rows per screen slot: the draw form of the mapping each screen
    /// publishes (<c>SourceMapping.Draw</c>), which the screen shading draws its face from, and the screen's state,
    /// whether its source is bound and the sampler it reads through.</summary>
    public const string ScreenMappings = "screenMappings";
    /// <summary>The screen-light table: each screen slot's emitted color and gain, one float4 row per slot.</summary>
    public const string ScreenLights = "sdfScreenLights";
    /// <summary>The glyph decal table.</summary>
    public const string DecalCells = "sdfDecalCells";
    /// <summary>The brick pool, read by the beam and the hit passes.</summary>
    public const string BrickPool = "sdfBrickPool";
    /// <summary>The bounded-volume table.</summary>
    public const string Volumes = "sdfVolumes";
    /// <summary>The mesh region as a stream of words: the draw records, then the positions and indices the mesh pass
    /// reads.</summary>
    public const string MeshRegion = "sdfMeshRegion";
    /// <summary>The mesh visibility target the mesh pass draws and the hit passes read: per pixel the ray parameter, the
    /// draw index plus one (zero where no mesh covers it) and the triangle.</summary>
    public const string MeshVisibility = "meshVisibility";
    /// <summary>The glyph atlas.</summary>
    public const string GlyphAtlas = "sdfGlyphAtlas";
    /// <summary>The mesh albedo atlas, sRGB-encoded BC7 the hit passes decode after sampling.</summary>
    public const string MeshAlbedo = "sdfMeshAlbedo";
    /// <summary>The mesh normal atlas: octahedral object-space normal pairs, BC5.</summary>
    public const string MeshNormals = "sdfMeshNormals";
    /// <summary>The mesh occlusion atlas, BC4.</summary>
    public const string MeshOcclusion = "sdfMeshOcclusion";
    /// <summary>The mesh material atlas: each texel's palette entry, R8, read without filtering.</summary>
    public const string MeshMaterials = "sdfMeshMaterials";
    /// <summary>The mesh emission atlas: linear emitted light, BC6H.</summary>
    public const string MeshEmission = "sdfMeshEmission";
    /// <summary>The impostor albedo atlas, sRGB-encoded BC7 with coverage in alpha.</summary>
    public const string ImpostorAlbedo = "sdfImpostorAlbedo";
    /// <summary>The impostor normal atlas: octahedral object-space normal pairs, BC5.</summary>
    public const string ImpostorNormals = "sdfImpostorNormals";
    /// <summary>The impostor depth atlas, BC4: 0 at a view's near side of the bounding sphere, 1 at its far side and where
    /// the view's ray missed.</summary>
    public const string ImpostorDepth = "sdfImpostorDepth";
    /// <summary>The impostor material atlas: each texel's program material id, R8, read without filtering.</summary>
    public const string ImpostorMaterials = "sdfImpostorMaterials";
    /// <summary>The impostor emission atlas: linear emitted light, BC6H.</summary>
    public const string ImpostorEmission = "sdfImpostorEmission";

    /// <summary>Gets the impostor atlases, World-group members of every compute pass (and, for the depth atlas, the mesh
    /// pass's pass group), in the order the impostor atlases hold the usages they pack (albedo, normal, depth, material, emission):
    /// images that change only when the set of impostors a frame draws does.</summary>
    public static IReadOnlyList<string> ImpostorAtlases { get; } = [ImpostorAlbedo, ImpostorNormals, ImpostorDepth, ImpostorMaterials, ImpostorEmission];
    /// <summary>Gets the mesh atlases, World-group members of every compute pass, in the order the mesh atlases hold
    /// the usages they pack (albedo, normal, occlusion, material, emission): images that change only when the set of
    /// textured meshes a frame draws does.</summary>
    public static IReadOnlyList<string> MeshAtlases { get; } = [MeshAlbedo, MeshNormals, MeshOcclusion, MeshMaterials, MeshEmission];

    /// <summary>The screen sources: one sampled image per screen, indexed by screen index.</summary>
    public const string ScreenSources = "screenSources";
    /// <summary>The length of <see cref="ScreenSources"/>: the most screen surfaces one program declares.</summary>
    public const int ScreenSourceCount = 32;
    /// <summary>The package's samplers, one per filter, indexed by the filter's value (<see cref="GpuSamplerFilter"/>): a
    /// screen samples its source through the one its row chooses, and the glyph atlas through the nearest one.</summary>
    public const string Samplers = "samplers";
    /// <summary>The length of <see cref="Samplers"/>: one sampler per <see cref="GpuSamplerFilter"/>.</summary>
    public const uint SamplerCount = 2;
    /// <summary>The edge of one screen tile in pixels, the unit the beam, the instance masks and the cull buffer count in.
    /// KEEP IN SYNC with <c>WorldTileSize</c> in <c>frame/sdf-tile.hlsli</c>.</summary>
    public const uint TileSize = 16;
    /// <summary>The bytes of one visibility record: the twenty-four words in its V, C, L, N, S, K and I rows that
    /// <c>sdf-visibility.hlsli</c> lays out (<c>SdfVisibilityWords</c>).</summary>
    public const int VisibilityRecordByteLength = (24 * sizeof(uint));
    /// <summary>The planes the cull buffer holds per tile: the march start, the first exit, the second entry and the far
    /// bound. KEEP IN SYNC with <c>WorldTilePlaneCount</c> in <c>frame/sdf-frame.hlsli</c>.</summary>
    public const uint TilePlaneCount = 4;
    /// <summary>The floats the cull buffer holds per live instance after its tile planes: two float3 corners in each of
    /// the primary and ambient part-bound bands. KEEP IN SYNC with <c>SdfPartBoundFloatCount</c> in
    /// <c>march/sdf-part-bounds.hlsli</c>.</summary>
    public const uint PartBoundFloatCount = 12;
    /// <summary>The bytes of the dispatch box cull-args writes: the surviving tiles' group origin, then their exclusive
    /// group end, four <c>uint</c>s.</summary>
    public const ulong CullBoundsByteLength = (4 * sizeof(uint));
    /// <summary>The format of the mesh visibility target: per pixel the ray parameter, the draw index plus one and the
    /// triangle, each a whole number a float holds exactly.</summary>
    public const GpuPixelFormat MeshTargetFormat = GpuPixelFormat.R32G32B32A32Float;
    /// <summary>The format of the mesh pass's reversed-Z depth attachment.</summary>
    public const GpuPixelFormat MeshDepthFormat = GpuPixelFormat.D32Float;
    /// <summary>The depth the mesh pass clears its depth attachment to: zero, the reversed-Z far plane at infinity.</summary>
    public const float MeshClearDepth = 0f;

    /// <summary>Gets the mesh pass's depth attachment: cleared to <see cref="MeshClearDepth"/> and discarded, the one
    /// statement of its clear that the pass's render pass and the depth image the node creates for it share.</summary>
    public static GpuDepthAttachment MeshDepthAttachment { get; } = new(
        ClearDepth: MeshClearDepth,
        Format: MeshDepthFormat,
        Load: GpuAttachmentLoad.Clear,
        Store: GpuAttachmentStore.Discard
    );

    /// <summary>The name the fragment's output port version takes: the view's color, which the composite writes from the
    /// sky's runs and the lit image.</summary>
    public const string Color = "color";

    /// <summary>Gets the values every pass of the fragment reads from its pass block beside the extent, which the mesh
    /// pass's interface shares so its block lies alike: the world values, the view, the frame's levers, its light count
    /// and shadow slot table, and its curvature shading. The lights and the sky are World-group tables the SDF engine's
    /// kernel interface adds (<c>SdfKernelInterfaces</c>), bound only by the passes that read them.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Values { get; } = [
        Value(name: LightSweepRadius, type: ShaderValueType.Float),
        Value(name: LightMap, type: ShaderValueType.Uint),
        Value(name: LightMapCount, type: ShaderValueType.Uint),
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, length: (SdfIndirectLightLayout.MaxMaps * SdfIndirectLightLayout.MetadataRows), name: LightMaps, type: ShaderValueType.Float4),
        Value(name: IndirectTier, type: ShaderValueType.Uint),
        Value(name: IndirectSources, type: ShaderValueType.Uint),
        Value(name: IndirectBodies, type: ShaderValueType.Uint),
        Value(name: IndirectMethod, type: ShaderValueType.Uint),
        Value(name: ImageExtent, type: ShaderValueType.Uint2),
        Value(name: InstanceMaskWordCount, type: ShaderValueType.Uint),
        Value(name: MeshDraws, type: ShaderValueType.Uint),
        Value(name: ScreenCount, type: ShaderValueType.Uint),
        Value(name: TileGrid, type: ShaderValueType.Uint2),
        Value(name: ViewBase, type: ShaderValueType.Uint),
        Value(name: ViewportCount, type: ShaderValueType.Uint),
        Value(name: ViewPosition, type: ShaderValueType.Float3),
        Value(name: ViewRight, type: ShaderValueType.Float3),
        Value(name: ViewUp, type: ShaderValueType.Float3),
        Value(name: ViewForward, type: ShaderValueType.Float3),
        Value(name: TanHalfFieldOfView, type: ShaderValueType.Float),
        Value(name: AspectRatio, type: ShaderValueType.Float),
        Value(name: FrustumOffset, type: ShaderValueType.Float2),
        Value(name: Jitter, type: ShaderValueType.Float2),
        Value(name: HistoryFrames, type: ShaderValueType.Uint),
        Value(name: ShadowAmortize, type: ShaderValueType.Uint),
        Value(name: ShadowOwnershipReject, type: ShaderValueType.Uint),
        Value(name: ShadowLightReject, type: ShaderValueType.Uint),
        Value(name: Temporal, type: ShaderValueType.Uint),
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, length: 6, name: PreviousView, type: ShaderValueType.Float4),
        Value(name: NearDistance, type: ShaderValueType.Float),
        Value(name: FarDistance, type: ShaderValueType.Float),
        Value(name: DebugMode, type: ShaderValueType.Uint),
        Value(name: LightCount, type: ShaderValueType.Uint),
        Value(name: ShadowSlots, type: ShaderValueType.Int4),
        Value(name: ShadowSlotCount, type: ShaderValueType.Uint),
        Value(name: ShadowFadeCount, type: ShaderValueType.Uint),
        Value(name: CurvatureCavity, type: ShaderValueType.Float),
        Value(name: CurvatureRim, type: ShaderValueType.Float),
        Value(name: CurvatureInk, type: ShaderValueType.Float),
        Value(name: CurvatureInkLow, type: ShaderValueType.Float),
        Value(name: CurvatureInkHigh, type: ShaderValueType.Float),
        Value(name: CurvatureInkColor, type: ShaderValueType.Float3),
        Value(name: DebugSliceAxis, type: ShaderValueType.Float),
        Value(name: DebugSliceOffset, type: ShaderValueType.Float),
        Value(name: GridFlags, type: ShaderValueType.Uint),
        Value(name: GridPlaneY, type: ShaderValueType.Float),
        Value(name: GridLineWidth, type: ShaderValueType.Float),
        Value(name: GridWorldPitch, type: ShaderValueType.Float3),
        Value(name: GridWorldOrigin, type: ShaderValueType.Float3),
        Value(name: GridWorldFrame, type: ShaderValueType.Float4),
        Value(name: GridObjectOrigin, type: ShaderValueType.Float3),
        Value(name: GridObjectPitch, type: ShaderValueType.Float3),
        Value(name: GridObjectFrame, type: ShaderValueType.Float4),
        Value(name: GridObjectPatchRadius, type: ShaderValueType.Float),
        Value(name: FiniteDifferenceNormals, type: ShaderValueType.Uint),
        Value(name: DisableShadowCull, type: ShaderValueType.Uint),
        Value(name: DisableSoftShadows, type: ShaderValueType.Uint),
        Value(name: DisableAmbientOcclusion, type: ShaderValueType.Uint),
        Value(name: ShadowDistanceScale, type: ShaderValueType.Float),
        Value(name: DisableScreenLights, type: ShaderValueType.Uint),
        Value(name: EnableShadowProxy, type: ShaderValueType.Uint),
        Value(name: CameraTileShadowMask, type: ShaderValueType.Uint),
        Value(name: FastSoftShadowMarch, type: ShaderValueType.Uint),
        Value(name: FastAmbientOcclusion, type: ShaderValueType.Uint),
        Value(name: DisableFarBound, type: ShaderValueType.Uint),
        Value(name: GeometryOnly, type: ShaderValueType.Uint),
        ShaderWorkCounters.RowMember,
        ShaderWorkCounters.DetailRowMember,
    ];
    /// <summary>Gets the World group's members: what every pass of every view reads alike, the residency's tables, the
    /// brick pool, the glyph atlas, the samplers and the mesh and impostor atlases (<see cref="MeshAtlases"/>,
    /// <see cref="ImpostorAtlases"/>), which the residency
    /// binds as one set per upload ring slot, written once and again only when what it binds moves.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Tables { get; } = [
        Table(element: ShaderValueType.Uint4, name: ProgramWords),
        Table(element: ShaderValueType.Float4, name: DynamicTransforms),
        Table(element: ShaderValueType.Float4, name: PreviousDynamicTransforms),
        Table(element: ShaderValueType.Float4, name: PreviousMeshTransforms),
        Table(element: ShaderValueType.Uint, name: FrameInstanceGrid),
        Table(element: ShaderValueType.Float4, name: ScreenSurfaces),
        Table(element: ShaderValueType.Float4, name: ScreenMappings),
        Table(element: ShaderValueType.Float4, name: ScreenLights),
        Table(element: ShaderValueType.Uint4, name: DecalCells),
        Table(element: ShaderValueType.Float, name: BrickPool),
        Table(element: ShaderValueType.Float4, name: Volumes),
        Table(element: ShaderValueType.Uint, name: MeshRegion),
        WorldImage(name: GlyphAtlas),
        ShaderInterfaceMember.Sampler(
            group: ShaderInterfaceGroup.World,
            length: SamplerCount,
            name: Samplers
        ),
        WorldImage(name: MeshAlbedo),
        WorldImage(name: MeshNormals),
        WorldImage(name: MeshOcclusion),
        WorldImage(name: MeshMaterials),
        WorldImage(name: MeshEmission),
        WorldImage(name: ImpostorAlbedo),
        WorldImage(name: ImpostorNormals),
        WorldImage(name: ImpostorDepth),
        WorldImage(name: ImpostorMaterials),
        WorldImage(name: ImpostorEmission),
    ];
    /// <summary>Gets what every compute pass of the fragment reads: from its pass group, beside the extent, the values
    /// (<see cref="Values"/>), the view's scratch, its output, the screens it shows, the mesh target and the work counters;
    /// and the World
    /// group's members (<see cref="Tables"/>).</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Members { get; } = [
        .. Values,
        .. WorldIndirectValues,
        Read(element: ShaderValueType.Float, name: IndirectLightDepth),
        Written(element: ShaderValueType.Float, name: IndirectLightDepthWritten),
        Read(element: ShaderValueType.Uint, name: IndirectCache),
        Written(element: ShaderValueType.Uint, name: IndirectCacheWritten),
        Written(element: ShaderValueType.Uint, name: IndirectPickWritten),
        Read(element: ShaderValueType.Int4, name: IndirectBricks),
        Read(element: ShaderValueType.Uint, name: InstanceMasks),
        Written(element: ShaderValueType.Uint, name: InstanceMasksWritten),
        Read(element: ShaderValueType.Uint, name: SegmentTapes),
        Written(element: ShaderValueType.Uint, name: SegmentTapesWritten),
        Read(element: ShaderValueType.Float, name: Tiles),
        Written(element: ShaderValueType.Float, name: TilesWritten),
        Read(element: ShaderValueType.Uint, name: CullBounds),
        Written(element: ShaderValueType.Uint, name: CullBoundsWritten),
        Written(element: ShaderValueType.Uint, name: ViewsArgsWritten),
        Read(element: ShaderValueType.Uint, name: VisibilityRecords),
        Written(element: ShaderValueType.Uint, name: VisibilityRecordsWritten),
        Written(element: ShaderValueType.Float, name: ReactivityWritten),
        Read(element: ShaderValueType.Uint, name: ShadowHistory),
        Written(element: ShaderValueType.Uint, name: ShadowHistoryWritten),
        ShaderInterfaceMember.StorageImage(
            format: RenderGraphPackageCatalog.WorkingFormat,
            group: ShaderInterfaceGroup.Pass,
            name: Output,
            type: ShaderValueType.Float4
        ),
        ShaderInterfaceMember.SampledImage(
            group: ShaderInterfaceGroup.Pass,
            length: ScreenSourceCount,
            name: ScreenSources,
            type: ShaderValueType.Float4
        ),
        ShaderInterfaceMember.SampledImage(
            group: ShaderInterfaceGroup.Pass,
            name: MeshVisibility,
            type: ShaderValueType.Float4
        ),
        ShaderWorkCounters.BufferMember,
        .. Tables,
    ];
    /// <summary>Gets the fragment the package runs as: one view's dispatch set, its scratch retained and counted, its
    /// one output the view's color. Views shades the hits into the lit image (<see cref="Parts.Lit"/>), the sky evaluates
    /// its field runs where the lit image's coverage is below one (<see cref="Parts.Sky"/>), and the composite writes the
    /// color (<see cref="Parts.Composite"/>). Every pass counts its kernels' march steps and texels written into the work counters
    /// (<see cref="RenderGraphFragmentPass.CountsKernelWork"/>): the mesh pass each fragment it writes to its
    /// target.</summary>
    public static RenderGraphPackageFragment NativeFragment { get; } = new(
        InputVersions: [],
        OutputVersions: [Color],
        Passes: [
            Pass(name: Parts.Mask, outputs: [Parts.InstanceMasks]),
            Pass(inputs: [Parts.InstanceMasks], name: Parts.Beam, outputs: [Parts.Tiles]),
            Pass(inputs: [Parts.InstanceMasks, Parts.Tiles], name: Parts.Tape, outputs: [Parts.SegmentTapes]),
            Pass(inputs: [Parts.Tiles], name: Parts.CullArgs, outputs: [Parts.Arguments, Parts.CullBounds]),
            new RenderGraphFragmentPass(
                CountsKernelWork: true,
                InputAccesses: [],
                Inputs: [],
                Name: Parts.Mesh,
                OutputAccesses: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite],
                Outputs: [Parts.MeshTarget, Parts.MeshDepth]
            ),
            Hit(mesh: true, name: Parts.Primary, visibility: null, written: Parts.Visibility),
            Hit(mesh: false, name: Parts.Surface, visibility: null, written: Parts.SurfaceVisibility),
            Hit(mesh: false, name: Parts.Ambient, visibility: null, written: Parts.AmbientVisibility),
            Hit(mesh: false, name: Parts.Shadow, visibility: null, written: Parts.ShadowVisibility),
            Hit(mesh: false, name: Parts.Views, visibility: Parts.ShadowVisibility, written: Parts.Lit),
            Pass(inputs: [Parts.Lit, Parts.CullBounds], name: Parts.Sky, outputs: SkyRuns) with { Members = SkyMembers },
            Pass(inputs: [Parts.Lit, Parts.CullBounds, Parts.ShadowVisibility, .. SkyRuns], name: Parts.Composite, outputs: [Color]) with { Members = SkyMembers },
        ],
        Resources: [
            Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: Parts.Lit, retained: true),
            .. SkyResources,
            Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: Color, retained: false),
            Buffer(
                count: [Term(1, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles, ShaderPipelineCountBasis.InstanceMaskWords)],
                name: Parts.InstanceMasks,
                sizeBytes: null,
                strideBytes: sizeof(uint)
            ),
            Buffer(
                count: [Term(1, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles, ShaderPipelineCountBasis.SegmentTapeWords)],
                name: Parts.SegmentTapes,
                sizeBytes: null,
                strideBytes: sizeof(uint)
            ),
            Buffer(
                count: [
                    Term(TilePlaneCount, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles),
                    Term(PartBoundFloatCount, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Instances),
                ],
                name: Parts.Tiles,
                sizeBytes: null,
                strideBytes: sizeof(float)
            ),
            Buffer(count: null, name: Parts.Arguments, sizeBytes: ShaderPipelineDispatch.ArgumentBytes, strideBytes: sizeof(uint)),
            Buffer(count: null, name: Parts.CullBounds, sizeBytes: CullBoundsByteLength, strideBytes: sizeof(uint)),
            Image(format: MeshTargetFormat, from: null, name: Parts.MeshTarget, retained: true),
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: MeshDepthFormat.ToString(),
                ClearDepth: MeshClearDepth,
                Kind: ShaderPipelineResourceKind.Depth,
                Name: Parts.MeshDepth,
                Retained: true
            ),
            Visibility(from: null, name: Parts.Visibility),
            Visibility(from: Parts.Visibility, name: Parts.SurfaceVisibility),
            Visibility(from: Parts.SurfaceVisibility, name: Parts.AmbientVisibility),
            Visibility(from: Parts.AmbientVisibility, name: Parts.ShadowVisibility),
        ]
    );

    private static ShaderInterfaceMember Read(string name, ShaderValueType element) => ShaderInterfaceMember.ReadOnlyBuffer(
        element: element,
        group: ShaderInterfaceGroup.Pass,
        name: name
    );
    private static ShaderInterfaceMember Table(string name, ShaderValueType element) => ShaderInterfaceMember.ReadOnlyBuffer(
        element: element,
        group: ShaderInterfaceGroup.World,
        name: name
    );
    private static ShaderInterfaceMember Value(string name, ShaderValueType type) => ShaderInterfaceMember.Value(
        group: ShaderInterfaceGroup.Pass,
        name: name,
        type: type
    );
    private static ShaderInterfaceMember WorldImage(string name) => ShaderInterfaceMember.SampledImage(
        group: ShaderInterfaceGroup.World,
        name: name,
        type: ShaderValueType.Float4
    );
    private static ShaderInterfaceMember Written(string name, ShaderValueType element) => ShaderInterfaceMember.ReadWriteBuffer(
        element: element,
        group: ShaderInterfaceGroup.Pass,
        name: name
    );
    private static ShaderPipelineCountTerm Term(ulong elements, params ShaderPipelineCountBasis[] per) => new(
        Elements: elements,
        Per: per
    );
    private static ShaderPipelineResource Image(string name, GpuPixelFormat format, string? from, bool retained) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: format.ToString(),
        From: from,
        Name: name,
        Retained: retained
    );
    // A retained buffer counted by its bases, or of a fixed size.
    private static ShaderPipelineResource Buffer(string name, uint strideBytes, ulong? sizeBytes, IReadOnlyList<ShaderPipelineCountTerm>? count) => new(
        Count: count,
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: name,
        SizeBytes: sizeBytes,
        StrideBytes: strideBytes,
        Retained: true
    );
    // A version of the visibility records forwarding another: the storage's declaration, less the retained class only its
    // first version declares.
    private static ShaderPipelineResource Visibility(string name, string? from) => new(
        Count: [Term(1, ShaderPipelineCountBasis.Extent, ShaderPipelineCountBasis.Viewports)],
        From: from,
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: name,
        StrideBytes: VisibilityRecordByteLength,
        Retained: (from is null),
        PreservesPredecessor: (from is not null)
    );
    // A compute pass, whose kernel counts its own work as every pass of the fragment does.
    private static RenderGraphFragmentPass Pass(string name, string[] outputs, string[]? inputs = null) => new(
        CountsKernelWork: true,
        InputAccesses: [.. (inputs ?? []).Select(selector: static _ => RenderGraphPortAccess.ComputeRead)],
        Inputs: [.. (inputs ?? []).Select(selector: static input => new ResourceReference(Name: input))],
        Name: name,
        OutputAccesses: [.. outputs.Select(selector: static _ => RenderGraphPortAccess.ComputeWrite)],
        Outputs: [.. outputs.Select(selector: static output => new ResourceReference(Name: output))]
    );
    // A pass dispatched indirectly over the cull arguments' surviving tiles: it reads the dispatch box, the masks and the
    // tiles, the mesh visibility target when it resolves mesh pixels, and the visibility records when it shades them.
    private static RenderGraphFragmentPass Hit(string name, bool mesh, string? visibility, string written) {
        string[] inputs = [
            Parts.CullBounds,
            Parts.InstanceMasks,
            Parts.Tiles,
            Parts.SegmentTapes,
            .. (mesh ? (string[])[Parts.MeshTarget] : []),
            .. ((visibility is null) ? [] : (string[])[visibility]),
        ];

        return Pass(
            inputs: inputs,
            name: name,
            outputs: [written]
        ) with {
            Dispatch = ShaderPipelineDispatch.Indirect(arguments: Parts.Arguments),
        };
    }

    /// <summary>The names of the fragment's passes and versions.</summary>
    public static class Parts {
        /// <summary>The instance-cull pass building each tile's instance mask.</summary>
        public const string Mask = "mask";
        /// <summary>The beam prepass writing the tile planes and part bounds.</summary>
        public const string Beam = "beam";
        /// <summary>The certified segment-pruning pass over the tile's depth slabs.</summary>
        public const string Tape = "tape";
        /// <summary>The live-segment masks, summary words, slab balls and working mask.</summary>
        public const string SegmentTapes = "segmentTapes";
        /// <summary>The reduction to the indirect dispatch arguments and the dispatch box.</summary>
        public const string CullArgs = "cull-args";
        /// <summary>The mesh pass drawing the frame's mesh draws into the mesh visibility target.</summary>
        public const string Mesh = "mesh";
        /// <summary>Primary traversal, writing the visibility records.</summary>
        public const string Primary = "primary";
        /// <summary>Surface resolution, continuing the visibility records.</summary>
        public const string Surface = "surface";
        /// <summary>Ambient resolution, continuing the visibility records.</summary>
        public const string Ambient = "ambient";
        /// <summary>Each stable and active incoming slot's soft shadow, continuing the visibility records.</summary>
        public const string Shadow = "shadow";
        /// <summary>Shading the hits into the lit image.</summary>
        public const string Views = "views";
        /// <summary>The sky's field runs, evaluated where the lit image's coverage is below one.</summary>
        public const string Sky = "sky";
        /// <summary>The composite: the sky's runs in their authored order, the lit image over them by its coverage, then
        /// the fog and the bounded media, into the view's color.</summary>
        public const string Composite = "composite";
        /// <summary>The lit image: the hits' shaded color premultiplied by coverage and by the fog's transmittance over each
        /// hit's ray distance, the coverage in its alpha (one for a solid hit, the silhouette's weight on an edge, zero on a
        /// miss), written by views in a native view and, at the output extent, by the resolve in a reduced or temporal
        /// one.</summary>
        public const string Lit = "lit";
        /// <summary>Each output pixel's surface transport, which the resolve writes for the composite in a reduced or
        /// temporal view, reconstructed from the render samples with the lit image's weights: two words a pixel
        /// (<c>shade/sdf-transport.hlsli</c>). With the first word's top bit clear, four half floats: the fog's in-scatter
        /// weight and the coverage over the ray distance, scaled, in the first word, the haze's and the medium's
        /// in-scatter weights in the second; with it set, a pixel the resolve copied whole from one render sample, carrying
        /// that sample's ray distance's float bits, from which the composite derives the transport as a native view's
        /// composite does.</summary>
        public const string Transport = "transport";
        /// <summary>The sky's lowest field run, which composes over nothing, so its offset alone, with whether the sky
        /// evaluated the texel in its alpha.</summary>
        public const string SkyBase = "skyBase";
        /// <summary>The first of the sky's upper field runs' images: six half floats a run (its scale, then its offset),
        /// packed across the upper images' channels in run order.</summary>
        public const string SkyUpper0 = "skyUpper0";
        /// <summary>The second of the sky's upper field runs' images.</summary>
        public const string SkyUpper1 = "skyUpper1";
        /// <summary>The third of the sky's upper field runs' images.</summary>
        public const string SkyUpper2 = "skyUpper2";
        /// <summary>The per-tile instance masks.</summary>
        public const string InstanceMasks = "instanceMasks";
        /// <summary>The cull buffer: the tile planes and the part bounds.</summary>
        public const string Tiles = "tiles";
        /// <summary>The hit passes' indirect dispatch arguments.</summary>
        public const string Arguments = "arguments";
        /// <summary>The dispatch box.</summary>
        public const string CullBounds = "cullBounds";
        /// <summary>The mesh visibility target.</summary>
        public const string MeshTarget = "meshTarget";
        /// <summary>The mesh pass's depth attachment.</summary>
        public const string MeshDepth = "meshDepth";
        /// <summary>Primary's visibility records.</summary>
        public const string Visibility = "visibility";
        /// <summary>Surface's visibility records, forwarding primary's.</summary>
        public const string SurfaceVisibility = "surfaceVisibility";
        /// <summary>Ambient's visibility records, forwarding surface's.</summary>
        public const string AmbientVisibility = "ambientVisibility";
        /// <summary>Shadow's visibility records, forwarding ambient's.</summary>
        public const string ShadowVisibility = "shadowVisibility";
        /// <summary>The reactivity buffer views writes where it shades and the resolve reads inside the dispatch
        /// box.</summary>
        public const string Reactivity = "reactivity";
        /// <summary>The history color: the resolved color, one image a frame slot, read by the next frame.</summary>
        public const string HistoryColor = "historyColor";
        /// <summary>The history surface: each output pixel's identity, ray distance, gathered weight and accumulated
        /// transport, read by the next frame.</summary>
        public const string HistorySurface = "historySurface";
    }
}
