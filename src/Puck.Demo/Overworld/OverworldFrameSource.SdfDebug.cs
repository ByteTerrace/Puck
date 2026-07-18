using Puck.Input.Devices;
using Puck.SdfVm.Debug;

namespace Puck.Demo.Overworld;

/// <summary>
/// The SDF-DEBUG mode's frame-source surface: thin forwarders the render node and the <c>sdf.*</c> console module drive.
/// The whole mode is composed behind ONE facade (<see cref="Puck.SdfVm.Debug.SdfDebugMode"/>) so this source names a
/// single type for it. The node drives it through these primitive members; the module reaches the scene through
/// <c>ICreatorModeHost.CreatorFrameSource.SdfDebug.Scene</c>. Presentation only — the deterministic simulation never
/// learns the debug subject exists.
/// </summary>
public sealed partial class OverworldFrameSource {
    /// <summary>The SDF-debug mode facade (scene + orbit controller + emitter). The <c>sdf.*</c> verbs reach the debug
    /// scene through its <see cref="Puck.SdfVm.Debug.SdfDebugMode.Scene"/>.</summary>
    public Puck.SdfVm.Debug.SdfDebugMode SdfDebug => m_sdfDebug;

    /// <summary>Whether SDF-debug mode is active (the fullscreen debug subject replaces the room).</summary>
    public bool SdfDebugActive => m_sdfDebug.Active;

    /// <summary>Enters or leaves SDF-debug mode. No manual rebuild is needed either way: <see cref="CaptureFrame"/>
    /// picks the room composition or <see cref="m_sdfDebugComposition"/> by this flag every call, and each holds its
    /// own independently-cached program object, so <see cref="Dress"/>'s reference-diff already sees a takeover
    /// enter/exit as a real change.</summary>
    /// <param name="active">Whether the mode should be active.</param>
    public void SetSdfDebugActive(bool active) {
        if (m_sdfDebug.Active == active) {
            return;
        }

        m_sdfDebug.SetActive(active: active);
    }

    /// <summary>Forwards the creating slot's raw pad state to the orbit controller (only while the mode is active) —
    /// adapts it to the engine's neutral orbit-input vocabulary (sticks/triggers pass through; the exit/carve/
    /// carve-guard buttons map to North/South/RightShoulder). Both this source and <see cref="SdfOrbitInput"/> already
    /// reference <c>Puck.SdfVm</c>, so this builds the record directly — no primitive-typed overload needed.</summary>
    /// <param name="raw">The pad state.</param>
    /// <param name="deltaSeconds">The render-clock delta driving the orbit/pan/zoom.</param>
    public void AdvanceSdfDebugInput(in GamepadState raw, float deltaSeconds) {
        var orbit = new SdfOrbitInput(
            LeftStick: raw.LeftStick,
            RightStick: raw.RightStick,
            LeftTrigger: raw.LeftTrigger,
            RightTrigger: raw.RightTrigger,
            ExitButton: (0 != (raw.Buttons & GamepadButtons.ButtonNorth)),
            CarveButton: (0 != (raw.Buttons & GamepadButtons.ButtonSouth)),
            CarveGuardButton: (0 != (raw.Buttons & GamepadButtons.RightShoulder))
        );

        m_sdfDebug.AdvanceInput(raw: in orbit, deltaSeconds: deltaSeconds);
    }

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

    /// <summary>Drives the SDF-debug mode's carve-bake settle planner one produced frame (carve-bake plan §3/§4) — the
    /// <see cref="Puck.SdfVm.ISdfFrameSource.AdvanceBricks"/> hook the engine node calls right before this frame's
    /// capture, handing the planner the live engine's brick-bake seam so a settled cluster bakes and adopts (a handoff
    /// bumps the mode's revision, which this source's <see cref="Dress"/> reference-diff rebuilds on). No-op while the
    /// mode is down. Presentation only — the deterministic simulation never learns a brick baked.</summary>
    /// <param name="bakes">The engine's brick-bake service (poll/request).</param>
    public void AdvanceBricks(Puck.SdfVm.ISdfBrickBakeService bakes) {
        m_sdfDebug.AdvanceBricks(bakes: bakes);
        // The headless synthetic sdf.carves scene's settle-0 planner (carve-bake plan §4 "the bench carves scene"):
        // driven every produced frame while registered, so its background bake completes and adopts before the harness
        // samples. Independent of the interactive mode's own planner above.
        m_benchBrickAdvance?.Invoke(obj: bakes);
    }
}
