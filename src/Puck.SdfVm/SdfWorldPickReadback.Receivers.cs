using System.Buffers.Binary;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

internal sealed partial class SdfWorldPickReadback {
    public Action<SdfIndirectReceiverScope, SdfIndirectLightingCompletion, uint>? ReceiversCompleted { get; set; }

    public void PrepareReceivers(int slot, SdfIndirectCache? cache, string? version, SdfIndirectReceiverScope scope) {
        Poll();
        var target = m_slots[slot];
        target.ReceiverRecord = false;
        target.ReceiverSubmit = false;
        if (cache is null || cache.PublishedStamp == 0u || version is null) { return; }
        target.ReceiverBuffer ??= m_context.Services.BufferFactory.CreateReadback(sizeBytes: sizeof(uint),
            name: new GpuObjectName(owner: m_context.Instance, part: m_context.Pass, detail: "indirect-deferred", index: slot));
        target.ReceiverVersion = version;
        target.ReceiverOffset = 0;
        target.ReceiverScope = scope;
        target.ReceiverLighting = cache.LightingCompletion;
        target.ReceiverRecord = true;
    }

    private void PollReceivers() {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        foreach (var slot in m_slots) {
            if (slot.ReceiverFence is not { IsSignaled: true }) { continue; }
            slot.ReceiverFence = null;
            slot.ReceiverBuffer!.Read(bytes);
            ReceiversCompleted?.Invoke(slot.ReceiverScope, slot.ReceiverLighting, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        }
    }

    private sealed partial class Slot {
        public IGpuReadbackBuffer? ReceiverBuffer;
        public IGpuSubmissionFence? ReceiverFence;
        public string? ReceiverVersion;
        public ulong ReceiverOffset;
        public SdfIndirectReceiverScope ReceiverScope;
        public SdfIndirectLightingCompletion ReceiverLighting;
        public bool ReceiverRecord;
        public bool ReceiverSubmit;
    }
}
