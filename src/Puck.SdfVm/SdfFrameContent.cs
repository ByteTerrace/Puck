using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>What changes from one frame to the next in an <see cref="SdfFrame"/>: the program it carries, its views and
/// clocks, the borrowed tables of moving entities and draws, and the lighting resolved at its presented tick. A producer
/// writes the whole value each frame (<see cref="SdfFrame.Writer.Write(in SdfFrameContent, in SdfFrameSettings)"/>), so a
/// value it does not set this frame takes its default rather than standing from the last; the slow-changing part is
/// <see cref="SdfFrameSettings"/>. A struct, so composing it allocates nothing.</summary>
/// <param name="Program">The SDF program to render.</param>
/// <param name="ProgramChanged">Whether the renderer must upload <paramref name="Program"/> for this frame.</param>
/// <param name="Views">The camera views to render, each into its own output.</param>
/// <param name="Time">The presentation time in seconds.</param>
public readonly record struct SdfFrameContent(SdfProgram Program, bool ProgramChanged, IReadOnlyList<SdfViewSnapshot> Views, float Time) {
    private readonly SdfMovedTransforms? m_movedTransforms;

    /// <summary>Per-frame transforms for the scene's moving entities, indexed by dynamic-transform slot. Must supply
    /// at least the program's <see cref="SdfProgram.RequiredDynamicTransformCapacity"/> entries (the render frame
    /// throws otherwise — a dynamic slot silently rendering at identity is a bug, not a default); empty is therefore
    /// valid only for a program with no dynamic slots (the renderer then binds a single identity slot the program
    /// never references). Updating this list is how entities move — the program (binding 1) is uploaded once and left
    /// untouched.</summary>
    public IReadOnlyList<DynamicTransform> DynamicTransforms { get; init; } = [];
    /// <summary>The moved set of <see cref="DynamicTransforms"/>: which slots changed in each recent frame of the
    /// table's producer. An engine stages only the rows moved since the frame it last consumed from this producer,
    /// and every row when it has none of this producer's history. <see langword="null"/>, the default, declares the
    /// table static: an engine stages it whole the first time it sees that table instance and never again, so a
    /// producer whose transforms move must supply its moved set.</summary>
    public SdfMovedTransforms? MovedTransforms {
        get => m_movedTransforms;
        init {
            m_movedTransforms = value;
            MovedTransformsSerial = (value?.Serial ?? 0);
        }
    }
    /// <summary>Gets the moved-set endpoint captured with this frame's borrowed transform table.</summary>
    public long MovedTransformsSerial { get; private init; }
    /// <summary>The views of this frame's world that another residency renders: the same world, in the same coordinates,
    /// shown elsewhere in the presentation (the world a session screen's scene is a second scene of). They render
    /// nothing here and are demand points alone (<see cref="SdfMeshDemand"/>), so this residency plans each group at
    /// the tier the finest of both demands asks, which is the tier the other residency draws: the two then name the
    /// same meshes, which the presentation's mesh region holds once (<see cref="SdfMeshRegionStore"/>). Each carries the
    /// height its sizes are measured in (<see cref="SdfViewSnapshot.DemandHeight"/>), since its region places it in
    /// the other residency's output. Empty, the default, for a frame whose world no other residency shows.</summary>
    public IReadOnlyList<SdfViewSnapshot> DemandViews { get; init; } = [];
    /// <summary>What names the world this frame draws among the presentation's residencies, compared by reference: the
    /// residency that draws the world for the presentation (a frame with no <see cref="DemandViews"/>) and each that
    /// draws a second scene of it (a frame that carries them) hold the same object. A second scene's bake draws each
    /// group at the tier the first draws it at (<see cref="SdfMeshLibrary.TiersOf"/>), whatever its own budget would
    /// choose, since the first's mesh is already placed and a tier of its own would be placed beside it.
    /// <see langword="null"/>, the default, for a frame no other residency shares a world with.</summary>
    public object? DemandWorld { get; init; }
    /// <summary>The frame's bounded flow and cloud volumes, at most
    /// <see cref="SdfWorldTables.MaxVolumes"/>; submission refuses a list beyond that capacity.
    /// Packed into its own structured buffer (never <c>sdfScreenLights</c>) and shaded by <c>shade-volumes.hlsli</c>'s
    /// one call site at the end of the views stage (<c>sdfViewsStage</c>), after the surface color is final. Empty (the default) uploads an
    /// all-zero table whose first bound ends the shader's scan.</summary>
    public IReadOnlyList<SdfVolume> Volumes { get; init; } = [];
    /// <summary>The frame's opaque triangle meshes: every placement and stamp of a prototype that carries a mesh. Empty by
    /// default; rasterized per view before primary traversal. A residency's tables repack them when the list is a different one or
    /// <see cref="MeshDrawsRevision"/> moved, so a producer that reuses one list rewrites it in place and moves the
    /// revision.</summary>
    public IReadOnlyList<SdfMeshDraw> MeshDraws { get; init; } = [];
    /// <summary>The revision of <see cref="MeshDraws"/>' content, which a producer that rewrites one list in place moves
    /// whenever it does; 0 for a producer that supplies a new list instead. It is the host's input: dressing leaves it
    /// as the producer set it.</summary>
    public long MeshDrawsRevision { get; init; }
    /// <summary>Gets the producer's bounded draw-row changes, or null when a consumer must compare the revised list.
    /// The list and journal are borrowed together until their producer next rewrites them. It is the host's input:
    /// dressing leaves it as the producer set it, and the dressed rows keep their own journal
    /// (<see cref="SdfMeshRows.Changes"/>).</summary>
    public GpuRowChanges? MeshDrawChanges { get; init; }
    /// <summary>Gets the journal endpoint captured with <see cref="MeshDraws"/>. The dresser collects only through this
    /// serial and retains its own rows when a later producer frame rewrites the borrowed list.</summary>
    public long MeshDrawChangesSerial { get; init; }
    /// <summary>Gets the slot ranges drawn on both sides of a portal this frame (<see cref="SdfSlotClip"/>): the frame's
    /// producer fills it from the stamp pools whose slots the frame carries (a composition from every emitter's
    /// <see cref="ISdfSceneEmitter.SlotClips"/>), and the mesh bake's dressing clips each draw in a range and adds its
    /// carried copy through <see cref="SdfMeshRows.Writer.SetClips"/>. Empty when no body straddles a portal.</summary>
    public IReadOnlyList<SdfSlotClip> SlotClips { get; init; } = [];
    /// <summary>The lit path's lights table, its shadow slots and its curvature shading, or <see langword="null"/>, the
    /// default, for the pinned sun an unauthored world renders (<see cref="SdfFrame.Lights"/>).</summary>
    public SdfLights? Lights { get; init; }
    /// <summary>The sky: its open layer stack, fog and environment gains, or <see langword="null"/>, the default, for the
    /// default look an unauthored world renders: the two-stop gradient and the default fog, as data
    /// (<see cref="SdfFrame.Sky"/>).</summary>
    public SdfSky? Sky { get; init; }
    /// <summary>The camera exposure every exposed view of the frame maps the luminance it shades to the display by
    /// (<c>render.exposure</c>). The default is the manual exposure an unauthored world renders at.</summary>
    public SdfExposure Exposure { get; init; } = SdfExposure.Default;
    /// <summary>Each dynamic-transform slot's <see cref="SdfUpdateRateCode"/>, indexed by slot, which a residency showing
    /// a tier view (<see cref="DebugViewModes.ShowsRates"/>) packs into the slot's position row; a slot past the end
    /// packs <see cref="SdfUpdateRateCode.None"/>. Empty, the default, tints nothing.</summary>
    public ReadOnlyMemory<byte> SlotRateCodes { get; init; }
    /// <summary>Each screen's <see cref="SdfUpdateRateCode"/>, indexed by screen, which a residency showing a tier view
    /// binds into the screen's record; a screen past the end binds <see cref="SdfUpdateRateCode.None"/>. Empty, the
    /// default, tints nothing.</summary>
    public ReadOnlyMemory<byte> ScreenRateCodes { get; init; }
    /// <summary>Whether presentation uses completed simulation ticks and the frame's own time for exposure adaptation,
    /// rather than the host interval. Lighting convergence is an independent request through
    /// <see cref="ConvergesLighting"/>. Default <see langword="false"/>.</summary>
    public bool TickLocked { get; init; }
    /// <summary>Whether the renderer finishes this frame's scene, irradiance-window trace and lighting feedback before
    /// rendering it: the work runs off the frame thread, and the frame is held, its residency answering waiting with where
    /// the work stands (<see cref="SdfMeshIrradiance.Convergence"/>), until it lands. False retains ordinary budgeted
    /// asynchronous lighting at the same quality settings. Independent of clock pinning: a tick-locked throughput run need
    /// not hold every tick for its indirect light.</summary>
    public bool ConvergesLighting { get; init; }
    /// <summary>Gets the presented engine tick the sky and the bounded media animate on: the star-twinkle phase, the
    /// cloud layer's drift, shear and spin, and each medium's advection and pulse, all reduced on the host (the sky
    /// block, <see cref="SdfSkyBlock"/>, and the volume table), so no pass reads a raw tick. It comes from the
    /// state mirror of the world the frame draws, never from <see cref="Time"/>, which advances by wall-clock
    /// deltas, so a frame at a given tick and fraction draws the same sky and media on every run.</summary>
    public PresentedTick Clock { get; init; }
    /// <summary>Gets the world's four render lanes (<see cref="SdfLanes"/> 4 to 7), which every surface reads alike: the
    /// host resolves them from the world's state at the frame's presented tick, as it resolves the sky's bindings, and the
    /// pass block carries them (<c>SdfWorldPackage.WorldLanes</c>). Zero where the world authors none.</summary>
    public System.Numerics.Vector4 WorldLanes { get; init; }
}
