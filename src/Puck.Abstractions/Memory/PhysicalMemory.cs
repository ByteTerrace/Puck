namespace Puck.Abstractions.Memory;

/// <summary>
/// A pool: one physical memory the process's bytes occupy. A discrete adapter has two, the host's memory and the
/// adapter's own; a unified-memory device has one, the host's, which its device allocations occupy as well. Every
/// counted byte is charged to exactly one pool, chosen by where it was placed, never by what its caller asked for.
/// </summary>
public enum PhysicalMemory {
    /// <summary>The host's memory: the managed heap, native allocations, and device allocations placed in system memory
    /// (a discrete adapter's non-local segment, or every segment of a unified-memory device).</summary>
    Host,
    /// <summary>A discrete adapter's own memory: the device allocations placed in its local segments.</summary>
    Device,
}
