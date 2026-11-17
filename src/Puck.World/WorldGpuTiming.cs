using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>Presentation-only timestamp demand: the operator's (<c>world.gpu-timing</c>) or dynamic resolution's, which
/// reads the world's views' frame time from it. The demand is applied to the graph's nodes on the frame thread, and off
/// never walks the graph during a steady frame.</summary>
/// <param name="probe">The optional live render root.</param>
public sealed class WorldGpuTiming(WorldRenderProbe? probe = null) {
    // Every on or off is a new demand, applied whole on the next frame even when the two cancel out between frames, so
    // an off then on still asks a node that refused timing to try once more.
    private long m_demand;
    private long m_applied;
    private bool m_operator;
    private bool m_dynamicResolution;
    private bool m_recording;

    /// <summary>Whether a render root exists.</summary>
    public bool Available => (probe?.Root is not null);
    /// <summary>Whether any consumer requires timestamps.</summary>
    public bool Enabled => (m_operator || m_dynamicResolution || m_recording);
    // Whether the operator asked for the readout, which dynamic resolution's demand alone never shows.
    public bool ReadoutEnabled => m_operator;

    /// <summary>Sets the operator's timestamp demand.</summary>
    /// <param name="enabled">Whether timestamps are wanted.</param>
    public void Set(bool enabled) {
        SetDemand(ref m_operator, enabled, renew: true);
    }
    /// <summary>Sets a report recorder's timestamp demand, renewing its window on entry.</summary>
    /// <param name="required">Whether a recording is active.</param>
    public void RequireRecording(bool required) => SetDemand(ref m_recording, required, renew: required);

    private void SetDemand(ref bool demand, bool required, bool renew) {
        var was = Enabled;
        demand = required;
        if (renew || was != Enabled) { m_demand++; }
    }

    /// <summary>Copies the closing rolling GPU window, including the overlay pass, into a report. This is not a
    /// whole-run distribution; the window and per-pass sample counts state its coverage.</summary>
    /// <returns>A detached report with explicit availability.</returns>
    public JsonObject Capture() {
        var nodes = new JsonArray();
        var measured = false;
        if (probe?.Root?.Runtime is { } runtime) {
            for (var index = 0; index < runtime.Instances.Instances.Count; index++) {
                if (runtime.Producer(instance: index) is not null) { continue; }
                var node = runtime.Node(instance: index);
                var passes = new JsonArray();
                foreach (var timing in node.Timings) {
                    measured = true;
                    passes.Add(new JsonObject { ["pass"] = timing.Pass, ["milliseconds"] = timing.Milliseconds, ["samples"] = timing.Samples });
                }
                nodes.Add(new JsonObject {
                    ["node"] = runtime.Instances.Instances[index].Name,
                    ["state"] = node.Timings.IsEmpty ? "unmeasured" : "present",
                    ["refusal"] = node.TimingRefusal,
                    ["passes"] = passes,
                });
            }
        }
        return new JsonObject {
            ["state"] = !Available ? "absent" : measured ? "present" : "unmeasured",
            ["coverage"] = "closing rolling window",
            ["window"] = Puck.Shaders.ShaderPipelineRenderNode.TimingWindow,
            ["nodes"] = nodes,
        };
    }
    // Dynamic resolution's demand, a new demand only when it moves what the nodes record.
    /// <summary>Sets dynamic resolution's timestamp demand.</summary>
    /// <param name="required">Whether timestamps are wanted.</param>
    public void Require(bool required) {
        SetDemand(ref m_dynamicResolution, required, renew: false);
    }
    /// <summary>Applies current timestamp demand on the frame thread.</summary>
    public void Tick() {
        var renewed = (m_demand != m_applied);

        if (!Enabled && !renewed) { return; }
        m_applied = m_demand;
        Apply(renewed: renewed);
    }

    private void Apply(bool renewed) {
        if (probe?.Root?.Runtime is not { } runtime) { return; }
        for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
            if (runtime.Producer(instance: index) is not null) { continue; }
            var node = runtime.Node(instance: index);

            if (renewed) { node.TimingEnabled = false; }
            node.TimingEnabled = Enabled;
        }
    }

    // Each timed node's latest submission as a span on the shared queue clock, ordered by start and relative to the
    // earliest, with the idle gap before each: the queue's own order of work and where it waits.
    /// <summary>Describes the latest completed queue spans.</summary>
    /// <returns>The diagnostic readout.</returns>
    public string DescribeTimeline() {
        var text = new StringBuilder(value: $"[world.gpu-timing timeline: {DemandState}");

        if ((m_operator || m_recording) && (probe?.Root?.Runtime is { } runtime)) {
            var spans = new List<(string Name, double Start, double End, double Sum)>();

            for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
                if (runtime.Producer(instance: index) is not null) { continue; }
                var node = runtime.Node(instance: index);
                var (start, end) = node.LatestTimingSpan;

                if (end > start) { spans.Add(item: (runtime.Instances.Instances[index].Name, start, end, node.LatestTimingMilliseconds)); }
            }
            spans.Sort(comparison: static (a, b) => a.Start.CompareTo(value: b.Start));
            var origin = ((spans.Count > 0) ? spans[0].Start : 0d);
            var previousEnd = origin;

            foreach (var (name, start, end, sum) in spans) {
                text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n{(start - origin),10:0.000} +{(end - start),8:0.000} ms passes={sum:0.000} gap={(start - previousEnd),8:0.000} {name}");
                previousEnd = Math.Max(val1: previousEnd, val2: end);
            }
        }
        return text.Append(value: ']').ToString();
    }

    /// <summary>Reads each pass whose completed window the nodes hold, by the name the readout prints (the node's name, a
    /// slash, the pass). Nodes that refuse timing or hold no completed window have none.</summary>
    /// <returns>The passes; empty while no consumer asks for timestamps.</returns>
    public List<GpuPassTiming> Readings() {
        var readings = new List<GpuPassTiming>();

        if (!(m_operator || m_recording) || (probe?.Root?.Runtime is not { } runtime)) { return readings; }
        for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
            if (runtime.Producer(instance: index) is not null) { continue; }
            var node = runtime.Node(instance: index);
            var name = runtime.Instances.Instances[index].Name;

            if (node.TimingRefusal is not null) { continue; }
            foreach (var timing in node.Timings) {
                readings.Add(item: timing with { Pass = $"{name}/{timing.Pass}" });
            }
        }

        return readings;
    }

    /// <summary>Describes each pass's rolling timestamp window.</summary>
    /// <returns>The diagnostic readout.</returns>
    public string Describe() {
        var text = new StringBuilder(value: $"[world.gpu-timing: {DemandState} window={Puck.Shaders.ShaderPipelineRenderNode.TimingWindow}");

        if ((m_operator || m_recording) && (probe?.Root?.Runtime is { } runtime)) {
            for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
                if (runtime.Producer(instance: index) is not null) { continue; }
                var node = runtime.Node(instance: index);
                var name = runtime.Instances.Instances[index].Name;

                if (node.TimingRefusal is { } refusal) {
                    text.Append(value: $"\n{name}: refused {refusal}");
                    continue;
                }
                if (node.Timings.IsEmpty) { text.Append(value: $"\n{name}: awaiting completed timestamps"); }
                foreach (var timing in node.Timings) {
                    text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n{name}/{timing.Pass}: {timing.Milliseconds:0.000} ms samples={timing.Samples}");
                }
            }
        }
        return text.Append(value: ']').ToString();
    }

    private string DemandState => m_operator ? "on" : m_recording ? "on (recording)" : m_dynamicResolution ? "off (recording for dynamic resolution)" : "off";
}
