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
/// under the destination's own sky and lighting, including typed keys, on the destination's own sky
/// clock. Every view of the world is a view of
/// the scene's one frame, so they share one program and one residency, each with its own camera and quality
/// (<see cref="SdfViewSnapshot.Quality"/>):
/// <list type="bullet">
/// <item>each seat presented in the world (<see cref="WorldContinuum.PresentedElsewhere"/>), framed by the camera
/// <see cref="WorldFramePresenter"/> resolved for it this frame at the presentation's quality, latched afresh every frame
/// (<see cref="AddView"/>);</item>
/// <item>each window onto the world attached through <see cref="WorldFramePresenter.AttachWindow"/>, whose view its
/// holder sets (<see cref="WorldRoutedWindow.View"/>), after the seats.</item>
/// </list>
/// </summary>
public sealed class WorldRoutedScene : ISdfFrameDresser {
    private readonly WorldSessionSceneEmitter m_emitter;
    private readonly Func<SdfFrame?> m_hostFrame;
    private readonly List<SdfViewSnapshot> m_views = [];
    private readonly List<WorldRoutedWindow> m_windows = [];
    // The seats and windows the presenter latched last (EndViews), which membership changes wait for, and the views the
    // last dressed frame rendered, kept for a frame with neither: a frame always carries a view.
    private readonly List<WorldRoutedWindow> m_latchedWindows = [];
    private readonly List<SdfViewSnapshot> m_dressedViews = [];

    private int m_latchedSeatCount;

    /// <summary>Initializes the scene of one endpoint.</summary>
    /// <param name="endpoint">The authority the scene draws.</param>
    /// <param name="hostFrame">The frame the boot presentation dressed this frame, whose presentation clock and cadence the
    /// scene's frame takes; <see langword="null"/> before the first.</param>
    /// <param name="bodyColor">The color each avatar is painted with by body index: a local seat keeps the color the
    /// boot presentation paints it with.</param>
    public WorldRoutedScene(WorldAuthorityEndpoint endpoint, Func<SdfFrame?> hostFrame, Func<int, Vector3> bodyColor) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);
        ArgumentNullException.ThrowIfNull(argument: hostFrame);
        ArgumentNullException.ThrowIfNull(argument: bodyColor);

        Endpoint = endpoint;
        m_hostFrame = hostFrame;
        m_emitter = new WorldSessionSceneEmitter(
            bodyColor: bodyColor,
            castsAvatarShadows: true,
            effectiveCameraName: null,
            mirror: endpoint.Mirror
        );
        FrameSource = new SdfCompositionFrameSource(
            dresser: this,
            emitters: [m_emitter]
        );
    }

    /// <summary>Gets the authority the scene draws.</summary>
    public WorldAuthorityEndpoint Endpoint { get; }
    /// <summary>Gets the frame source a residency renders the scene through.</summary>
    public SdfCompositionFrameSource FrameSource { get; }
    /// <summary>The destination's environment work, shared by all its routed views.</summary>
    public WorldEnvironmentResolve TimelineWork => m_emitter.TimelineWork;
    /// <summary>Gets how many seat views the presenter latched into the scene this frame.</summary>
    public int ViewCount => m_views.Count;
    /// <summary>Gets how many windows are attached to the scene.</summary>
    public int WindowCount => m_windows.Count;

    /// <summary>Clears the views the presenter latched, before it latches this frame's.</summary>
    public void BeginViews() => m_views.Clear();
    /// <summary>Latches a seat's view of this world for the frame.</summary>
    /// <param name="view">The seat's view, framed in this world's own coordinates.</param>
    /// <returns>The view's index in the scene's frames.</returns>
    public int AddView(in SdfViewSnapshot view) {
        m_views.Add(item: view);

        return (m_views.Count - 1);
    }
    /// <summary>Finishes the presenter's view latch. Windows attached after this call enter the next latch; disposing
    /// a window leaves its slot reserved until then, so resolved indices stay valid through the scene's dress.</summary>
    public void EndViews() {
        m_latchedSeatCount = m_views.Count;
        m_latchedWindows.Clear();
        m_latchedWindows.AddRange(collection: m_windows);
    }
    /// <inheritdoc/>
    /// <remarks>The session emitter dresses first, so its stamp pool advances and its dressed definition is retained
    /// exactly as a session screen's; the frame then takes the seats' views and the windows' after them, the host frame's
    /// presentation clock and cadence. The sky, its clock and the environment stay the emitter's: this world's own.</remarks>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        var frame = m_emitter.Dress(
            deltaSeconds: deltaSeconds,
            height: height,
            interpolationAlpha: interpolationAlpha,
            meshDraws: meshDraws,
            meshDrawsRevision: meshDrawsRevision,
            moved: moved,
            program: program,
            transforms: transforms,
            width: width
        );

        if ((m_latchedSeatCount + m_latchedWindows.Count) != 0) {
            m_dressedViews.Clear();

            for (var seat = 0; (seat < m_latchedSeatCount); seat++) {
                m_dressedViews.Add(item: m_views[seat]);
            }

            // The emitter's own view frames the world's default projection at a session screen's quality.
            foreach (var window in m_latchedWindows) {
                m_dressedViews.Add(item: (window.View ?? frame.Views[0]));
            }
        } else if (m_dressedViews.Count == 0) {
            m_dressedViews.AddRange(collection: frame.Views);
        }

        if (m_hostFrame() is not { } host) {
            return frame with {
                Views = m_dressedViews,
            };
        }

        return frame with {
            EnableCadenceGate = host.EnableCadenceGate,
            Time = host.Time,
            Views = m_dressedViews,
        };
    }
    /// <summary>Finds the camera a view of the scene's last dressed frame rendered from, in the scene world's own
    /// coordinates: a seat's, or a window's (its <see cref="WorldRoutedWindow.View"/>, or the default projection).</summary>
    /// <param name="view">The view's index in the scene's frames (<see cref="WorldRoutedWindow.Index"/>).</param>
    /// <param name="camera">The camera, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the last dressed frame carries the view.</returns>
    public bool TryCamera(int view, out CameraSnapshot camera) {
        if (((uint)view) < ((uint)m_dressedViews.Count)) {
            camera = m_dressedViews[view].Camera;

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
    internal int IndexOf(WorldRoutedWindow window) {
        var order = m_latchedWindows.IndexOf(item: window);

        return ((order < 0)
            ? -1
            : (m_latchedSeatCount + order)
        );
    }
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

    /// <summary>Gets the scene the window is a view of.</summary>
    /// <exception cref="ObjectDisposedException">The window is disposed.</exception>
    public WorldRoutedScene Scene => (m_scene ?? throw new ObjectDisposedException(objectName: nameof(WorldRoutedWindow)));
    /// <summary>Gets the window's index in its scene's frames as the presenter latched this frame, after the seats'
    /// views; -1 before its first latch or once the window is disposed.</summary>
    public int Index => (m_scene?.IndexOf(window: this) ?? -1);
    /// <summary>Gets or sets the view the window renders: its camera, framed in the scene world's own coordinates, and its
    /// quality. <see langword="null"/>, the default, renders the world's default projection at a session screen's
    /// reduced quality (<see cref="WorldSessionSceneEmitter.ReducedQuality"/>).</summary>
    public SdfViewSnapshot? View { get; set; }

    /// <summary>Detaches the window from its scene; the scene's later windows move down at the next presenter latch.</summary>
    public void Dispose() {
        m_scene?.Detach(window: this);
        m_scene = null;
    }
}
