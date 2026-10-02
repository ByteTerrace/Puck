using System.Globalization;
using System.Text;

namespace Puck.World;

/// <summary>Presentation-only timestamp demand: the operator's (<c>world.gpu-timing</c>) or dynamic resolution's, which
/// reads the world's views' frame time from it. The demand is applied to the graph's nodes on the frame thread, and off
/// never walks the graph during a steady frame.</summary>
/// <param name="probe">The optional live render root.</param>
internal sealed class WorldGpuTiming(WorldRenderProbe? probe = null) {
    // Every on or off is a new demand, applied whole on the next frame even when the two cancel out between frames, so
    // an off then on still asks a node that refused timing to try once more.
    private long m_demand;
    private long m_applied;
    private bool m_operator;
    private bool m_dynamicResolution;

    public bool Available => (probe?.Root is not null);
    public bool Enabled => (m_operator || m_dynamicResolution);
    // Whether the operator asked for the readout, which dynamic resolution's demand alone never shows.
    public bool ReadoutEnabled => m_operator;

    public void Set(bool enabled) {
        m_operator = enabled;
        m_demand++;
    }
    // Dynamic resolution's demand, a new demand only when it moves what the nodes record.
    public void Require(bool required) {
        if (m_dynamicResolution == required) { return; }
        var was = Enabled;

        m_dynamicResolution = required;
        if (Enabled != was) { m_demand++; }
    }
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

    public string Describe() {
        var state = (m_operator ? "on" : (m_dynamicResolution ? "off (recording for dynamic resolution)" : "off"));
        var text = new StringBuilder(value: $"[world.gpu-timing: {state} window={Puck.Shaders.ShaderPipelineRenderNode.TimingWindow}");

        if (m_operator && (probe?.Root?.Runtime is { } runtime)) {
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
}
