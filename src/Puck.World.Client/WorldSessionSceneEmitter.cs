using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Text;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The session projection's content half: composes a destination world's static authored placement geometry, its
/// stamp pool (animated, inhabited and attached creations) and its live mirrored avatars (see
/// <see cref="WorldSessionMirror"/>'s own remarks) into an <see cref="SdfProgramBuilder"/>, and dresses the result
/// into one <see cref="SdfFrame"/> framed through the destination's chosen camera — the
/// <see cref="ISdfSceneEmitter"/>/<see cref="ISdfFrameDresser"/> split <c>WorldFramePresenter</c> and
/// <c>WorldSceneEmitter</c> already establish, collapsed into one type here because a session
/// projection has exactly one content source and needs no second host to own presentation separately. The destination's
/// text screens draw their lines through the destination's own font catalog, resolved beside its own document
/// (<see cref="GlyphAtlas"/>, <see cref="ScreenDecals"/>), and every camera of the destination a screen shows is filmed
/// into the frame after the session's own view (<see cref="Film"/>).
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="WorldPlacementStamper"/> directly — the same static-stamp compiler
/// <c>WorldSceneEmitter</c> calls for the boot world's own decoration placements — a <see cref="WorldStampPool"/>
/// of its own, rooted on the destination through <see cref="WorldSessionStampSource"/> with its census kept by a
/// <see cref="WorldBodyStampCensus"/>, and <see cref="WorldRigCatalog"/> directly for avatars, rather than a second
/// implementation of any of them. The destination's own screens are drawn too, its declared rows and its creations'
/// derived faces, as <c>WorldSceneEmitter</c> draws the boot world's (<c>WorldStaticSceneEmit</c>): what each one
/// shows is bound per view by whoever renders the frame (<c>WorldScreenBinder</c>'s nested screens), so a portal inside
/// the destination shows its own destination to the presentation's nesting depth. No editor overlay. A body that
/// renders its creation through the pool (an inhabitant, or a crowd body wearing a creation look) parks its catalog
/// avatar, as the local scene's does.
/// </para>
/// <para>
/// <b>The interpolation timebase.</b> A session's view calls <c>ISdfFrameSource.CaptureFrame</c> with its own
/// produced-frame interval and a zero alpha on every frame it renders, never through the host's own clock — so
/// neither <see cref="Dress"/>'s <c>interpolationAlpha</c> parameter nor <see cref="SdfEmitContext.InterpolationAlpha"/>
/// ever carries anything but 0 for this emitter. <see cref="WorldSessionMirror.InterpolationAlpha"/> derives its own honest
/// fraction instead, purely from the mirror's (tick, pose) pairs plus this call's own wall-clock arrival: real elapsed
/// time since <see cref="WorldSessionMirror.SnapshotArrivalTimestamp"/>, normalized by
/// <see cref="WorldSessionMirror.StepSeconds"/>. The destination's tick thread and the thread that calls
/// <see cref="PackDynamicTransforms"/> are the same process (frequently the same thread — see
/// <see cref="WorldSessionMirror"/>'s own threading remarks), so a real-time read between them is an honest
/// presentation-only measure, not a cross-machine clock assumption; it is the floor the task brief calls out
/// explicitly: interpolating between the two most recent mirrored snapshots is the honest ceiling of what a session
/// view — which is handed no simulation clock at all — can ever do.
/// </para>
/// </remarks>
public sealed class WorldSessionSceneEmitter : ISdfSceneEmitter, ISdfFrameDresser, IDisposable {
    /// <summary>Releases the environment's delivered-tick subscription.</summary>
    public void Dispose() {
        m_shadowObservation.Dispose();
        m_environment.Dispose();
    }
    /// <summary>Reads the last presented slot census of this session.</summary>
    /// <param name="definition">The queried authority's definition.</param>
    /// <returns>The census, or null before a matching frame.</returns>
    public string? DescribeShadowSlots(WorldDefinition definition) => m_environment.DescribeShadowSlots(definition: definition);

    // The BIND-time resolved camera choice: a validated, currently-present camera NAME, or null for "use the
    // destination's default projection" (its first declared camera, else the spawn-centroid overview) — see this
    // type's own construction site in WorldScreenBinder.ResolveSession, which is where the "unknown camera refuses at
    // bind with a loud note, falling back to the default projection" decision is made and narrated.
    private readonly string? m_effectiveCameraName;
    private readonly float m_fieldOfViewRadians;
    private readonly WorldSessionMirror m_mirror;
    // The color each avatar is painted with: the mirror's, unless the host paints some bodies its own way.
    private readonly Func<int, Vector3> m_bodyColor;

    // The frame's views: the session's own, then the destination's cameras Film adds.
    private readonly List<SdfViewSnapshot> m_views = [];
    // The destination's own font catalog and its text screens' decals, followed from its delivered definition (FollowText):
    // the rows, its screens then its seated faces, a text screen's lines are read from.
    private readonly WorldTextCatalog m_text = new();

    private readonly WorldScreenDecals m_decals;

    private WorldDefinition? m_textDefinition;

    private IReadOnlyList<WorldScreen> m_textRows = [];

    /// <summary>Gets the quality a session screen's view renders at: a budgeted panel image skips soft shadows, ambient
    /// occlusion and the far bound, whose cost buys little in a small screen-space result.</summary>
    public static SdfViewQuality ReducedQuality { get; } = new() {
        DisableAmbientOcclusion = true,
        DisableFarBound = true,
        DisableSoftShadows = true,
    };

