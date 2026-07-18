using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The client's per-frame source: a grass-green ground plane with a loose cluster of smooth-blended stone boulders, and
/// the whole entity table drawn as one avatar system — up to <see cref="WorldPopulation.MaxPopulation"/> distinct
/// articulated rigs, slots 0..3 the local roster seats and slots 4.. the network stand-ins, every pose read from the
/// snapshot-fed <see cref="WorldClient"/> view. Only the four local seats get a chased over-the-shoulder camera into
/// their own viewport (fullscreen → side-by-side → big-top/two-bottom → 2×2 quad as players join); the rest of the
/// population renders into those views as ordinary avatars.
/// </summary>
/// <remarks>The geometry is rebuilt only when the declared set or palette moves (a seat joins/leaves/recolors, or the
/// simulated count changes). A construction probe emits the complete 128-rig catalog and freezes the word, instance,
/// and dynamic-transform envelopes; live programs contain active avatars only.</remarks>
internal sealed class WorldFrameSource : ISdfFrameSource {
    private readonly FrameRateMonitor m_frameRate;
    private readonly PlayerRoster m_roster;
    private readonly WorldClient m_client;
    private readonly WorldSimulation m_simulation;
    // One rig slot per local seat, chase (OrientedFollowRig) by default: its defaults (eye up-and-back along the
    // anchor's +Z, target lifted a touch) frame that seat's avatar from behind, tracking its heading. The editor
    // session swaps its own rig in per frame while a seat edits. Only local seats get cameras/views.
    private readonly ISdfCameraRig[] m_cameraRigs;
    // The per-seat editor mode: camera rig swap + the sole-editor layout policy, both read during produce.
    private readonly WorldEditorSession m_editor;
    // Per-frame scratch reused to keep CaptureFrame allocation-free: one transform per leaf in the frozen all-avatar
    // catalog, plus movement-driven gait state per avatar and the joined seats' views. Live programs address only the
    // active avatars' stable slot ranges; stale inactive slots are unreachable.
    private readonly DynamicTransform[] m_transforms = new DynamicTransform[WorldAvatarCatalog.DynamicTransformCapacity];
    private readonly float[] m_avatarGaitPhases = new float[WorldPopulation.MaxPopulation];
    private readonly Vector3[] m_avatarPreviousPositions = new Vector3[WorldPopulation.MaxPopulation];
    private readonly bool[] m_avatarPoseSeeded = new bool[WorldPopulation.MaxPopulation];
    private readonly List<SdfViewSnapshot> m_views = new(capacity: PlayerRoster.MaxSlots);
    private readonly WorldRenderSettings m_settings;
    // The binder that owns the diegetic screens' CPU-fed GPU sources. The scene (ground + boulders) and the screens are
    // read LIVE from the client's delivered definition each rebuild, so a mutation's new geometry lands on the next
    // program rebuild; the binder's runtime source machinery is reconciled when the definition revision moves.
    private readonly WorldScreenBinder m_binder;
    private SdfProgram m_program;
    private int m_builtRevision;
    private int m_builtDefinitionRevision;
    private float m_elapsedSeconds;
    private bool m_uploaded;

