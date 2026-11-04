using System.Numerics;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>Owns the reusable input storage that transports prepared mesh-bake rows to their upload.</summary>
/// <param name="allocator">The host allocator, or null for managed offline storage.</param>
public sealed class SdfMeshBakeInputTransport(IAllocator? allocator) : IDisposable {
    private readonly MemoryAccount.Owner m_memory = MemoryAccount.Shared.CreateOwner(name: "mesh-bake-points");
    private OwnedBuffer<Vector4>? m_storage;
    private bool m_disposed;

    /// <summary>Acquires exactly the requested input prefix, growing storage on demand. Existing rows survive and new
    /// rows are cleared. The borrow ends before another acquisition or disposal.</summary>
    /// <param name="requiredRows">The nonnegative input prefix length.</param>
    /// <returns>The writable prefix; empty demand allocates no backing storage.</returns>
    public Span<Vector4> Acquire(int requiredRows) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfNegative(value: requiredRows);
        if (requiredRows == 0) { return []; }
        var storage = m_storage ??= new(allocator: allocator, observer: m_memory);
        if (storage.Length < requiredRows) { storage.Resize(requiredRows); }
        return storage.GetSpan()[..requiredRows];
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        m_storage?.Dispose();
        m_storage = null;
        m_memory.Dispose();
    }
}
