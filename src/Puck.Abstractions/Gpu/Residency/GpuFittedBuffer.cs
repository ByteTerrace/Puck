using Puck.Maths;

namespace Puck.Abstractions.Gpu;

/// <summary>Controls when a buffer is replaced and the size of its next allocation.</summary>
public enum GpuBufferSizing {
    /// <summary>Grow from the current allocation with geometric headroom.</summary>
    Grown,
    /// <summary>Round each larger allocation up to a 4 MiB granule.</summary>
    Granule,
    /// <summary>Allocate the requested size only when the buffer is too small.</summary>
    Fit,
    /// <summary>Replace the buffer whenever its requested size changes.</summary>
    Exact,
}

/// <summary>Owns the current allocation. A replacement is created before the owner retires its predecessor.</summary>
public sealed class GpuFittedBuffer(Func<ulong, IGpuBuffer> allocate, GpuBufferSizing sizing = GpuBufferSizing.Grown,
    Action<IGpuBuffer>? retire = null) : IDisposable {
    /// <summary>Creates a holder for a device-local buffer with a fixed name and usage.</summary>
    public GpuFittedBuffer(IGpuBufferFactory factory, GpuObjectName name, GpuBufferUsage usage = GpuBufferUsage.Storage,
        GpuBufferSizing sizing = GpuBufferSizing.Grown, Action<IGpuBuffer>? retire = null)
        : this(bytes => factory.CreateDeviceLocal(bytes, usage, name), sizing, retire) { }

    /// <summary>The current allocation, or null before the first fit.</summary>
    public IGpuBuffer? Buffer { get; private set; }

    /// <summary>Whether fitting the requested bytes would replace the current allocation.</summary>
    public bool NeedsFit(ulong bytes) => AllocationSize(sizing, Buffer?.SizeBytes ?? 0, bytes) != Buffer?.SizeBytes;

    /// <summary>Returns the allocation to hold for this request, including any reusable current capacity.</summary>
    public static ulong AllocationSize(GpuBufferSizing sizing, ulong current, ulong bytes) {
        bytes = Math.Max(bytes, 1UL);
        if (sizing != GpuBufferSizing.Exact && bytes <= current) { return current; }
        return sizing switch {
            GpuBufferSizing.Granule => bytes.AlignUp(alignment: 4UL << 20),
            GpuBufferSizing.Grown => (ulong)GpuCapacity.Grown(checked((long)current), checked((long)bytes), long.MaxValue, divisor: 1),
            _ => bytes,
        };
    }

    /// <summary>Replaces the buffer when needed. The retirement callback takes ownership of the old allocation
    /// on success. Without a callback, the caller guarantees exclusive use and the buffer is disposed immediately.</summary>
    public bool Fit(ulong bytes, Action<IGpuBuffer>? retirePrevious = null) {
        var previous = Buffer;
        var size = AllocationSize(sizing, previous?.SizeBytes ?? 0, bytes);
        if (previous?.SizeBytes == size) { return false; }
        var next = allocate(size);
        try {
            if (previous is not null) {
                if ((retirePrevious ?? retire) is { } release) { release(previous); }
                else { previous.Dispose(); }
            }
        } catch {
            next.Dispose();
            throw;
        }
        Buffer = next;
        return true;
    }

    /// <summary>Replaces the buffer when needed, disposing the current allocation before the next is made, so the two
    /// never stand at once: for an owner every reader of whose current allocation has completed. A refused allocation
    /// leaves no buffer and throws.</summary>
    /// <param name="bytes">The bytes the buffer must hold.</param>
    /// <returns>Whether the buffer was replaced.</returns>
    public bool Refit(ulong bytes) {
        var previous = Buffer;
        var size = AllocationSize(sizing, previous?.SizeBytes ?? 0, bytes);
        if (previous?.SizeBytes == size) { return false; }
        Buffer = null;
        previous?.Dispose();
        Buffer = allocate(size);
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() { Buffer?.Dispose(); Buffer = null; }
}
