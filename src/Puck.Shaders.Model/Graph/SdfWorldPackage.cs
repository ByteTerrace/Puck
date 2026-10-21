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
/// <para>The fragment (<see cref="NativeFragment"/>) is one view of the residency's baked meshes: the bake's instance
/// masks and field samples while a bake is pending, the shadow cascades' and the view's mesh rasterization, the surface
/// pass that writes every pixel's visibility record from them, shading the records into the lit image, then the sky's
/// field runs and the composite that writes the output. Its scratch is retained, one allocation shared by every frame
/// slot, and scales with the counts its host resolves: the view's extent, one viewport, and the program's instance-mask
/// words.</para>
/// </summary>
public static partial class SdfWorldPackage {
    /// <summary>The pass-group value holding the render extent in pixels: the per-view visibility record stride.</summary>
    public const string ImageExtent = "imageExtent";
    /// <summary>The pass-group value holding every view the dispatch set renders.</summary>
    public const string ViewportCount = "viewportCount";
    /// <summary>The pass-group value holding one past the highest screen whose source is bound this frame, or zero when
    /// none is: the bound the screen-light loop runs to.</summary>
    public const string ScreenCount = "screenCount";
    /// <summary>The pass-group value holding the live program's instance-mask width: the words one bake lattice's mask
    /// holds before its summary.</summary>
    public const string InstanceMaskWordCount = "instanceMaskWordCount";
    /// <summary>The pass-group value naming the view the dispatch set renders, whose visibility records it indexes.</summary>
    public const string ViewBase = "viewBase";
    /// <summary>The pass-group value holding the frame's mesh draws, or zero when no mesh draws: the surface pass reads the
    /// mesh visibility target only when it is non-zero.</summary>
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
    /// <summary>The pass-group value set to one to skip the soft-shadow march (<c>uint</c>).</summary>
    public const string DisableSoftShadows = "disableSoftShadows";
    /// <summary>The pass-group value set to one to skip ambient occlusion (<c>uint</c>).</summary>
    public const string DisableAmbientOcclusion = "disableAmbientOcclusion";
    /// <summary>The pass-group value set to one to skip the screens' area lights (<c>uint</c>).</summary>
    public const string DisableScreenLights = "disableScreenLights";
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
    /// <summary>The bake lattices' instance masks, read by the bake.</summary>
    public const string InstanceMasks = "sdfInstanceMasks";
    /// <summary>The bake lattices' instance masks, written by the mask pass.</summary>
    public const string InstanceMasksWritten = "sdfInstanceMasksRW";
    /// <summary>The visibility records, read by views, the resolve and the composite.</summary>
    public const string VisibilityRecords = "sdfVisibilityRecords";
    /// <summary>The visibility records, written by the surface pass.</summary>
    public const string VisibilityRecordsWritten = "sdfVisibilityRecordsRW";
    /// <summary>The image a pass writes its color to: the lit image views shades, the resolved lit image the resolve
    /// writes, and the view's output the composite writes.</summary>
    public const string Output = "output";
    /// <summary>The screen-surface table, three float4 rows per screen slot.</summary>
    public const string ScreenSurfaces = "screenSurfaces";
    /// <summary>The screen-mapping table, seven float4 rows per screen slot: the draw form of the mapping each screen
    /// publishes (<c>SourceMapping.Draw</c>), which the screen shading draws its face from, and the screen's state,
    /// whether its source is bound, the sampler it reads through, and whether it emits into the consuming world.</summary>
    public const string ScreenMappings = "screenMappings";
    /// <summary>The acquired-image reduction: one float4 a screen slot, its pixel-weighted whole-image mean and the
    /// direct-light gain.</summary>
    public const string ScreenLights = "sdfScreenLights";
    /// <summary>The glyph decal table.</summary>
    public const string DecalCells = "sdfDecalCells";
    /// <summary>The brick pool, read by the bake.</summary>
    public const string BrickPool = "sdfBrickPool";
    /// <summary>The bounded-volume table.</summary>
    public const string Volumes = "sdfVolumes";
    /// <summary>The mesh region as a stream of words: the draw records, then the positions and indices the mesh pass
    /// reads.</summary>
    public const string MeshRegion = "sdfMeshRegion";
    /// <summary>The mesh visibility target the mesh pass draws and the surface pass reads: per pixel the ray parameter, the
    /// draw index plus one (zero where no mesh covers it) and the triangle.</summary>
    public const string MeshVisibility = "meshVisibility";
    /// <summary>The glyph atlas.</summary>
    public const string GlyphAtlas = "sdfGlyphAtlas";
    /// <summary>The mesh depth pyramid the surface pass's occlusion reads, which the pyramid pass writes through
    /// <see cref="MeshPyramidWritten"/>: levels one up of the mesh visibility target, each texel an actual pixel's ray
    /// parameter and coordinate (<c>frame/sdf-mesh-pyramid.hlsli</c>).</summary>
    public const string MeshPyramid = "sdfMeshPyramid";
    /// <summary>The pyramid pass's writable binding of <see cref="MeshPyramid"/>.</summary>
    public const string MeshPyramidWritten = "sdfMeshPyramidRW";

