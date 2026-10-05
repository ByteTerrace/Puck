using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class RenderGraphRuntime {
    private RenderGraphReadEpoch? ReadEpochOf(int index, string producer) {
        if (m_graphs[index] is not { } graph) { return null; }
        RenderGraphReadEpoch? selected = null;
        foreach (var pass in graph.Pipeline.Plan.Passes) {
            if (pass.Package is not { } step || !m_packages.TryGetFactory(step.Package, out var factory) ||
                factory.ReadEpochOf(m_set.Instances[index].Name, producer) is not { IsActive: true } epoch) { continue; }
            if (selected is not null && !ReferenceEquals(selected, epoch)) {
                throw new InvalidDataException($"The instance '{m_set.Instances[index].Name}' assigns conflicting frozen epochs to '{producer}'.");
            }
            selected = epoch;
        }
        return selected;
    }

    // Observe the live acquisition before replacing it, so the finite owner can adopt changes only in its next epoch.
    // A copied publication keeps its original source identity and taint; an incoming clean frame cannot relabel it.
    private RenderGraphExternalOutput FrozenReadOf(int index, string producer, RenderGraphExternalOutput acquired) {
        if (ReadEpochOf(index, producer) is not { } epoch) { return acquired; }
        epoch.Observe(new(producer, acquired.Image, acquired.Layout, acquired.Lease, acquired.Tainted));
        if (!epoch.TryGet(producer, out var held) || held is null) { return acquired; }
        acquired.Lease.Retire();
        return new(held.Image, GpuImageLayout.ShaderReadOnly,
            LeaseOf(held.Image) with { Publication = held.Publication, Image = held.Image }, held.Tainted);
    }

    private FrameRender PrepareReadEpochs(int index, ShaderPipelineRenderNode node) {
        var count = node.Reads?.Count ?? 0;
        if (node.ReadEpochs.Length != count) { node.ReadEpochs = new RenderGraphReadEpoch?[count]; }
        for (var read = 0; read < count; read++) {
            var input = node.Reads![read];
            var epoch = ReadEpochOf(index, input.Producer);
            node.ReadEpochs[read] = epoch;
            if (epoch is null) { continue; }
            if (!epoch.IsValid) {
                return FrameRender.Waiting($"the instance '{m_set.Instances[index].Name}' awaits a new frozen image epoch after its copy owner was released");
            }
            if (input.Lease.ImageViewHandle == 0) {
                return FrameRender.Waiting($"the instance '{m_set.Instances[index].Name}' awaits '{input.Producer}' for its frozen image epoch");
            }
            if (!input.Image.IsSameDeviceImage || !input.Publication.IsKnown) {
                return FrameRender.Refused($"the source '{input.Producer}' exposes no complete acquired image and publication for a frozen image epoch");
            }
        }
        return FrameRender.Rendered;
    }
}
