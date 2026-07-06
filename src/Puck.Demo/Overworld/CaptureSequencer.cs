using System.Numerics;
using Puck.Demo.Configuration;

namespace Puck.Demo.Overworld;

/// <summary>
/// Drives a <see cref="ScenarioOptions"/> capture plan over the live overworld as a SETTLE-THEN-CAPTURE,
/// COMPLETION-DRIVEN state machine — never keyed to produced-frame numbers (a wall-clock <c>--exit-after-seconds</c>
/// racing frame-number shots was the reliability bug: under GPU contention the fps could drop below the shot frames and
/// the run exited with zero captures). It expands the plan into a sorted list of shots (each an output path plus a
/// verbatim camera pose) and, for the CURRENT shot, holds its pose (via <see cref="ScreenLayoutDirector.ScenarioCameraPose"/>,
/// verbatim, no easing), SETTLES for at least the scenario's <c>SettleSeconds</c> of wall-clock AND a few produced
/// frames, then asks the render node to arm its one-shot capture; once the PNG is written (the next produced frame) it
/// advances to the next shot. When the last shot is written it reports COMPLETE and the node requests a graceful
/// shutdown. Composed BY the frame source (like the bake preview), so the overworld render node — which sits at its
/// class-coupling ceiling — never names any of these types.
///
/// THE DETERMINISM RULE: wall-clock influences ONLY when a capture is armed. The camera pose is verbatim from the plan,
/// and each shot's content/animation time is PINNED (<see cref="PinnedContentTime"/> = StartTime + shot·TimeStep) so a
/// time-animated creation renders identically regardless of the run's fps — two runs at different wall-clock timelines
/// produce byte-identical PNGs. Presentation / capture only; never touches the simulation.
/// </summary>
public sealed class CaptureSequencer {
    private readonly record struct Shot(string Path, Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite);

    // The minimum produced frames a shot's pose is held before its capture arms — belt-and-braces on top of the
    // wall-clock settle so the pose (and any program rebuild it triggered) is unambiguously in the rendered frame even
    // when a frame is unusually cheap. Small: the wall-clock SettleSeconds is the real dwell.
    private const int MinSettleFrames = 3;

    private readonly ScreenLayoutDirector m_director;
    private readonly Shot[] m_shots;
    private readonly string[] m_shotPaths;
    private readonly double m_settleSeconds;
    private readonly double m_startTime;
    private readonly double m_timeStep;

    // The shot currently being processed (settling, or awaiting its capture write). Equal to m_shots.Length once every
    // shot is written — the COMPLETE state.
    private int m_activeShot;
    // Wall-clock + produced-frame dwell accumulated while the active shot's pose settles (reset per shot).
    private double m_settledSeconds;
    private int m_settledFrames;
    // Whether the active shot's capture has been armed (we then wait for the write, one produced frame later).
    private bool m_armed;
    private int m_framesSinceArm;
    // How many shots have actually had their capture armed (the completion / safety-net accounting).
    private int m_capturedCount;

    /// <summary>Initializes a new instance of the <see cref="CaptureSequencer"/> class.</summary>
    /// <param name="director">The screen director whose room-view pose the scenario overrides.</param>
    /// <param name="options">The bound scenario options (its capture plan is expanded here).</param>
    /// <param name="defaultTarget">The orbit target for shots that omit an explicit one (the creation's center).</param>
    public CaptureSequencer(ScreenLayoutDirector director, ScenarioOptions options, Vector3 defaultTarget) {
        ArgumentNullException.ThrowIfNull(director);
        ArgumentNullException.ThrowIfNull(options);

        m_director = director;
        m_shots = ExpandShots(capture: options.Capture, defaultTarget: defaultTarget);
        m_shotPaths = new string[m_shots.Length];
        m_settleSeconds = Math.Max(val1: 0d, val2: options.SettleSeconds);
        m_startTime = options.StartTime;
        m_timeStep = options.TimeStep;

        for (var index = 0; (index < m_shots.Length); index++) {
            m_shotPaths[index] = m_shots[index].Path;
        }
    }

    /// <summary>The output PNG path of each shot, in shot order.</summary>
    public IReadOnlyList<string> ShotPaths => m_shotPaths;

    /// <summary>How many shots the plan contains.</summary>
    public int ShotCount => m_shots.Length;

    /// <summary>How many shots have had their capture armed so far (equals <see cref="ShotCount"/> once complete) —
    /// the count the safety-net stderr line reports if the auto-exit ever fires early.</summary>
    public int CapturedCount => m_capturedCount;

    /// <summary>Whether every shot's capture has been written (the completion-driven exit condition).</summary>
    public bool IsComplete => (m_activeShot >= m_shots.Length);

    /// <summary>The DETERMINISTIC content/animation time (seconds) the active shot renders at — StartTime + shot·TimeStep,
    /// clamped to the last shot's value once complete. The frame source feeds this to the rendered frame in place of the
    /// wall-clock accumulator, so the pose AND the animation phase are identical every run.</summary>
    public float PinnedContentTime {
        get {
            var shot = Math.Min(val1: m_activeShot, val2: Math.Max(val1: 0, val2: (m_shots.Length - 1)));

            return (float)(m_startTime + (shot * m_timeStep));
        }
    }

