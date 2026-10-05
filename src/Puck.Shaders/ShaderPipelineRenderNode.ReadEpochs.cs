using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    private readonly List<RenderGraphReadEpoch.FrozenRead> m_recordedReadCopies = [];
    internal RenderGraphReadEpoch?[] ReadEpochs { get; set; } = [];

    // The normal frame command owns these copies, so no slot fence, source lease or command pool is needed while a
    // graph is still building. Source leases protect only the copying submission; every later read leases the copy.
    private void RecordFrozenReads(nint command) {
        if (Reads is not { } reads) { return; }
        for (var index = 0; index < reads.Count && index < ReadEpochs.Length; index++) {
            if (ReadEpochs[index] is not { IsActive: true } epoch) { continue; }
            var input = reads[index];
            epoch.Observe(input);
            if (!epoch.TryGet(input.Producer, out var held)) {
                held = m_recordedReadCopies.Find(image => ReferenceEquals(image.Epoch, epoch) && image.Producer == input.Producer);
            }
            if (held is null) {
                var image = m_gpu.ImageFactory.Create(format: input.Image.Format, height: input.Image.Height,
                    width: input.Image.Width, usage: GpuImageUsage.Sampled,
                    name: new GpuObjectName(owner: m_name, part: "epoch-copy"));
                held = new(epoch, input.Producer, image, input.Publication, input.Tainted);
                m_recordedReadCopies.Add(held);
                m_held.Add(new HeldResource(Handle: image.ImageHandle, Resource: held,
                    Bytes: ImageBytes(input.Image.Format.ToString(), input.Image.Width, input.Image.Height), Epoch: epoch));
                m_frameLeases.Hold(reads.Take(index));
                RecordImageCopy(command, input.Image.ImageHandle, input.Layout, image.ImageHandle,
                    input.Image.Width, input.Image.Height, GpuImageLayout.ShaderReadOnly);
            } else {
                reads.Take(index).Retire();
            }
            var lease = m_images is not null && m_images.TryLease(held.Image.ImageHandle, out var acquired)
                ? acquired : new GpuImageLease(held.Image.ImageViewHandle);
            reads.Bind(index, held.Image, GpuImageLayout.ShaderReadOnly,
                lease with { Publication = held.Publication, Image = held.Image }, held.Tainted);
        }
    }

    private void CommitFrozenReads() {
        foreach (var image in m_recordedReadCopies) { image.Epoch.Submitted(image); }
        m_recordedReadCopies.Clear();
    }
    private void DiscardUnsubmittedReadCopies() {
        foreach (var image in m_recordedReadCopies) {
            m_held.RemoveAll(held => ReferenceEquals(held.Resource, image));
            image.Dispose();
        }
        m_recordedReadCopies.Clear();
    }
}
