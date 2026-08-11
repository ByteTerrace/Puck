using System.Text;
using Puck.Maths;

namespace Puck.World;

/// <summary>UTF-8 string folding for deterministic world choices. The recurrence and integer byte order remain the
/// shared <see cref="Fnv1aHash"/> primitive; this adapter only makes the string encoding explicit.</summary>
public static class WorldDeterministicHash {
    /// <summary>Folds one string as UTF-8 bytes into an already-primed shared accumulator.</summary>
    public static void AddUtf8(ref Fnv1aHash hash, string value) {
        ArgumentNullException.ThrowIfNull(argument: value);
        var byteCount = Encoding.UTF8.GetByteCount(s: value);
        var bytes = ((byteCount <= 256) ? ((Span<byte>)stackalloc byte[byteCount]) : new byte[byteCount]);
        _ = Encoding.UTF8.GetBytes(value, bytes);
        hash.Add(values: bytes);
    }
}
