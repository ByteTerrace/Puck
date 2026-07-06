namespace Puck.Assets;

/// <summary>
/// Derives a deterministic, pronounceable petname from a content hash — three capitalized words joined by
/// hyphens (e.g. <c>"Willow-Lantern-Nine"</c>), for narrating a saved object without printing raw hex. The same
/// hash always yields the same name; the word lists are fixed and trademark-clean (evocative nature/object words
/// plus small number words), so the mapping never drifts across builds or machines.
/// </summary>
public static class ContentPetname {
    private static readonly string[] FirstWords = [
        "Willow", "Cedar", "Maple", "Birch", "Aspen", "Rowan", "Alder", "Hazel",
        "Sable", "Amber", "Coral", "Ember", "Frost", "Ash", "Moss", "Fern",
        "Cinder", "Dune", "Marsh", "Reed", "Slate", "Flint", "Quartz", "Basalt",
        "Cobalt", "Copper", "Bronze", "Silver", "Golden", "Violet", "Indigo", "Crimson",
        "Meadow", "Harbor", "Ridge", "Valley", "Brook", "River", "Delta", "Canyon",
        "Boulder", "Pebble", "Granite", "Marble", "Cypress", "Cascade", "Juniper", "Sequoia",
        "Thistle", "Clover", "Heather", "Bramble", "Lichen", "Fungus", "Sprout", "Blossom",
        "Comet", "Nebula", "Meteor", "Aurora", "Zenith", "Horizon", "Tundra", "Glacier",
    ];

    private static readonly string[] SecondWords = [
        "Lantern", "Compass", "Anchor", "Beacon", "Kettle", "Ladder", "Basket", "Barrel",
        "Satchel", "Locket", "Whistle", "Buckle", "Bobbin", "Spindle", "Chisel", "Mallet",
        "Trowel", "Bucket", "Pouch", "Bundle", "Pulley", "Hinge", "Rivet", "Bracket",
        "Cradle", "Hammock", "Wagon", "Sled", "Skiff", "Canoe", "Paddle", "Rudder",
        "Bellow", "Ember", "Kiln", "Forge", "Anvil", "Crucible", "Prism", "Mirror",
        "Feather", "Pinwheel", "Kite", "Balloon", "Marble", "Domino", "Ribbon", "Tassel",
        "Thimble", "Needle", "Loom", "Spool", "Quill", "Parchment", "Scroll", "Envelope",
        "Nugget", "Pebblestone", "Shard", "Crystal", "Ingot", "Medallion", "Trinket", "Charm",
    ];

    private static readonly string[] NumberWords = [
        "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight",
        "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen",
    ];

    /// <summary>Derives the petname for a content hash.</summary>
    /// <param name="hashHex">The content hash, as <c>sha256/&lt;hex64&gt;</c> or a bare hex string (any length ≥ 6
    /// hex characters; only the leading bytes are used).</param>
    /// <returns>Three capitalized words joined by hyphens, e.g. <c>"Willow-Lantern-Nine"</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="hashHex"/> is <see langword="null"/>, empty, or too
    /// short to derive three indices from.</exception>
    public static string From(string hashHex) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: hashHex);

        var hex = (hashHex.StartsWith(value: "sha256/", comparisonType: StringComparison.Ordinal) ? hashHex["sha256/".Length..] : hashHex);

        if (hex.Length < 6) {
            throw new ArgumentException(message: $"'{hashHex}' is too short to derive a petname from (need at least 6 hex characters).", paramName: nameof(hashHex));
        }

        var firstIndex = (ParseByte(hex: hex, offset: 0) % FirstWords.Length);
        var secondIndex = (ParseByte(hex: hex, offset: 2) % SecondWords.Length);
        var numberIndex = (ParseByte(hex: hex, offset: 4) % NumberWords.Length);

        return $"{FirstWords[firstIndex]}-{SecondWords[secondIndex]}-{NumberWords[numberIndex]}";
    }

    // Parses a two-hex-character byte at the given character offset (offsets are always even, from hex64 input).
    private static int ParseByte(string hex, int offset) =>
        byte.Parse(s: hex.AsSpan(start: offset, length: 2), style: System.Globalization.NumberStyles.HexNumber);
}
