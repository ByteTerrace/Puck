using System.Globalization;

namespace Puck.World.Server;

/// <summary>Canonical decimal body keys, cached once so rule scans never allocate their address strings per tick.</summary>
internal static class WorldBodyKeyCache {
    private static readonly string[] s_keys = CreateKeys();

    public static string Get(int index) => (((uint)index < (uint)s_keys.Length)
        ? s_keys[index]
        : index.ToString(provider: CultureInfo.InvariantCulture)
    );

    public static string Get(long index) => (((ulong)index < (ulong)s_keys.Length)
        ? s_keys[(int)index]
        : index.ToString(provider: CultureInfo.InvariantCulture)
    );

    private static string[] CreateKeys() {
        var keys = new string[WorldBodiesLimits.CapacityCeiling];
        for (var index = 0; index < keys.Length; index++) {
            keys[index] = index.ToString(provider: CultureInfo.InvariantCulture);
        }
        return keys;
    }
}
