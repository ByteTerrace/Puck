using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Recursive.Scene;

internal sealed class SdfScene : ISdfFrameSource {
    private const float TransitionSeconds = 0.6f;

    private readonly OrbitCamera[] m_cameras;
    private readonly LayoutTransition m_layout;
    private bool m_programChanged = true;
    private string m_sceneName = SdfSceneLibrary.DefaultName;
    private SdfProgram m_program;
    private float m_time;

    public SdfScene() {
        m_program = SdfSceneLibrary.TryBuild(name: SdfSceneLibrary.DefaultName)!;
        m_cameras = [
            new OrbitCamera { AngularSpeedRadiansPerSecond = 0.45f, AzimuthRadians = 0.0f, Height = 1.8f, Radius = 4.5f },
            new OrbitCamera { AngularSpeedRadiansPerSecond = -0.30f, AzimuthRadians = 2.1f, FieldOfViewRadians = (MathF.PI * 0.30f), Height = 3.4f, Radius = 6.5f },
            new OrbitCamera { AngularSpeedRadiansPerSecond = 0.22f, AzimuthRadians = 4.0f, FieldOfViewRadians = (MathF.PI * 0.36f), Height = 5.4f, Radius = 2.8f },
            new OrbitCamera { AngularSpeedRadiansPerSecond = 0.60f, AzimuthRadians = 1.0f, FieldOfViewRadians = (MathF.PI * 0.42f), Height = 0.7f, Radius = 2.1f },
        ];
        m_layout = new LayoutTransition(
            initial: SplitLayouts.TryGet(name: SplitLayouts.DefaultName)!,
            transitionSeconds: TransitionSeconds
        );
    }

    public string LayoutName => m_layout.TargetName;
    public bool Paused { get; private set; }
    public string SceneName => m_sceneName;

    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds) {
        if (!Paused) {
            m_time += deltaSeconds;

            foreach (var camera in m_cameras) {
                camera.Advance(deltaSeconds: deltaSeconds);
            }

            m_layout.Advance(deltaSeconds: deltaSeconds);
        }

        var views = new SdfViewSnapshot[SplitLayouts.ViewportCount];

        for (var index = 0; (index < views.Length); index++) {
            views[index] = new SdfViewSnapshot(
                Camera: m_cameras[index].Capture(
                    viewportHeight: height,
                    viewportWidth: width
                ),
                Region: m_layout.RegionFor(viewportIndex: index)
            );
        }

        var programChanged = m_programChanged;

        m_programChanged = false;

        return new SdfFrame(
            Program: m_program,
            ProgramChanged: programChanged,
            Time: m_time,
            Views: views,
            WarpAmount: m_layout.WarpAmount
        );
    }
    public string CycleLayout(int direction) {
        var ordered = SplitLayouts.Ordered;
        var currentIndex = IndexOf(
            list: ordered,
            value: m_layout.TargetName
        );
        var nextIndex = ((((currentIndex + direction) % ordered.Count) + ordered.Count) % ordered.Count);
        var nextName = ordered[nextIndex];

        m_layout.BeginTransition(target: SplitLayouts.TryGet(name: nextName)!);
        return nextName;
    }
    public bool SetLayout(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var layout = SplitLayouts.TryGet(name: name);

        if (layout is null) {
            return false;
        }

        m_layout.BeginTransition(target: layout);
        return true;
    }
    public bool SelectScene(string name) {
        ArgumentNullException.ThrowIfNull(name);

        var program = SdfSceneLibrary.TryBuild(name: name);

        if (program is null) {
            return false;
        }

        m_program = program;
        m_sceneName = name.ToLowerInvariant();
        m_programChanged = true;
        return true;
    }
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
}
