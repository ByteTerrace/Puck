using Puck.Maths;

namespace Puck.State;

/// <summary>The one canonical fold of a <see cref="StateFrame"/>'s stored values — the determinism canary's shared
/// comparator: a native run and a wasm run of the same document, ticks, and writes must produce the identical
/// <see cref="ulong"/> this returns, since both read through the same <see cref="FrameLayout"/> and both fold the
/// same <see cref="StateFrame.Values"/> span in the same order.</summary>
public static class StateFrameHash {
    /// <summary>Folds every value of <paramref name="frame"/>, in layout order, into one FNV-1a hash.</summary>
    /// <param name="frame">The frame to hash.</param>
    /// <returns>The hash value.</returns>
    public static ulong Compute(StateFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var hash = Fnv1aHash.Create();

        foreach (var value in frame.Values) {
            hash.Add(value: value);
        }

        return hash.Value;
    }
}