    private readonly Vector3[]? m_bodyColors;

    private int m_bodyColorRevision;

    private readonly bool m_castsAvatarShadows;
    // The prototypes a far scene holds (see the constructor), or null for a scene of the whole world.
    private readonly IReadOnlySet<string>? m_onlyPrototypes;

    // The static placements' palettes, reused across rebuilds (WorldPlacementStamper.EmitStatic).
    private readonly WorldStaticPalettes m_palettes = new();

    // The bound colors the static build bakes and the mirror they read (BakedColors), and the revision component a
    // moved baked color bumps (WriteRevision).
    private WorldBakedColors? m_bakedColors;
    private WorldStateMirror? m_bakedColorsMirror;
    private int m_bakedColorRevision;
    // A counter moved when a stamped body's live scale moves, so the stamps bake it (WorldBodyStampCensus.TryTakeMove).
    private int m_censusRevision;

    // The destination's stamp pool, packed past the avatar catalog's slots, the source it roots on, its creation-stamp
    // census, and the mesh draws: the static placements' the last live Emit fixed, then the pool's.
    private readonly WorldBodyStampCensus m_census = new();

    private readonly WorldSceneMeshDraws m_meshDraws;

    private readonly WorldPickMapBuilder m_picks = new();

    /// <inheritdoc/>
    public ISdfPickMap? PickMap => m_picks.Snapshot(pool: m_pool.PickMeshTargets);

    private readonly WorldStampPool m_pool = new();

    private readonly WorldSessionStampSource m_source;

    private SdfProgram? m_lastProgram;
    // Retain the geometry's definition through emission and dressing: a later delivery must not change a held image's pick.
    private WorldDefinition? m_emittedDefinition;
    private WorldDefinition? m_dressedDefinition;
    // The WINDOW projection's per-produced-frame override — set by WorldScreenBinder.Publish (the one place with access
    // to both the local eye and the border pair's two face rows) before the render graph renders this view.
    // Null (the default, and every non-window session's steady state) leaves Dress on the ordinary camera path below.
    private Func<CameraSnapshot?>? m_windowFit;
    // The camera the last dressed frame renders from, which a hit on the session's image continues through.
    private CameraSnapshot? m_dressedCamera;
    // The last dressed program's fixed-point field, built when a pick first asks for it, and the far distance a pick
    // marches it to.
    private SdfFieldEvaluator? m_dressedField;
    private SdfProgram? m_dressedFieldProgram;
    private float m_dressedFarDistance;

    // The mirrored world's environment, resolved each dressed frame. The track double-buffers its output, so the frame
    // the residency holds keeps its environment through the next dress, as the boot presentation's does.
    private readonly WorldValueDomainGuard m_domains;
    private readonly WorldEnvironmentResolve m_environment;
    private readonly Func<WorldShadowSettings>? m_shadowSettings;

    /// <summary>The presentation's session-only layer audition.</summary>
    public WorldSkyAudition? SkyLayers { get; set; }

    private readonly WorldShadowSelection m_deliveredShadows = new();

    private readonly WorldSessionMirror.DeliveredStateObservation m_shadowObservation;

    // Per-avatar movement-driven gait state, scratch reused across frames to keep packing allocation-free — the SAME
    // distance-driven approach Client.WorldSceneEmitter.PackDynamicTransforms uses, over this emitter's own
    // interpolated (not host-supplied) positions.
    private readonly float[] m_avatarGaitPhases = new float[WorldBodiesLimits.CapacityCeiling];
    private readonly Vector3[] m_avatarPreviousPositions = new Vector3[WorldBodiesLimits.CapacityCeiling];
    private readonly bool[] m_avatarPoseSeeded = new bool[WorldBodiesLimits.CapacityCeiling];
    private readonly WorldTransformOwners m_avatarOwners = new(capacity: WorldBodiesLimits.CapacityCeiling);
    private readonly WorldEntityAddress[] m_avatarMotionAddresses = new WorldEntityAddress[WorldBodiesLimits.CapacityCeiling];
    private readonly int[] m_emittedRigs = new int[WorldBodiesLimits.CapacityCeiling];
    private readonly float[] m_emittedScales = new float[WorldBodiesLimits.CapacityCeiling];
    private readonly float[] m_emittedGaitAmplitudes = new float[WorldBodiesLimits.CapacityCeiling];

    /// <summary>Initializes the emitter over a resolved session mirror and a bind-time-resolved camera choice.</summary>
    /// <param name="mirror">The destination's client-side mirror this emitter reads static geometry from.</param>
    /// <param name="effectiveCameraName">A validated, currently-declared camera name, or <see langword="null"/> for
    /// the destination's default projection.</param>
    /// <param name="fieldOfViewRadians">The vertical field of view used only by the spawn-centroid overview fallback
    /// (a named camera row carries its own lens).</param>
    /// <param name="bodyColor">The color each avatar is painted with by body index, or <see langword="null"/> for the
    /// mirror's own (<see cref="WorldSessionMirror.BodyColor"/>).</param>
    /// <param name="castsAvatarShadows">Whether avatar transforms participate in soft shadows when the host enables them.</param>
    /// <param name="shadowSettings">The routed view's live policy, or its own boot policy when absent.</param>
    /// <param name="domains">The guard that holds the last valid value of a bound value and reports its transitions.</param>
    /// <param name="onlyPrototypes">The prototypes the scene holds, or <see langword="null"/> for the whole world: far
    /// geometry (<c>Puck.SdfVm.Views.InfinityViewKind.Far</c>) emits the placements of these prototypes alone, with no
    /// screen, no avatar and no body stamp, so its cost scales with what it shows.</param>
    public WorldSessionSceneEmitter(WorldSessionMirror mirror, string? effectiveCameraName, WorldValueDomainGuard domains, float fieldOfViewRadians = (MathF.PI / 3f), Func<int, Vector3>? bodyColor = null, bool castsAvatarShadows = false, Func<WorldShadowSettings>? shadowSettings = null, IReadOnlySet<string>? onlyPrototypes = null) {
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: domains);

