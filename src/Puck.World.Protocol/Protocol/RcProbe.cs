using System.Collections.Concurrent;

namespace Puck.World.Protocol;

/// <summary>A scratch probe for the remote-content lane's headless pair. Deleted before the lane's commit.</summary>
public static class RcProbe {
    private static readonly ConcurrentDictionary<string, int> Seen = new(comparer: StringComparer.Ordinal);

    /// <summary>Writes one line the first time a key is seen and at each power of two count after it.</summary>
    /// <param name="key">The site and reason.</param>
    /// <param name="text">The detail.</param>
    public static void Note(string key, string text = "") {
        var count = Seen.AddOrUpdate(key: key, addValue: 1, updateValueFactory: static (_, held) => (held + 1));

        if ((count & (count - 1)) == 0) { Console.Error.WriteLine(value: $"[rc-probe] {key} x{count} {text}"); }
    }
}
