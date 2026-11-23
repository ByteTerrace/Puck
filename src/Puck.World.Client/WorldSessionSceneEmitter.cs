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
using Puck.World.Authoring;

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
/// <c>WorldSceneEmitter</c> calls for the boot world's own decoration placements — a <see cref="WorldCreationInstances"/>
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
    /// <summary>Reads the last presented slot census of this session when it presents the queried authority, matched by
    /// the delivered version's activation.</summary>
    /// <param name="authority">The queried authority's document version.</param>
    /// <returns>The census, or <see langword="null"/> for another authority or before a frame.</returns>
    public string? DescribeShadowSlots(Puck.World.Protocol.WorldDocumentVersion authority) =>
        ((m_mirror.Document.Version.Activation == authority.Activation) ? m_environment.DescribeShadowSlots() : null);

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
    private readonly SdfFrame.Writer m_frameWriter = new();
    // The destination's own font catalog and its text screens' decals, followed from its delivered definition (FollowText):
    // the rows, its screens then its seated faces, a text screen's lines are read from.
    private readonly WorldTextCatalog m_text = new();

    private readonly WorldScreenDecals m_decals;

    private WorldDefinition? m_textDefinition;

    private IReadOnlyList<WorldScreen> m_textRows = [];


    private Vector3[]? m_bodyColors;

    private int m_bodyColorRevision;

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
    private readonly WorldCatalogBand m_catalog;

    private readonly WorldBodyStampCensus m_census = new();

    private readonly WorldSceneMeshDraws m_meshDraws;

    private readonly WorldPickMapBuilder m_picks = new();

    /// <inheritdoc/>
    public ISdfPickMap? PickMap => m_picks.Snapshot(pool: m_pool.PickMeshTargets);

    private readonly WorldCreationInstances m_pool;

    /// <summary>The scene's body presentation registrations, shared by routed rig reads and animation reseeding.</summary>
    public WorldCreationInstances BodyStamps => m_pool;

    private readonly WorldSessionStampSource m_source;

    private SdfProgram? m_lastProgram;
    // Retain the geometry's definition through emission and dressing: a later delivery must not change a held image's pick.
    private WorldDefinition? m_emittedDefinition;
    private WorldDefinition? m_dressedDefinition;
    // The WINDOW projection's per-produced-frame override — set by WorldScreenBinder.Publish (the one place with access
    // to both the local eye and the border pair's two face rows) before the render graph renders this view.
    // Null (the default, and every non-window session's steady state) leaves Dress on the ordinary camera path below.
    private Func<WorldWindowFit?>? m_windowFit;
    // The camera the last dressed frame renders from, which a hit on the session's image continues through.
    private CameraSnapshot? m_dressedCamera;
    // The last dressed program's fixed-point field, built when a pick first asks for it, and the far distance a pick
    // marches it to.
    private WorldQueryField? m_dressedField;
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

    // The catalog avatars: the band, the look identity the live program was compiled for, and each body's gait and root
    // follower (WorldCatalogAvatars, the pack every scene shares), and the seconds the next pack advances the followers by.
    private readonly WorldCatalogAvatars m_avatars;
    private float m_avatarSeconds;

    /// <summary>Initializes the emitter over a resolved session mirror and a bind-time-resolved camera choice.</summary>
    /// <param name="mirror">The destination's client-side mirror this emitter reads static geometry from.</param>
    /// <param name="effectiveCameraName">A validated, currently-declared camera name, or <see langword="null"/> for
    /// the destination's default projection.</param>
    /// <param name="fieldOfViewRadians">The vertical field of view used only by the spawn-centroid overview fallback
    /// (a named camera row carries its own lens).</param>
    /// <param name="bodyColor">The color each avatar is painted with by body index, or <see langword="null"/> for the
    /// mirror's own (<see cref="WorldSessionMirror.BodyColor"/>).</param>
    /// <param name="shadowSettings">The routed view's live policy, or its own boot policy when absent.</param>
    /// <param name="domains">The guard that holds the last valid value of a bound value and reports its transitions.</param>
    /// <param name="onlyPrototypes">The prototypes the scene holds, or <see langword="null"/> for the whole world: far
    /// geometry (<c>Puck.SdfVm.Views.InfinityViewKind.Far</c>) emits the placements of these prototypes alone, with no
    /// screen, no avatar and no body stamp, so its cost scales with what it shows.</param>
    /// <param name="jobs">The pool the scene's stamp pool forks its poses across, or <see langword="null"/> to pose them on
    /// the calling thread.</param>
    public WorldSessionSceneEmitter(WorldSessionMirror mirror, string? effectiveCameraName, WorldValueDomainGuard domains, float fieldOfViewRadians = (MathF.PI / 3f), Func<int, Vector3>? bodyColor = null, Func<WorldShadowSettings>? shadowSettings = null, IReadOnlySet<string>? onlyPrototypes = null, Puck.Hosting.Jobs.JobPool? jobs = null) {
        m_pool = new WorldCreationInstances(jobs: jobs);
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: domains);

        m_domains = domains;
        m_environment = new WorldEnvironmentResolve(domains: domains);

        m_mirror = mirror;
        m_bodyColor = (bodyColor ?? mirror.BodyColor);
        m_bodyColors = ((bodyColor is null) ? null : []);
        m_onlyPrototypes = onlyPrototypes;
        m_shadowSettings = shadowSettings;
        m_effectiveCameraName = effectiveCameraName;
        m_fieldOfViewRadians = fieldOfViewRadians;
        m_meshDraws = new WorldSceneMeshDraws(pool: m_pool);
        m_source = new WorldSessionStampSource(mirror: mirror);
        m_catalog = new WorldCatalogBand(m_source.CopyActiveBodies, HoldsAvatar, index => (mirror.CatalogRig(index), m_source.Look(index), m_bodyColor(index)));
        m_avatars = new WorldCatalogAvatars(band: m_catalog);
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
    /// <summary>Gets or sets what reads the display pixels a metre covers one metre in front of the presentation's
    /// viewer (<see cref="WorldSeatViewports.ViewerPixelsPerMetre"/>), which a fitted window's mesh demand measures by
    /// (<see cref="WorldWindowFrustumFit.ShownHeight"/>). <see langword="null"/>, the default, or a reading of zero
    /// measures the view in its own image.</summary>
    public Func<float>? ViewerPixelsPerMetre { get; set; }
    /// <summary>The host's live render levers that reach this world (its cel-shading override and exposure pin), or
    /// <see langword="null"/> to follow this world's document alone.</summary>
    public WorldRenderSettings? Session { get; init; }
    /// <summary>Gets the render-graph instance name the emitter's own view is presented under, whose share of the display
    /// selects what the nested-view table gives up for it (<see cref="WorldRenderSettings.NestedViewQuality"/>), or
    /// <see langword="null"/> for a view presented under no name of its own, which counts as filling the display.</summary>
    public string? ViewName { get; init; }
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
    /// <summary>Gets why the destination's font catalog does not resolve, which leaves its text screens blank: a font
    /// of a world delivered from a remote authority that this process does not hold, a font
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
    // Registers the avatar palette and emits the mirrored avatars the band holds, as the boot scene emits its own
    // (WorldCatalogAvatars): the program draws exactly the avatars the pack poses.
    private void EmitAvatars(SdfProgramBuilder builder, int slotBase) {
        var definition = m_mirror.Definition;

        m_avatars.EmitPalette(builder: builder, dynamics: definition.Dynamics, noseFactor: definition.PlayerDefaults.NoseFactor);
        m_avatars.EmitBodies(builder: builder, picks: m_picks, slotBase: slotBase);
    }

    // The rig each authored camera program compiles to against one state mirror, recompiled only when the cache's
    // inputs move. A rig ResolveCamera resolves holds no history (its look and spread stay at rest), so the cached
    // rig answers what a fresh compile would.
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldStateMirror, Puck.Abstractions.CodeDerivedTable<WorldCameraProgram, WorldCameraRigCompiler.Cache>> ResolvedRigs = new();
    // Each destination mirror read as the pose source its cameras' anchors resolve against, so a resolve allocates none.
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldSessionMirror, WorldSessionStampSource> AnchorSources = new();

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
            // The one resolver over the destination's mirrored poses, with no stamp pool: a placement rides its rest
            // pose, an entity its mirrored body, and what the mirror cannot answer (a part, a seat, since the
            // presentation seats no one in the destination) frames the origin.
            var (position, orientation) = (WorldAnchorPoses.TryResolve(
                anchor: cameraRow.Anchor,
                pose: out var anchorPose,
                scope: new WorldAnchorScope(Source: AnchorSources.GetValue(key: mirror, derive: static held => new WorldSessionStampSource(mirror: held)), Stamps: null, Transforms: default)
            )
                ? (anchorPose.Position, anchorPose.Orientation)
                : (Vector3.Zero, Quaternion.Identity));
            var state = mirror.FollowState();
            var cache = ResolvedRigs.GetValue(key: state, derive: static _ => new()).GetValue(key: cameraRow.Rig, derive: static _ => new());
            IWorldCameraProgramRig rig;

            lock (cache) {
                rig = cache.Resolve(
                    definition: definition,
                    domains: domains,
                    mirror: state,
                    program: cameraRow.Rig
                );
            }
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

        return WorldOverviewCamera.Resolve(
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
    /// <inheritdoc/>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, SdfMeshDrawList meshDraws, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
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
        m_avatarSeconds += deltaSeconds;

        var fitted = m_windowFit?.Invoke();
        var camera = (fitted?.Camera ?? ResolveCamera(
            cameraName: m_effectiveCameraName,
            domains: m_domains,
            height: height,
            mirror: m_mirror,
            width: width
        ));

        // The display pixels the glass spans from the viewer's eye, for a window fitted to it; a view no fit measures
        // takes its presented height (WorldRenderSettings.NestedDemandHeight).
        var fittedHeight = ((fitted is null)
            ? 0f
            : WorldWindowFrustumFit.ShownHeight(fitted: in camera, viewerPixelsPerMetre: (ViewerPixelsPerMetre?.Invoke() ?? 0f)));

        m_dressedCamera = camera;
        m_dressedFarDistance = WorldRenderFarDistance.Resolve(defaults: m_mirror.Definition.Render);
        m_views.Clear();
        m_views.Add(item: new SdfViewSnapshot(
            Camera: camera,
            Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f)
        ) {
            DemandHeight = (Session?.NestedDemandHeight(fitted: fittedHeight, imageHeight: height, view: ViewName) ?? fittedHeight),
            DemandWithinView = (fitted is not null),
            ImageWindow = (fitted?.Window ?? default),
            Quality = (Session?.NestedViewQuality(view: ViewName) ?? default),
            RenderScale = (((Session is { } settings) && (ViewName is { } name)) ? settings.Ceiling(view: name) : 1f),
            // A window shows the luminance beyond its glass, which the view looking through it exposes.
            Unexposed = (m_windowFit is not null),
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

        // The session's one frame, written again for each capture: a residency renders its own copy of it.
        var frame = m_frameWriter.Write(
            content: new SdfFrameContent(
                Program: program,
                ProgramChanged: programChanged,
                Time: 0f,
                Views: m_views
            ) {
                // The destination's own presented tick, never the viewer's, so its sky and media show its authority's time.
                Clock = m_mirror.FollowState().Presented,
                WorldLanes = environment.Lanes,
                DynamicTransforms = transforms,
                MovedTransforms = moved,
                Lights = environment.Lights,
                Exposure = WorldRenderSettings.Pinned(exposure: environment.Exposure, pin: Session?.ExposurePin),
                Sky = environment.Sky,
                // The mirrored world's static placements' meshes, then its stamp pool's.
                MeshDraws = meshDraws.Draws,
                MeshDrawsRevision = meshDraws.Revision,
                MeshDrawChanges = meshDraws.Changes,
                MeshDrawChangesSerial = meshDraws.Serial,
            },
            settings: new SdfFrameSettings {
                // The mirrored world's own far plane (its render.farDistance), so the panel frames the same depth its
                // authority renders.
                FarDistance = m_dressedFarDistance,
                ShadowCoverage = WorldRenderFarDistance.ResolveShadowCoverage(defaults: m_mirror.Definition.Render),
                // The destination's own authored tier: its views read its own irradiance volume, which also meters its
                // exposure and occludes its fixtures, as a seat presented there would.
                IndirectTier = (m_mirror.Definition.Render.Indirect?.Tier ?? SdfIndirectTier.Medium),
                // Whether the volume's rays take the ray hardware changes what the trace costs on this device, not what
                // it answers, so it is the presenting session's lever (render.rayQueries at its boot, then `world.rays`,
                // a quality preset or a device profile), never the destination document's: a painting world authors no
                // device's cost.
                RayQueries = (Session?.RayQueries ?? m_mirror.Definition.Render.RayQueries),
                // The distant shadow map's reach, texels and tolerance are a device table's row, so the presenting
                // session's lever too, as the rays are.
                DistantShadows = WorldRenderFarDistance.ResolveDistantShadows(authored: ((Session is { } session) ? session.DistantShadows : m_mirror.Definition.Render.DistantShadows)),
                FarShadows = WorldRenderFarDistance.ResolveFarShadows(authored: ((Session is { } presenting) ? presenting.FarShadows : m_mirror.Definition.Render.FarShadows)),
                IndirectSources = environment.Indirect.Gains.Sources,
                IndirectGains = environment.Indirect.Gains,
                IndirectBounces = environment.Indirect.Bounces,
                // What a frame may spend on lighting is the presenting device's row, as the distant map's and the rays are.
                IndirectBudget = WorldRenderSettings.ResolveIndirectBudget(authored: environment.Indirect.Budget, row: Session?.IndirectBudget),
                // What the presenting session's nested-view table keeps of the volume at the glass's presented size, and
                // the share of the display the glass covers, which weighs this world's background work against the
                // others'.
                IndirectLevels = (Session?.NestedIndirectLevels(view: ViewName) ?? 0),
                DisplayShare = (Session?.NestedDisplayShare(view: ViewName) ?? 1f),
                IndirectLightingGrid = environment.Indirect.LightingGrid,
                IndirectApply = environment.Indirect.Apply,
                Toon = ToonStyleDocument.ResolveDefault(toon: m_mirror.Definition.Render.Toon, shown: Session?.ToonOverride),
                Tone = WorldRenderSettings.ToneOf(render: m_mirror.Definition.Render),
                ShadePrograms = Session?.ShadePrograms ?? true,
                ShadeDefault = WorldRenderSettings.CompileShade(Session?.ShadeOverride ?? m_mirror.Definition.Render.Shade?.Definition),
                ShadeDeclared = WorldRenderSettings.DeclaredShades(definition: m_mirror.Definition),
            }
        );

        return ((HostFrame is { } host) ? TakeLevers(frame: m_frameWriter, height: height, host: host(), views: m_views, width: width) : frame);
    }
    /// <summary>Gets or sets the frame the presentation dressed this frame, whose levers the frames of a session rendered
    /// through a residency of its own take (<see cref="TakeLevers"/>), or <see langword="null"/> for an emitter whose scene
    /// takes them itself (<see cref="WorldRoutedScene"/>).</summary>
    public Func<SdfFrame?>? HostFrame { get; set; }
    /// <summary>Gets or sets what hands the stamp pool this frame's presentation rate and update rates before it packs
    /// (<see cref="WorldFramePresenter.PresentStamps"/>), or <see langword="null"/> for an emitter whose scene does.</summary>
    public Action<WorldCreationInstances>? PresentStamps { get; set; }
    /// <summary>Puts a frame under the presentation's levers (its cadence gate, cull and detail levels, update rates,
    /// rate counters and intervals), and rates the stamp pool by the views and shadow cascades the frame renders its world
    /// through, so its next pack holds a body or animated placement none of them, nor a shadow of them, may show. Each
    /// view counts its region of the frame's height, as the presentation counts a view it has no display share for.</summary>
    /// <param name="frame">The writer of the frame dressed for the session or scene, which holds its final views.</param>
    /// <param name="host">The frame the presentation dressed this frame, or <see langword="null"/> before it has dressed
    /// one, when the frame keeps its own levers.</param>
    /// <param name="views">The frame's final views.</param>
    /// <param name="width">The frame's width, in pixels.</param>
    /// <param name="height">The frame's height, in pixels.</param>
    /// <returns>The frame under the presentation's levers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> or <paramref name="views"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="frame"/> has written no frame yet.</exception>
    public SdfFrame TakeLevers(SdfFrame.Writer frame, SdfFrame? host, IReadOnlyList<SdfViewSnapshot> views, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(argument: frame);
        ArgumentNullException.ThrowIfNull(argument: views);

        var written = (frame.Frame ?? throw new InvalidOperationException(message: "A frame takes the presentation's levers only once it is written."));
        var dressed = ((host is null) ? written : frame.Write(settings: (written.Settings with {
            EnableCadenceGate = host.EnableCadenceGate,
            MeshCull = host.MeshCull,
            MeshDetail = host.MeshDetail,
            UpdateRates = host.UpdateRates,
            RateCounts = host.RateCounts,
            RateIntervals = host.RateIntervals,
        })));

        m_pool.RateBy(counts: dressed.RateCounts, height: height, intervals: dressed.RateIntervals, views: views);
        m_pool.ShadowBy(frame: dressed);
        return dressed;
    }
    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        var definition = Scoped(definition: m_mirror.Definition);

        m_emittedDefinition = definition;
        m_picks.Clear();
        var colors = BakedColors();

        colors.Begin();
        // The destination's creations' text runs are not emitted: only its text screens draw text, as decals
        // (ScreenDecals).
        var meshDraws = new List<SdfMeshDraw>();

        WorldPlacementStamper.EmitStatic(
            builder: builder,
            colors: colors,
            definition: definition,
            creations: definition.Creations,
            placements: definition.Placements,
            palettes: m_palettes,
            meshDraws: meshDraws,
            picks: m_picks,
            sharedAssets: m_pool.Rigid
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
            slotBase: (context.SlotBase + m_catalog.SlotCount),
            picks: m_picks
        );

        EmitAvatars(
            builder: builder,
            slotBase: context.SlotBase
        );
    }

    // Whether the scene draws a body's avatar: the mirror's active bodies, none in far geometry.
    private bool HoldsAvatar(int index) => ((m_onlyPrototypes is null) && !m_pool.HasBodyRegistration(index) && m_mirror.IsActive(index: index));
    // The definition the scene emits: the whole of it, or, for far geometry, its placements of the named prototypes alone and
    // none of its screens.
    private WorldDefinition Scoped(WorldDefinition definition) => ((m_onlyPrototypes is null)
        ? definition
        : (definition with {
            PlacementRowsRaw = [.. definition.Placements.Where(predicate: placement => m_onlyPrototypes.Contains(item: placement.PrototypeId))],
            ScreensRaw = null,
        }));

    /// <summary>Counts what a proposed destination definition costs this view's composed program: its scene as this
    /// view draws it, without the adjacency bands a session never reserves.</summary>
    /// <param name="candidate">The candidate destination definition.</param>
    /// <returns>The cost.</returns>
    public WorldRenderCost MeasureCandidate(WorldDefinition candidate) => WorldSceneEnvelope.Of(
        adjacency: false,
        candidate: Scoped(definition: candidate)
    ).Cost;
    /// <inheritdoc/>
    /// <remarks>Packs every mirrored-active avatar's interpolated pose into its frozen catalog leaf slots (see
    /// <see cref="WorldSessionMirror.InterpolationAlpha"/> for the timebase) plus a distance-driven gait phase, through
    /// the pack the boot scene shares (<see cref="WorldCatalogAvatars.Pack"/>): an avatar repacks only while that pose
    /// moves, and one that stops being mirrored parks its range once.</remarks>
    public void PackDynamicTransforms(DynamicTransform[] slots, in SdfEmitContext context, SdfMovedTransforms moved) {
        var deltaSeconds = m_avatarSeconds;

        m_avatarSeconds = 0f;
        m_avatars.Pack(context: in context, deltaSeconds: deltaSeconds, moved: moved, pool: m_pool, slots: slots, source: m_source);

        // The pool packs after the avatar catalog, whose reserved slots it sits past: animated placements ride their
        // static pose, attached and body-rooted stamps the destination's interpolated body pose.
        if (m_onlyPrototypes is null) { m_pool.PresentCues(m_mirror.CueFrames, m_source); }
        m_pool.PackTransforms(
            client: m_source,
            moved: moved,
            parkPosition: context.ParkPosition,
            slotBase: (context.SlotBase + m_catalog.SlotCount),
            transforms: slots
        );
    }
    /// <summary>Sets (or clears) the fit a window session renders through: asked once as each frame is dressed, it returns
    /// the off-axis frustum fit against the border pair's two face rows and the part of the glass the viewer sees in that
    /// same frame (<see cref="WorldWindowFit.Window"/>, which the view carries as its image window), its shear carried as <see cref="CameraSnapshot.FrustumOffset"/>. A fit returning <see langword="null"/> (no eye or
    /// aperture yet, or the fit refused — see <c>SdfAsymmetricFrustum.TryFit</c>) falls back to
    /// <see cref="ResolveCamera"/>'s ordinary named/default projection for that frame.</summary>
    /// <param name="fit">The window's fit, or <see langword="null"/> for a session that is not a window.</param>
    public void SetWindowFit(Func<WorldWindowFit?>? fit) => m_windowFit = fit;
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
                placements: WorldPlacementStamper.Queryable(definition: definition, placements: definition.Placements)
            );
            m_dressedFieldProgram = program;

            try {
                m_dressedField = new WorldQueryField(fixedPart: new SdfFieldEvaluator(program: builder.Build(), reading: SdfFieldReading.Contact, regional: true));
            } catch (ArgumentException) {
                // A static placement the fixed-point evaluator does not interpret (a path, a non-uniform scale).
                m_dressedField = null;
            }
        }

        if (!WorldFieldSurface.TryMeet(farDistance: m_dressedFarDistance, field: m_dressedField, from: from, point: out var met, ray: ray)) {
            return false;
        }

        point = (met - FixedPosition.Zero);
        normal = WorldFieldSurface.NormalAt(field: m_dressedField!, point: met);

        return true;
    }

    private int m_preparedDefinition = int.MinValue;
    private int m_preparedComposing = int.MinValue;
    private int m_preparedSnapshot = int.MinValue;

    public void PrepareFrame() {
        // The pool packs during the capture that follows, so it rates its bodies by this frame's presentation rate.
        PresentStamps?.Invoke(obj: m_pool);
        PrepareRegistrations();
        m_pool.PrepareGuess(source: m_source);
    }
    /// <summary>Gets or sets what offers the presenting client's guess to this scene's guess layer
    /// (<see cref="WorldSessionStampSource.Guess"/>): set only on the scene a seat may be presented in.</summary>
    public Func<IWorldSeatGuess?>? SeatGuess {
        get => m_source.Guess;
        set => m_source.Guess = value;
    }
    private void PrepareRegistrations() {
        var standing = ((m_preparedSnapshot == m_mirror.SnapshotRevision) && (m_preparedComposing == m_mirror.ComposingRevision) && !m_census.TryTakeMove());

        if (standing && (m_preparedDefinition == m_mirror.DefinitionRevision)) { return; }
        m_preparedDefinition = m_mirror.DefinitionRevision;
        m_preparedSnapshot = m_mirror.SnapshotRevision;
        m_preparedComposing = m_mirror.ComposingRevision;
        var definition = Scoped(m_mirror.Definition);

        // A delivery that moved holdings alone, on rows the rigid catalog copies, moves those copies and nothing else.
        if (standing && m_pool.Rigid.TryPresentHoldingsAlone(definition: definition)) { return; }

        m_census.Refresh(m_source);
        var stamps = (m_onlyPrototypes is null) ? m_census.Stamps : [];
        m_catalog.ObserveArticulation(stamps);
        m_pool.Rigid.ReconcileStatic(definition, definition.Placements, riding: true);
        m_pool.Reconcile(definition.PlacementFrames, definition.Placements, definition.Creations, definition.Dynamics, stamps, definition.Looks);
        m_pool.Rigid.PresentHoldings(placements: definition.Placements);
        m_avatars.Prepare();
    }

    public IReadOnlyList<int> MeshAssetSlots => m_pool.Rigid.Slots;
    public IReadOnlyList<SdfMeshInstance> MeshInstances => m_pool.Rigid.Copies;
    /// <inheritdoc/>
    public IReadOnlyList<int> MeshInstanceChanges => m_pool.Rigid.ChangedCopies;
    /// <inheritdoc/>
    public long MeshInstancesRevision => m_pool.Rigid.CopiesRevision;

    /// <summary>Writes each rebuild revision independently so opposing moves cannot mask a change.</summary>
    public void WriteRevision(Span<int> destination) {
        destination[0] = m_mirror.ComposingRevision;
        destination[1] = m_catalog.ArticulatedRevision;

        if (BakedColors().TryTakeMove()) {
            m_bakedColorRevision++;
        }

        destination[2] = m_bakedColorRevision;

        destination[3] = m_catalog.Revision;
        if (m_bodyColors is not null) {
            // The avatars the program draws are the only bodies whose colour it bakes; the record grows with the band.
            Puck.Abstractions.Memory.ArrayCapacity.EnsureLength(array: ref m_bodyColors, required: m_catalog.BodySpan);
            var colors = m_bodyColors;
            foreach (var index in m_catalog.Bodies) {
                var color = m_bodyColor(arg: index);

                if (colors[index] != color) {
                    colors[index] = color;
                    m_bodyColorRevision++;
                }
            }
        }
        destination[4] = m_bodyColorRevision;
        destination[5] = m_pool.Rigid.HoldingComposeRevision;
        destination[6] = m_pool.Rigid.StaticTemplateRevision;
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

    /// <summary>Gets the actual catalog, articulation and local-template transform count.</summary>
    public int DynamicSlotCount => (m_catalog.SlotCount + m_pool.DynamicSlotCount);
    /// <inheritdoc/>
    public int RevisionComponentCount => 7;
    /// <inheritdoc/>
    /// <remarks>The mirrored world's static placements' meshes, fixed by each live <see cref="Emit"/>, then its stamp
    /// pool's (<see cref="WorldCreationInstances.MeshDraws"/>).</remarks>
    public IReadOnlyList<SdfMeshDraw> MeshDraws => m_meshDraws.Draws;
    /// <inheritdoc/>
    public long MeshDrawsRevision => m_meshDraws.Revision;
    /// <inheritdoc/>
    public Puck.Abstractions.Gpu.GpuRowChanges? MeshDrawChanges => m_meshDraws.Changes;
    /// <inheritdoc/>
    public long MeshDrawChangesSerial => m_meshDraws.Serial;
}
