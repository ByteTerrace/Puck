using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>
/// The scene of one endpoint's world as this presentation renders it: the endpoint's own static scene, stamp pool and
/// population, drawn from its delivered definition and state mirror by a <see cref="WorldSessionSceneEmitter"/> over
/// <see cref="WorldAuthorityEndpoint.Mirror"/>, the same emitter a session screen draws a destination with, which lights it
/// under the destination's own sky and lighting, its keys read on the destination's own clocks. Every view of the world is a view of
/// the scene's one frame, so they share one program and one residency, each with its own camera and quality
/// (<see cref="SdfViewSnapshot.Quality"/>):
/// <list type="bullet">
/// <item>each seat presented in the world (<see cref="WorldContinuum.PresentedElsewhere"/>), framed by the camera
/// <see cref="WorldFramePresenter"/> resolved for it this frame at the presentation's quality, latched afresh every frame
/// (<see cref="AddView"/>);</item>
/// <item>each window onto the world attached through <see cref="WorldFramePresenter.AttachWindow"/>, whose view its
/// holder sets (<see cref="WorldRoutedWindow.View"/>), after the seats;</item>
/// <item>each camera of the world a screen shows, which <see cref="Film"/> adds after the windows.</item>
/// </list>
/// The world's text screens draw through its own font catalog (<see cref="GlyphAtlas"/>, <see cref="ScreenDecals"/>).
/// </summary>
public sealed class WorldRoutedScene : ISdfFrameDresser, IDisposable {
    /// <summary>Releases this scene's delivered-tick subscription.</summary>
    public void Dispose() => m_emitter.Dispose();
    /// <summary>Reads this authority's last presented shadow slots.</summary>
    /// <param name="authority">The queried authority's document version.</param>
    /// <returns>The census, or <see langword="null"/> for another authority or before a frame.</returns>
    public string? DescribeShadowSlots(Puck.World.Protocol.WorldDocumentVersion authority) => m_emitter.DescribeShadowSlots(authority: authority);

    private readonly WorldSessionSceneEmitter m_emitter;
    /// <summary>The body presentation registrations of this routed authority.</summary>
    public WorldCreationInstances BodyStamps => m_emitter.BodyStamps;
    private readonly Func<SdfFrame?> m_hostFrame;
    private readonly List<SdfViewSnapshot> m_views = [];
    // The roster slot each latched seat view belongs to (-1 for none), which decides the one view the guess overlay reaches.
    private readonly List<int> m_viewSeats = [];
    private readonly List<WorldRoutedWindow> m_windows = [];
    // The seats and windows the presenter latched last (EndViews), which membership changes wait for, and the views the
    // last dressed frame rendered, kept for a frame with neither: a frame always carries a view.
    private readonly List<WorldRoutedWindow> m_latchedWindows = [];
    private readonly List<SdfViewSnapshot> m_dressedViews = [];
    // The views the last dressed frame carries: the seats' and windows', then the cameras Film added.
    private readonly List<SdfViewSnapshot> m_frameViews = [];

    private int m_latchedSeatCount;

