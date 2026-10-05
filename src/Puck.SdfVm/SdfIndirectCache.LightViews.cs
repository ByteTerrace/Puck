using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfIndirectCache {
    private readonly Lock m_lightBanksGate = new();
    private readonly List<LightViewBank> m_lightBanks = [];

    /// <summary>Gets the live light-camera bank bytes, including banks held by retiring graphs.</summary>
    public ulong LightViewBytes {
        get {
            lock (m_lightBanksGate) {
                return m_lightBanks.Aggregate(seed: 0UL, func: static (sum, bank) => checked(sum + bank.Buffer.SizeBytes));
            }
        }
    }

    // The graph's successful build owns this hold until its recorder retires. A replacement gets another bank:
    // neither its scheduled region writes nor its disposal can affect readers of the previous publication.
    internal LightViewBank CreateLightViewBank(IGpuBufferFactory buffers, ulong bytes, GpuObjectName name) {
        Retain();
        IGpuBuffer? buffer = null;
        try {
            buffer = buffers.CreateDeviceLocal(sizeBytes: bytes, usage: GpuBufferUsage.Storage, name: name);
            var bank = new LightViewBank(owner: this, buffer: buffer);
            lock (m_lightBanksGate) { m_lightBanks.Add(item: bank); }
            return bank;
        } catch { buffer?.Dispose(); Dispose(); throw; }
    }

    internal sealed class LightViewBank(SdfIndirectCache owner, IGpuBuffer buffer) : IDisposable {
        private SdfIndirectCache? m_owner = owner;
        public IGpuBuffer Buffer { get; } = buffer;

        public void Dispose() {
            if (Interlocked.Exchange(location1: ref m_owner, value: null) is not { } held) { return; }
            lock (held.m_lightBanksGate) {
                Buffer.Dispose();
                held.m_lightBanks.Remove(item: this);
            }
            held.Dispose();
        }
    }
}
