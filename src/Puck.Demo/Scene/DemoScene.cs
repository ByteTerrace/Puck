using Puck.Demo.Cameras;
using Puck.Demo.Compositing;
using Puck.Demo.Viewports;
using Puck.SdfVm;

namespace Puck.Demo.Scene;

/// <summary>The demo's runtime model: a fixed pool of <see cref="SplitLayouts.ViewportCount"/> viewports —
/// one shared SDF program seen through distinct cameras — plus the active split-screen layout and the
/// in-flight transition between layouts. The renderer reads each viewport's camera + program; the
/// compositor reads each viewport's current region + the warp amount. Command handlers switch
/// layout/scene and toggle pause; <see cref="Advance"/> ticks the cameras and the transition.</summary>
internal sealed class DemoScene {
    private const float TransitionSeconds = 0.6f;

    private readonly OrbitCamera[] m_cameras;
    private readonly Viewport[] m_viewports;
    private SplitLayout m_fromLayout;
    private bool m_programChanged = true;
    private string m_sceneName = DemoSceneLibrary.DefaultName;
    private SplitLayout m_toLayout;
    private float m_transitionProgress = 1f;

    public DemoScene() {
        var program = DemoSceneLibrary.TryBuild(name: DemoSceneLibrary.DefaultName)!;

        // Four orbit cameras around the same world: a main orbit, a high wide reverse orbit, a steep
        // overhead, and a low close "lure". Distinct phases so they read differently the moment the
        // quad layout appears.
        m_cameras = [
            new OrbitCamera { AngularSpeedRadiansPerSecond = 0.45f, AzimuthRadians = 0.0f, Height = 1.8f, Radius = 4.5f },
            new OrbitCamera { AngularSpeedRadiansPerSecond = -0.30f, AzimuthRadians = 2.1f, FieldOfViewRadians = (MathF.PI * 0.30f), Height = 3.4f, Radius = 6.5f },
            new OrbitCamera { AngularSpeedRadiansPerSecond = 0.22f, AzimuthRadians = 4.0f, FieldOfViewRadians = (MathF.PI * 0.36f), Height = 5.4f, Radius = 2.8f },
            new OrbitCamera { AngularSpeedRadiansPerSecond = 0.60f, AzimuthRadians = 1.0f, FieldOfViewRadians = (MathF.PI * 0.42f), Height = 0.7f, Radius = 2.1f },
        ];
        m_viewports = new Viewport[SplitLayouts.ViewportCount];

        for (var index = 0; (index < m_viewports.Length); index++) {
            m_viewports[index] = new Viewport(
                camera: m_cameras[index],
                id: new ViewportId(Value: index),
                program: program
            );
        }

        m_fromLayout = SplitLayouts.TryGet(name: SplitLayouts.DefaultName)!;
        m_toLayout = m_fromLayout;
    }

    /// <summary>The active (target) layout's name.</summary>
    public string LayoutName => m_toLayout.Name;

    /// <summary>Whether the animation clock is frozen.</summary>
    public bool Paused { get; private set; }

    /// <summary>The shared SDF program all viewports render.</summary>
    public SdfProgram Program => m_viewports[0].Program;

    /// <summary>The active scene's name.</summary>
    public string SceneName => m_sceneName;

    /// <summary>The animation clock in seconds; frozen while <see cref="Paused"/>.</summary>
    public float Time { get; private set; }

    /// <summary>The viewports, in ordinal order — each a camera onto the shared program.</summary>
    public IReadOnlyList<Viewport> Viewports => m_viewports;

    /// <summary>The shader-driven ripple strength for the in-flight transition (0 unless a Warp transition
    /// is mid-flight, peaking at its midpoint).</summary>
    public float WarpAmount => (((m_toLayout.Curve == TransitionCurve.Warp) && (m_transitionProgress < 1f))
        ? MathF.Sin(x: (m_transitionProgress * MathF.PI))
        : 0f);

    /// <summary>The screen region a viewport currently occupies, interpolated across the active transition.</summary>
    public NormalizedRect CurrentRegionFor(int viewportIndex) {
        var eased = TransitionCurves.Ease(
            curve: m_toLayout.Curve,
            progress: m_transitionProgress
        );

        return NormalizedRect.Lerp(
            from: m_fromLayout.RegionFor(viewportIndex: viewportIndex),
            t: eased,
            to: m_toLayout.RegionFor(viewportIndex: viewportIndex)
        );
    }
    /// <summary>Advances the clock, every camera, and the layout transition unless paused.</summary>
    public void Advance(float deltaSeconds) {
        if (Paused) {
            return;
        }

        Time += deltaSeconds;

        foreach (var camera in m_cameras) {
            camera.Advance(deltaSeconds: deltaSeconds);
        }

        if (m_transitionProgress < 1f) {
            m_transitionProgress = Math.Min(
                val1: 1f,
                val2: (m_transitionProgress + (deltaSeconds / TransitionSeconds))
            );
        }
    }
    /// <summary>Returns and clears the "program changed" flag, so the renderer re-uploads exactly once.</summary>
    public bool ConsumeProgramChanged() {
        var changed = m_programChanged;

        m_programChanged = false;
        return changed;
    }
    /// <summary>Steps to the next/previous layout in cycle order and returns its name.</summary>
    public string CycleLayout(int direction) {
        var ordered = SplitLayouts.Ordered;
        var currentIndex = IndexOf(
            list: ordered,
            value: m_toLayout.Name
        );
        var nextIndex = ((((currentIndex + direction) % ordered.Count) + ordered.Count) % ordered.Count);
        var nextName = ordered[nextIndex];

        BeginTransition(target: SplitLayouts.TryGet(name: nextName)!);
        return nextName;
    }
    /// <summary>Transitions to the named layout; returns <see langword="false"/> for an unknown name.</summary>
    public bool SetLayout(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var layout = SplitLayouts.TryGet(name: name);

        if (layout is null) {
            return false;
        }

        BeginTransition(target: layout);
        return true;
    }
    /// <summary>Switches the shared scene program; returns <see langword="false"/> for an unknown name.</summary>
    public bool SelectScene(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var program = DemoSceneLibrary.TryBuild(name: name);

        if (program is null) {
            return false;
        }

        foreach (var viewport in m_viewports) {
            viewport.Program = program;
        }

        m_sceneName = name.ToLowerInvariant();
        m_programChanged = true;
        return true;
    }
    /// <summary>Flips the pause state and returns the new value.</summary>
    public bool TogglePause() {
        Paused = !Paused;
        return Paused;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value) {
        for (var index = 0; (index < list.Count); index++) {
            if (string.Equals(
                a: list[index],
                b: value,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                return index;
            }
        }

        return 0;
    }
    private void BeginTransition(SplitLayout target) {
        // Start from the layout currently being shown (the last target). Mid-transition switches snap
        // their start, which is imperceptible at the demo's transition speed.
        m_fromLayout = m_toLayout;
        m_toLayout = target;
        m_transitionProgress = 0f;
    }
}
