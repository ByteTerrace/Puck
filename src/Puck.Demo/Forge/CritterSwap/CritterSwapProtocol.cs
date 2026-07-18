using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>One critter species as DATA: a name (framework-font characters only), a default level, a background palette,
/// and an 8×8 "face" tile authored in the forge's ASCII-row style ('#' = colour index 3 on the transparent index-0
/// field, exactly like <see cref="TextModule"/>'s glyphs). Both the SM83 game and the C# self-verify read the SAME
/// table, so the cart and its oracle can never drift.</summary>
/// <param name="Name">The species name (space, 0-9, A-Z, '-', '.', '&gt;' only).</param>
/// <param name="Level">The default level as a packed-BCD byte (two decimal digits).</param>
/// <param name="Palette">The species' four-shade background palette (index 0 is the field, index 3 the face ink).</param>
/// <param name="Face">The eight-row face pattern.</param>
internal sealed record CritterSpecies(string Name, byte Level, HgbImage.Rgb[] Palette, string[] Face);

/// <summary>
/// The CRITTER-SWAP cartridge's shared constants — a whimsical two-cart trading toy whose SRAM holds ONE critter
/// (species + level) and whose whole point is the link cable: press START to offer a trade, and when the partner cart
/// offers too, the two critters cross the wire (each side commits the OTHER's critter to its own battery save) and the
/// new arrival appears. State ids, the game-owned work-RAM layout, the save layout, and the species table all live here
/// as DATA the self-verify battery reads alongside the ROM. The link handshake itself is the reusable
/// <see cref="LinkProtocolModule"/> (magic <see cref="LinkProtocolModule.MagicByte"/>, a 4-byte checksummed block).
/// </summary>
internal static class CritterSwapProtocol {
    // State ids.
    /// <summary>The title/roster screen: the current critter (face, name, level) and PRESS START TO TRADE.</summary>
    public const byte StateTitle = 0;
    /// <summary>The trading screen: the link protocol runs; on success it commits, on timeout it narrates NO LINK.</summary>
    public const byte StateTrade = 1;
    /// <summary>The arrival screen: the newly-received critter, then A returns to the title (now showing it).</summary>
    public const byte StateArrival = 2;
    /// <summary>The no-link screen: "NO LINK" when the trade found no partner; A/B returns to the title.</summary>
    public const byte StateNoLink = 3;

    // Battery-save payload (the framework save mirror at 0xC060): the one held critter.
    /// <summary>The held critter's species id byte (the save mirror's first payload byte).</summary>
    public const ushort SaveSpecies = FrameworkMemoryMap.SaveMirror;
    /// <summary>The held critter's level byte (packed BCD; the save mirror's second payload byte).</summary>
    public const ushort SaveLevel = (ushort)(FrameworkMemoryMap.SaveMirror + 1);
    /// <summary>The save payload's byte count (species + level).</summary>
    public const int SavePayloadByteCount = 2;
    /// <summary>The save block's version byte (bump to orphan old saves on a layout change).</summary>
    public const byte SaveVersion = 1;

    // Game-owned work RAM (0xC200+): the link-protocol scratch + the two exchange blocks. Nothing else is needed — the
    // title/arrival screens render straight from the save mirror.
    private const ushort GameRam = FrameworkMemoryMap.GameRam;
    /// <summary>The link protocol's work-RAM layout (all in the game-owned page).</summary>
    public static readonly LinkProtocolRam LinkRam = new(
        Phase: GameRam,
        Role: (ushort)(GameRam + 1),
        Backoff: (ushort)(GameRam + 2),
        Attempts: (ushort)(GameRam + 3),
        ByteIndex: (ushort)(GameRam + 4),
        Stage: (ushort)(GameRam + 5),
        ReadScratch: (ushort)(GameRam + 6),
        OutBlock: (ushort)(GameRam + 7),
        InBlock: (ushort)((GameRam + 7) + LinkProtocolModule.BlockByteCount)
    );

    // Layout.
    /// <summary>The A button bit of the active-high input bytes.</summary>
    public const byte ButtonA = 0x10;
    /// <summary>The B button bit.</summary>
    public const byte ButtonB = 0x20;
    /// <summary>The START button bit.</summary>
    public const byte ButtonStart = 0x80;

    /// <summary>The tile id of the first species face — the font lands at bank base 0 (40 glyphs), so faces follow it.</summary>
    public const byte FaceTileBase = TextModule.GlyphCount;

