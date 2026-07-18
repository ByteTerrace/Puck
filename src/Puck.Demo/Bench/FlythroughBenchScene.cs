using System.Numerics;

using Puck.Bench;
using Puck.Demo.Overworld;

namespace Puck.Demo.Bench;

/// <summary>
/// The <c>room.flythrough</c> engine-bench scene: a scripted dolly through the REVEALED room (zero cabinets booted).
/// Setup reveals the room and settles; then each produced frame asserts a deterministic camera pose derived from a
/// 6-waypoint dolly path — evaluated by SAMPLE INDEX, not the wall clock, so every machine renders the identical
/// <see cref="BenchSceneDescriptor.SampleFrames"/>-frame path and per-frame work is comparable across hardware (a faster GPU sweeps the
/// same path in less wall time). The pose is asserted onto the director's verbatim <c>ScreenLayoutDirector.ScenarioCameraPose</c>
/// seam through the frame source's one-frame-sticky bench-camera pin, so a finished scene releases the pin with no
/// teardown verb. The waypoints are hardcoded demo-side constants (the <see cref="RoomBenchScene"/> pin doctrine — a
/// diagnostic channel, never authored content).
/// <para>
/// The path tours the room legibly: a high establishing overview → push in toward the cabinet row → a low left sweep
/// past the cabinets → a low close pass in front of a cabinet's CRT face → a rising right sweep → back up to a high
/// overview. Authored in the pose seam's native basis (target + yaw + pitch + distance, which fully determines the eye
/// and framing — the seam carries no per-pose FOV, so none is authored).
/// </para>
/// </summary>
internal sealed class FlythroughBenchScene : IBenchSceneController {
    // One dolly waypoint in the ScenarioCameraPose basis.
    private readonly record struct Waypoint(Vector3 Target, float Yaw, float Pitch, float Distance);

    // The 6 authored waypoints (5 segments). Cabinets line the −Z wall (their CRT faces looking +Z into the room);
    // yaw 0 looks toward −Z (down the room at the cabinets), the same convention RoomBenchScene's overview pin uses.
    private static readonly Waypoint[] s_waypoints = [
        new Waypoint(Target: new Vector3(x: 0f, y: 0.6f, z: -2f), Yaw: 0.0f, Pitch: 0.70f, Distance: 18.0f),    // 0: high establishing overview
        new Waypoint(Target: new Vector3(x: 0f, y: 0.7f, z: -5f), Yaw: 0.0f, Pitch: 0.30f, Distance: 9.0f),     // 1: push in toward the cabinet row
        new Waypoint(Target: new Vector3(x: -3.5f, y: 0.7f, z: -5.5f), Yaw: -0.6f, Pitch: 0.15f, Distance: 6.0f), // 2: low sweep LEFT along the cabinets
        new Waypoint(Target: new Vector3(x: 0.5f, y: 0.9f, z: -5.5f), Yaw: 0.05f, Pitch: 0.08f, Distance: 4.2f),  // 3: low CLOSE pass on a CRT face
        new Waypoint(Target: new Vector3(x: 3.5f, y: 0.7f, z: -5.0f), Yaw: 0.6f, Pitch: 0.30f, Distance: 8.0f),   // 4: rising sweep RIGHT along the cabinets
        new Waypoint(Target: new Vector3(x: 0f, y: 0.6f, z: -2f), Yaw: 0.0f, Pitch: 0.72f, Distance: 19.0f),      // 5: pull back up to a high overview
    ];
    private readonly Func<OverworldFrameSource?> m_frameSource;
    private readonly int m_warmFrames;
    private readonly int m_sampleFrames;

    /// <summary>Creates the flythrough over a lazy frame-source resolver and the scene's warm/sample frame counts
    /// (needed to map <see cref="OnFrame"/>'s warm-relative frame index onto a sample index along the path).</summary>
    /// <param name="frameSource">Resolves the overworld frame source (the bench-camera pin seam) — null until the
    /// node's first frame, by which point every bench run has started.</param>
    /// <param name="warmFrames">The scene's warm-frame count (the path holds waypoint 0 through warming).</param>
    /// <param name="sampleFrames">The scene's sample-frame count (the full path spans these frames).</param>
    public FlythroughBenchScene(Func<OverworldFrameSource?> frameSource, int warmFrames, int sampleFrames) {
        ArgumentNullException.ThrowIfNull(argument: frameSource);

        m_frameSource = frameSource;
        m_warmFrames = warmFrames;
        m_sampleFrames = Math.Max(val1: 1, val2: sampleFrames);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> SetupScript => ["reveal", "settle"];

    /// <inheritdoc/>
    public IReadOnlyList<string> TeardownScript => [];

    /// <inheritdoc/>
    public void OnFrame(int frameIndex) {
        // Hold waypoint 0 through the warm window, then sweep the path across the sample window (frame-indexed).
        var sampleIndex = (frameIndex - m_warmFrames);

        if (sampleIndex < 0) {
            sampleIndex = 0;
        }

        var pose = Evaluate(sampleIndex: sampleIndex);

        m_frameSource()?.ArmBenchCamera(distance: pose.Distance, pitch: pose.Pitch, target: pose.Target, yaw: pose.Yaw);
    }

    /// <inheritdoc/>
    public bool IsReady() => true;

    // Evaluates the dolly pose at a sample index: split the sample window evenly across the 5 segments, smoothstep
    // within a segment, and interpolate the pose components. Beyond the last sampled frame the path holds the final
    // waypoint (a benign clamp — the sampler never asks past SampleFrames-1).
    private Waypoint Evaluate(int sampleIndex) {
        var segments = (s_waypoints.Length - 1);
        var framesPerSegment = Math.Max(val1: 1, val2: (m_sampleFrames / segments));
        var segment = Math.Min(val1: (sampleIndex / framesPerSegment), val2: (segments - 1));
        var local = (float)(sampleIndex - (segment * framesPerSegment));
        var t = Math.Clamp(value: (local / framesPerSegment), min: 0f, max: 1f);
        var ease = ((t * t) * (3f - (2f * t)));   // smoothstep

        var a = s_waypoints[segment];
        var b = s_waypoints[(segment + 1)];

        return new Waypoint(
            Distance: Lerp(a: a.Distance, b: b.Distance, t: ease),
            Pitch: Lerp(a: a.Pitch, b: b.Pitch, t: ease),
            Target: Vector3.Lerp(value1: a.Target, value2: b.Target, amount: ease),
            Yaw: Lerp(a: a.Yaw, b: b.Yaw, t: ease)
        );
    }
    private static float Lerp(float a, float b, float t) => (a + ((b - a) * t));
}