    /// <summary>Advances the scenario one produced frame: holds the active shot's verbatim pose, accumulates its settle
    /// dwell, and — once the pose has settled (≥ SettleSeconds wall-clock AND ≥ a few produced frames) — returns the
    /// shot's output path so the render node can arm its one-shot capture THIS frame (the pose is already in effect, so
    /// the capture is deterministic). After the capture is written (the following produced frame) it advances to the
    /// next shot. Returns null on every frame that does not arm a capture. Call once per produced frame BEFORE the frame
    /// is composed; a no-op returning null once <see cref="IsComplete"/>.</summary>
    /// <param name="deltaSeconds">The wall-clock delta since the previous produced frame (settle timing ONLY — it never
    /// reaches a rendered value).</param>
    /// <returns>The path to arm a capture for this frame, or null.</returns>
    public string? Advance(float deltaSeconds) {
        if (m_activeShot >= m_shots.Length) {
            return null; // complete — the run is being torn down.
        }

        // Hold the active shot's verbatim pose every frame it is current (idempotent; a step function until the shot
        // advances). Set unconditionally so a program rebuild that clobbered nothing can't leave a stale pose.
        var shot = m_shots[m_activeShot];

        m_director.ScenarioCameraPose = (shot.Target, shot.Yaw, shot.Pitch, shot.Distance, shot.Sprite);

        if (m_armed) {
            // The capture was armed on a prior frame; the render node's producer writes the PNG the same produced frame
            // it is armed, so by the next Advance the file exists. Give it one full frame, then advance.
            if (++m_framesSinceArm >= 1) {
                ++m_activeShot;
                m_armed = false;
                m_framesSinceArm = 0;
                m_settledSeconds = 0d;
                m_settledFrames = 0;
            }

            return null;
        }

        // Settling: accumulate wall-clock + produced-frame dwell with the pose held. Wall-clock gates only WHEN we arm.
        m_settledSeconds += Math.Max(val1: 0d, val2: deltaSeconds);
        ++m_settledFrames;

        if ((m_settledSeconds < m_settleSeconds) || (m_settledFrames < MinSettleFrames)) {
            return null;
        }

        // Settled — arm this shot's capture this frame (the pose is in effect for the frame about to render, whose
        // read-back this arm requests).
        m_armed = true;
        m_framesSinceArm = 0;
        ++m_capturedCount;

        return shot.Path;
    }

    private static Shot[] ExpandShots(ScenarioCaptureOptions capture, Vector3 defaultTarget) {
        var directory = (string.IsNullOrWhiteSpace(value: capture.Directory) ? "artifacts/scenario" : capture.Directory);
        var prefix = (string.IsNullOrWhiteSpace(value: capture.Prefix) ? "shot" : capture.Prefix);
        var shots = new List<Shot>();

        if (capture.Shots.Count > 0) {
            foreach (var shot in capture.Shots) {
                shots.Add(item: new Shot(
                    Path: string.Empty,
                    Target: ResolveTarget(x: shot.TargetX, y: shot.TargetY, z: shot.TargetZ, fallback: defaultTarget),
                    Yaw: shot.Yaw,
                    Pitch: shot.Pitch,
                    Distance: shot.Distance,
                    Sprite: shot.Sprite
                ));
            }
        }
        else if (capture.Orbit is { } orbit) {
            var count = Math.Max(val1: 1, val2: orbit.Count);
            var target = ResolveTarget(x: orbit.TargetX, y: orbit.TargetY, z: orbit.TargetZ, fallback: defaultTarget);

            for (var index = 0; (index < count); index++) {
                shots.Add(item: new Shot(
                    Path: string.Empty,
                    Target: target,
                    Yaw: (((float)index / count) * MathF.Tau),
                    Pitch: orbit.Pitch,
                    Distance: orbit.Distance,
                    Sprite: false
                ));
            }
        }

        // The capture readback (SdfEngineNode.RequestCapture) writes the PNG directly and does NOT create the
        // directory — do it once here so the first shot doesn't throw.
        if (shots.Count > 0) {
            _ = System.IO.Directory.CreateDirectory(path: directory);
        }

        var resolved = new Shot[shots.Count];

        for (var index = 0; (index < shots.Count); index++) {
            resolved[index] = (shots[index] with {
                Path = System.IO.Path.Combine(path1: directory, path2: $"{prefix}-{index:000}.png"),
            });
        }

        return resolved;
    }

    private static Vector3 ResolveTarget(float? x, float? y, float? z, Vector3 fallback) {
        return ((x is null) && (y is null) && (z is null))
            ? fallback
            : new Vector3(x: (x ?? fallback.X), y: (y ?? fallback.Y), z: (z ?? fallback.Z));
    }
}
