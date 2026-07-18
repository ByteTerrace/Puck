using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The shared constants and pure math of the 128-bit cartridge win condition — the seam a GamingBrick game converges on.
/// The gate lives at the TOP of the cartridge's external (battery) RAM: the last <see cref="RegionByteCount"/> bytes of
/// the highest possible SRAM address. On an MBC5 128&#160;KiB cartridge that is physical offset <c>0x1FFF0</c>–<c>0x1FFFF</c>
/// (RAM bank <c>0x0F</c> offset <c>0x1FF0</c>, seen in the CPU window at <c>0xBFF0</c>–<c>0xBFFF</c> when that bank is paged).
/// The host reads it bank-independently, so the check never fights the running game over which bank is selected.
/// </summary>
public static class VictoryGate {
    /// <summary>The gate width: 128 bits = 16 bytes.</summary>
    public const int RegionByteCount = 16;

    /// <summary>Parses a GUID string into its 16 bytes in CANONICAL (big-endian, textual) order — the layout that appears
    /// at <c>0xBFF0</c>…<c>0xBFFF</c> so the printed GUID reads left-to-right up the address range. This is deliberately
    /// NOT <see cref="Guid.ToByteArray()"/> (which byte-swaps the first three fields); the memory image must match what a
    /// human writes.</summary>
    /// <param name="text">The GUID string (any format <see cref="Guid.TryParse(string, out Guid)"/> accepts).</param>
    /// <param name="destination">A span of exactly <see cref="RegionByteCount"/> bytes to receive the layout.</param>
    /// <returns>Whether the text parsed as a GUID.</returns>
    public static bool TryParseGuidBytes(string? text, Span<byte> destination) {
        if ((destination.Length != RegionByteCount) || (text is null) || !Guid.TryParse(input: text, result: out var guid)) {
            return false;
        }

        // "N" is the 32-hex-digit canonical form (no braces/dashes) in RFC field order — big-endian, exactly the byte
        // order we lay into memory. Decoding the text (not ToByteArray) keeps the layout endianness-proof.
        var hex = guid.ToString(format: "N");

        for (var index = 0; (index < RegionByteCount); index++) {
            destination[index] = (byte)((HexValue(c: hex[(index * 2)]) << 4) | HexValue(c: hex[((index * 2) + 1)]));
        }

        return true;
    }

    /// <summary>XORs <paramref name="operand"/> into <paramref name="accumulator"/> byte-for-byte — the meta gate's
    /// combine: the room wins when the XOR of every participating cabinet's region equals the meta target.</summary>
    /// <param name="accumulator">The running combine (mutated in place); <see cref="RegionByteCount"/> bytes.</param>
    /// <param name="operand">One cabinet's region; <see cref="RegionByteCount"/> bytes.</param>
    public static void Xor(Span<byte> accumulator, ReadOnlySpan<byte> operand) {
        for (var index = 0; (index < RegionByteCount); index++) {
            accumulator[index] ^= operand[index];
        }
    }

    /// <summary>Whether a region equals a target byte-for-byte — the solo gate's compare.</summary>
    /// <param name="region">The cartridge's top-16 SRAM bytes.</param>
    /// <param name="target">The gate constant's 16 bytes.</param>
    /// <returns>Whether they are identical.</returns>
    public static bool RegionEquals(ReadOnlySpan<byte> region, ReadOnlySpan<byte> target) =>
        region.SequenceEqual(other: target);

    private static int HexValue(char c) =>
        ((c <= '9') ? (c - '0') : ((char.ToLowerInvariant(c: c) - 'a') + 10));
}

/// <summary>
/// Two named, memorable 128-bit gate constants, expressed as the minimal and maximal VALID v4 GUIDs — every free bit at
/// 0 vs 1, with only the six structural bits (the version nibble <c>0100</c> in byte 6 and the variant bits <c>10</c> in
/// byte 8) held so each stays a legal v4. Their Hamming distance is 122 (the six fixed bits are shared). A document names
/// its own gate values; these are the defaults the shipped example and the POST proof use.
/// </summary>
public static class VictoryConstants {
    /// <summary>The "zero" v4 GUID — the suggested SOLO gate constant. <c>00000000-0000-4000-8000-000000000000</c>.</summary>
    public const string ZeroV4Guid = "00000000-0000-4000-8000-000000000000";

    /// <summary>The "one" v4 GUID — the suggested META gate constant. <c>ffffffff-ffff-4fff-bfff-ffffffffffff</c>.</summary>
    public const string OneV4Guid = "ffffffff-ffff-4fff-bfff-ffffffffffff";
}