    /// <summary>Initializes a new instance of the <see cref="WorldFrameSource"/> class, building the static scene over
    /// the snapshot-fed client view (the primer snapshot must already be delivered, so the initial program declares the
    /// boot seats and census active before the first frame).</summary>
    /// <param name="frameRate">The frame-rate witness sampled once per captured frame (the <c>world.fps</c> verb reads it).</param>
    /// <param name="client">The snapshot-fed entity view every pose, color, and active flag is read from.</param>
    /// <param name="simulation">The host-ticked simulation whose completed tick drives presentation sources.</param>
    /// <param name="settings">The live render settings read every captured frame (console-mutated in real time).</param>
    /// <param name="binder">The screen binder owning the declared screens' CPU-fed GPU sources, published each frame.</param>
    /// <param name="envelope">The render-capacity oracle configured here with the probed floors and a candidate
    /// measurer, so the server can reject an over-envelope scene/screen mutation at apply time.</param>
    /// <param name="editor">The per-seat editor mode (camera rig swap + the sole-editor layout policy).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldFrameSource(FrameRateMonitor frameRate, WorldClient client, WorldSimulation simulation, WorldRenderSettings settings, WorldScreenBinder binder, WorldRenderEnvelope envelope, WorldEditorSession editor) {
        ArgumentNullException.ThrowIfNull(argument: frameRate);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: simulation);
        ArgumentNullException.ThrowIfNull(argument: settings);
        ArgumentNullException.ThrowIfNull(argument: binder);
        ArgumentNullException.ThrowIfNull(argument: envelope);
        ArgumentNullException.ThrowIfNull(argument: editor);

        m_frameRate = frameRate;
        m_client = client;
        m_roster = client.Roster;
        m_simulation = simulation;
        m_settings = settings;
        m_binder = binder;
        m_editor = editor;
        m_cameraRigs = new ISdfCameraRig[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            m_cameraRigs[slot] = new OrientedFollowRig();
        }

        // Resolve the primer snapshot's render poses once so the initial program and camera anchors are live before
        // the first frame. Alpha 0 is immaterial — a freshly spawned entity has previous == current pose.
        m_client.UpdateRenderPoses(alpha: 0f);

        var scene = m_client.Definition.Scene;
        var screens = m_client.Definition.Screens;

        // The envelope probe (never rendered): a worst-case all-128-avatars build whose word/instance counts become the
        // spec's capacity floors, so every active-only live rebuild fits.
        var probe = BuildWorld(client: client, scene: scene, screens: screens, probeWorstCase: true);

        ProgramWordCapacity = probe.Words.Length;
        InstanceCapacity = probe.Instances.Count;

        // Publish the probed envelope + a candidate measurer so a scene/screen mutation is capacity-checked at apply
        // time against the SAME worst-case build (avatars are always at worst case; only scene/screens vary).
        envelope.Configure(
            programWordCapacity: ProgramWordCapacity,
            instanceCapacity: InstanceCapacity,
            measure: (candidateScene, candidateScreens) => {
                var candidate = BuildWorld(client: m_client, scene: candidateScene, screens: candidateScreens, probeWorstCase: true);

                return (Words: candidate.Words.Length, Instances: candidate.Instances.Count);
            }
        );

        m_builtRevision = m_client.Revision;
        m_builtDefinitionRevision = m_client.DefinitionRevision;
        m_program = BuildWorld(client: client, scene: scene, screens: screens, probeWorstCase: false);
    }

    /// <summary>The worst-case (all avatars active) program word count — the spec's <c>ProgramWordCapacity</c> floor.</summary>
    public int ProgramWordCapacity { get; }

    /// <summary>The worst-case (all avatars active) instance count — the spec's <c>InstanceCapacity</c> floor.</summary>
    public int InstanceCapacity { get; }

    /// <summary>The frozen transform-slot count for every leaf in the all-128 avatar catalog.</summary>
    public int DynamicTransformCapacity => WorldAvatarCatalog.DynamicTransformCapacity;

    /// <inheritdoc/>
    public void NotifyDeviceLost() => m_binder.NotifyDeviceLost();

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        // deltaSeconds is the launcher's clamped presentation interval, distinct from its whole-step simulation delta.
        // It may drive visual-only animation and the FPS witness, but never feeds authoritative world state.
        m_elapsedSeconds += deltaSeconds;
        m_frameRate.Sample(deltaSeconds: deltaSeconds);

        // Simulation has already advanced on the launcher's exact fixed ticks; the client view holds the two latest
        // snapshot poses. Each active entry's render pose is Lerp(previous tick → current, alpha) plus any eased
        // server-correction offset, so above the fixed-step rate the crowd glides instead of stepping; a frame that banked zero
        // sub-steps holds a stable lerp (previous == current), no snap-back. Presentation only: every player.where
        // still reads the authoritative sim pose server-side.
        m_client.UpdateRenderPoses(alpha: interpolationAlpha);

        // A declared-set or palette change since the last frame (a seat join/leave/recolor or a simulated-count change)
        // rebuilds the program and marks ProgramChanged so the engine re-uploads it, always within the frozen
        // capacities. The first frame also uploads (the initial program is not yet on the GPU).
        var revision = m_client.Revision;
        var programChanged = (!m_uploaded || (revision != m_builtRevision));

        if (revision != m_builtRevision) {
            // A definition delivery (scene/screen mutation, swap, or undo) landed since the last build: reconcile the
            // binder's runtime source machinery to the new screens BEFORE rebuilding the program off the live geometry.
            var definitionRevision = m_client.DefinitionRevision;

            if (definitionRevision != m_builtDefinitionRevision) {
                m_binder.ReconcileScreens(screens: m_client.Definition.Screens);
                m_builtDefinitionRevision = definitionRevision;
            }

            m_program = BuildWorld(client: m_client, scene: m_client.Definition.Scene, screens: m_client.Definition.Screens, probeWorstCase: false);
            m_builtRevision = revision;
        }

        m_uploaded = true;

        // Emit one view per joined local seat (a 1..MaxSlots count up to the ViewportCapacity floor, so players can join
        // later without freezing the count at the first frame's). Views ride slot order; the layout ladder places each
        // by its position among the joined players. The MaxPopulation dynamic transforms are separate and always
        // supplied in full; the active-only program addresses only its avatars' stable leaf ranges.
        var joinedCount = m_roster.Count;

        // Self-heal a seat that left the roster while editing (its mode layer and camera drop), then resolve this
        // frame's layout policy: a SOLE editing seat among 2+ players takes the dominant workbench region.
        m_editor.PruneDeparted();

        var soleEditorViewIndex = m_editor.SoleEditorViewIndex();

        m_views.Clear();

        // Per-instance soft-shadow participation (the crowd lever): a local seat always casts; a stand-in casts only
        // within m_settings.ShadowCrowdRadius of some joined seat, bounding the dominant GPU term to the crowd around
        // the real players. CastsSoftShadow false suppresses the entry from the soft-shadow march only (it still renders
        // and self-lights). Radius 0 => only the seats cast; a large radius => everyone casts. Parked entries are not
        // emitted, so their flag is irrelevant.
        Span<Vector3> joinedSeats = stackalloc Vector3[WorldPopulation.LocalSeatCount];
        var joinedSeatCount = 0;

        for (var seat = 0; (seat < WorldPopulation.LocalSeatCount); seat++) {
            if (m_client.IsActive(index: seat)) {
                joinedSeats[joinedSeatCount++] = m_client.Position(index: seat);
            }
        }

        var crowdRadiusSquared = (m_settings.ShadowCrowdRadius * m_settings.ShadowCrowdRadius);

        for (var index = 0; (index < WorldPopulation.MaxPopulation); index++) {
            if (!m_client.IsActive(index: index)) {
                m_avatarPoseSeeded[index] = false;

                continue;
            }

            var position = m_client.Position(index: index);
            var castsSoftShadow = ((index < WorldPopulation.LocalSeatCount) || WithinCrowd(position: position, seats: joinedSeats[..joinedSeatCount], radiusSquared: crowdRadiusSquared));

            if (m_avatarPoseSeeded[index]) {
                // Phase advances by DISTANCE, not wall time: idle avatars hold their pose; walking speed controls cadence.
                // Clamp a teleport/server snap so it cannot spin the limbs through dozens of cycles in one frame.
                var travelled = MathF.Min(x: Vector3.Distance(value1: position, value2: m_avatarPreviousPositions[index]), y: 0.25f);

                m_avatarGaitPhases[index] += (travelled * 8.0f);
            } else {
                m_avatarPoseSeeded[index] = true;
            }

            m_avatarPreviousPositions[index] = position;
            WorldAvatarCatalog.PackTransforms(
                avatar: index,
                rootPosition: position,
                rootOrientation: m_client.Orientation(index: index),
                gaitPhase: m_avatarGaitPhases[index],
                castsSoftShadow: castsSoftShadow,
                transforms: m_transforms
            );
        }

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.Seat(slot: slot) is null) {
                continue;
            }

            var region = LayoutRegion(count: joinedCount, index: m_views.Count, soleEditorIndex: soleEditorViewIndex);

            // The live render-scale tier rides each view's own RenderScale: native = 1.0 is the bit-exact fast path,
            // any lower tier renders that view's SDF at a reduced extent and upsamples.
            m_views.Add(item: new SdfViewSnapshot(Camera: ResolveCamera(slot: slot, region: region, width: width, height: height, deltaSeconds: deltaSeconds), Region: region) {
                RenderScale = m_settings.RenderScale,
                UpscaleSharpness = m_settings.UpscaleSharpness,
            });
        }

        return new SdfFrame(
            Program: m_program,
            ProgramChanged: programChanged,
            Views: m_views,
            Time: m_elapsedSeconds,
            WarpAmount: 0f
        ) {
            // Shadow reach is continuous: zero skips the march; (0,1) scales gather + march reach; one uses the
            // engine's 0 sentinel for full reach.
            DisableAmbientOcclusion = !m_settings.AmbientOcclusion,
            DisableSoftShadows = (m_settings.ShadowReach <= 0f),
            // Ambient occlusion — the world.ao toggle rides the DisableAmbientOcclusion lane.
            // Far-field isolators (world.far-field): both features ship ON, so the frame DISABLES only when the settings
            // clear them — the "off" sides of the owner's paired A/B.
            DisableFarBound = !m_settings.FarBound,
            DisableShadowFarExit = !m_settings.ShadowFarExit,
            DynamicTransforms = m_transforms,
            ShadowDistanceScale = ((m_settings.ShadowReach >= 1f) ? 0f : m_settings.ShadowReach),
            // At the machine-fleet tiers the correctness-complete per-pixel shadow-grid gather is the dominant frame
            // cost (measured 64.5 ms views at 124 stand-ins versus 16.5 ms with the existing camera-tile fallback).
            // Small sessions keep exact off-camera shadow candidates; 16/64/128-player sessions take the explicit crowd
            // approximation, while the independent crowd-radius policy still controls which avatars cast at all.
            UseCameraTileShadowMask = (m_settings.ShadowMask switch {
                ShadowMaskMode.ExactGather => false,
                ShadowMaskMode.CameraTile => true,
                _ => (m_client.ActivePeerCount >= 16),
            }),
            UseFastSoftShadowMarch = (m_settings.ShadowMarch switch {
                ShadowMarchMode.Exact => false,
                ShadowMarchMode.Fast => true,
                _ => (m_client.ActivePeerCount >= 16),
            }),
            UseFastAmbientOcclusion = (m_settings.AmbientOcclusionQuality switch {
                AmbientOcclusionMode.Exact => false,
                AmbientOcclusionMode.Fast => true,
                _ => (m_client.ActivePeerCount >= 16),
            }),
        };
    }

    /// <inheritdoc/>
    public void PrepareScreenSources(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        // Render + upload every CPU-fed screen for this frame off the sim tick advanced during CaptureFrame, so the
        // provider polled just after this call returns a handle to THIS frame's image. The engine seam calls this
        // AFTER capture and BEFORE the source poll.
        m_binder.Publish(tick: m_simulation.Tick, elapsedTicks: m_simulation.ElapsedTicks, deviceContext: deviceContext, gpu: gpu);
    }

    /// <inheritdoc/>
    public void RenderViews(in Puck.Hosting.FrameContext context) {
        // Render this frame's jumbotron views (the View screens) against the live device, feeding each the SAME world
        // program / dynamic transforms / content clock the room renders with, so a jumbotron shows this world from its
        // placeable camera. Called AFTER PrepareScreenSources (the CPU-fed screens the views sample are already this
        // frame's) and BEFORE the source poll (so a View screen's provider returns this frame's offscreen render).
        m_binder.RenderViews(context: in context, program: m_program, revision: m_builtRevision, transforms: m_transforms, time: m_elapsedSeconds);
    }

    // Whether `position` lies within the crowd radius of any joined local seat (the stand-in soft-shadow gate). With no
    // joined seats or a zero radius, nothing qualifies.
    private static bool WithinCrowd(Vector3 position, ReadOnlySpan<Vector3> seats, float radiusSquared) {
        foreach (var seat in seats) {
            if (Vector3.DistanceSquared(value1: position, value2: seat) < radiusSquared) {
                return true;
            }
        }

        return false;
    }

    // Frames the slot's view at the region's pixel size (region × window dims), so each split keeps its own aspect.
    // The rig is the seat's chase rig by default; while the seat edits, the editor session's rig (advanced by this
    // frame's presentation delta) frames it instead. The anchor is the seat's render pose (interpolated and
    // error-eased, resolved by the client view this frame), so the chase camera tracks the pose the avatar is drawn
    // at and the orbit pivot rides it live.
    private CameraSnapshot ResolveCamera(int slot, NormalizedRect region, uint width, uint height, float deltaSeconds) {
        var anchor = new SdfAnchor(Position: m_client.Position(index: slot), Orientation: m_client.Orientation(index: slot));
        var rig = m_editor.ResolveRig(slot: slot, chase: m_cameraRigs[slot], anchor: in anchor, time: m_elapsedSeconds, deltaSeconds: deltaSeconds);
        var (eye, target, fieldOfView) = rig.Resolve(anchor: in anchor, time: m_elapsedSeconds);

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: (uint)(region.Width * width)),
            viewportHeight: Math.Max(val1: 1u, val2: (uint)(region.Height * height))
        );
    }

    // The editor-aware viewport resolver: when EXACTLY one seat edits while others play (soleEditorIndex >= 0, 2+
    // joined), the editing view takes the full-height left 70% — the workbench wants width and an honest aspect —
    // and the playing seats stack in a live right rail (each keeps a visible, playable view). All-playing,
    // single-seat, and multi-editor sessions fall through to the standard ladder.
    internal static NormalizedRect LayoutRegion(int count, int index, int soleEditorIndex) {
        if ((soleEditorIndex >= 0) && (count >= 2)) {
            if (index == soleEditorIndex) {
                return new NormalizedRect(X: 0f, Y: 0f, Width: 0.70f, Height: 1f);
            }

            var railCount = (count - 1);
            var railIndex = ((index < soleEditorIndex) ? index : (index - 1));

            return new NormalizedRect(X: 0.70f, Y: ((float)railIndex / railCount), Width: 0.30f, Height: (1f / railCount));
        }

        return LayoutRegion(count: count, index: index);
    }

    // The viewport region for the player at slot-order position `index` of `count`. NormalizedRect convention: origin
    // top-left, Y increasing down. 1 = fullscreen; 2 = side-by-side halves; 3 = big-top (full-width, top half) over two
    // bottom quarters; 4 = the 2×2 quad (index 0=TL, 1=TR, 2=BL, 3=BR). Internal: the overlay feed scopes each seat's
    // screen-space UI (binding bar, later the editor HUD) into the SAME rect the seat renders in.
    internal static NormalizedRect LayoutRegion(int count, int index) {
        return count switch {
            1 => new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f),
            2 => new NormalizedRect(X: (0.5f * index), Y: 0f, Width: 0.5f, Height: 1f),
            3 => (index switch {
                0 => new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 0.5f),
                1 => new NormalizedRect(X: 0f, Y: 0.5f, Width: 0.5f, Height: 0.5f),
                _ => new NormalizedRect(X: 0.5f, Y: 0.5f, Width: 0.5f, Height: 0.5f),
            }),
            _ => new NormalizedRect(X: (0.5f * (index % 2)), Y: (0.5f * (index / 2)), Width: 0.5f, Height: 0.5f),
        };
    }

    // The scene: a grass ground plane, then the stone boulders (each smooth-unioned into the accumulated field), then
    // the view's active avatars as leaf-level dynamic instances riding frozen catalog slots. Active-only, never
    // declared-but-parked: the per-tile instance mask width derives from the program's total declared instance count
    // (SdfProgram.InstanceMaskWordCount), so parked avatar declarations widen every shadow-gather pixel's mask walk.
    // Instead the program is rebuilt on population change (the revision watch), and the 128-avatar worst case is held
    // by the spec's capacity floors (ProgramWordCapacity / InstanceCapacity / DynamicTransformCapacity), probed at
    // construction. Every avatar keeps its own body + accent material (cheap constant words), so a recolor is data,
    // not a resize. Unions only, so the accumulator stays additive.
    private static SdfProgram BuildWorld(WorldClient client, WorldScene scene, IReadOnlyList<WorldScreen> screens, bool probeWorstCase) {
        var builder = new SdfProgramBuilder();
        var grass = builder.AddMaterial(material: new SdfMaterial(Albedo: scene.GroundAlbedo));
        var stone = builder.AddMaterial(material: new SdfMaterial(Albedo: scene.StoneAlbedo));
        // The per-avatar body + accent materials, allocated up front so the catalog emitter is a straight builder chain.
        // A local seat's colors come from its seated profile (a pending seat renders a desaturated candidate); a stand-in's
        // from its snapshot palette. A color change bumps the revision and rebuilds; a settings-only edit does not.
        var avatarBodyMaterials = new int[WorldPopulation.MaxPopulation];
        var avatarAccentMaterials = new int[WorldPopulation.MaxPopulation];

        for (var index = 0; (index < WorldPopulation.MaxPopulation); index++) {
            var bodyColor = client.BodyColor(index: index);

            avatarBodyMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: bodyColor));
            avatarAccentMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: WorldColor.Nose(body: bodyColor)));
        }

        // The static field from the scene data: the grass ground plane, then each boulder (a sphere smooth-unioned into
        // the field) translated to its center and reset back for the next (see WorldBoulder for the Puck.Scene
        // convergence note).
        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: grass);

        foreach (var boulder in scene.Boulders) {
            _ = builder
                .Translate(offset: boulder.Center)
                .Sphere(radius: boulder.Radius, material: stone, blend: SdfBlendOp.SmoothUnion, smooth: boulder.Smooth)
                .ResetPoint();
        }

        // The diegetic screens: each a sampled ScreenSlab whose lit face samples its bound source (or the engine's
        // procedural no-signal fallback when unbound). STATIC data — emitted every build (probe and live), so the
        // capacity floors cover them by construction (no probe-only branch). The sampled overload takes the explicit
        // world frame (Origin/Right/Up) baked into the surface table for UV mapping; the geometry rounded box is
        // placed by translating to its CENTER, which sits one HalfDepth behind the face along the face normal
        // (Right × Up). The material sentinel the overload assigns needs no palette entry.
        foreach (var screen in screens) {
            var normal = Vector3.Normalize(value: Vector3.Cross(vector1: screen.Right, vector2: screen.Up));
            var center = (screen.Origin - (normal * screen.HalfDepth));

            _ = builder
                .Translate(offset: center)
                .ScreenSlab(
                    halfExtents: new Vector3(x: screen.HalfWidth, y: screen.HalfHeight, z: screen.HalfDepth),
                    round: screen.Round,
                    worldOrigin: screen.Origin,
                    worldRight: screen.Right,
                    worldUp: screen.Up,
                    screenIndex: screen.Index
                )
                .ResetPoint();
        }

        // The view's active avatars: 12..20 independently animated leaves and 60..100 authored VM instructions
        // each. The probe emits every catalog range; a live build emits only active ranges without renumbering slots.
        WorldAvatarCatalog.Emit(
            builder: builder,
            isActive: client.IsActive,
            bodyMaterials: avatarBodyMaterials,
            accentMaterials: avatarAccentMaterials,
            probeWorstCase: probeWorstCase
        );

        return builder.Build();
    }
}