    /// <summary>Initializes the scene of one endpoint.</summary>
    /// <param name="endpoint">The authority the scene draws.</param>
    /// <param name="hostFrame">The frame the boot presentation dressed this frame, whose presentation clock and cadence the
    /// scene's frame takes; <see langword="null"/> before the first.</param>
    /// <param name="bodyColor">The color each avatar is painted with by body index: a local seat keeps the color the
    /// boot presentation paints it with.</param>
    /// <param name="shadowSettings">The presentation's live slot policy.</param>
    /// <param name="skyLayers">The presentation's session-only layer audition.</param>
    /// <param name="domains">The guard that holds the last valid value of a bound value and reports its transitions.</param>
    /// <param name="buildsInBackground">Whether the scene's rebuilt programs build on the thread pool
    /// (<see cref="SdfCompositionFrameSource.BuildsInBackground"/>).</param>
    /// <param name="session">The host's live render levers that reach the destination (its cel-shading override and
    /// exposure pin); absent follows the destination's document.</param>
    public WorldRoutedScene(WorldAuthorityEndpoint endpoint, Func<SdfFrame?> hostFrame, Func<int, Vector3> bodyColor, WorldValueDomainGuard domains, Func<WorldShadowSettings>? shadowSettings = null, WorldSkyAudition? skyLayers = null, bool buildsInBackground = false, WorldRenderSettings? session = null)
        : this(authority: endpoint?.Identity!, bodyColor: bodyColor, buildsInBackground: buildsInBackground, domains: domains, endpoint: endpoint, hostFrame: hostFrame,
            key: endpoint!, mirror: endpoint?.Mirror!, name: endpoint?.Identity!, session: session, shadowSettings: shadowSettings, skyLayers: skyLayers) { }
    /// <summary>Initializes the scene several session screens share: one session's mirror, drawn for every session whose
    /// stream its authority proved is handed the same scene inputs. It holds no endpoint, so no seat is presented in
    /// it.</summary>
    /// <param name="mirror">The mirror of the session whose stream the scene draws.</param>
    /// <param name="authority">The identity of the world the session observes, whose screens the scene's views
    /// show.</param>
    /// <param name="name">The scene's own name, unique among the presentation's scenes, which its views are named by.</param>
    /// <param name="hostFrame">The frame the boot presentation dressed this frame, whose presentation clock and cadence the
    /// scene's frame takes; <see langword="null"/> before the first.</param>
    /// <param name="bodyColor">The color each avatar is painted with by body index.</param>
    /// <param name="domains">The guard that holds the last valid value of a bound value and reports its transitions.</param>
    /// <param name="shadowSettings">The presentation's live slot policy.</param>
    /// <param name="skyLayers">The presentation's session-only layer audition.</param>
    /// <param name="buildsInBackground">Whether the scene's rebuilt programs build on the thread pool.</param>
    /// <param name="session">The host's live render levers that reach the destination.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldRoutedScene(WorldSessionMirror mirror, string authority, string name, Func<SdfFrame?> hostFrame, Func<int, Vector3> bodyColor, WorldValueDomainGuard domains, Func<WorldShadowSettings>? shadowSettings = null, WorldSkyAudition? skyLayers = null, bool buildsInBackground = false, WorldRenderSettings? session = null)
        : this(authority: authority, bodyColor: bodyColor, buildsInBackground: buildsInBackground, domains: domains, endpoint: null, hostFrame: hostFrame,
            key: mirror, mirror: mirror, name: name, session: session, shadowSettings: shadowSettings, skyLayers: skyLayers) { }
    // The one constructor both scene kinds and the presenter build through: what the scene is keyed by, the endpoint a
    // seat may be presented through (none for a shared scene), and the mirror it draws.
    internal WorldRoutedScene(object key, WorldAuthorityEndpoint? endpoint, WorldSessionMirror mirror, string authority, string name, Func<SdfFrame?> hostFrame, Func<int, Vector3> bodyColor, WorldValueDomainGuard domains, Func<WorldShadowSettings>? shadowSettings, WorldSkyAudition? skyLayers, bool buildsInBackground, WorldRenderSettings? session, Puck.Hosting.Jobs.JobPool? jobs = null) {
        ArgumentNullException.ThrowIfNull(argument: domains);
        ArgumentNullException.ThrowIfNull(argument: key);
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentException.ThrowIfNullOrEmpty(argument: authority);
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentNullException.ThrowIfNull(argument: hostFrame);
        ArgumentNullException.ThrowIfNull(argument: bodyColor);

        Key = key;
        Endpoint = endpoint;
        Mirror = mirror;
        Authority = authority;
        Name = name;
        m_hostFrame = hostFrame;
        m_emitter = new WorldSessionSceneEmitter(
            bodyColor: bodyColor,
            shadowSettings: shadowSettings,
            domains: domains,
            effectiveCameraName: null,
            mirror: mirror,
            jobs: jobs
        ) { SkyLayers = skyLayers, Session = session };
        FrameSource = new SdfCompositionFrameSource(
            dresser: this,
            emitters: [m_emitter]
        ) { BuildsInBackground = buildsInBackground };
    }

