using System.Numerics;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Demo.MiniAction;

/// <summary>
/// Turns the active players' positions into the per-frame view list — PURE PRESENTATION, derived from the simulation,
/// never fed back into it (its animated state is not part of <see cref="MiniActionWorld.StateHash"/>, so it never
/// perturbs determinism or replay). It always emits exactly <see cref="MiniActionWorld.MaxPlayers"/> views (so the world
/// compositor's first-frame viewport count never changes): when players are close it frames everyone in ONE shared view
/// (the others zero-area, hence invisible); as they spread past a hysteresis threshold it animates a guillotine SPLIT —
/// dividers sliding in from the screen edges while each camera eases from the shared framing to a per-player chase.
/// Inactive slots are zero-area. The transition is rectangular (the compositor does first-rect-wins over axis-aligned
/// rects); a diagonal/blended split is out of scope.
/// </summary>
public sealed class CameraDirector {
    private const float SplitEnterSpread = 3.5f; // smoothed spread (world units) above which the screen splits
    private const float SplitExitSpread = 2f;    // and below which it merges (a hysteresis band, so it doesn't flicker)
    private const float TransitionRate = 2.5f;  // how fast the split divider animates (per second)
    private const float SmoothRate = 6f;        // low-pass rate for the centroid + spread metrics
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));

    private static readonly Vector3 ChaseOffset = new(0f, 6.5f, 11f);
    private static readonly Vector3 TargetLift = new(0f, 0.4f, 0f);
    private bool m_initialized;
    private bool m_split;
    private float m_transition;
    private Vector3 m_smoothCentroid;
    private float m_smoothSpread;

    /// <summary>Composes the per-frame views for the active players (in slot order). Always returns
    /// <see cref="MiniActionWorld.MaxPlayers"/> views; <paramref name="deltaSeconds"/> drives the animation.</summary>
    public IReadOnlyList<SdfViewSnapshot> Compose(IReadOnlyList<Vector3> activePositions, uint imageWidth, uint imageHeight, float deltaSeconds) {
        ArgumentNullException.ThrowIfNull(activePositions);

        var views = new SdfViewSnapshot[MiniActionWorld.MaxPlayers];
        var count = Math.Min(activePositions.Count, MiniActionWorld.MaxPlayers);

        if (count == 0) {
            // Empty room: one full-screen overview so the window isn't black; the rest hidden.
            var overview = CameraSnapshot.LookAt(position: new Vector3(0f, 12f, 16f), target: Vector3.Zero, fieldOfViewRadians: FieldOfViewRadians, viewportWidth: imageWidth, viewportHeight: imageHeight);

            views[0] = new SdfViewSnapshot(Camera: overview, Region: FullScreen);

            for (var slot = 1; (slot < MiniActionWorld.MaxPlayers); slot++) {
                views[slot] = HiddenView(camera: overview);
            }

            return views;
        }

        var centroid = Vector3.Zero;

        for (var index = 0; (index < count); index++) {
            centroid += activePositions[index];
        }

        centroid /= count;

        var spread = 0f;

        for (var index = 0; (index < count); index++) {
            var delta = (activePositions[index] - centroid);

            spread = MathF.Max(spread, MathF.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z)));
        }

        var firstFrame = !m_initialized;

        if (firstFrame) {
            m_smoothCentroid = centroid;
            m_smoothSpread = spread;
            m_initialized = true;
        } else {
            var smoothing = MathF.Min(1f, (SmoothRate * deltaSeconds));

            m_smoothCentroid += ((centroid - m_smoothCentroid) * smoothing);
            m_smoothSpread += ((spread - m_smoothSpread) * smoothing);
        }

        // Hysteresis-latched mode, then ease the transition toward it — but SNAP to it on the first frame so a session
        // that opens already-split doesn't animate from a misleading shared frame.
        if (count <= 1) {
            m_split = false;
        } else if (m_smoothSpread > SplitEnterSpread) {
            m_split = true;
        } else if (m_smoothSpread < SplitExitSpread) {
            m_split = false;
        }

        m_transition = (firstFrame
            ? (m_split ? 1f : 0f)
            : Math.Clamp((m_transition + ((m_split ? 1f : -1f) * TransitionRate * deltaSeconds)), 0f, 1f));

        var ease = ((count == 1) ? 0f : SmoothStep(t: m_transition));

        // Shared framing fits everyone: pull the eye up + back as the spread grows.
        var sharedTarget = (m_smoothCentroid + TargetLift);
        var sharedEye = (m_smoothCentroid + new Vector3(0f, (6.5f + m_smoothSpread), (11f + (m_smoothSpread * 1.5f))));
        var sharedCamera = CameraSnapshot.LookAt(position: sharedEye, target: sharedTarget, fieldOfViewRadians: FieldOfViewRadians, viewportWidth: imageWidth, viewportHeight: imageHeight);

        for (var index = 0; (index < count); index++) {
            var rect = LayoutRect(count: count, index: index, ease: ease);
            var eye = Vector3.Lerp(value1: sharedEye, value2: (activePositions[index] + ChaseOffset), amount: ease);
            var target = Vector3.Lerp(value1: sharedTarget, value2: (activePositions[index] + TargetLift), amount: ease);
            var cellWidth = Math.Max(1u, (uint)(rect.Width * imageWidth));
            var cellHeight = Math.Max(1u, (uint)(rect.Height * imageHeight));

            views[index] = new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(position: eye, target: target, fieldOfViewRadians: FieldOfViewRadians, viewportWidth: cellWidth, viewportHeight: cellHeight),
                Region: rect
            );
        }

        for (var slot = count; (slot < MiniActionWorld.MaxPlayers); slot++) {
            views[slot] = HiddenView(camera: sharedCamera);
        }

        return views;
    }

    private static SdfViewSnapshot HiddenView(CameraSnapshot camera) {
        return new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 0f, Height: 0f));
    }

    // A guillotine layout parameterized by `ease` in [0,1]: at 0, cell 0 fills the screen and the rest are zero-area
    // (one shared image); at 1, the cells tile the screen (2-up, P0-left+stacked-right, or quad). The dividers slide in.
    private static NormalizedRect LayoutRect(int count, int index, float ease) {
        var vertical = Lerp(a: 1f, b: 0.5f, t: ease);
        var horizontal = Lerp(a: 1f, b: 0.5f, t: ease);

        return count switch {
            1 => FullScreen,
            2 => ((index == 0)
                ? new NormalizedRect(X: 0f, Y: 0f, Width: vertical, Height: 1f)
                : new NormalizedRect(X: vertical, Y: 0f, Width: (1f - vertical), Height: 1f)),
            3 => index switch {
                0 => new NormalizedRect(X: 0f, Y: 0f, Width: vertical, Height: 1f),
                1 => new NormalizedRect(X: vertical, Y: 0f, Width: (1f - vertical), Height: horizontal),
                _ => new NormalizedRect(X: vertical, Y: horizontal, Width: (1f - vertical), Height: (1f - horizontal)),
            },
            _ => index switch {
                0 => new NormalizedRect(X: 0f, Y: 0f, Width: vertical, Height: horizontal),
                1 => new NormalizedRect(X: vertical, Y: 0f, Width: (1f - vertical), Height: horizontal),
                2 => new NormalizedRect(X: 0f, Y: horizontal, Width: vertical, Height: (1f - horizontal)),
                _ => new NormalizedRect(X: vertical, Y: horizontal, Width: (1f - vertical), Height: (1f - horizontal)),
            },
        };
    }

    private static NormalizedRect FullScreen => new(X: 0f, Y: 0f, Width: 1f, Height: 1f);

    private static float SmoothStep(float t) => ((t * t) * (3f - (2f * t)));
    private static float Lerp(float a, float b, float t) => (a + ((b - a) * t));
}
