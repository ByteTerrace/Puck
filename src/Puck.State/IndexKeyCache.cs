using System.Globalization;

namespace Puck.State;

/// <summary>Canonical decimal cell keys for small non-negative indices, cached once so a rule scan never allocates
/// its address strings per tick.</summary>
public static class IndexKeyCache {
    private static readonly string[] s_keys = CreateKeys();

    /// <summary>Returns the decimal spelling of an index — cached below <see cref="StateCapacity.MaxCellsPerRow"/>,
    /// minted otherwise.</summary>
    /// <param name="index">The index.</param>
    public static string Get(int index) => (((uint)index < (uint)s_keys.Length)
        ? s_keys[index]
        : index.ToString(provider: CultureInfo.InvariantCulture)
    );

    /// <summary>Returns the decimal spelling of an index — cached below <see cref="StateCapacity.MaxCellsPerRow"/>,
    /// minted otherwise.</summary>
    /// <param name="index">The index.</param>
    public static string Get(long index) => (((ulong)index < (ulong)s_keys.Length)
        ? s_keys[(int)index]
        : index.ToString(provider: CultureInfo.InvariantCulture)
    );

    private static string[] CreateKeys() {
        var keys = new string[StateCapacity.MaxCellsPerRow];
        for (var index = 0; index < keys.Length; index++) {
            keys[index] = index.ToString(provider: CultureInfo.InvariantCulture);
        }
        return keys;
    }
}
