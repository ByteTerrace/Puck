using Puck.Input.Devices;

namespace Puck.Demo.Overworld;

/// <summary>
/// The SDF-DEBUG mode's frame-source surface: thin forwarders the render node and the <c>sdf.*</c> console module drive.
/// The whole mode is composed behind ONE facade (<see cref="Puck.Demo.SdfDebug.SdfDebugMode"/>) so this source names a
/// single type for it — it sits at its analyzer coupling ceiling. The node drives it through these primitive members;
/// the module reaches the scene through <c>ICreatorModeHost.CreatorFrameSource.SdfDebug.Scene</c>. Presentation only —
/// the deterministic simulation never learns the debug subject exists.
/// </summary>
public sealed partial class OverworldFrameSource {
    /// <summary>The SDF-debug mode facade (scene + orbit controller + emitter). The <c>sdf.*</c> verbs reach the debug
    /// scene through its <see cref="Puck.Demo.SdfDebug.SdfDebugMode.Scene"/>.</summary>
    public Puck.Demo.SdfDebug.SdfDebugMode SdfDebug => m_sdfDebug;

    /// <summary>Whether SDF-debug mode is active (the fullscreen debug subject replaces the room).</summary>
    public bool SdfDebugActive => m_sdfDebug.Active;

    /// <summary>Enters or leaves SDF-debug mode; forces a program rebuild (the subject replaces the room, and the room
    /// must return on exit).</summary>
    /// <param name="active">Whether the mode should be active.</param>
    public void SetSdfDebugActive(bool active) {
        if (m_sdfDebug.Active == active) {
            return;
        }

        m_sdfDebug.SetActive(active: active);
        // Force a rebuild: entering swaps the room for the subject, exiting swaps it back (the room program is rebuilt
        // fresh — booted masks / creator content unchanged, so it lands identical to before the takeover).
        m_program = null;
    }

    /// <summary>Forwards the creating slot's raw pad state to the orbit controller (only while the mode is active).</summary>
    /// <param name="raw">The pad state.</param>
    /// <param name="deltaSeconds">The render-clock delta driving the orbit/pan/zoom.</param>
    public void AdvanceSdfDebugInput(in GamepadState raw, float deltaSeconds) =>
        m_sdfDebug.AdvanceInput(raw: in raw, deltaSeconds: deltaSeconds);

    /// <summary>Whether the debug orbit controller's EXIT (North) fired since the last consume (clears it) — the node
    /// leaves the mode and restores the room view when this reports true.</summary>
    public bool ConsumeSdfDebugExitRequest() =>
        m_sdfDebug.ConsumeExitRequest();

    /// <summary>Whether an SDF perf-bench run is in flight — the render node feeds it timings each produced frame.</summary>
    public bool SdfBenchRunning => m_sdfDebug.BenchRunning;

    /// <summary>Advances an in-flight bench run one produced frame with the previous frame's per-pass GPU ms and the
    /// render info the report header names. The node reads the timings (it owns the producer) and passes them through;
    /// this source stays coupling-flat.</summary>
    public void AdvanceSdfBench(bool hasTimings, double beam, double views, double composite, double frame, uint width, uint height, bool backendIsDirectX) =>
        m_sdfDebug.AdvanceBench(hasTimings: hasTimings, beam: beam, views: views, composite: composite, frame: frame, width: width, height: height, backendIsDirectX: backendIsDirectX);
}