    /// <summary>The species table: eight small, legible critters. Indices are the on-wire species ids.</summary>
    public static readonly CritterSpecies[] Species = [
        new CritterSpecies(Name: "PUCKLING", Level: 0x05, Palette: Field(r: 40, g: 24, b: 64), Face: [
            "..####..", ".#....#.", "#.#..#.#", "#......#", "#.#..#.#", "#..##..#", ".#....#.", "..####..",
        ]),
        new CritterSpecies(Name: "BLIPMOUSE", Level: 0x07, Palette: Field(r: 20, g: 40, b: 72), Face: [
            "#......#", "##....##", "#.#..#.#", "#..##..#", "#.####.#", "#.#..#.#", ".#....#.", "..####..",
        ]),
        new CritterSpecies(Name: "GLOWNEWT", Level: 0x09, Palette: Field(r: 16, g: 56, b: 32), Face: [
            "...##...", "..####..", ".#.##.#.", "#..##..#", "#.####.#", "#......#", ".#.##.#.", "..#..#..",
        ]),
        new CritterSpecies(Name: "CRTGECKO", Level: 0x08, Palette: Field(r: 12, g: 48, b: 56), Face: [
            "########", "#..##..#", "#.#..#.#", "#.#..#.#", "#..##..#", "#.####.#", "#......#", "########",
        ]),
        new CritterSpecies(Name: "BITWYRM", Level: 0x0C, Palette: Field(r: 56, g: 20, b: 20), Face: [
            ".#....#.", "#.#..#.#", "#......#", "#.####.#", "#.#..#.#", ".#....#.", "..#..#..", "...##...",
        ]),
        new CritterSpecies(Name: "FUZZBYTE", Level: 0x06, Palette: Field(r: 48, g: 40, b: 12), Face: [
            "#.#..#.#", ".######.", "#..##..#", "#.#..#.#", "#......#", "#.####.#", ".#....#.", "#.#..#.#",
        ]),
        new CritterSpecies(Name: "HEXHOPPER", Level: 0x0B, Palette: Field(r: 40, g: 16, b: 56), Face: [
            "...##...", "..#..#..", ".#.##.#.", "#.####.#", "#.#..#.#", ".#....#.", ".#.##.#.", "#.#..#.#",
        ]),
        new CritterSpecies(Name: "VOLTVOLE", Level: 0x0A, Palette: Field(r: 56, g: 48, b: 16), Face: [
            "#......#", ".#....#.", "..#..#..", "#.####.#", "#.#..#.#", "#..##..#", ".#....#.", "..####..",
        ]),
        new CritterSpecies(Name: "MOSSMOTH", Level: 0x04, Palette: Field(r: 24, g: 52, b: 40), Face: [
            "#......#", "##.##.##", "#.####.#", "#..##..#", "#.####.#", "#.#..#.#", ".######.", "#......#",
        ]),
    ];

    /// <summary>The number of species (the modulo the seat-default derivation wraps).</summary>
    public static int SpeciesCount => Species.Length;

    /// <summary>Builds the 16-byte 2bpp face-tile block (one tile per species) appended after the framework font, so a
    /// species' face tile id is <see cref="FaceTileBase"/> + its species id.</summary>
    /// <returns>The concatenated face tile bytes.</returns>
    public static byte[] BuildFaceTiles() {
        var tiles = new byte[(Species.Length * 16)];

        for (var index = 0; (index < Species.Length); index++) {
            EncodeFace(rows: Species[index].Face).CopyTo(array: tiles, index: (index * 16));
        }

        return tiles;
    }

    /// <summary>The additive checksum the exchange block carries (magic + species + level, low byte) — the SAME sum the
    /// SM83 game emits into the block's last byte and the <see cref="LinkProtocolModule"/> validates on receipt.</summary>
    /// <param name="species">The species id byte.</param>
    /// <param name="level">The level byte.</param>
    /// <returns>The checksum byte.</returns>
    public static byte BlockChecksum(byte species, byte level) =>
        (byte)(((LinkProtocolModule.MagicByte + species) + level) & 0xFF);

    /// <summary>The seat-default critter species for a save slot — a cabinet's OWN starting critter, distinct per slot so
    /// two linked cabinets hold different critters (the swap is then visible). Slot -1 (no per-cabinet slot) and slot 0
    /// keep species 0; each higher slot steps through the table.</summary>
    /// <param name="slot">The cabinet's save slot (see the demo's per-(console, type) save seam).</param>
    /// <returns>The default species id.</returns>
    public static byte DefaultSpeciesForSlot(int slot) =>
        (byte)(((slot > 0) ? slot : 0) % SpeciesCount);

    // A species field palette: a deep tinted field (index 0), two ramp shades, and pale ink (index 3) for the face + text.
    private static HgbImage.Rgb[] Field(byte r, byte g, byte b) => [
        new HgbImage.Rgb(R: r, G: g, B: b),
        new HgbImage.Rgb(R: (byte)((r + 96) / 2), G: (byte)((g + 96) / 2), B: (byte)((b + 96) / 2)),
        new HgbImage.Rgb(R: (byte)((r + 300) / 3), G: (byte)((g + 320) / 3), B: (byte)((b + 340) / 3)),
        new HgbImage.Rgb(R: 232, G: 240, B: 250),
    ];

    // Encodes an 8-row ASCII face ('#' = colour index 3) to the 16-byte 2bpp tile form (the TextModule glyph convention).
    private static byte[] EncodeFace(string[] rows) {
        var indices = new byte[64];

        for (var row = 0; (row < 8); row++) {
            var line = rows[row];

            for (var column = 0; (column < 8); column++) {
                indices[((row * 8) + column)] = (byte)(((column < line.Length) && (line[column] == '#')) ? 3 : 0);
            }
        }

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }
}
