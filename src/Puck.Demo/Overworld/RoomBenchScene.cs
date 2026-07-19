using System.Globalization;
using System.Numerics;
using System.Text;

namespace Puck.Demo.Overworld;

/// <summary>
/// The REVEALED-ROOM fixed-camera perf-bench channel — the arc's baseline measurement primitive. Distinct from
/// <see cref="Puck.SdfVm.Debug.SdfBenchScene"/> (which takes over the mode's program with a SYNTHETIC gallery
/// workload): this one changes nothing about what is rendered — no program swap, no simulation touch — it only pins
/// the room camera to a fixed, hardcoded deterministic pose (see <see cref="CameraFrame"/>) over the ACTUAL live
/// room content, so it measures exactly what the player sees, framed identically every session. It mirrors
/// <see cref="Puck.SdfVm.Debug.SdfBenchScene"/>'s WARM-then-SAMPLE async per-frame state machine shape and reads
/// timings through the same <see cref="Puck.SdfVm.SdfEngineNode.TryReadPassTimings"/> seam that scene's Advance
/// loop does, but keeps its own tiny median/min/p95 report (one line, not a table) since there is only ever one
/// "configuration" — the pinned room — per run.
/// <para>
/// Driven by the <c>room.bench</c> console verb (<c>OverworldControlCommandModule</c> /
/// <c>IOverworldControlHost.RoomBench</c>): <c>room.bench [n]</c> starts a run sampling n produced frames (default
/// <see cref="DefaultSampleFrames"/>), <c>room.bench abort</c> cancels one in flight. While <see cref="Running"/> the
/// frame source asserts <see cref="CameraFrame"/> onto <see cref="ScreenLayoutDirector.ScenarioCameraPose"/> — the
/// SAME verbatim-pose seam the <c>--scenario</c> capture harness uses, chosen because it is already the settled
/// "no chase, no ease, byte-identical framing every run" primitive (see that property's remarks); releasing the pin
/// (a finished or aborted run) clears it so the camera returns to the player's normal chase/creator/reveal framing
/// on the very next composed frame. Zero cost while idle — <see cref="Advance"/> is a single bool check.
/// </para>
/// </summary>
public sealed class RoomBenchScene {
    /// <summary>The default sample-frame count a bare <c>room.bench</c> (no argument) measures.</summary>
    public const int DefaultSampleFrames = 300;

    // How many produced frames the pin is held before sampling starts — enough to (a) clear the one-frame GPU-timing
    // readback lag (the frame right after the pose changes still reports the PRIOR pose's timing) and (b) let any
    // transient program rebuild the pose's tile/mask footprint might provoke land. The room's pipelines are already
    // warm (this never switches program or workload, only the camera), so this is a small settle, not a compile-stall
    // buffer like SdfBenchScene's.
    private const int WarmFrames = 5;

    // The fixed diegetic pose: an isometric-ish overview at the room's center, matching the shape of the shipped
    // reveal-overview shot (the room becomes visible, players stand at their machines) — so the pinned frame already
    // contains a booted cabinet's lit CRT face and the sun soft-shadow path renders exactly as ordinary play does.
    // Hardcoded on purpose (a diagnostic channel measured session to session, never authored content) — this file
    // intentionally names no coupling to ScreenLayoutDirector's own (private) overview constants.
    // ONE POSE OF RECORD: exposed as internal statics so the engine-bench harness's ACTIVE-ROOM scene
    // (Puck.Demo.Bench.ActiveRoomBenchScene) pins the identical overview instead of forking a second copy — the plan's
    // "one pose of record, not two copies" rule.
    internal static readonly Vector3 PinTarget = new(x: 0f, y: 0.5f, z: -1f);

    internal const float PinYaw = 0f;
    internal const float PinPitch = 0.5651f;   // ~32.4 degrees — eye pulled up and back
    internal const float PinDistance = 17.7553f;

    private enum Phase {
        Idle,
        Warm,
        Sample,
        Done,
    }