    /// <summary>Gets what the presentation keys the scene by: its endpoint, or the mirror a shared scene draws.</summary>
    public object Key { get; }
    /// <summary>Gets the authority the scene draws, or <see langword="null"/> for a scene several session screens share,
    /// which no seat is presented in.</summary>
    public WorldAuthorityEndpoint? Endpoint { get; }
    /// <summary>Gets the mirror the scene draws from.</summary>
    public WorldSessionMirror Mirror { get; }
    /// <summary>Gets the identity of the world the scene draws.</summary>
    public string Authority { get; }
    /// <summary>Gets the scene's name, unique among the presentation's scenes: the endpoint's identity, or a shared
    /// scene's own.</summary>
    public string Name { get; }
    /// <summary>Gets the definition the scene's mirror holds.</summary>
    public WorldDefinition Definition => Mirror.Definition;
    /// <summary>Gets the frame source a residency renders the scene through.</summary>
    public SdfCompositionFrameSource FrameSource { get; }
    /// <inheritdoc/>
    public bool TickLocked => (m_hostFrame()?.TickLocked ?? false);
    /// <summary>Gets how many seat views the presenter latched into the scene this frame.</summary>
    public int ViewCount => m_views.Count;
    /// <summary>Gets how many windows are attached to the scene.</summary>
    public int WindowCount => m_windows.Count;
    /// <summary>Gets or sets the demand of the presentation's own frame, for a scene that is a second scene of the world
    /// that frame draws: the views the presentation renders that world through, each with the height its sizes are
    /// measured in. The scene's frames carry them as demand points alone (<see cref="SdfFrameContent.DemandViews"/>), so
    /// the scene plans each group at the tier the presentation's own residency draws it at and the two name the same
    /// meshes. <see langword="null"/>, the default, for a scene of any other world.</summary>
    public Func<IReadOnlyList<SdfViewSnapshot>>? HostDemand { get; set; }
    /// <summary>Gets or sets what names the world the presentation's own frame draws, where this scene is a second scene
    /// of it (<see cref="HostDemand"/>): the scene's frames carry it (<see cref="SdfFrameContent.DemandWorld"/>), so its
    /// bake draws each group at the tier the presentation's own residency draws it at.</summary>
    public object? HostWorld { get; set; }
    /// <summary>Gets or sets what films the world's cameras into each dressed frame: handed the frame's views, the seats'
    /// and windows' first, it adds a view of each camera of the world a screen shows, at an index the caller records.
    /// <see langword="null"/>, the default, films none.</summary>
    public Action<List<SdfViewSnapshot>>? Film { get; set; }
    /// <summary>Gets or sets what fits the named sky layers to the final consuming views. The callback receives this
    /// authority's resolved sky and the frame dimensions after seat, window and camera views have been dressed.</summary>
    public Action<List<SdfViewSnapshot>, SdfSky, uint, uint>? FitSkyViews { get; set; }
    /// <inheritdoc/>
    /// <remarks>The world's own font atlas (<see cref="WorldSessionSceneEmitter.GlyphAtlas"/>).</remarks>
    public SdfGlyphAtlas? GlyphAtlas => m_emitter.GlyphAtlas;
    /// <inheritdoc/>
    /// <remarks>The world's text screens' (<see cref="WorldSessionSceneEmitter.ScreenDecals"/>).</remarks>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => m_emitter.ScreenDecals;
    /// <summary>Gets why the world's font catalog does not resolve, which leaves its text screens blank, or
    /// <see langword="null"/> (<see cref="WorldSessionSceneEmitter.TextFault"/>).</summary>
    public string? TextFault => m_emitter.TextFault;

