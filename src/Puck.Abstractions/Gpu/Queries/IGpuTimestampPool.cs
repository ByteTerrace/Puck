namespace Puck.Abstractions.Gpu;

/// <summary>Creates optional device timestamp pools for observational pass timing. Nothing is created until called.</summary>
public interface IGpuTimestampFactory {
    /// <summary>Creates a pool, or returns null when the recording queue does not support timestamps.</summary>
    /// <param name="count">The positive number of query slots.</param>
    /// <param name="name">The debug object name.</param>
    /// <returns>The owned pool, or null for an unsupported queue.</returns>
    IGpuTimestampPool? Create(uint count, in GpuObjectName name);
}
/// <summary>A device-bound timestamp pool. Commands are recorded outside render passes, and each range is reset,
/// written, then resolved before it is reused. The caller waits its submission fence before reading or disposing it.
/// Timestamps describe observed GPU time only; they never judge work or affect simulation.</summary>
public interface IGpuTimestampPool : IDisposable {
    /// <summary>Gets nanoseconds per timestamp tick on this queue.</summary>
    double NanosecondsPerTick { get; }
    /// <summary>Gets the number of valid low bits, from 1 through 64. Subtraction masks to this width to handle wrap.</summary>
    uint ValidBits { get; }

    /// <summary>Resets a consecutive range before its next writes. Direct3D 12 needs no reset command.</summary>
    /// <param name="command">The recording command buffer.</param>
    /// <param name="first">The first query index.</param>
    /// <param name="count">The number of queries.</param>
    void Reset(nint command, uint first, uint count);
    /// <summary>Writes the queue timestamp after commands preceding this point have completed.</summary>
    /// <param name="command">The recording command buffer.</param>
    /// <param name="index">The query index.</param>
    void Write(nint command, uint index);
    /// <summary>Resolves written queries as consecutive 64-bit unsigned values into a readback buffer. The caller
    /// records a transfer-write to host-read barrier afterwards and reads only after the submission fence signals.</summary>
    /// <param name="command">The recording command buffer.</param>
    /// <param name="first">The first written query.</param>
    /// <param name="count">The number of written queries.</param>
    /// <param name="destination">The readback buffer's native handle.</param>
    /// <param name="offset">The destination byte offset, a multiple of eight.</param>
    void Resolve(nint command, uint first, uint count, nint destination, ulong offset);
}
/// <summary>One pass's observed average GPU time; unavailable until a timestamp pair completed.</summary>
/// <param name="Pass">The pass's ledger name.</param>
/// <param name="Milliseconds">The mean duration of the retained samples.</param>
/// <param name="Samples">The number of completed samples in the mean.</param>
public readonly record struct GpuPassTiming(string Pass, double Milliseconds, int Samples);