    private Phase m_phase = Phase.Idle;
    private int m_framesLeft;
    private int m_sampleFrames = DefaultSampleFrames;
    private bool m_anyTimingsSeen;
    private string m_tierName = "native";
    private bool m_backendIsDirectX;
    // Resolved lazily on the first Advance that reports a pass count (mirrors the label set — never assumed fixed at
    // compile time), so a future pass addition to SdfWorldEngine.PassTimingLabels is picked up without a code change.
    private int m_beamPassIndex = -1;

    // One samples list per render pass (sized to Puck.SdfVm.SdfEngineNode.PassTimingCount) plus the whole-frame list.
    private readonly List<double>[] m_passSamples = new List<double>[Puck.SdfVm.SdfEngineNode.PassTimingCount];
    private readonly List<double> m_frameSamples = [];

    /// <summary>Initializes a new instance of the <see cref="RoomBenchScene"/> class.</summary>
    public RoomBenchScene() {
        for (var index = 0; (index < m_passSamples.Length); index++) {
            m_passSamples[index] = [];
        }
    }

    /// <summary>Whether a run is mid-flight (warming or sampling). While true the frame source asserts
    /// <see cref="CameraFrame"/> onto the director each composed frame.</summary>
    public bool Running => (m_phase is Phase.Warm or Phase.Sample);

    /// <summary>The fixed deterministic pose while <see cref="Running"/>, else null (the frame source releases the
    /// pin the instant this reads null after having read non-null — see <c>OverworldFrameSource.RoomBench.cs</c>).</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? CameraFrame =>
        (Running ? (PinTarget, PinYaw, PinPitch, PinDistance, false) : null);

    /// <summary>Starts a run: warms <see cref="WarmFrames"/> produced frames under the pinned pose, then samples
    /// <paramref name="frames"/> (clamped to at least 1; <c>&lt;= 0</c> selects <see cref="DefaultSampleFrames"/>).
    /// Rejected while a run is already in flight.</summary>
    /// <returns>A console status line.</returns>
    public string Start(int frames) {
        if (Running) {
            return $"[room.bench: a run is already in flight ({m_framesLeft} frame(s) left this phase) — room.bench abort first]";
        }

        m_sampleFrames = ((frames > 0) ? frames : DefaultSampleFrames);
        m_phase = Phase.Warm;
        m_framesLeft = WarmFrames;
        m_anyTimingsSeen = false;

        m_frameSamples.Clear();

        foreach (var samples in m_passSamples) {
            samples.Clear();
        }

        Console.Out.WriteLine(value: $"[room.bench] START — warm={WarmFrames} samples={m_sampleFrames} — pinned camera over the live room (no program swap). Summary line to stdout on completion.");

        return $"[room.bench: started — warm {WarmFrames}, sampling {m_sampleFrames} frame(s); summary to stdout when done]";
    }

    /// <summary>Cancels an in-flight run (no-op when idle). The camera pin releases on the next composed frame.</summary>
    /// <returns>A console status line.</returns>
    public string Abort() {
        if (!Running) {
            return "[room.bench.abort: nothing running]";
        }

        m_phase = Phase.Done;

        return "[room.bench.abort: cancelled — camera released]";
    }

    /// <summary>Advances the run one produced frame: called from the render node's produce loop with the PREVIOUS
    /// frame's per-pass GPU ms (<paramref name="hasTimings"/> false when timing is off or no timestamp landed).
    /// No-op when idle/finished — the zero-cost path while not benching.</summary>
    /// <param name="hasTimings">Whether this frame's readback carried valid GPU timestamps.</param>
    /// <param name="passMilliseconds">Each render pass's milliseconds, in <see cref="Puck.SdfVm.SdfEngineNode.PassTimingLabels"/> order.</param>
    /// <param name="passCount">How many entries of <paramref name="passMilliseconds"/> are valid.</param>
    /// <param name="frame">The whole-frame milliseconds.</param>
    /// <param name="renderScaleTierName">The live render-scale tier name the report names.</param>
    /// <param name="backendIsDirectX">Whether the host backend is Direct3D 12 (else Vulkan) — the report names it.</param>
    public void Advance(bool hasTimings, ReadOnlySpan<double> passMilliseconds, int passCount, double frame, string renderScaleTierName, bool backendIsDirectX) {
        if (!Running) {
            return;
        }

        m_tierName = renderScaleTierName;
        m_backendIsDirectX = backendIsDirectX;

        if (m_beamPassIndex < 0) {
            var labels = Puck.SdfVm.SdfEngineNode.PassTimingLabels;

            for (var index = 0; (index < labels.Length); index++) {
                if (string.Equals(a: labels[index], b: "beam", comparisonType: StringComparison.Ordinal)) {
                    m_beamPassIndex = index;

                    break;
                }
            }
        }

        if (m_phase == Phase.Warm) {
            if (--m_framesLeft <= 0) {
                m_phase = Phase.Sample;
                m_framesLeft = m_sampleFrames;
            }

            return;
        }

        // Sample phase.
        if (hasTimings) {
            m_anyTimingsSeen = true;

            m_frameSamples.Add(item: frame);

            for (var index = 0; ((index < passCount) && (index < m_passSamples.Length)); index++) {
                m_passSamples[index].Add(item: passMilliseconds[index]);
            }
        }

        if (--m_framesLeft > 0) {
            return;
        }

        Finish();
    }