        m_domains = domains;
        m_environment = new WorldEnvironmentResolve(domains: domains);

        m_mirror = mirror;
        m_bodyColor = (bodyColor ?? mirror.BodyColor);
        m_bodyColors = ((bodyColor is null) ? null : new Vector3[WorldBodiesLimits.CapacityCeiling]);
        m_castsAvatarShadows = castsAvatarShadows;
        m_onlyPrototypes = onlyPrototypes;
        m_shadowSettings = shadowSettings;
        m_effectiveCameraName = effectiveCameraName;
        m_fieldOfViewRadians = fieldOfViewRadians;
        m_meshDraws = new WorldSceneMeshDraws(pool: m_pool);
        m_source = new WorldSessionStampSource(mirror: mirror);
        m_deliveredShadows.SetSettings(settings: (shadowSettings?.Invoke() ?? WorldShadowSettings.From(render: mirror.Definition.Render)));
        m_shadowObservation = mirror.ObserveDeliveredState(observer: (definition, revision, state) => {
            if (m_shadowSettings is null) { m_deliveredShadows.SetSettings(settings: WorldShadowSettings.From(render: definition.Render)); }
            m_deliveredShadows.Advance(definition: definition, mirror: state, revision: revision);
        });
        m_decals = new WorldScreenDecals(
            catalog: TextCatalog,
            colors: new WorldBakedColors(mirror: mirror.FollowState()),
            textAt: TextAt
        );
    }

    /// <summary>Gets or sets what films the destination's cameras into each dressed frame: handed the frame's views, the
    /// session's own first, it adds a view of each camera of the destination a screen shows, at an index the caller
    /// records. <see langword="null"/>, the default, films none.</summary>
    public Action<List<SdfViewSnapshot>>? Film { get; set; }

    /// <summary>Gets or sets what fits the named sky layers to the final consuming views. The callback receives the
    /// destination's resolved sky and the frame dimensions after its cameras have been dressed.</summary>
    public Action<List<SdfViewSnapshot>, SdfSky, uint, uint>? FitSkyViews { get; set; }
    /// <inheritdoc/>
    /// <remarks>The destination's own font atlas, resolved beside its own document.</remarks>
    public SdfGlyphAtlas? GlyphAtlas {
        get {
            FollowText();

            return ((TextFault is null) ? m_text.GlyphAtlas : null);
        }
    }
    /// <inheritdoc/>
    /// <remarks>The destination's text screens', drawn through its own font catalog.</remarks>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => m_decals.Providers;
    /// <summary>Gets why the destination's font catalog does not resolve, which leaves its text screens blank: a
    /// document that names no directory its fonts resolve beside (a world delivered from a remote authority), a font
    /// asset that fails its pin, or text its fonts cannot draw; <see langword="null"/> while it resolves or the
    /// destination declares no text.</summary>
    public string? TextFault { get; private set; }

    // The destination's font catalog, followed from its delivered definition.
    private PackedFontAtlasCatalog? TextCatalog() {
        FollowText();

        return ((TextFault is null) ? m_text.Catalog : null);
    }
    // The text a screen of the destination shows, or null.
    private WorldScreenSource.Text? TextAt(int screen) {
        FollowText();

        for (var index = 0; (index < m_textRows.Count); index++) {
            if (m_textRows[index].Index == screen) {
                return (m_textRows[index].Source as WorldScreenSource.Text);
            }
        }

        return null;
    }
    // Follows the destination's delivered definition: its text rows and its font catalog, which every decal rebakes
    // against. A catalog that does not resolve leaves the text blank and says why (TextFault), never stopping the frame.
    private void FollowText() {
        var definition = m_mirror.Definition;

        if (ReferenceEquals(
            objA: definition,
            objB: m_textDefinition
        )) {
            return;
        }

        m_textDefinition = definition;
        m_textRows = [
            .. definition.Screens,
            .. WorldPrototypeFacets.Seated(definition: definition),
        ];
        m_decals.Invalidate();

        try {
            m_text.Reconcile(definition: definition);
            TextFault = null;
        } catch (Exception exception) when ((exception is ArgumentException or InvalidDataException or IOException or KeyNotFoundException or UnauthorizedAccessException or NotSupportedException or OverflowException)) {
            TextFault = exception.Message.ReplaceLineEndings(replacementText: " ");
        }
    }
    // Registers the avatar palette and emits the hybrid catalog range — the probe branch (largest detailed rigs plus
    // the full coarse band at unit scale) and the live branch (only mirrored-active avatars, each sourcing its look's pinned rig and uniform
    // scale) both flow through the ONE WorldRigCatalog.Emit call, exactly like Client.WorldSceneEmitter.Compose's
    // own avatar block.
    private void EmitAvatars(SdfProgramBuilder builder, bool probeWorstCase, int slotBase) {
        var bodyMaterials = new int[WorldBodiesLimits.CapacityCeiling];
        var accentMaterials = new int[WorldBodiesLimits.CapacityCeiling];
        var noseFactor = m_mirror.Definition.PlayerDefaults.NoseFactor;

        for (var index = 0; (index < WorldBodiesLimits.CapacityCeiling); index++) {
            // A body the pool registered renders its creation there and parks this catalog avatar; its palette entry
            // is still emitted, so the catalog keeps the frozen shape its probe reserved.
            WorldMirroredAvatarBand.EmitPalette(
                accentMaterials: accentMaterials,
                bodyColor: m_bodyColor(arg: index),
                bodyMaterials: bodyMaterials,
                builder: builder,
                catalogRig: m_mirror.CatalogRig(index: index),
                emittedGaitAmplitudes: m_emittedGaitAmplitudes,
                emittedRigs: m_emittedRigs,
                emittedScales: m_emittedScales,
                identityIndex: index,
                look: m_mirror.Look(index: index),
                materialIndex: index,
                noseFactor: noseFactor
            );
        }

        WorldRigCatalog.Emit(
            builder: builder,
            isActive: HoldsAvatar,
            bodyMaterials: bodyMaterials,
            accentMaterials: accentMaterials,
            probeWorstCase: probeWorstCase,
            slotBase: slotBase,
            rigFor: (probeWorstCase
            ? null
            : index => m_emittedRigs[index]),
            scaleFor: (probeWorstCase
            ? null
            : index => m_emittedScales[index]),
            picks: (probeWorstCase ? null : m_picks),
            indirectFor: (probeWorstCase ? null : index => WorldIndirectParticipation.ForPlacement(m_mirror.Definition, m_mirror.PlacementId(index)))
        );
    }
    // The camera row's anchor pose as the destination's mirror resolves it: a Placement anchor reads the destination's
    // own authored transform, and an Entity anchor its mirrored body's interpolated pose while the body is active. Any
    // other anchor (a body part, a group, a seat) resolves to the world origin: the mirror carries no part poses, and the
    // presentation seats no one in the destination.
    private static (Vector3 Position, Quaternion Orientation) ResolveAnchorPose(WorldSessionMirror mirror, WorldDefinition definition, WorldAnchor? anchor) {
        switch (anchor) {
            case WorldAnchor.Placement placement:
                return (WorldAnchorGeometry.StaticPlacementPosition(
                    definition: definition,
                    placementId: placement.PlacementId,
                    shapeId: placement.ShapeId
                ), Quaternion.Identity);
            case WorldAnchor.Entity entity when (
                (((uint)entity.Index) < WorldBodiesLimits.CapacityCeiling) &&
                mirror.IsActive(index: entity.Index)
            ):
                var alpha = mirror.InterpolationAlpha;

                return (Vector3.Lerp(
                    amount: alpha,
                    value1: mirror.PreviousPosition(index: entity.Index),
                    value2: mirror.CurrentPosition(index: entity.Index)
                ), Quaternion.Lerp(
                    amount: alpha,
                    quaternion1: mirror.PreviousOrientation(index: entity.Index),
                    quaternion2: mirror.CurrentOrientation(index: entity.Index)
                ));
            default:
                return (Vector3.Zero, Quaternion.Identity);
        }
    }

    /// <summary>Resolves the camera a session's ordinary projection renders a destination through this frame: the named
    /// camera if the destination still declares it, else the destination's first declared camera, else a fixed overview
    /// over its spawn points. It is resolved from the live mirrored definition every time, so a live edit of the
    /// destination's camera row shows at once.</summary>
    /// <param name="mirror">The destination's mirror.</param>
    /// <param name="cameraName">The camera the session names, or <see langword="null"/> for the destination's
    /// default projection.</param>
    /// <param name="width">The view's width, in pixels.</param>
    /// <param name="height">The view's height, in pixels.</param>
    /// <param name="domains">The guard that holds the last valid value of a bound camera operand and reports its transitions.</param>
    /// <returns>The camera, in the destination's space.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public static CameraSnapshot ResolveCamera(WorldSessionMirror mirror, string? cameraName, uint width, uint height, WorldValueDomainGuard domains) {
        ArgumentNullException.ThrowIfNull(argument: domains);
        ArgumentNullException.ThrowIfNull(argument: mirror);

        var definition = mirror.Definition;
        var row = ResolveCameraRow(
            definition: definition,
            name: cameraName
        );

        if (row is { } cameraRow) {
            var (position, orientation) = ResolveAnchorPose(
                anchor: cameraRow.Anchor,
                definition: definition,
                mirror: mirror
            );
            var rig = WorldCameraRigCompiler.Compile(
                definition: definition,
                domains: domains,
                mirror: mirror.FollowState(),
                program: cameraRow.Rig
            );
            var anchor = new SdfAnchor(
                Orientation: orientation,
                Position: position
            );
            var clock = new SdfCameraClock(
                PresentationSeconds: 0f,
                AuthoritativeTick: mirror.Tick
            );

            var (eye, target, fieldOfView) = rig.Resolve(
                anchor: in anchor,
                clock: in clock
            );

            return CameraSnapshot.LookAt(
                position: eye,
                target: target,
                fieldOfViewRadians: fieldOfView,
                viewportWidth: Math.Max(
                    val1: 1u,
                    val2: width
                ),
                viewportHeight: Math.Max(
                    val1: 1u,
                    val2: height
                )
            );
        }

        return ResolveOverviewCamera(
            definition: definition,
            height: height,
            width: width
        );
    }

    private static WorldCamera? ResolveCameraRow(WorldDefinition definition, string? name) {
        if (name is not null) {
            foreach (var camera in definition.Cameras) {
                if (string.Equals(
                    a: camera.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return camera;
                }
            }

            // The named camera vanished from a LATER destination mutation — fall through to the destination's
            // default projection rather than freezing on a dangling reference.
            return ((definition.Cameras.Count > 0)
                ? definition.Cameras[0]
                : null
            );
        }

        return ((definition.Cameras.Count > 0)
            ? definition.Cameras[0]
            : null
        );
    }
    // The spawn-centroid overview — the SAME construction WorldFramePresenter.ResolveSpectatorCamera uses for the
    // boot world's own no-local-seats fallback, applied here to a destination with no declared camera at all: a
    // pulled-back, elevated look-at over the centroid of its authored local-seat spawn points.
    private static CameraSnapshot ResolveOverviewCamera(WorldDefinition definition, uint width, uint height) {
        var centroid = Vector3.Zero;
        var resolved = 0;

        foreach (var name in definition.Population.SeatSpawns) {
            if (WorldDefinitionRows.FindSpawnPoint(
                spawnPoints: definition.SpawnPoints,
                id: name
            ) is { } spawn) {
                centroid += spawn.Position;
                resolved++;
            }
        }

        if (resolved > 0) {
            centroid /= resolved;
        }

        var target = (centroid + new Vector3(
            x: 0f,
            y: 1f,
            z: 0f
        ));
        var eye = (centroid + new Vector3(
            x: 0f,
            y: 14f,
            z: 18f
        ));

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: (MathF.PI / 3f),
            viewportWidth: Math.Max(
                val1: 1u,
                val2: width
            ),
            viewportHeight: Math.Max(
                val1: 1u,
                val2: height
            )
        );
    }

    /// <inheritdoc/>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        var programChanged = !ReferenceEquals(
            objA: program,
            objB: m_lastProgram
        );

        m_lastProgram = program;
        if (programChanged) {
            m_dressedDefinition = m_emittedDefinition;
        }
        // The pool's replay cursors advance on this view's own produced-frame interval, latched for the next pack.
        m_pool.Tick(deltaSeconds: deltaSeconds);

        var camera = (m_windowFit?.Invoke() ?? ResolveCamera(
            cameraName: m_effectiveCameraName,
            domains: m_domains,
            height: height,
            mirror: m_mirror,
            width: width
        ));

        m_dressedCamera = camera;
        m_dressedFarDistance = WorldRenderFarDistance.Resolve(defaults: m_mirror.Definition.Render);
        m_views.Clear();
        m_views.Add(item: new SdfViewSnapshot(
            Camera: camera,
            Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
        ) {
            Quality = ReducedQuality,
        });
        Film?.Invoke(obj: m_views);

        // The mirrored world's own sky and lighting, its keys on the mirrored world's own clocks.
        var environment = m_environment.Resolve(
            definition: m_mirror.Definition,
            mirror: m_mirror.FollowState(),
            revision: m_mirror.DefinitionRevision,
            shadows: m_shadowSettings?.Invoke(),
            layers: SkyLayers,
            shadowSelection: m_deliveredShadows
        );

        FitSkyViews?.Invoke(m_views, environment.Sky, width, height);

        return new SdfFrame(
            Program: program,
            ProgramChanged: programChanged,
            Time: 0f,
            Views: m_views
        ) {
            // The destination's own presented tick, never the viewer's, so its sky and media show its authority's time.
            Clock = m_mirror.FollowState().Presented,
            DynamicTransforms = transforms,
            MovedTransforms = moved,
            // The mirrored world's own far plane (its render.farDistance), so the panel frames the same depth its
            // authority renders.
            FarDistance = m_dressedFarDistance,
            IndirectBodies = m_mirror.Definition.Render.Indirect?.Bodies ?? SdfIndirectParticipation.Default,
            IndirectSources = environment.Indirect.Gains.Sources,
            IndirectGains = environment.Indirect.Gains,
            IndirectBounces = environment.Indirect.Bounces,
            IndirectApply = environment.Indirect.Apply,
            Lights = environment.Lights,
            ShadowFadeVariants = WorldShadowSettings.FadeVariants(render: m_mirror.Definition.Render),
            Sky = environment.Sky,
            // The mirrored world's static placements' meshes, then its stamp pool's.
            MeshDraws = meshDraws,
            MeshDrawsRevision = meshDrawsRevision,
        };
    }
    /// <inheritdoc/>
    /// <remarks>The placement branch chooses static reservation or emission. The avatar branch is
    /// appended after it, in both the probe and the live arm: <see cref="WorldRigCatalog.Emit"/> already owns its
    /// own probe-vs-live split internally (see <see cref="EmitAvatars"/>), so this call site never branches on
    /// <see cref="SdfEmitContext.Probe"/> a second time for it.</remarks>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        var definition = Scoped(definition: m_mirror.Definition);

        if (context.Probe) {
            WorldSessionRenderEnvelope.EmitProbe(
                builder: builder,
                candidate: definition,
                bodyColor: m_bodyColor,
                colors: BakedColors(),
                pool: m_pool,
                slotBase: context.SlotBase
            );

            return;
        } else {
            m_emittedDefinition = definition;
            m_picks.Clear();
            var colors = BakedColors();

            colors.Begin();
            // The destination's creations' text runs are not emitted: only its text screens draw text, as decals
            // (ScreenDecals).
            var meshDraws = new List<SdfMeshDraw>();

            // The pool reconciles first on every rebuild: animated placements root statically, attached ones on
            // their target body, and the census's bodies on their live poses, as the local scene's pool does.
            m_census.Refresh(source: m_source);
            m_pool.Reconcile(
                bodyStamps: ((m_onlyPrototypes is null) ? m_census.Stamps : []),
                creations: definition.Creations,
                dynamics: definition.Dynamics,
                placements: definition.Placements
            );
            WorldPlacementStamper.EmitStatic(
                builder: builder,
                colors: colors,
                definition: definition,
                creations: definition.Creations,
                placements: definition.Placements,
                palettes: m_palettes,
                meshDraws: meshDraws,
                picks: m_picks
            );
            m_meshDraws.Static = meshDraws;

            // The destination's screens: its rows, then the faces its creations seat at the reserved band, within what
            // the probe reserves (WorldSessionRenderEnvelope). The band's unseated placeholders are left out, so a world
            // with no screen keeps a program that may stand unchanged between frames.
            WorldStaticSceneEmit.Emit(
                builder: builder,
                derivedFaces: WorldPrototypeFacets.Seated(definition: definition),
                screens: definition.Screens
            );
            m_pool.Emit(
                builder: builder,
                colors: colors,
                maxPlacementScale: definition.Authoring.MaxPlacementScale,
                probeWorstCase: false,
                slotBase: (context.SlotBase + WorldRigCatalog.DynamicTransformCapacity),
                picks: m_picks
            );
        }

        EmitAvatars(
            builder: builder,
            probeWorstCase: false,
            slotBase: context.SlotBase
        );
    }

    // Whether the scene draws a body's avatar: the mirror's active bodies, none in far geometry.
    private bool HoldsAvatar(int index) => ((m_onlyPrototypes is null) && m_mirror.IsActive(index: index));
    // The definition the scene emits: the whole of it, or, for far geometry, its placements of the named prototypes alone and
    // none of its screens.
    private WorldDefinition Scoped(WorldDefinition definition) => ((m_onlyPrototypes is null)
        ? definition
        : (definition with {
            PlacementRowsRaw = [.. definition.Placements.Where(predicate: placement => m_onlyPrototypes.Contains(item: placement.PrototypeId))],
            ScreensRaw = null,
        }));

    /// <summary>Measures a proposed destination definition against this view's frozen render envelope.</summary>
    public (int Words, int Instances) MeasureCandidate(WorldDefinition candidate) =>
        WorldSessionRenderEnvelope.MeasureCandidate(
            candidate: Scoped(definition: candidate),
            bodyColor: m_bodyColor,
            colors: BakedColors(),
            pool: m_pool
        );
    /// <inheritdoc/>
    /// <remarks>Packs every mirrored-active avatar's interpolated pose into its frozen catalog leaf slots (see
    /// <see cref="WorldSessionMirror.InterpolationAlpha"/> for the timebase) plus a distance-driven gait phase, repacking
    /// an avatar only while that pose moves; an avatar that stops being mirrored parks its range once.</remarks>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context, SdfMovedTransforms moved) {
        var alpha = m_mirror.InterpolationAlpha;

        for (var index = 0; (index < WorldBodiesLimits.CapacityCeiling); index++) {
            if (!HoldsAvatar(index: index)) {
                m_avatarPoseSeeded[index] = false;

                if (m_avatarOwners.Vacate(
                    moved: moved,
                    owner: index
                )) {
                    WorldTransformOwners.ParkBody(
                        avatar: index,
                        catalogBase: context.SlotBase,
                        moved: moved,
                        parkPosition: context.ParkPosition,
                        table: slots
                    );
                }

                continue;
            }

            var position = Vector3.Lerp(
                value1: m_mirror.PreviousPosition(index: index),
                value2: m_mirror.CurrentPosition(index: index),
                amount: alpha
            );
            // Quaternion.Lerp is the nlerp: shortest-path dot-sign flip and renormalize — the SAME formula
            // Client.WorldClient.UpdateRenderPoses uses.
            var orientation = Quaternion.Lerp(
                quaternion1: m_mirror.PreviousOrientation(index: index),
                quaternion2: m_mirror.CurrentOrientation(index: index),
                amount: alpha
            );
            var address = m_mirror.Address(index: index);

            if (!m_avatarOwners.Wake(
                castsSoftShadow: m_castsAvatarShadows,
                deltaSeconds: 1f,
                discontinuity: (
                !m_avatarPoseSeeded[index] ||
                (m_avatarMotionAddresses[index] != address)
            ),
                moved: moved,
                orientation: orientation,
                owner: index,
                position: position
            )) {
                continue;
            }

            WorldMirroredAvatarBand.AdvanceGait(
                address: address,
                gaitPhase: ref m_avatarGaitPhases[index],
                lastAddress: ref m_avatarMotionAddresses[index],
                lastPosition: ref m_avatarPreviousPositions[index],
                position: position,
                seeded: ref m_avatarPoseSeeded[index]
            );

            m_avatarOwners.Settle(
                deltaSeconds: 1f,
                moved: WorldTransformOwners.PackBody(
                avatar: index,
                // Session panels omit avatar shadows; a routed scene participates when its host enables them.
                castsSoftShadow: m_castsAvatarShadows,
                catalogBase: context.SlotBase,
                gaitPhase: (m_avatarGaitPhases[index] * m_emittedGaitAmplitudes[index]),
                moved: moved,
                rig: m_emittedRigs[index],
                rootOrientation: orientation,
                rootPosition: (m_pool.HasBodyRegistration(bodyIndex: index)
                ? context.ParkPosition
                : position),
                reseat: m_avatarOwners.Reseats(owner: index),
                scale: m_emittedScales[index],
                table: slots
            ),
                owner: index
            );
        }

        // The pool packs after the avatar catalog, whose reserved slots it sits past: animated placements ride their
        // static pose, attached and body-rooted stamps the destination's interpolated body pose.
        m_pool.PackTransforms(
            client: m_source,
            moved: moved,
            parkPosition: context.ParkPosition,
            slotBase: (context.SlotBase + WorldRigCatalog.DynamicTransformCapacity),
            transforms: slots
        );
    }
    /// <summary>Sets (or clears) the fit a window session renders through: asked once as each frame is dressed, it returns
    /// the off-axis frustum fit against the border pair's two face rows and the viewer's eye in that same frame, its
    /// shear carried as <see cref="CameraSnapshot.FrustumOffset"/>. A fit returning <see langword="null"/> (no eye or
    /// aperture yet, or the fit refused — see <c>SdfAsymmetricFrustum.TryFit</c>) falls back to
    /// <see cref="ResolveCamera"/>'s ordinary named/default projection for that frame.</summary>
    /// <param name="fit">The window's fit, or <see langword="null"/> for a session that is not a window.</param>
    public void SetWindowFit(Func<CameraSnapshot?>? fit) => m_windowFit = fit;
    /// <summary>Finds the camera the last frame <see cref="Dress"/> dressed renders from, in the destination's own
    /// space: a window's fitted camera, its shear included, or the named or default projection. A hit on the session's
    /// image continues through it into the destination.</summary>
    /// <param name="camera">The camera when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> once a frame has been dressed.</returns>
    public bool TryCamera(out CameraSnapshot camera) {
        camera = m_dressedCamera.GetValueOrDefault();

        return m_dressedCamera.HasValue;
    }
    /// <summary>Finds the surface a ray meets among the destination's static placements a session view shows, marched in
    /// fixed point (<see cref="SdfFieldEvaluator.Raycast"/>) out to the last dressed frame's far distance, measured from
    /// the dressed camera's position as the view pass measures it: a ray that starts on the camera's near plane (a
    /// window's glass) has only the far distance less its distance from the camera's position left to march. The field
    /// is the static placements alone, emitted once per dressed program: the fixed-point evaluator takes no dynamic
    /// transforms, so neither a mirrored avatar nor a creation the stamp pool draws is a surface a pick lands on.</summary>
    /// <param name="ray">The ray, in the destination's space: one cast through the dressed camera's image
    /// (<see cref="TryCamera"/>, <see cref="SourceRay.Through"/>).</param>
    /// <param name="point">The point the ray meets, in the destination's space, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a frame has been dressed, the evaluator admits the static placements'
    /// program, and the ray proves a surface within the far distance; a bounded, non-converged march answers nothing.</returns>
    public bool TrySurface(SourceRay ray, out FixedVector3 point) => TrySurface(
        from: m_dressedCamera.GetValueOrDefault().Position,
        point: out point,
        ray: ray
    );
    /// <summary>Finds the surface a ray meets among the destination's static placements, as <see cref="TrySurface(SourceRay, out FixedVector3)"/>
    /// does, with the far distance measured from the position of the camera the ray was cast through: one view of a frame
    /// that several views share (<see cref="WorldRoutedScene.TrySurface(int, SourceRay, out FixedVector3)"/>).</summary>
    /// <param name="ray">The ray, in the destination's space.</param>
    /// <param name="from">The position of the camera the ray was cast through, which the far distance is measured from.</param>
    /// <param name="point">The point the ray meets, in the destination's space, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a frame has been dressed, the evaluator admits the static placements'
    /// program, and the ray proves a surface within the far distance.</returns>
    public bool TrySurface(SourceRay ray, Vector3 from, out FixedVector3 point) => TrySurface(
        from: from,
        normal: out _,
        point: out point,
        ray: ray
    );
    /// <summary>Finds the surface a ray meets among the destination's static placements, as
    /// <see cref="TrySurface(SourceRay, Vector3, out FixedVector3)"/> does, with the surface's unit normal there: the
    /// static field's gradient, or zero where the field gives none.</summary>
    /// <param name="ray">The ray, in the destination's space.</param>
    /// <param name="from">The position of the camera the ray was cast through, which the far distance is measured from.</param>
    /// <param name="point">The point the ray meets, in the destination's space, when this returns <see langword="true"/>.</param>
    /// <param name="normal">The surface's unit normal at the point, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a frame has been dressed, the evaluator admits the static placements'
    /// program, and the ray proves a surface within the far distance.</returns>
    public bool TrySurface(SourceRay ray, Vector3 from, out FixedVector3 point, out Vector3 normal) {
        point = default;
        normal = Vector3.Zero;

        if ((m_lastProgram is not { } program) || (m_dressedDefinition is not { } definition)) {
            return false;
        }

        if (!ReferenceEquals(
            objA: m_dressedFieldProgram,
            objB: program
        )) {
            var builder = new SdfProgramBuilder();

            WorldPlacementStamper.EmitStatic(
                builder: builder,
                creations: definition.Creations,
                definition: definition,
                placements: definition.Placements
            );
            m_dressedFieldProgram = program;

            try {
                m_dressedField = new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
            } catch (ArgumentException) {
                // A static placement the fixed-point evaluator does not interpret (a path, a non-uniform scale).
                m_dressedField = null;
            }
        }

        // The far distance is measured from the camera's position; the ray's origin already lies this far along it.
        var reach = (FixedQ4816.FromDouble(value: m_dressedFarDistance) - (ray.Origin - FixedVector3.FromVector3(value: from)).Length);

        if (
            (m_dressedField is not { } field) ||
            (reach <= FixedQ4816.Zero) ||
            !field.Raycast(
                dir: ray.Direction,
                hit: out var hit,
                maxDist: reach,
                origin: FixedPosition.FromLocal(local: ray.Origin)
            ) ||
            (hit.Confidence != WorldQueryConfidence.Exact)
        ) {
            return false;
        }

        point = (hit.Point - FixedPosition.Zero);

        if (field.TryFieldGradient(gradient: out var gradient, position: hit.Point)) {
            var unit = Vector3.Normalize(value: gradient.ToVector3());

            normal = (float.IsFinite(f: unit.X) ? unit : Vector3.Zero);
        }

        return true;
    }
    /// <summary>Writes five components, never their sum: the definition-delivery revision, the mirrored snapshot's
    /// declared-set/palette revision (<see cref="WorldSessionMirror.SnapshotRevision"/>, assigned from the wire and
    /// able to move down), a counter that moves when a bound color the live build baked moves in the session's state
    /// mirror (<see cref="WorldBakedColors.TryTakeMove"/>, as <c>WorldSceneEmitter.WriteRevision</c> does for the local
    /// world), and a counter that moves when a stamped body's live scale moves
    /// (<see cref="WorldBodyStampCensus.TryTakeMove"/>), and the host's avatar colors — the same non-summing rule <see cref="WorldClient.WriteRevision"/> documents for the
    /// identical reason: a rebuild must never be maskable by two counters moving in opposite directions.</summary>
    public void WriteRevision(Span<int> destination) {
        destination[0] = m_mirror.DefinitionRevision;
        destination[1] = m_mirror.SnapshotRevision;

        if (BakedColors().TryTakeMove()) {
            m_bakedColorRevision++;
        }

        destination[2] = m_bakedColorRevision;

        // After the baked colors' follow, so the census reads the latest delivery.
        if (m_census.TryTakeMove()) {
            m_censusRevision++;
        }

        destination[3] = m_censusRevision;
        if (m_bodyColors is { } colors) {
            for (var index = 0; (index < colors.Length); index++) {
                if (!m_mirror.IsActive(index: index)) {
                    continue;
                }
                var color = m_bodyColor(arg: index);

                if (colors[index] != color) {
                    colors[index] = color;
                    m_bodyColorRevision++;
                }
            }
        }
        destination[4] = m_bodyColorRevision;
    }

    // The bound colors the static build bakes, over the session's own followed state mirror: the one path the local
    // scene emitter reads its colors through (WorldBakedColors over WorldClient.StateMirror). Following first brings
    // the mirror up to the latest delivery, so a state-cell write reaches TryTakeMove.
    private WorldBakedColors BakedColors() {
        var mirror = m_mirror.FollowState();

        if (
            (m_bakedColors is null) ||
            !ReferenceEquals(
                objA: m_bakedColorsMirror,
                objB: mirror
            )
        ) {
            m_bakedColors = new WorldBakedColors(mirror: mirror);
            m_bakedColorsMirror = mirror;
        }

        return m_bakedColors;
    }

    /// <summary>The frozen transform-slot count this emitter declares: maximum-sized catalog ranges for the detailed
    /// band and one root slot per remaining crowd body — the same hybrid worst case
    /// <c>WorldSceneEmitter.DynamicSlotCount</c> reserves for its own avatar range, sized off the engine-wide
    /// <see cref="WorldBodiesLimits.CapacityCeiling"/>, so a full destination can never outgrow this emitter's probe.</summary>
    public int DynamicSlotCount => (WorldRigCatalog.DynamicTransformCapacity + WorldStampPool.DynamicSlotCount);
    /// <inheritdoc/>
    public int RevisionComponentCount => 5;
    /// <inheritdoc/>
    /// <remarks>The mirrored world's static placements' meshes, fixed by each live <see cref="Emit"/>, then its stamp
    /// pool's (<see cref="WorldStampPool.MeshDraws"/>).</remarks>
    public IReadOnlyList<SdfMeshDraw> MeshDraws => m_meshDraws.Draws;
    /// <inheritdoc/>
    public long MeshDrawsRevision => m_meshDraws.Revision;
}