    /// <summary>Gets or sets what offers the presenting client's guess to this scene's guess layer: the guess for this
    /// scene's endpoint (<see cref="WorldClient.SeatGuessAt"/>), drawn in the guessing seat's own view alone
    /// (<see cref="Dress"/>). <see langword="null"/>, the default and a shared session scene's, offers none.</summary>
    public Func<IWorldSeatGuess?>? SeatGuess {
        get => m_emitter.SeatGuess;
        set => m_emitter.SeatGuess = value;
    }
    /// <summary>Clears the views the presenter latched, before it latches this frame's.</summary>
    public void BeginViews() {
        m_views.Clear();
        m_viewSeats.Clear();
    }
    /// <summary>Latches a seat's view of this world for the frame.</summary>
    /// <param name="view">The seat's view, framed in this world's own coordinates; this scene's residency renders it,
    /// whatever its boot frame's copy says (<see cref="SdfViewSnapshot.Elsewhere"/>).</param>
    /// <param name="seat">The roster slot whose view it is, which takes this scene's guess overlay when the guess is that
    /// seat's (<see cref="WorldCreationInstances.GuessSlot"/>); -1 for none.</param>
    /// <returns>The view's index in the scene's frames.</returns>
    public int AddView(in SdfViewSnapshot view, int seat = -1) {
        m_views.Add(item: (view with { Elsewhere = false }));
        m_viewSeats.Add(item: seat);

        return (m_views.Count - 1);
    }
    /// <summary>Finishes the presenter's view latch. Windows attached after this call enter the next latch; disposing
    /// a window leaves its slot reserved until then, so resolved indices stay valid through the scene's dress.</summary>
    public void EndViews() {
        m_latchedSeatCount = m_views.Count;
        foreach (var window in m_latchedWindows) { window.LatchedOrder = -1; }
        m_latchedWindows.Clear();
        m_latchedWindows.AddRange(collection: m_windows);
        for (var order = 0; (order < m_latchedWindows.Count); order++) { m_latchedWindows[order].LatchedOrder = order; }
    }
    /// <inheritdoc/>
    /// <remarks>The session emitter dresses first, so its stamp pool advances and its dressed definition is retained
    /// exactly as a session screen's; the frame then takes the seats' views and the windows' after them, the host frame's
    /// presentation clock and cadence. The sky, its clock and the environment stay the emitter's: this world's own.</remarks>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, SdfMeshDrawList meshDraws, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        var frame = m_emitter.Dress(
            deltaSeconds: deltaSeconds,
            height: height,
            interpolationAlpha: interpolationAlpha,
            meshDraws: meshDraws,
            moved: moved,
            program: program,
            transforms: transforms,
            width: width
        );

        if ((m_latchedSeatCount + m_latchedWindows.Count) != 0) {
            m_dressedViews.Clear();

            // The pool packed before this dress, so its guess overlay is this frame's; only the guessing seat's own view
            // reads it, and every window, filmed camera and other seat reads the shared pose.
            var guessSlot = m_emitter.BodyStamps.GuessSlot;

            for (var seat = 0; (seat < m_latchedSeatCount); seat++) {
                m_dressedViews.Add(item: (((guessSlot >= 0) && (m_viewSeats[seat] == guessSlot))
                    ? (m_views[seat] with { PoseOverlay = m_emitter.BodyStamps.GuessOverlay })
                    : m_views[seat]));
            }

            // The emitter's own view frames the world's default projection at a session screen's quality.
            foreach (var window in m_latchedWindows) {
                m_dressedViews.Add(item: (window.View ?? ((window.FallbackResolution is { } resolve) ? resolve(frame.Views[0]) : frame.Views[0])));
            }
        } else if (m_dressedViews.Count == 0) {
            m_dressedViews.AddRange(collection: frame.Views);
        }

        m_frameViews.Clear();
        m_frameViews.AddRange(collection: m_dressedViews);
        Film?.Invoke(obj: m_frameViews);
        FitSkyViews?.Invoke(m_frameViews, frame.Sky, width, height);

        // The scene's stamp pool takes the boot presentation's levers (the presenter's PresentStamps), so its next pack
        // rates its bodies by the views and shadow cascades this frame renders the world through, as the boot world's pool
        // is rated by its own. The scene also takes the boot presentation's clock and lockstep.
        var host = m_hostFrame();
        var content = (frame.Content with { Views = m_frameViews });

        if ((HostWorld is { } world) && (HostDemand?.Invoke() is { Count: > 0 } demand)) { content = (content with { DemandViews = demand, DemandWorld = world }); }

        if (host is not null) {
            content = (content with {
                Time = host.Time,
                TickLocked = host.TickLocked,
                ConvergesLighting = host.ConvergesLighting,
            });
        }
        // The document version a diagnostic or the edit-loop clock compares the scene's frames by, as the boot
        // presentation's frames carry theirs (WorldFramePresenter), boxed once for each version rather than every frame.
        var version = Mirror.Document.Version;

        if ((m_diagnosticSource is null) || (m_diagnosticVersion != version)) {
            m_diagnosticVersion = version;
            m_diagnosticSource = version;
        }
        // The scene's one frame, written again for each capture from the emitter's, which the emitter writes again too.
        _ = m_frameWriter.Write(content: in content, settings: (Interest(settings: frame.Settings) with { DiagnosticSource = m_diagnosticSource }));

        return m_emitter.TakeLevers(frame: m_frameWriter, height: height, host: host, views: m_frameViews, width: width);
    }

    // Puts the frame under the interest its views show: a scene a seat is presented in fills the display, and one shown
    // through windows alone is weighed by the largest window and measures the irradiance levels the window reading the
    // most reads, so a world behind a small or unseen glass draws on the presentation's background work and lights
    // itself by that glass, never as though it filled the display.
    private SdfFrameSettings Interest(SdfFrameSettings settings) {
        if ((m_latchedSeatCount != 0) || (m_latchedWindows.Count == 0)) { return settings; }

        var share = 0f;
        var levels = -1;

        foreach (var window in m_latchedWindows) {
            share = MathF.Max(x: share, y: window.DisplayShare);
            levels = (((window.IndirectLevels <= 0) || (levels == 0)) ? 0 : Math.Max(val1: levels, val2: window.IndirectLevels));
        }

        return (settings with { DisplayShare = share, IndirectLevels = levels });
    }

    private readonly SdfFrame.Writer m_frameWriter = new();
    private object? m_diagnosticSource;
    private Puck.World.Protocol.WorldDocumentVersion m_diagnosticVersion;
    /// <summary>Finds the camera the first seat presented in the world renders with this frame, as the presenter latched
    /// it: the eye every window a screen of this world shows fits to, as the boot world's windows fit to its viewer.</summary>
    /// <param name="camera">The camera, in the scene world's own coordinates, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the presenter latched a seat's view into the scene this frame.</returns>
    public bool TrySeatCamera(out CameraSnapshot camera) {
        if (m_views.Count != 0) {
            camera = m_views[0].Camera;

            return true;
        }

        camera = default;

        return false;
    }

    /// <summary>Gets how many of the views the scene's last dressed frame carries are its seats', the views before its
    /// windows.</summary>
    public int SeatViewCount => m_latchedSeatCount;

    /// <summary>Finds the camera a view of the scene's last dressed frame rendered from, in the scene world's own
    /// coordinates: a seat's, or a window's (its <see cref="WorldRoutedWindow.View"/>, or the default projection).</summary>
    /// <param name="view">The view's index in the scene's frames (<see cref="WorldRoutedWindow.Index"/>).</param>
    /// <param name="camera">The camera, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the last dressed frame carries the view.</returns>
    public bool TryCamera(int view, out CameraSnapshot camera) {
        if (((uint)view) < ((uint)m_frameViews.Count)) {
            camera = m_frameViews[view].Camera;

            return true;
        }

        camera = default;

        return false;
    }
    /// <summary>Finds the surface a ray cast through a view of the scene meets among the world's static placements, marched
    /// in fixed point out to the frame's far distance measured from that view's camera (<see cref="WorldSessionSceneEmitter.TrySurface(SourceRay, Vector3, out FixedVector3)"/>),
    /// so a pick through a window onto the world lands on the surface the window shows.</summary>
    /// <param name="view">The view's index in the scene's frames.</param>
    /// <param name="ray">The ray, in the scene world's own coordinates, cast through that view's camera.</param>
    /// <param name="point">The point the ray meets, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the last dressed frame carries the view and the ray proves a surface.</returns>
    public bool TrySurface(int view, SourceRay ray, out FixedVector3 point) => TrySurface(
        normal: out _,
        point: out point,
        ray: ray,
        view: view
    );
    /// <summary>Finds the surface a ray cast through a view of the scene meets, as
    /// <see cref="TrySurface(int, SourceRay, out FixedVector3)"/> does, with the surface's unit normal there, or zero
    /// where the static field gives none.</summary>
    /// <param name="view">The view's index in the scene's frames.</param>
    /// <param name="ray">The ray, in the scene world's own coordinates, cast through that view's camera.</param>
    /// <param name="point">The point the ray meets, when this returns <see langword="true"/>.</param>
    /// <param name="normal">The surface's unit normal at the point, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the last dressed frame carries the view and the ray proves a surface.</returns>
    public bool TrySurface(int view, SourceRay ray, out FixedVector3 point, out Vector3 normal) {
        if (!TryCamera(
            camera: out var camera,
            view: view
        )) {
            point = default;
            normal = Vector3.Zero;

            return false;
        }

        return m_emitter.TrySurface(
            from: camera.Position,
            normal: out normal,
            point: out point,
            ray: ray
        );
    }

    // Attaches a window after every window already attached.
    internal WorldRoutedWindow Attach() {
        var window = new WorldRoutedWindow(scene: this);

        m_windows.Add(item: window);

        return window;
    }
    // Detaches a window from the next latch; the current frame retains its slot.
    internal void Detach(WorldRoutedWindow window) => _ = m_windows.Remove(item: window);
    // A window's index in the scene's frames: after this frame's seats, in the order the windows attached.
    internal int IndexOf(WorldRoutedWindow window) => ((window.LatchedOrder < 0)
        ? -1
        : (m_latchedSeatCount + window.LatchedOrder)
    );
}
/// <summary>
/// A window onto an endpoint's world, attached to the scene the presentation renders that world with
/// (<see cref="WorldFramePresenter.AttachWindow"/>): a view of the scene's frame beside its seats' views, so it renders
/// from the residency they render from, with its own camera and quality. Its holder sets <see cref="View"/> before the
/// scene dresses each frame, and disposes the window when it closes.
/// </summary>
public sealed class WorldRoutedWindow : IDisposable {
    private WorldRoutedScene? m_scene;

