namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The balls covering one tile's beam interval.</summary>
    public const int TapeSlabCount = 8;
    /// <summary>The active word followed by one center and radius per slab.</summary>
    public const int TapeHeaderWords = (1 + (4 * TapeSlabCount));

    /// <summary>Returns one tile's tape storage: balls and each slab's live instruction and segment masks with summary.</summary>
    /// <param name="segments">The program's segment count.</param>
    /// <param name="tokens">The shapes and field pops addressed by the masks.</param>
    /// <returns>The number of uint words.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public static int SegmentTapeWordCountFor(int segments, int tokens) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: segments);
        ArgumentOutOfRangeException.ThrowIfNegative(value: tokens);
        var words = ((segments / 32) + (((segments & 31) == 0) ? 0 : 1));
        var instructionWords = ((tokens / 32) + (((tokens & 31) == 0) ? 0 : 1));

        return checked((TapeHeaderWords + (TapeSlabCount * (((2 * instructionWords) + words) + ((words + 31) / 32)))));
    }
}