/// <summary>
/// A gaming-brick viewport's 128-bit WIN condition: the host reads the top <see cref="VictoryGate.RegionByteCount"/> bytes
/// of the cartridge's external RAM (the highest SRAM address) after each stepped frame and fires once the game has driven
/// them onto the gate constant. Two shapes, each gated by its own constant:
/// <list type="bullet">
///   <item><b>solo</b> (<see cref="Mode"/> <c>"solo"</c>): the cabinet wins alone when its region equals <see cref="Target"/>.</item>
///   <item><b>meta</b> (<see cref="Mode"/> <c>"meta"</c>): the cabinet converges its region on its private <see cref="Share"/>; the
///   ROOM wins when the XOR of every meta cabinet's region (in the same <see cref="Group"/>) equals <see cref="Target"/>. Chosen so
///   no single cabinet's bytes reveal the target — cooperation is structural, not enforced.</item>
/// </list>
/// Pure data; the host owns the polling and the win. A deterministic READ of emulated state, never a write into it.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrickVictoryCondition {
    /// <summary>The win shapes a condition may name.</summary>
    public static readonly IReadOnlyList<string> SupportedModes = ["solo", "meta"];

    /// <summary>The win shape: <c>solo</c> (the cabinet's region converges directly on <see cref="Target"/>) or <c>meta</c>
    /// (the cabinet converges on <see cref="Share"/>; the room's XOR converges on <see cref="Target"/>).</summary>
    public string Mode { get; init; } = "solo";
    /// <summary>The 128-bit gate constant, as a GUID string. For <c>solo</c> the region must equal this; for <c>meta</c> the
    /// XOR across the group must equal this.</summary>
    public string Target { get; init; } = "";
    /// <summary>META only: this cabinet's private 128-bit value (a GUID string) — the value its game converges on. Absent for
    /// <c>solo</c>. The group's shares are authored so their XOR equals <see cref="Target"/>.</summary>
    public string? Share { get; init; }
    /// <summary>META only: an optional group id linking the cabinets whose regions XOR together. Cabinets with the same
    /// group (null groups together) form one meta set. Absent for <c>solo</c>.</summary>
    public string? Group { get; init; }
    /// <summary>An optional label for the win log line (e.g. <c>"all three aligned"</c>).</summary>
    public string? Label { get; init; }

    /// <summary>Whether this is a meta (cooperative XOR) condition.</summary>
    [JsonIgnore]
    public bool IsMeta =>
        string.Equals(a: Mode, b: "meta", comparisonType: StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses <see cref="Target"/> into its 16 memory bytes (validated documents always succeed).</summary>
    /// <param name="destination">A <see cref="VictoryGate.RegionByteCount"/>-byte span.</param>
    /// <returns>Whether the target parsed.</returns>
    public bool TryParseTarget(Span<byte> destination) =>
        VictoryGate.TryParseGuidBytes(text: Target, destination: destination);

    /// <summary>Parses <see cref="Share"/> into its 16 memory bytes.</summary>
    /// <param name="destination">A <see cref="VictoryGate.RegionByteCount"/>-byte span.</param>
    /// <returns>Whether a share is present and parsed.</returns>
    public bool TryParseShare(Span<byte> destination) =>
        ((Share is not null) && VictoryGate.TryParseGuidBytes(text: Share, destination: destination));

    internal void Validate(string path, ValidationErrors errors) {
        if (!SupportedModes.Contains(value: Mode, comparer: StringComparer.OrdinalIgnoreCase)) {
            errors.Add(path: $"{path}.mode", message: $"mode '{Mode}' is not one of: {string.Join(separator: ", ", values: SupportedModes)}");
        }

        if (!Guid.TryParse(input: Target, result: out _)) {
            errors.Add(path: $"{path}.target", message: $"target '{Target}' must be a GUID (the 128-bit gate constant, e.g. a v4 GUID)");
        }

        if (IsMeta) {
            if (Share is null) {
                errors.Add(path: $"{path}.share", message: "a meta victory needs a share (this cabinet's converge-toward 128 bits, as a GUID)");
            } else if (!Guid.TryParse(input: Share, result: out _)) {
                errors.Add(path: $"{path}.share", message: $"share '{Share}' must be a GUID");
            }
        } else {
            if (Share is not null) {
                errors.Add(path: $"{path}.share", message: "share is only valid for a meta victory; a solo victory converges its region directly on target");
            }

            if (Group is not null) {
                errors.Add(path: $"{path}.group", message: "group is only valid for a meta victory");
            }
        }
    }
}
