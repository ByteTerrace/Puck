using System.Globalization;
using System.Text;

namespace Puck.World;

/// <summary>Presentation-only timestamp demand. Off never walks the graph during a steady frame.</summary>
/// <param name="probe">The optional live render root.</param>
internal sealed class WorldGpuTiming(WorldRenderProbe? probe = null) {
    public bool Available => (probe?.Root is not null);
    public bool Enabled { get; private set; }

    public void Set(bool enabled) {
        Enabled = enabled;
        Apply();
    }
    public void Tick() { if (Enabled) { Apply(); } }

    private void Apply() {
        if (probe?.Root?.Runtime is not { } runtime) { return; }
        for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
            if (runtime.Producer(instance: index) is null) { runtime.Node(instance: index).TimingEnabled = Enabled; }
        }
    }

    public string Describe() {
        var text = new StringBuilder(value: $"[world.gpu-timing: {(Enabled ? "on" : "off")} window={Puck.Shaders.ShaderPipelineRenderNode.TimingWindow}");

        if (Enabled && (probe?.Root?.Runtime is { } runtime)) {
            for (var index = 0; (index < runtime.Instances.Instances.Count); index++) {
                if (runtime.Producer(instance: index) is not null) { continue; }
                var node = runtime.Node(instance: index);
                var name = runtime.Instances.Instances[index].Name;

                if (node.Timings.IsEmpty) { text.Append(value: $"\n{name}: awaiting completed timestamps or unsupported"); }
                foreach (var timing in node.Timings) {
                    text.Append(provider: CultureInfo.InvariantCulture, handler: $"\n{name}/{timing.Pass}: {timing.Milliseconds:0.000} ms samples={timing.Samples}");
                }
            }
        }
        return text.Append(value: ']').ToString();
    }
}