    internal WorldRoutedWindow(WorldRoutedScene scene) => m_scene = scene;

    // The window's place among its scene's latched windows (WorldRoutedScene.EndViews), or -1 while it holds none.
    internal int LatchedOrder { get; set; } = -1;

    /// <summary>Gets the scene the window is a view of.</summary>
    /// <exception cref="ObjectDisposedException">The window is disposed.</exception>
    public WorldRoutedScene Scene => (m_scene ?? throw new ObjectDisposedException(objectName: nameof(WorldRoutedWindow)));
    /// <summary>Gets the window's index in its scene's frames as the presenter latched this frame, after the seats'
    /// views; -1 before its first latch or once the window is disposed.</summary>
    public int Index => (m_scene?.IndexOf(window: this) ?? -1);
    /// <summary>Gets or sets the view the window renders: its camera, framed in the scene world's own coordinates, and its
    /// quality. <see langword="null"/>, the default, renders the world's default projection at a nested view's quality
    /// (<see cref="Puck.World.WorldRenderSettings.NestedViewQuality"/>).</summary>
    public SdfViewSnapshot? View { get; set; }
    /// <summary>Gets or sets how the window dresses the world's default projection it renders while <see cref="View"/> is
    /// <see langword="null"/>, or <see langword="null"/> to render it as the emitter framed it.</summary>
    public Func<SdfViewSnapshot, SdfViewSnapshot>? FallbackResolution { get; set; }
    /// <summary>Gets or sets the share of the display's pixels the window is presented over, in [0, 1]: zero for one no
    /// view shows, and one, the default, while unmeasured. Its holder sets it with <see cref="View"/>; a scene shown
    /// through windows alone is weighed by the largest (<see cref="SdfFrame.DisplayShare"/>).</summary>
    public float DisplayShare { get; set; } = 1f;
    /// <summary>Gets or sets how many of the world's irradiance-volume levels the window's view reads, counted from the
    /// coarsest (<see cref="SdfFrame.IndirectLevels"/>); zero, the default, reads every level.</summary>
    public int IndirectLevels { get; set; }

    /// <summary>Detaches the window from its scene; the scene's later windows move down at the next presenter latch.</summary>
    public void Dispose() {
        m_scene?.Detach(window: this);
        m_scene = null;
    }
}