    private void Finish() {
        m_phase = Phase.Done;

        Console.Out.WriteLine(value: FormatReport());
    }

    /// <summary>Renders the completed (or aborted-without-samples) run as ONE fixed summary line: per render pass
    /// median/min/p95, the whole-frame median/min/p95, the live render-scale tier + backend, and the beam pass singled
    /// out again as the DVFS-clock canary (measurement hygiene: only within-session paired runs at matched beam clocks
    /// compare cleanly — see the wave-3 arc notes).</summary>
    public string FormatReport() {
        if (!m_anyTimingsSeen) {
            return "[room.bench] ABORTED — no per-pass GPU timings available. Arm GPU timing live (the gpu.timing switch / the world.timing verb) and use a Vulkan/D3D12 host with timestamp support.";
        }

        var backend = (m_backendIsDirectX ? "Direct3D 12" : "Vulkan");
        var labels = Puck.SdfVm.SdfEngineNode.PassTimingLabels;
        var builder = new StringBuilder();

        builder.Append(value: "[room.bench] DONE tier=").Append(value: m_tierName)
            .Append(value: " backend=").Append(value: backend)
            .Append(value: " samples=").Append(value: m_frameSamples.Count)
            .Append(value: " (ms, med/min/p95)");

        for (var index = 0; ((index < m_passSamples.Length) && (index < labels.Length)); index++) {
            AppendStat(builder: builder, label: labels[index], samples: m_passSamples[index]);
        }

        AppendStat(builder: builder, label: "frame", samples: m_frameSamples);

        builder.Append(value: " | beam-clock canary=").Append(value: FormatMs(value: Median(values: BeamSamples)));

        return builder.ToString();
    }

    private List<double> BeamSamples => ((m_beamPassIndex >= 0) ? m_passSamples[m_beamPassIndex] : m_frameSamples);

    private static void AppendStat(StringBuilder builder, string label, List<double> samples) {
        builder.Append(value: " | ").Append(value: label).Append(value: ' ')
            .Append(value: FormatMs(value: Median(values: samples))).Append(value: '/')
            .Append(value: FormatMs(value: Min(values: samples))).Append(value: '/')
            .Append(value: FormatMs(value: Percentile(values: samples, p: 0.95)));
    }
    private static string FormatMs(double value) =>
        value.ToString(format: "F3", provider: CultureInfo.InvariantCulture);
    private static double Median(List<double> values) => Percentile(values: values, p: 0.5);
    private static double Min(List<double> values) {
        if (values.Count == 0) {
            return 0.0;
        }

        var min = values[0];

        foreach (var value in values) {
            min = Math.Min(val1: min, val2: value);
        }

        return min;
    }

    // Linear-interpolation percentile over a SORTED COPY (never mutates the caller's sample list).
    private static double Percentile(List<double> values, double p) {
        if (values.Count == 0) {
            return 0.0;
        }

        var sorted = values.ToArray();

        Array.Sort(array: sorted);

        var rank = (p * (sorted.Length - 1));
        var lower = (int)Math.Floor(d: rank);
        var upper = (int)Math.Ceiling(a: rank);

        if (lower == upper) {
            return sorted[lower];
        }

        var fraction = (rank - lower);

        return (sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction));
    }
}
