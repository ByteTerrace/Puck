using System.Text;
using System.Text.Unicode;

namespace Puck.Scripting;

/// <summary>The STRUCTURAL, VARIABLE-STRIDE channel-name table decoder: validates and decodes exactly
/// <c>count</c> packed entries — each a <c>u8</c> length in <c>[1, MaxChannelNameBytes]</c> followed by that many
/// UTF-8 bytes, with no padding between entries — applying the load-time guards in order (length out of range,
/// truncated table, invalid UTF-8, empty text, a control character, a duplicate name). Unlike <see cref="AddonOutCellReader"/>'s
/// fixed-stride cells, an entry's byte length is data, so this reader also reports how many bytes it consumed —
/// the caller needs that to bound the table's region for the mount's overlap sweep, since nothing upstream of
/// this call can compute it from <c>count</c> alone. Resolution against <see cref="IAddonChannelResolver"/> is
/// called once per entry here and NEVER faults the decode: an unresolvable name still decodes cleanly, carrying
/// a sentinel (see <see cref="AddonChannelBinding"/>) — see that type's remarks for why. Read once at
/// instantiation and cached; never re-read per tick.</summary>
public static class AddonChannelNameTableReader {
    /// <summary>Validates and decodes <paramref name="count"/> channel-name entries from <paramref name="source"/>.</summary>
    /// <param name="source">The packed entry bytes, from the declared table offset to the end of guest memory — the caller does not know the table's total length up front.</param>
    /// <param name="count">The number of entries the guest declared.</param>
    /// <param name="resolver">The host channel table each declared name is resolved against (see <see cref="IAddonChannelResolver"/>).</param>
    /// <param name="destination">The caller-owned buffer decoded bindings are written into.</param>
    /// <param name="consumedBytes">When this returns <see langword="true"/>, the total byte length of the decoded table — the region the caller bounds its overlap sweep against.</param>
    /// <param name="errorIndex">When this returns <see langword="false"/>, the offending entry index; otherwise <c>-1</c>.</param>
    /// <param name="error">When this returns <see langword="false"/>, the specific rejection reason; otherwise empty.</param>
    /// <returns><see langword="true"/> if every entry decoded cleanly; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> or <paramref name="resolver"/> is <see langword="null"/>.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> source, int count, IAddonChannelResolver resolver, AddonChannelBinding[] destination, out int consumedBytes, out int errorIndex, out string error) {
        ArgumentNullException.ThrowIfNull(argument: destination);
        ArgumentNullException.ThrowIfNull(argument: resolver);

        consumedBytes = 0;
        errorIndex = -1;
        error = "";

        if (
            (count < 0) ||
            (count > destination.Length)
        ) {
            errorIndex = 0;
            error = "channel name table count out of range";
            return false;
        }

        var position = 0;

        for (var index = 0; (index < count); ++index) {
            if (position >= source.Length) {
                errorIndex = index;
                error = $"entry {index}: channel name table truncated";
                return false;
            }

            var length = source[position];

            if (
                (length < 1) ||
                (length > AddonAbi.MaxChannelNameBytes)
            ) {
                errorIndex = index;
                error = $"entry {index}: name length {length} out of range [1, {AddonAbi.MaxChannelNameBytes}]";
                return false;
            }

            var nameStart = (position + 1);
            var nameEnd = (nameStart + length);

            if (nameEnd > source.Length) {
                errorIndex = index;
                error = $"entry {index}: channel name table truncated";
                return false;
            }

            var nameBytes = source[nameStart..nameEnd];

            if (!Utf8.IsValid(value: nameBytes)) {
                errorIndex = index;
                error = $"entry {index}: name is not valid UTF-8";
                return false;
            }

            var name = Encoding.UTF8.GetString(bytes: nameBytes);

            foreach (var nameChar in name) {
                if (char.IsControl(c: nameChar)) {
                    errorIndex = index;
                    error = $"entry {index}: name contains a control character";
                    return false;
                }
            }

            for (var priorIndex = 0; (priorIndex < index); ++priorIndex) {
                if (string.Equals(
                    a: destination[priorIndex].Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    errorIndex = index;
                    error = $"entry {index}: duplicate channel name '{name}' (also entry {priorIndex})";
                    return false;
                }
            }

            var resolved = resolver.TryResolve(
                name: name,
                ordinal: out var ordinal,
                shape: out var shape
            );

            destination[index] = new AddonChannelBinding(
                Name: name,
                Ordinal: (resolved
                ? ordinal
                : -1),
                Resolved: resolved,
                Shape: (resolved
                ? shape
                : default)
            );

            position = nameEnd;
        }

        consumedBytes = position;
        return true;
    }
}
