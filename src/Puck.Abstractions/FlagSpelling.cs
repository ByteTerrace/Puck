using System.Globalization;

namespace Puck.Abstractions;

/// <summary>
/// The one way a readout spells a set of flags: each set flag's name in the order its table lists them, joined by
/// <c>+</c>, or <c>none</c> when no listed flag is set. Each flag type keeps its own table of names, the one spelling of
/// each flag, which its readout and any parser of it share.
/// </summary>
public static class FlagSpelling {
    /// <summary>Returns a set of flags as it prints.</summary>
    /// <typeparam name="T">The flag enumeration.</typeparam>
    /// <param name="flags">The flags.</param>
    /// <param name="names">Each flag's one spelling, in the order they print.</param>
    /// <returns>The spelling.</returns>
    public static string Of<T>(T flags, ReadOnlySpan<(T Flag, string Name)> names) where T : struct, Enum {
        var value = Convert.ToUInt64(value: flags, provider: CultureInfo.InvariantCulture);
        var spelled = new List<string>(capacity: names.Length);

        foreach (var (flag, name) in names) {
            if ((value & Convert.ToUInt64(value: flag, provider: CultureInfo.InvariantCulture)) != 0UL) { spelled.Add(item: name); }
        }

        return ((spelled.Count == 0) ? "none" : string.Join(separator: '+', values: spelled));
    }
}
