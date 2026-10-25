namespace Puck.Abstractions.Gpu;

/// <summary>
/// The owner of a <see cref="GpuRegion"/>'s words past its host copy (<see cref="GpuRegion.ShadowBytes"/>): words the
/// owner holds in its own arrays, so the region keeps no copy of them and asks the owner for them when a flush owes
/// them (<see cref="GpuRegion.OweSourced"/>).
/// </summary>
public interface IGpuRegionSource {
    /// <summary>Writes the region's words <paramref name="word"/> to <paramref name="word"/> plus
    /// <paramref name="count"/> into a host-visible buffer from <paramref name="destinationOffsetBytes"/>, as the region
    /// holds them now: every word, including those no reader reads, which the owner writes as any value it likes.</summary>
    /// <param name="buffer">The host-visible buffer the flush stages into.</param>
    /// <param name="destinationOffsetBytes">The byte offset in <paramref name="buffer"/> the first word lands at.</param>
    /// <param name="word">The first region word, at or past the region's host copy.</param>
    /// <param name="count">The words; positive.</param>
    void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count);
}
