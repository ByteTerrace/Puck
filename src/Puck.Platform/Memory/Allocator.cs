namespace Puck.Memory;

/// <summary>The process's native allocator on a platform host: mimalloc, installed as <see cref="ProcessAllocator.Current"/>
/// the first time this type is read, unless a host assigned another allocator before then. A host reads
/// <see cref="Current"/> before it builds anything that owns storage, so that storage is mimalloc's.</summary>
public static class Allocator {
    static Allocator() {
        if (ProcessAllocator.Current is SystemAllocator) { ProcessAllocator.Current = new MimallocAllocator(); }
    }

    /// <summary>Gets or sets the process's allocator (<see cref="ProcessAllocator.Current"/>).</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public static IAllocator Current {
        get => ProcessAllocator.Current;
        set => ProcessAllocator.Current = value;
    }
}
