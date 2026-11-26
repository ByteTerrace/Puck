using Puck.Abstractions.Collections;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Describes one viewport slot of an SDF frame: an SDF camera render into the view's own output image, and the
/// normalized display region a host places that output in.</summary>
/// <param name="Camera">The camera used to render the view.</param>
/// <param name="Region">The view's normalized display region, which sizes its output when no host asks for an
/// extent (the render graph's scheduled extent).</param>
public readonly record struct SdfViewSnapshot(CameraSnapshot Camera, NormalizedRect Region) {
    /// <summary>The camera's resolved optical scatter; absent on intermediate transport views.</summary>
    public CameraBloom? Bloom { get; init; }
    /// <summary>The resolved cost gate. Off allocates no pyramid.</summary>
    public BloomQuality BloomQuality { get; init; }
    /// <summary>A screen-door fade of one stamp's transform-slot range in this view only. Default draws every stamp.</summary>
    public SdfStampFade StampFade { get; init; }
    /// <summary>A pose of one stamp drawn in this view only: its transform slots read their rows from the overlay's rows.
    /// Default reads every slot's own row.</summary>
    public SdfPoseOverlay PoseOverlay { get; init; }
    /// <summary>Gets the audience whose disclosure this view draws (<see cref="SdfMeshAudience"/>): zero, the default, for
    /// the residency's own viewers, otherwise a viewer of another disclosure, whose view selects the neutral draws and
    /// those tagged for its audience alone.</summary>
    public byte Audience { get; init; }

    /// <summary>The infinity layers fitted to this consumer's camera, identified by their authored names.
    /// Their producer names identify existing render-graph outputs; the sky recorder binds them under the frame's
    /// graph-read leases. Empty means no fitted infinity image. The residency's authored sky remains shared.</summary>
    public IReadOnlyList<SdfSkyViewBinding> SkyViews { get; init; } = [];

    /// <summary>The camera's cut revision, moved when its framing is reseeded or a layout slot changes its source.</summary>
    public long CutRevision { get; init; }
    /// <summary>The object whose view the slot shows (a seat's camera rig, a named camera, a screen's camera
    /// registration), the same object for as long as the slot shows that view, or <see langword="null"/> when the cut
    /// revision alone tells the slot's views apart. A residency's per-view state keyed by slot (an exposure and the
    /// readings in flight for it) starts a new generation when another source or cut takes the slot, so state one view
    /// left never lands on another (<see cref="SdfViewExposures.GenerationOf"/>).</summary>
    public object? Source { get; init; }
    // Readings belong to the same view only while its source object and cut revision both stand.
    internal ReferenceRevision Cut => new(Source, CutRevision);

    /// <summary>The pixels high the mesh demand measures the view's projected sizes in (<see cref="SdfMeshDemand"/>)
    /// when the view renders into an image apart from the residency's output: a camera filmed for a screen its own
    /// image's height, and a window fitted to its glass the display pixels the glass spans from the viewer's eye, since
    /// its image is the viewer's own view carried through the glass. Zero, the default, for a view whose region places
    /// it in the residency's output.</summary>
    public float DemandHeight { get; init; }
    /// <summary>Whether the view demands meshes only inside its frustum (<see cref="SdfMeshDemand"/>): a window fitted
    /// to its glass, whose frustum is the aperture from the viewer's eye however the viewer turns, so nothing beside or
    /// before the glass can enter its image without the viewer walking. <see langword="false"/>, the default, for a view
    /// that can turn to anything about its eye within a frame, which demands by distance alone.</summary>
    public bool DemandWithinView { get; init; }
    /// <summary>The part of the glass a window's image spans when its frustum is fitted to the part of the glass its
    /// viewer sees: the rectangle's left, top, right and bottom in the glass's image coordinates, each in [0, 1]. The
    /// views that sample the image map it onto that rectangle (<see cref="SdfWorldPasses"/>), whatever frame it was
    /// rendered in. Zero, the default, and the unit rectangle span the whole glass.</summary>
    public System.Numerics.Vector4 ImageWindow { get; init; }

    /// <summary>The render-scale ceiling in (0, 1], quantized by <see cref="RenderGraphExtent"/> into
    /// <see cref="RenderCeiling"/>. Scratch is allocated at that fraction of the view's output extent; its published
    /// output keeps its full extent. Zero or less reads as native.</summary>
    public float RenderScale { get; init; } = 1f;

    /// <summary>This frame's render scale inside the ceiling, quantized by <see cref="RenderGraphExtent"/> and bounded by
    /// <see cref="RenderCeiling"/> into <see cref="RenderGrid"/>. Zero uses the ceiling. It moves the render grid and
    /// nothing else: no allocation, fragment or graph follows it, so it can change every frame (a layout transition's
    /// dip). A view whose ceiling is native reconstructs nothing, so it renders its output grid and ignores this.</summary>
    public float ResolvedRenderScale { get; init; }
    /// <summary>The spatial reconstruction sharpness, from zero for bilinear to one for clamped Catmull-Rom.</summary>
    public float UpscaleSharpness { get; init; }
    /// <summary>Gets the allocation ceiling as a fraction of the output on each axis: <see cref="RenderScale"/>
    /// quantized, one for a scale of zero or less. It alone chooses whether the view reconstructs
    /// (<see cref="Reconstructs"/>) and sizes its scratch.</summary>
    public double RenderCeiling => RenderGraphExtent.Quantize(fraction: ((RenderScale > 0f) ? RenderScale : 1f));
    /// <summary>Gets whether the view renders a grid below its output and reconstructs it: exactly when
    /// <see cref="RenderCeiling"/> is below one.</summary>
    public bool Reconstructs => (RenderCeiling < 1d);
    /// <summary>Gets this frame's render grid as a fraction of the output on each axis: one for a view that does not
    /// reconstruct, otherwise <see cref="ResolvedRenderScale"/> quantized and bounded by <see cref="RenderCeiling"/>, or
    /// the ceiling when that is zero or less.</summary>
    public double RenderGrid => (!Reconstructs
        ? 1d
        : ((ResolvedRenderScale > 0f)
            ? Math.Min(val1: RenderGraphExtent.Quantize(fraction: ResolvedRenderScale), val2: RenderCeiling)
            : RenderCeiling));

    /// <summary>The editor grid the view draws, which its pass block carries (<see cref="SdfFrameBlock"/>); a view that
    /// draws none carries <see cref="GridOverlayState.Hidden"/>.</summary>
    public GridOverlayState Grid { get; init; } = GridOverlayState.Hidden;

    /// <summary>The quality the view renders at. Each view's pass block carries its own, so views of one frame, and of
    /// one residency, render the same scene at different cost. The default is full quality.</summary>
    public SdfViewQuality Quality { get; init; }
    /// <summary>The view's screen fidelity, from 0 to 1, which its pass block carries
    /// (<c>SdfWorldPackage.ScreenFidelity</c>): the share of a screen's own pixels that reaches the view untouched, past
    /// a screen material's treatment (scanlines, subpixel stripes, vignette, glint and edge mask), and past camera bloom and the world's atmosphere. A seat
    /// framing the screen it engaged carries its ease's weight; the default, 0, is the in-world look.</summary>
    public float ScreenFidelity { get; init; }
    /// <summary>Whether the view publishes the luminance it shades for a window: a window's view of the world beyond its
    /// glass, or a sky layer's view of another world, whose consumer shows it in its own scene. Such a view applies the
    /// window share of its exposure (<see cref="SdfPhotometry.WindowAdaptation"/>) and the consumer the rest of its own,
    /// so the window shows the world beyond at an exposure between the two. The default, <see langword="false"/>,
    /// exposes the view by the frame's <see cref="SdfFrame.Exposure"/> whole, and its display pass converts it for the
    /// display.</summary>
    public bool Unexposed { get; init; }
    /// <summary>Whether another residency renders the view: a seat presented in another world keeps its view's place
    /// among its own world's views while that world's scene renders it, its camera in that world's coordinates. The
    /// frame's own residency draws nothing for such a view: its irradiance window, which follows the first view, holds
    /// where it stood while that view is such a view, and it meters no exposure for one. The default,
    /// <see langword="false"/>, is a view the frame's residency renders.</summary>
    public bool Elsewhere { get; init; }
}
/// <summary>A view-local stamp fade. The shared transforms, mirrors, portal views and shadow casters remain intact.</summary>
/// <param name="FirstSlot">The stamp's first dynamic-transform slot.</param>
/// <param name="SlotCount">The number of slots owned by the stamp; zero disables fading.</param>
/// <param name="Opacity">The screen coverage, from zero (hidden) to one (solid).</param>
public readonly record struct SdfStampFade(int FirstSlot, int SlotCount, float Opacity);
/// <summary>A view-local pose overlay: the slots of one stamp read their dynamic-transform rows from another range of
/// the same table, which holds that stamp posed for this view alone (<c>frame/sdf-mesh-skin.hlsli</c>,
/// <c>sdfViewPoseSlot</c>). Every reader of the shared rows (the other views, mirrors and portals, the host's anchors and
/// picking) reads the stamp's own rows; only the draws this view rasterizes, and the shading of their pixels, read the
/// overlay.</summary>
/// <param name="FirstSlot">The stamp's first dynamic-transform slot.</param>
/// <param name="SlotCount">The number of slots the overlay replaces; zero disables it.</param>
/// <param name="OverlaySlot">The first row of the overlay's range, which holds the replacement for
/// <paramref name="FirstSlot"/> and the rows after it in order.</param>
public readonly record struct SdfPoseOverlay(int FirstSlot, int SlotCount, int OverlaySlot);
/// <summary>One infinity layer's fitted sampling parameters and the render-graph image this consumer reads.</summary>
/// <param name="Layer">The infinity layer's unique authored name.</param>
/// <param name="Parameters">The sampling basis and rectangle fitted to this consumer. The sky retains the
/// authored fallback, intensity and coverage.</param>
/// <param name="Producer">The instance whose completed output supplies the image, or null while the layer draws its fallback.</param>
public readonly record struct SdfSkyViewBinding(string Layer, SdfSkyView Parameters, string? Producer);
/// <summary>The quality levers one view renders with: each trades a shading term's cost against its fidelity. The
/// default is full quality, every term on, and no temporal reconstruction. The pass block carries each shading lever
/// under its own name (<see cref="SdfFrameBlock"/>), and the view's cadence signature folds it, so a change renders the
/// view again; <see cref="Temporal"/> chooses the view's fragment instead.</summary>
public readonly record struct SdfViewQuality {
    /// <summary>The sky field's grid fraction: one or one half; zero selects the full default.
    /// It changes neither the scene render grid nor retained field allocation capacity.</summary>
    public float SkyFieldScale { get; init; }
    /// <summary>Gets the validated sky field fraction, with the default zero reading as one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The field scale is neither zero, one nor one half.</exception>
    public float SkyFieldFraction => SkyFieldScale switch {
        0f or 1f => 1f,
        .5f => .5f,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(SkyFieldScale)),
    };
    /// <summary>Publishes only geometry, with premultiplied color and its coverage in alpha, for a far sky layer.
    /// The composite omits background sky and air on uncovered rays. Restrictions retain the consumer's output form.</summary>
    public bool GeometryOnly { get; init; }
    /// <summary>Gets whether the view skips ambient occlusion: the surface pass writes an occlusion of one, so creases
    /// read brighter. The pass block carries it as <c>disableAmbientOcclusion</c>.</summary>
    public bool DisableAmbientOcclusion { get; init; }
    /// <summary>Gets whether the view skips shadows: the shadow cascades do not render and every light goes unshadowed.
    /// The pass block carries it as <c>disableSoftShadows</c>.</summary>
    public bool DisableSoftShadows { get; init; }
    /// <summary>Gets the texels along each side of the view's shadow cascades while it renders shadows: its fragment
    /// allocates cascades of that size, so a change rebuilds the view's graph. The default is
    /// <see cref="SdfShadowResolution.Full"/>.</summary>
    public SdfShadowResolution ShadowResolution { get; init; }
    /// <summary>Gets whether the view asks for temporal reconstruction: it runs the temporal fragment
    /// (<c>SdfWorldPackage.TemporalFragment</c>), jitters its samples and resolves them over its history into its
    /// output, at native or reduced render scale. Its costs are the resolve's dispatch and the history's bytes and
    /// barriers. The default asks for none.</summary>
    public bool Temporal { get; init; }

    /// <summary>Returns this quality with another's restrictions added: a term either skips stays skipped, the cascades
    /// take the coarser of the two resolutions, and the view reconstructs over time only when both ask. Neither can lift a
    /// restriction the other set.</summary>
    /// <param name="other">The restrictions to add.</param>
    /// <returns>The restricted quality.</returns>
    public SdfViewQuality Restrict(in SdfViewQuality other) => new() {
        SkyFieldScale = MathF.Min(x: SkyFieldFraction, y: other.SkyFieldFraction),
        GeometryOnly = GeometryOnly,
        DisableAmbientOcclusion = (DisableAmbientOcclusion || other.DisableAmbientOcclusion),
        DisableSoftShadows = (DisableSoftShadows || other.DisableSoftShadows),
        ShadowResolution = ((SdfShadowResolution)Math.Max(val1: ((int)ShadowResolution), val2: ((int)other.ShadowResolution))),
        Temporal = (Temporal && other.Temporal),
    };
}
/// <summary>Contains the scene program and presentation state consumed by one SDF render frame: its per-frame
/// <see cref="Content"/> and its slow-changing <see cref="Settings"/>, both held by value, the geometry its composition
/// publishes beside the program, and the rows the mesh dresser dressed it into.</summary>
/// <remarks>A producer that captures every frame owns one frame through its <see cref="Writer"/> and writes it again for
/// each capture, so capturing allocates nothing; only the writer's holder can change it, and no consumer can. A frame a
/// source returns is borrowed until the source's next capture: a consumer that keeps it longer copies it into a frame of
/// its own (<see cref="Writer.CopyFrom(SdfFrame)"/>), as <see cref="SdfWorldResidency"/> does for the frame it renders.
/// The constructor and the init accessors make a frame of its own, for a one-off frame or a test.</remarks>
/// <param name="Program">The SDF program to render.</param>
/// <param name="ProgramChanged">Whether the renderer must upload <paramref name="Program"/> for this frame.</param>
/// <param name="Views">The camera views to render, each into its own output.</param>
/// <param name="Time">The presentation time in seconds.</param>
public sealed record SdfFrame(
    SdfProgram Program,
    bool ProgramChanged,
    IReadOnlyList<SdfViewSnapshot> Views,
    float Time
) {
    // Everything a frame carries is in these fields, so the writer resets and a copy carries all of it by assigning them:
    // a new value goes in the content, the settings or the composition, never in a field of its own beside them.
    private SdfFrameContent m_content = new(Program: Program, ProgramChanged: ProgramChanged, Views: Views, Time: Time);
    private SdfFrameSettings m_settings = new();
    private Composition m_composition;
    // The dressing a residency's dresser adopted onto its own frame; never copied, since the rows are that dresser's.
    private SdfMeshRows? m_meshRows;
    private ISdfPickMap? m_dressedPickMap;
    // The defaults this frame built (Lights, Sky), kept so a writer's frame whose producer supplies none builds each once
    // rather than on every write. The content, not these, carries a default once it is read.
    private SdfLights? m_defaultLights;
    private SdfSky? m_defaultSky;

    // The geometry and identities the composition publishes beside the program's (Compose, PickMap, ProgramDelta).
    private struct Composition {
        public ISdfPickMap? PickMap;
        public IReadOnlyList<int>? MeshAssetSlots;
        public IReadOnlyList<SdfMeshInstance>? MeshInstances;
        public IReadOnlyList<SdfMeshTemplateRange>? MeshTemplateRanges;
        public SdfSampledSet? Sampled;
        public GpuRowChanges? MeshCopyChanges;
        public long MeshCopyChangesSerial;
        public SdfProgramDelta? ProgramDelta;
    }

    /// <summary>Gets the frame's per-frame content, which a producer writes whole each frame.</summary>
    public SdfFrameContent Content => m_content;
    /// <summary>Gets the frame's slow-changing settings, which a producer carries from frame to frame.</summary>
    public SdfFrameSettings Settings {
        get => m_settings;
        init => m_settings = value;
    }

    /// <inheritdoc cref="SdfFrameContent.Program"/>
    public SdfProgram Program {
        get => m_content.Program;
        init => m_content = (m_content with { Program = value });
    }
    /// <inheritdoc cref="SdfFrameContent.ProgramChanged"/>
    public bool ProgramChanged {
        get => m_content.ProgramChanged;
        init => m_content = (m_content with { ProgramChanged = value });
    }
    /// <inheritdoc cref="SdfFrameContent.Views"/>
    public IReadOnlyList<SdfViewSnapshot> Views {
        get => m_content.Views;
        init => m_content = (m_content with { Views = value });
    }
    /// <inheritdoc cref="SdfFrameContent.Time"/>
    public float Time {
        get => m_content.Time;
        init => m_content = (m_content with { Time = value });
    }

    /// <summary>Gets the identity table that resolves this frame's visibility identities. Composition fills the host's
    /// table before publishing the frame; a frame the mesh dresser dressed answers its dressed table, which resolves
    /// retained row addresses to the host's identities. It is CPU-only presentation data and is never uploaded to the
    /// frame block.</summary>
    public ISdfPickMap? PickMap {
        get => (m_dressedPickMap ?? m_composition.PickMap);
        internal set => m_composition.PickMap = value;
    }

    /// <summary>Gets the identity table the host composed, which the dresser extends; a dressed frame keeps it
    /// unchanged.</summary>
    internal ISdfPickMap? HostPickMap => m_composition.PickMap;

    /// <summary>Adopts the draw rows the mesh dresser retains and the pick map that resolves them, which the dresser owns
    /// and keeps current: the frame references them and the host's inputs stay as they were, so dressing the same frame
    /// again reads them unchanged.</summary>
    /// <param name="rows">The dresser's retained rows.</param>
    /// <param name="picks">The pick map over <paramref name="rows"/>, or <see langword="null"/> when the host composed none.</param>
    internal void AdoptDressing(SdfMeshRows rows, ISdfPickMap? picks) {
        m_meshRows = rows;
        m_dressedPickMap = picks;
    }

    /// <summary>Sets the geometry the composition publishes beside the program's: the baked template slots, the rigid
    /// copies, the sampled-field snapshots and the slot ranges drawn on both sides of a portal. Composition calls it once
    /// for the frame it was dressed into.</summary>
    /// <param name="meshAssetSlots">The baked template slots.</param>
    /// <param name="meshInstances">The copies of local geometry assets.</param>
    /// <param name="meshTemplateRanges">The instance ranges that hold static templates.</param>
    /// <param name="sampled">The snapshots the program's sampled fields hold this frame.</param>
    /// <param name="slotClips">The slot ranges every emitter's stamp pool draws on both sides of a portal.</param>
    /// <param name="copyChanges">The journal of the copies that changed in <paramref name="meshInstances"/>, or
    /// <see langword="null"/> when a consumer must compare the whole list.</param>
    /// <param name="copyChangesSerial">The journal's endpoint captured with <paramref name="meshInstances"/>.</param>
    internal void Compose(IReadOnlyList<int> meshAssetSlots, IReadOnlyList<SdfMeshInstance> meshInstances, IReadOnlyList<SdfMeshTemplateRange> meshTemplateRanges, SdfSampledSet sampled,
        IReadOnlyList<SdfSlotClip> slotClips, GpuRowChanges? copyChanges = null, long copyChangesSerial = 0) {
        m_composition.MeshAssetSlots = meshAssetSlots;
        m_composition.MeshInstances = meshInstances;
        m_composition.MeshTemplateRanges = meshTemplateRanges;
        m_composition.Sampled = sampled;
        m_composition.MeshCopyChanges = copyChanges;
        m_composition.MeshCopyChangesSerial = copyChangesSerial;
        m_content = (m_content with { SlotClips = slotClips });
    }
    /// <summary>Gets the producer's journal of the rigid copies that changed in <see cref="MeshInstances"/> (another copy
    /// at an index, the copy at an index moved to another frame slot, or a copy whose <see cref="SdfMeshInstance.Revision"/>
    /// moved), or <see langword="null"/> when a consumer must compare the whole list. A frame whose template slots or copy
    /// count changed owes the whole list. The list and journal are borrowed together until their producer next rewrites
    /// them, so a consumer reads them while it dresses the frame (<see cref="SdfMeshDresser.Dress"/>), each through its own
    /// <see cref="GpuRowCursor"/>.</summary>
    public GpuRowChanges? MeshCopyChanges => m_composition.MeshCopyChanges;
    /// <summary>Gets the copy journal's endpoint captured with <see cref="MeshInstances"/>.</summary>
    public long MeshCopyChangesSerial => m_composition.MeshCopyChangesSerial;
    /// <summary>Gets the words <see cref="Program"/> differs in from the program built before it, which tables holding
    /// that earlier program upload alone, or <see langword="null"/> when its builder measured none.</summary>
    public SdfProgramDelta? ProgramDelta {
        get => m_composition.ProgramDelta;
        internal set => m_composition.ProgramDelta = value;
    }
    /// <summary>Gets the snapshots <see cref="Program"/>'s sampled fields hold this frame, by handle
    /// (<see cref="SdfProgram.SampledFields"/>): the producer's capture (<see cref="SdfSampledSet.Capture"/>), or the
    /// program's own (<see cref="SdfProgram.Sampled"/>) when it set none. A field publishing new values moves this set and
    /// no program word; the mesh bake reads only its <see cref="SdfSampledSet.Baked"/> part and plans again only the
    /// groups that read a moved field.</summary>
    public SdfSampledSet Sampled {
        get => (m_composition.Sampled ?? Program.Sampled);
        init => m_composition.Sampled = value;
    }
    /// <inheritdoc cref="SdfFrameContent.SlotClips"/>
    public IReadOnlyList<SdfSlotClip> SlotClips {
        get => m_content.SlotClips;
        init => m_content = (m_content with { SlotClips = value });
    }
    /// <inheritdoc cref="SdfFrameContent.DynamicTransforms"/>
    public IReadOnlyList<DynamicTransform> DynamicTransforms {
        get => m_content.DynamicTransforms;
        init => m_content = (m_content with { DynamicTransforms = value });
    }
    /// <inheritdoc cref="SdfFrameContent.MovedTransforms"/>
    public SdfMovedTransforms? MovedTransforms {
        get => m_content.MovedTransforms;
        init => m_content = (m_content with { MovedTransforms = value });
    }
    /// <inheritdoc cref="SdfFrameContent.MovedTransformsSerial"/>
    public long MovedTransformsSerial => m_content.MovedTransformsSerial;
    /// <inheritdoc cref="SdfFrameContent.Volumes"/>
    public IReadOnlyList<SdfVolume> Volumes {
        get => m_content.Volumes;
        init => m_content = (m_content with { Volumes = value });
    }
    /// <inheritdoc cref="SdfFrameContent.MeshDraws"/>
    public IReadOnlyList<SdfMeshDraw> MeshDraws {
        get => m_content.MeshDraws;
        init => m_content = (m_content with { MeshDraws = value });
    }

    /// <summary>The rows the mesh dresser retains and the frame was dressed into; <see langword="null"/> before dressing.
    /// The dresser owns them: the frame references them, and its revision, journal and cause are the rows' own. The host's
    /// input draw list remains in <see cref="MeshDraws"/>.</summary>
    public SdfMeshRows? MeshRows {
        get => m_meshRows;
        init => m_meshRows = value;
    }

    internal SdfMeshDraws DrawRows => new(frame: this);

    /// <summary>Gets the revision of the draws the frame stages: the dressed rows' revision when the frame was dressed,
    /// otherwise <see cref="MeshDrawsRevision"/>.</summary>
    public long DrawRowsRevision => DrawRows.Revision;

    /// <summary>Gets the baked template slots, which draw only through MeshInstances.</summary>
    public IReadOnlyList<int> MeshAssetSlots {
        get => (m_composition.MeshAssetSlots ?? []);
        init => m_composition.MeshAssetSlots = value;
    }
    /// <summary>Gets the instance ranges that hold static templates, which draw only through the copies in
    /// <see cref="MeshInstances"/> that name them.</summary>
    public IReadOnlyList<SdfMeshTemplateRange> MeshTemplateRanges {
        get => (m_composition.MeshTemplateRanges ?? []);
        init => m_composition.MeshTemplateRanges = value;
    }
    /// <summary>Gets the copies of local geometry assets.</summary>
    public IReadOnlyList<SdfMeshInstance> MeshInstances {
        get => (m_composition.MeshInstances ?? []);
        init => m_composition.MeshInstances = value;
    }

    /// <inheritdoc cref="SdfFrameContent.MeshDrawsRevision"/>
    public long MeshDrawsRevision {
        get => m_content.MeshDrawsRevision;
        init => m_content = (m_content with { MeshDrawsRevision = value });
    }
    /// <inheritdoc cref="SdfFrameContent.MeshDrawChanges"/>
    public GpuRowChanges? MeshDrawChanges {
        get => m_content.MeshDrawChanges;
        init => m_content = (m_content with { MeshDrawChanges = value });
    }
    /// <inheritdoc cref="SdfFrameContent.MeshDrawChangesSerial"/>
    public long MeshDrawChangesSerial {
        get => m_content.MeshDrawChangesSerial;
        init => m_content = (m_content with { MeshDrawChangesSerial = value });
    }
    // A default is written into the content on its first read, so a copy (a writer's, a `with`) carries the object a
    // producer that edits the default edits; a default held only beside the content is left behind by a writer's copy.
    /// <summary>The lit path's lights table, its shadow slots and its curvature shading. The default is the pinned sun
    /// an unauthored world renders.</summary>
    public SdfLights Lights {
        get {
            if (m_content.Lights is not { } lights) {
                lights = (m_defaultLights ??= SdfLights.Default());
                m_content = (m_content with { Lights = lights });
            }
            return lights;
        }
        init => m_content = (m_content with { Lights = value });
    }
    /// <summary>The sky: its open layer stack, fog and environment gains.
    /// The default is the default look an unauthored world renders: the two-stop gradient and the default fog, as
    /// data.</summary>
    public SdfSky Sky {
        get {
            if (m_content.Sky is not { } sky) {
                sky = (m_defaultSky ??= new SdfSky());
                m_content = (m_content with { Sky = sky });
            }
            return sky;
        }
        init => m_content = (m_content with { Sky = value });
    }
    /// <inheritdoc cref="SdfFrameContent.Exposure"/>
    public SdfExposure Exposure {
        get => m_content.Exposure;
        init => m_content = (m_content with { Exposure = value });
    }
    /// <inheritdoc cref="SdfFrameContent.SlotRateCodes"/>
    public ReadOnlyMemory<byte> SlotRateCodes {
        get => m_content.SlotRateCodes;
        init => m_content = (m_content with { SlotRateCodes = value });
    }
    /// <inheritdoc cref="SdfFrameContent.ScreenRateCodes"/>
    public ReadOnlyMemory<byte> ScreenRateCodes {
        get => m_content.ScreenRateCodes;
        init => m_content = (m_content with { ScreenRateCodes = value });
    }
    /// <inheritdoc cref="SdfFrameContent.TickLocked"/>
    public bool TickLocked {
        get => m_content.TickLocked;
        init => m_content = (m_content with { TickLocked = value });
    }
    /// <inheritdoc cref="SdfFrameContent.ConvergesLighting"/>
    public bool ConvergesLighting {
        get => m_content.ConvergesLighting;
        init => m_content = (m_content with { ConvergesLighting = value });
    }
    /// <inheritdoc cref="SdfFrameContent.Clock"/>
    public PresentedTick Clock {
        get => m_content.Clock;
        init => m_content = (m_content with { Clock = value });
    }
    /// <inheritdoc cref="SdfFrameContent.WorldLanes"/>
    public System.Numerics.Vector4 WorldLanes {
        get => m_content.WorldLanes;
        init => m_content = (m_content with { WorldLanes = value });
    }

    /// <inheritdoc cref="SdfFrameSettings.DiagnosticSource"/>
    public object? DiagnosticSource {
        get => m_settings.DiagnosticSource;
        init => m_settings = (m_settings with { DiagnosticSource = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.FarDistance"/>
    public float FarDistance {
        get => m_settings.FarDistance;
        init => m_settings = (m_settings with { FarDistance = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.ShadowCoverage"/>
    public SdfShadowCoverage ShadowCoverage {
        get => m_settings.ShadowCoverage;
        init => m_settings = (m_settings with { ShadowCoverage = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.DistantShadows"/>
    public SdfDistantShadows DistantShadows {
        get => m_settings.DistantShadows;
        init => m_settings = (m_settings with { DistantShadows = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.ShadowTexels"/>
    public int ShadowTexels {
        get => m_settings.ShadowTexels;
        init => m_settings = (m_settings with { ShadowTexels = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.FarShadows"/>
    public SdfFarShadows FarShadows {
        get => m_settings.FarShadows;
        init => m_settings = (m_settings with { FarShadows = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.Tone"/>
    public SdfToneCurve Tone {
        get => m_settings.Tone;
        init => m_settings = (m_settings with { Tone = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.Toon"/>
    public SdfToon Toon {
        get => m_settings.Toon;
        init => m_settings = (m_settings with { Toon = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.ShadePrograms"/>
    public bool ShadePrograms {
        get => m_settings.ShadePrograms;
        init => m_settings = (m_settings with { ShadePrograms = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.ShadeDefault"/>
    public SdfShadeProgram? ShadeDefault {
        get => m_settings.ShadeDefault;
        init => m_settings = (m_settings with { ShadeDefault = value });
    }
    /// <summary>All programs the frame's shading jobs may invoke.</summary>
    public IEnumerable<SdfShadeProgram> ShadingPrograms => ((ShadeDefault is { } fallback)
        ? Program.Appearance.ShadingPrograms.Append(element: fallback) : Program.Appearance.ShadingPrograms);
    /// <inheritdoc cref="SdfFrameSettings.ShadeDeclared"/>
    public IReadOnlyList<SdfShadeProgram> ShadeDeclared {
        get => m_settings.ShadeDeclared;
        init => m_settings = (m_settings with { ShadeDeclared = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.DisableScreenLights"/>
    public bool DisableScreenLights {
        get => m_settings.DisableScreenLights;
        init => m_settings = (m_settings with { DisableScreenLights = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.MeshCull"/>
    public SdfMeshCullLevel MeshCull {
        get => m_settings.MeshCull;
        init => m_settings = (m_settings with { MeshCull = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.MeshDetail"/>
    public SdfMeshDetailLevel MeshDetail {
        get => m_settings.MeshDetail;
        init => m_settings = (m_settings with { MeshDetail = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.UpdateRates"/>
    public SdfUpdateRates UpdateRates {
        get => m_settings.UpdateRates;
        init => m_settings = (m_settings with { UpdateRates = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.RateCounts"/>
    public WorkCounterSet? RateCounts {
        get => m_settings.RateCounts;
        init => m_settings = (m_settings with { RateCounts = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.RateIntervals"/>
    public SdfUpdateRateIntervals? RateIntervals {
        get => m_settings.RateIntervals;
        init => m_settings = (m_settings with { RateIntervals = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.EnableCadenceGate"/>
    public bool EnableCadenceGate {
        get => m_settings.EnableCadenceGate;
        init => m_settings = (m_settings with { EnableCadenceGate = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.IndirectTier"/>
    public SdfIndirectTier IndirectTier {
        get => m_settings.IndirectTier;
        init => m_settings = (m_settings with { IndirectTier = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.RayQueries"/>
    public bool RayQueries {
        get => m_settings.RayQueries;
        init => m_settings = (m_settings with { RayQueries = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.IndirectSources"/>
    public SdfIndirectSources IndirectSources {
        get => m_settings.IndirectSources;
        init => m_settings = (m_settings with { IndirectSources = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.IndirectGains"/>
    public SdfIndirectGains IndirectGains {
        get => m_settings.IndirectGains;
        init => m_settings = (m_settings with { IndirectGains = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.IndirectBounces"/>
    public int? IndirectBounces {
        get => m_settings.IndirectBounces;
        init => m_settings = (m_settings with { IndirectBounces = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.IndirectBudget"/>
    public SdfIndirectBudget IndirectBudget {
        get => m_settings.IndirectBudget;
        init => m_settings = (m_settings with { IndirectBudget = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.IndirectLevels"/>
    public int IndirectLevels {
        get => m_settings.IndirectLevels;
        init => m_settings = (m_settings with { IndirectLevels = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.DisplayShare"/>
    public float DisplayShare {
        get => m_settings.DisplayShare;
        init => m_settings = (m_settings with { DisplayShare = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.IndirectLightingGrid"/>
    public SdfIndirectLightingGrid IndirectLightingGrid {
        get => m_settings.IndirectLightingGrid;
        init => m_settings = (m_settings with { IndirectLightingGrid = value });
    }
    /// <inheritdoc cref="SdfFrameSettings.IndirectApply"/>
    public SdfIndirectApplication IndirectApply {
        get => m_settings.IndirectApply;
        init => m_settings = (m_settings with { IndirectApply = value });
    }

    /// <summary>The default far distance, 40 world units, which an unauthored <c>render.farDistance</c> resolves to.</summary>
    public const float DefaultFarDistance = 40f;

    /// <summary>
    /// Owns one frame and is the one door that changes it after construction: a producer that captures every frame keeps a
    /// writer and writes its frame again for each capture, so capturing allocates nothing. Only the writer's holder can
    /// change the frame, so a consumer handed it cannot; what a consumer must keep past the producer's next write it copies
    /// into a frame its own writer owns (<see cref="CopyFrom(SdfFrame)"/>). The frame is created by the first write. Not
    /// thread-safe: the producer writes on the thread that captures, while no consumer reads the frame.
    /// </summary>
    public sealed class Writer {
        private SdfFrame? m_frame;

        /// <summary>Gets the frame, or <see langword="null"/> before the first write.</summary>
        public SdfFrame? Frame => m_frame;

        /// <summary>Writes a frame of the producer's own: this content and these settings, and no composition or
        /// dressing.</summary>
        /// <param name="content">The frame's content.</param>
        /// <param name="settings">The frame's settings.</param>
        /// <returns>The written frame, the same object on every write.</returns>
        public SdfFrame Write(in SdfFrameContent content, in SdfFrameSettings settings) {
            var frame = Open(content: in content);

            frame.m_settings = settings;
            frame.m_composition = default;

            return frame;
        }
        /// <summary>Replaces the written frame's settings alone, keeping its content and composition.</summary>
        /// <param name="settings">The frame's settings.</param>
        /// <returns>The written frame.</returns>
        /// <exception cref="InvalidOperationException">Nothing has been written yet.</exception>
        public SdfFrame Write(in SdfFrameSettings settings) {
            var frame = (m_frame ?? throw new InvalidOperationException(message: "A frame's settings are replaced only after its first write."));

            frame.m_settings = settings;

            return frame;
        }
        /// <summary>Writes a copy of another frame: its content, settings and composition, and none of its dressing,
        /// which belongs to the dresser that dressed it.</summary>
        /// <param name="source">The frame to copy, which may be this writer's own.</param>
        /// <returns>The written frame.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
        public SdfFrame CopyFrom(SdfFrame source) {
            ArgumentNullException.ThrowIfNull(argument: source);

            return CopyFrom(content: in source.m_content, source: source);
        }
        /// <summary>Writes a copy of another frame's settings and composition with other content, and none of its
        /// dressing.</summary>
        /// <param name="source">The frame whose settings and composition are copied, which may be this writer's own.</param>
        /// <param name="content">The written frame's content.</param>
        /// <returns>The written frame.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
        public SdfFrame CopyFrom(SdfFrame source, in SdfFrameContent content) {
            ArgumentNullException.ThrowIfNull(argument: source);

            // Read before the open: the source may be this writer's frame.
            var settings = source.m_settings;
            var composition = source.m_composition;
            var frame = Open(content: in content);

            frame.m_settings = settings;
            frame.m_composition = composition;

            return frame;
        }
        private SdfFrame Open(in SdfFrameContent content) {
            if (m_frame is not { } frame) {
                frame = new SdfFrame(Program: content.Program, ProgramChanged: content.ProgramChanged, Time: content.Time, Views: content.Views);
                m_frame = frame;
            }
            frame.m_content = content;
            frame.m_meshRows = null;
            frame.m_dressedPickMap = null;

            return frame;
        }
    }
}