    /// <summary>The screen sources: one sampled image per screen, indexed by screen index.</summary>
    public const string ScreenSources = "screenSources";
    /// <summary>The length of <see cref="ScreenSources"/>: the most screen surfaces one program declares.</summary>
    public const int ScreenSourceCount = 32;
    /// <summary>The package's samplers, one per filter, indexed by the filter's value (<see cref="GpuSamplerFilter"/>): a
    /// screen samples its source through the one its row chooses, and the glyph atlas through the nearest one.</summary>
    public const string Samplers = "samplers";
    /// <summary>The length of <see cref="Samplers"/>: one sampler per <see cref="GpuSamplerFilter"/>.</summary>
    public const uint SamplerCount = 2;
    /// <summary>The bytes of one visibility record: the eight words <c>sdf-visibility.hlsli</c> lays out
    /// (<c>SdfVisibilityWords</c>).</summary>
    public const int VisibilityRecordByteLength = (8 * sizeof(uint));
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
    /// <summary>Gets a shadow cascade's depth attachment: cleared to <see cref="MeshClearDepth"/>, which reads as the
    /// cascade's far plane where nothing was drawn, and stored, since the surface pass samples it.</summary>
    public static GpuDepthAttachment MeshShadowDepthAttachment { get; } = MeshDepthAttachment with { Store = GpuAttachmentStore.Store };

    /// <summary>The name the fragment's output port version takes: the view's color, which the composite writes from the
    /// sky's runs and the lit image.</summary>
    public const string Color = "color";

