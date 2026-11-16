using Puck.Assets;

namespace Puck.Machines;

/// <summary>
/// A forward-only binary sink each component serializes its mutable state into for a whole-machine snapshot. Scalars are
/// written little-endian regardless of host endianness (portable), and large memory blocks are bulk-copied verbatim
/// through <see cref="LittleEndianWriter.WriteRaw{T}"/> — the flat, memcpy-friendly savestate. The writer keeps and
/// grows one backing buffer that <see cref="LittleEndianWriter.Reset"/> rewinds, so a machine that retains one writer
/// reuses the same allocation across snapshots rather than churning the GC. This is the one serialization sink shared by
/// every GamingBrick core, so a snapshot taken on one machine restores bit-identically on another — the property the
/// whole fork-and-diverge model rests on.
/// </summary>
public sealed class StateWriter : LittleEndianWriter {
    /// <summary>Initializes a new instance of the <see cref="StateWriter"/> class, empty.</summary>
    /// <param name="capacity">The initial backing-buffer capacity in bytes; it grows as needed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public StateWriter(int capacity = 256) : base(capacity: capacity) { }

    /// <summary>Opens a forward-only reader over the bytes written so far, aliasing the live backing buffer with no
    /// copy — the zero-allocation source that lets one machine's serialized state be read straight into a sibling
    /// (a pooled fork) without materializing a snapshot image. The reader is valid only until the next write or
    /// <see cref="LittleEndianWriter.Reset"/>, so consume it before reusing the writer.</summary>
    /// <returns>A reader over the current contents.</returns>
    public StateReader OpenReader() => new(
        buffer: Buffer,
        length: Length,
        start: 0
    );
}
