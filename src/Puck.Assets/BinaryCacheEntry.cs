namespace Puck.Assets;

/// <summary>Bounds sizes read from a binary cache entry by the bytes remaining in its payload.</summary>
public static class BinaryCacheEntry {
    /// <summary>Reads a little-endian count for items that each occupy at least one byte.</summary>
    /// <param name="reader">The reader positioned at the count within a bounded payload.</param>
    /// <returns>The non-negative count.</returns>
    /// <exception cref="InvalidDataException">The count cannot fit in the remaining payload.</exception>
    public static int ReadCount(BinaryReader reader) {
        var count = reader.ReadInt32();

        if ((count < 0) || (count > (reader.BaseStream.Length - reader.BaseStream.Position))) {
            throw new InvalidDataException(message: "Invalid cache entry length.");
        }
        return count;
    }
}