    /// <summary>Gets the values every pass of the fragment reads from its pass block beside the extent, which the mesh
    /// pass's interface shares so its block lies alike: the world values, the view, the frame's levers, its light count
    /// and shadow slot table, and its curvature shading. The lights and the sky are World-group tables the SDF engine's
    /// kernel interface adds (<c>SdfKernelInterfaces</c>), bound only by the passes that read them.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Values { get; } = [
        Value(name: IndirectSources, type: ShaderValueType.Uint),
        Value(name: IndirectSourceGains, type: ShaderValueType.Float4),
        Value(name: IndirectApply, type: ShaderValueType.Float4),
        Value(name: IndirectContact, type: ShaderValueType.Float),
        Value(name: ImageExtent, type: ShaderValueType.Uint2),
        Value(name: InstanceMaskWordCount, type: ShaderValueType.Uint),
        Value(name: MeshDraws, type: ShaderValueType.Uint),
        Value(name: ScreenCount, type: ShaderValueType.Uint),
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
        Value(name: DisableSoftShadows, type: ShaderValueType.Uint),
        Value(name: DisableAmbientOcclusion, type: ShaderValueType.Uint),
        Value(name: DisableScreenLights, type: ShaderValueType.Uint),
        Value(name: GeometryOnly, type: ShaderValueType.Uint),
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, length: (MeshShadowCascadeCount * MeshShadowCascadeRows), name: MeshShadowCascades, type: ShaderValueType.Float4),
        Value(name: MeshShadowCount, type: ShaderValueType.Uint),
        Value(name: MeshShadowParameters, type: ShaderValueType.Float4),
        Value(name: MeshShadowTexels, type: ShaderValueType.Uint),
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, length: (MeshBakeBatch * MeshBakeLatticeRows), name: MeshBakeLattices, type: ShaderValueType.Float4),
        Value(name: MeshBakeCount, type: ShaderValueType.Uint),
        Value(name: MeshIrradianceVolume, type: ShaderValueType.Uint4),
        Value(name: MeshIrradianceLow, type: ShaderValueType.Int4),
        ShaderWorkCounters.RowMember,
        ShaderWorkCounters.DetailRowMember,
    ];
    /// <summary>Gets the World group's members: what every pass of every view reads alike, the residency's tables, the
    /// brick pool, the glyph atlas and the samplers, which the residency binds as one set per upload ring slot, written
    /// once and again only when what it binds moves.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Tables { get; } = [
        Table(element: ShaderValueType.Uint4, name: ProgramWords),
        Table(element: ShaderValueType.Float4, name: DynamicTransforms),
        Table(element: ShaderValueType.Float4, name: PreviousDynamicTransforms),
        Table(element: ShaderValueType.Float4, name: PreviousMeshTransforms),
        Table(element: ShaderValueType.Float4, name: ScreenSurfaces),
        Table(element: ShaderValueType.Float4, name: ScreenMappings),
        Table(element: ShaderValueType.Float4, name: ScreenLights),
        Table(element: ShaderValueType.Uint4, name: DecalCells),
        Table(element: ShaderValueType.Float, name: BrickPool),
        Table(element: ShaderValueType.Float4, name: Volumes),
        Table(element: ShaderValueType.Uint, name: MeshRegion),
        Table(element: ShaderValueType.Float4, name: MeshBakePoints),
        Table(element: ShaderValueType.Uint, name: MeshIrradiance),
        WorldImage(name: GlyphAtlas),
        ShaderInterfaceMember.Sampler(
            group: ShaderInterfaceGroup.World,
            length: SamplerCount,
            name: Samplers
        ),
    ];
    /// <summary>Gets what every compute pass of the fragment reads: from its pass group, beside the extent, the values
    /// (<see cref="Values"/>), the view's scratch, its output, the screens it shows, the mesh and cascade targets and the
    /// work counters; and the World group's members (<see cref="Tables"/>).</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Members { get; } = [
        .. Values,
        Read(element: ShaderValueType.Uint, name: InstanceMasks),
        Written(element: ShaderValueType.Uint, name: InstanceMasksWritten),
        Read(element: ShaderValueType.Uint, name: VisibilityRecords),
        Written(element: ShaderValueType.Uint, name: VisibilityRecordsWritten),
        Written(element: ShaderValueType.Float, name: ReactivityWritten),
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
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: MeshShadowNear, type: ShaderValueType.Float4),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: MeshShadowFar, type: ShaderValueType.Float4),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: MeshShadowSecond, type: ShaderValueType.Float4),
        Written(element: ShaderValueType.Uint, name: MeshBakeSamplesWritten),
        ShaderWorkCounters.BufferMember,
        Read(element: ShaderValueType.Uint, name: MeshPyramid),
        Written(element: ShaderValueType.Uint, name: MeshPyramidWritten),
        .. Tables,
    ];
    /// <summary>Gets the fragment a native view runs as: one view's dispatch set, its scratch retained and counted, its
    /// one output the view's color. While a bake is pending the mask and bake passes sample its lattices
    /// (<see cref="Parts.Mask"/>, <see cref="Parts.Bake"/>); the cascades and the mesh pass rasterize the frame's mesh
    /// draws, the pyramid pass reduces the mesh target into the depth pyramid (<see cref="Parts.Pyramid"/>), the surface pass writes every pixel's visibility record from them (<see cref="Parts.Surface"/>), views shades
    /// the records into the lit image (<see cref="Parts.Lit"/>), the sky evaluates its field runs where the lit image's
    /// coverage is below one (<see cref="Parts.Sky"/>), and the composite writes the color (<see cref="Parts.Composite"/>).
    /// Every pass counts its kernels' work into the work counters (<see cref="RenderGraphFragmentPass.CountsKernelWork"/>):
    /// the raster passes each fragment they write to their target.</summary>
    public static RenderGraphPackageFragment NativeFragment { get; } = new(
        InputVersions: [],
        OutputVersions: [Color],
        Passes: [
            Pass(name: Parts.Mask, outputs: [Parts.InstanceMasks]),
            Pass(inputs: [Parts.InstanceMasks], name: Parts.Bake, outputs: [Parts.BakeSamples]),
            ShadowRaster(name: Parts.ShadowNear, depth: Parts.ShadowNearDepth),
            ShadowRaster(name: Parts.ShadowFar, depth: Parts.ShadowFarDepth),
            ShadowRaster(name: Parts.ShadowSecond, depth: Parts.ShadowSecondDepth),
            Raster(name: Parts.Mesh, target: Parts.MeshTarget, depth: Parts.MeshDepth),
            Pass(inputs: [Parts.MeshTarget], name: Parts.Pyramid, outputs: [Parts.MeshPyramid]),
            // The surface pass reads the bake samples it never touches so the bake pass, whose samples only the host reads
            // back, stays live in the planned graph.
            Pass(
                inputs: [Parts.MeshTarget, Parts.MeshPyramid, Parts.ShadowNearDepth, Parts.ShadowFarDepth, Parts.ShadowSecondDepth, Parts.BakeSamples],
                name: Parts.Surface,
                outputs: [Parts.Visibility]
            ),
            Pass(inputs: [Parts.Visibility], name: Parts.Views, outputs: [Parts.Lit]),
            Pass(inputs: [Parts.Lit], name: Parts.Sky, outputs: SkyRuns) with { Members = SkyMembers },
            Pass(inputs: [Parts.Lit, Parts.Visibility, .. SkyRuns], name: Parts.Composite, outputs: [Color]) with { Members = SkyMembers },
        ],
        Resources: [
            Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: Parts.Lit, retained: true),
            .. SkyResources,
            Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: Color, retained: false),
            Buffer(
                count: [Term(MeshBakeBatch, ShaderPipelineCountBasis.InstanceMaskWords)],
                name: Parts.InstanceMasks,
                sizeBytes: null,
                strideBytes: sizeof(uint)
            ),
            Buffer(
                count: null,
                name: Parts.BakeSamples,
                sizeBytes: (((ulong)MeshBakeMaxSamples) * MeshBakeSampleWords * sizeof(uint)),
                strideBytes: sizeof(uint)
            ),
            ShadowDepth(name: Parts.ShadowNearDepth, texels: MeshShadowResolution),
            ShadowDepth(name: Parts.ShadowFarDepth, texels: MeshShadowResolution),
            ShadowDepth(name: Parts.ShadowSecondDepth, texels: MeshShadowResolution),
            Image(format: MeshTargetFormat, from: null, name: Parts.MeshTarget, retained: true),
            // Two words a pyramid texel; the levels one up hold under a third of the extent's texels, with their tiles'
            // padding, and the kernels bound every level by the buffer's length.
            Buffer(
                count: [Term(1, ShaderPipelineCountBasis.Extent)],
                name: Parts.MeshPyramid,
                sizeBytes: null,
                strideBytes: sizeof(uint)
            ),
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: MeshDepthFormat.ToString(),
                ClearDepth: MeshClearDepth,
                Kind: ShaderPipelineResourceKind.Depth,
                Name: Parts.MeshDepth,
                Retained: true
            ),
            new ShaderPipelineResource(
                Count: [Term(1, ShaderPipelineCountBasis.Extent, ShaderPipelineCountBasis.Viewports)],
                Kind: ShaderPipelineResourceKind.Buffer,
                Name: Parts.Visibility,
                StrideBytes: VisibilityRecordByteLength,
                Retained: true
            ),
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
    // A compute pass, whose kernel counts its own work as every pass of the fragment does.
    private static RenderGraphFragmentPass Pass(string name, string[] outputs, string[]? inputs = null) => new(
        CountsKernelWork: true,
        InputAccesses: [.. (inputs ?? []).Select(selector: static _ => RenderGraphPortAccess.ComputeRead)],
        Inputs: [.. (inputs ?? []).Select(selector: static input => new ResourceReference(Name: input))],
        Name: name,
        OutputAccesses: [.. outputs.Select(selector: static _ => RenderGraphPortAccess.ComputeWrite)],
        Outputs: [.. outputs.Select(selector: static output => new ResourceReference(Name: output))]
    );
    // A raster pass drawing the frame's mesh draws through the mesh pipeline into its target and depth: the view's mesh
    // pass, or a shadow cascade's from the cascade's light camera.
    private static RenderGraphFragmentPass Raster(string name, string target, string depth) => new(
        CountsKernelWork: true,
        InputAccesses: [],
        Inputs: [],
        Name: name,
        OutputAccesses: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite],
        Outputs: [new ResourceReference(Name: target), new ResourceReference(Name: depth)]
    );
    // A shadow cascade's raster pass, drawing the frame's mesh draws through the shadow pipeline into the cascade's depth
    // alone.
    private static RenderGraphFragmentPass ShadowRaster(string name, string depth) => new(
        CountsKernelWork: true,
        InputAccesses: [],
        Inputs: [],
        Name: name,
        OutputAccesses: [RenderGraphPortAccess.ColorAttachmentWrite],
        Outputs: [new ResourceReference(Name: depth)]
    );
    // A cascade's depth, texels a side, which its raster pass writes and the surface pass samples.
    private static ShaderPipelineResource ShadowDepth(string name, int texels) => new(
        ClearDepth: MeshClearDepth,
        Dimensions: ShaderPipelineDimensions.Absolute(height: texels, width: texels),
        Format: MeshDepthFormat.ToString(),
        Kind: ShaderPipelineResourceKind.Depth,
        Name: name,
        Retained: true
    );

    /// <summary>The names of the fragment's passes and versions.</summary>
    public static partial class Parts {
        /// <summary>The mesh bake's instance-mask pass, selecting each bake lattice's instances.</summary>
        public const string Mask = "mask";
        /// <summary>The bake pass, sampling one instance group's field on a lattice for the host to mesh.</summary>
        public const string Bake = "bake";
        /// <summary>The bake samples, copied back to the host after the bake pass.</summary>
        public const string BakeSamples = "bakeSamples";
        /// <summary>The near shadow cascade's raster pass.</summary>
        public const string ShadowNear = "shadow-near";
        /// <summary>The far shadow cascade's raster pass.</summary>
        public const string ShadowFar = "shadow-far";
        /// <summary>The second shadow slot's cascade raster pass.</summary>
        public const string ShadowSecond = "shadow-second";
        /// <summary>The near cascade's depth: per texel the reversed-Z orthographic depth of the nearest caster, zero (the
        /// far plane) where none was drawn.</summary>
        public const string ShadowNearDepth = "shadowNearDepth";
        /// <summary>The far cascade's depth.</summary>
        public const string ShadowFarDepth = "shadowFarDepth";
        /// <summary>The second slot's cascade depth.</summary>
        public const string ShadowSecondDepth = "shadowSecondDepth";
        /// <summary>The mesh pass drawing the frame's mesh draws into the mesh visibility target.</summary>
        public const string Mesh = "mesh";
        /// <summary>The pyramid pass, writing the mesh depth pyramid the surface pass's occlusion reads from the mesh
        /// target.</summary>
        public const string Pyramid = "pyramid";
        /// <summary>The mesh depth pyramid (<see cref="SdfWorldPackage.MeshPyramid"/>).</summary>
        public const string MeshPyramid = "meshPyramid";
        /// <summary>The surface pass, writing every pixel's visibility record from the mesh target, the screen-space
        /// occlusion and the shadow cascades.</summary>
        public const string Surface = "surface";
        /// <summary>Shading the visibility records into the lit image.</summary>
        public const string Views = "views";
        /// <summary>The sky's field runs, evaluated where the lit image's coverage is below one.</summary>
        public const string Sky = "sky";
        /// <summary>The composite: the sky's runs in their authored order, the lit image over them by its coverage, then
        /// the fog and the bounded media, into the view's color.</summary>
        public const string Composite = "composite";
        /// <summary>The lit image: the hits' shaded color premultiplied by coverage and by the fog's transmittance over each
        /// hit's ray distance, the coverage in its alpha (one for a hit, zero on a miss), written by views in a native view
        /// and, at the output extent, by the resolve in a reduced or temporal one.</summary>
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
        /// <summary>The bake lattices' instance masks.</summary>
        public const string InstanceMasks = "instanceMasks";
        /// <summary>The mesh visibility target.</summary>
        public const string MeshTarget = "meshTarget";
        /// <summary>The mesh pass's depth attachment.</summary>
        public const string MeshDepth = "meshDepth";
        /// <summary>The visibility records the surface pass writes.</summary>
        public const string Visibility = "visibility";
        /// <summary>The reactivity buffer views writes where it shades and the resolve reads.</summary>
        public const string Reactivity = "reactivity";
        /// <summary>The history color: the resolved color, one image a frame slot, read by the next frame.</summary>
        public const string HistoryColor = "historyColor";
        /// <summary>The history surface: each output pixel's identity, ray distance, gathered weight and accumulated
        /// transport, read by the next frame.</summary>
        public const string HistorySurface = "historySurface";
    }
}
