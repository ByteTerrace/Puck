using System.Text.Json.Serialization;

namespace Puck.GamingBricks.Forge;

/// <summary>A complete authored cartridge. Game content and behavior live in this document; compilers supply only hardware machinery.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeDocument {
    /// <summary>The recognized document family.</summary>
    public const string SchemaId = "puck.cartridge.v1";
    /// <summary>Gets the declared schema; absence is invalid.</summary>
    public required string Schema { get; init; }
    /// <summary>Gets the target: cgb or agb.</summary>
    public required string Target { get; init; }
    /// <summary>Gets the ASCII cartridge title, at most twelve characters.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the four-character ASCII game code.</summary>
    public required string GameCode { get; init; }
    /// <summary>Gets the background and object palettes. Color zero of an object palette is transparent.</summary>
    public required CartridgePalettes Palettes { get; init; }
    /// <summary>Gets the named 8 by 8 tiles, addressed by declaration index.</summary>
    public required CartridgeTile[] Tiles { get; init; }
    /// <summary>Gets the 32 by 32 background tile map in row-major order.</summary>
    public required int[] Map { get; init; }
    /// <summary>Gets the background palette index of each map cell, or null to place every cell on palette zero.</summary>
    public int[]? MapPalettes { get; init; }
    /// <summary>Gets the named unsigned byte state slots and their initial values.</summary>
    public required CartridgeVariable[] Variables { get; init; }
    /// <summary>Gets the named unsigned byte arrays, addressed at run time by a byte index.</summary>
    public required CartridgeArray[] Arrays { get; init; }
    /// <summary>Gets the named rectangles of tile indices a blit step paints into the background map.</summary>
    public required CartridgeScreen[] Screens { get; init; }
    /// <summary>Gets the battery-backed state, or null when the cartridge keeps nothing across power cycles.</summary>
    public CartridgeSave? Save { get; init; }
    /// <summary>Gets the scrolling backgrounds drawn behind the document's own, nearest first.</summary>
    public required CartridgeLayer[] Layers { get; init; }
    /// <summary>Gets the scroll changes applied part way down the picture, in ascending scanline order.</summary>
    public required CartridgeRasterRow[] Raster { get; init; }
    /// <summary>Gets the per-pixel drawing surface, or null when the cartridge draws only from tiles.</summary>
    public CartridgeBitmap? Bitmap { get; init; }
    /// <summary>Gets the rotating and scaling background, or null when the cartridge has none.</summary>
    public CartridgeAffine? Affine { get; init; }
    /// <summary>Gets the real-time clock's landing places, or null when the cartridge carries no clock.</summary>
    public CartridgeClock? Clock { get; init; }
    /// <summary>Gets the named music tracks a play step can start.</summary>
    public required CartridgeSound[] Sounds { get; init; }
    /// <summary>Gets rules evaluated once per game frame, in declaration order. Later rules see earlier writes.</summary>
    public required CartridgeRule[] Rules { get; init; }
    /// <summary>Gets sprite bindings sampled after the rules, in hardware priority order.</summary>
    public required CartridgeSprite[] Sprites { get; init; }
    /// <summary>Gets the panel drawn over the background, or null when the cartridge has none.</summary>
    public CartridgeWindow? Window { get; init; }
    /// <summary>Gets a value indicating whether sprites are 8 by 16 rather than 8 by 8.</summary>
    /// <remarks>In tall mode a sprite's tile index selects a pair, so the low bit of the index is ignored.</remarks>
    public bool TallSprites { get; init; }
    /// <summary>Gets the horizontal background scroll, in pixels modulo 256.</summary>
    public required CartridgeValue ScrollX { get; init; }
    /// <summary>Gets the vertical background scroll, in pixels modulo 256.</summary>
    public required CartridgeValue ScrollY { get; init; }
}

/// <summary>
/// A panel drawn over the background from its top-left corner down and right, out of its own tile map. Games use it
/// for a status area that must not scroll with the world behind it.
/// </summary>
/// <param name="Map">The panel's 32 by 32 tile map, row-major.</param>
/// <param name="MapPalettes">The panel's per-cell background palette indices, or null for palette zero.</param>
/// <param name="X">The panel's left column in pixels; the panel covers everything right of it.</param>
/// <param name="Y">The panel's top scanline; the panel covers everything below it.</param>
/// <param name="Visible">Zero hides the panel; any other value shows it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeWindow(
    int[] Map,
    int[]? MapPalettes,
    CartridgeValue X,
    CartridgeValue Y,
    CartridgeValue Visible);

/// <summary>
/// The cartridge's color. Each palette holds four colors on the humble machine and sixteen on the advanced one, and a
/// tile's pixel digits index within whichever palette its map cell or sprite selects.
/// </summary>
/// <param name="Background">The background palettes, in selection order.</param>
/// <param name="Object">The object palettes, in selection order. Color zero is transparent.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgePalettes(int[][] Background, int[][] Object);

/// <summary>A named tile whose eight rows each contain eight hexadecimal palette indices.</summary>
/// <param name="Name">The tile's author-facing name.</param>
/// <param name="Pixels">Eight strings of eight hexadecimal digits.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeTile(string Name, string[] Pixels);

/// <summary>A mutable unsigned byte of cartridge state.</summary>
/// <param name="Name">The case-sensitive state name.</param>
/// <param name="Initial">The initial value, 0 through 255.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeVariable(string Name, int Initial);

/// <summary>
/// A named run of mutable unsigned bytes. The initial contents fix the length. Elements are addressed by a byte index,
/// bounding an array at 256 entries; an index at or beyond the length reads zero and discards a write.
/// </summary>
/// <param name="Name">The case-sensitive array name.</param>
/// <param name="Initial">The initial contents, 1 to 256 bytes, each 0 through 255.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeArray(string Name, int[] Initial);

/// <summary>
/// A per-pixel drawing surface covering the whole screen, which is what a cartridge draws on when its picture is not
/// made of tiles at all.
/// </summary>
/// <remarks>
/// <para>
/// The surface is one byte per pixel, indexing the background palette bank read as one flat run of 256 colours — the
/// first declared palette's entries are 0 through 15, the second's 16 through 31 — so a plot carries an ordinary byte
/// of state as its colour. It replaces the tile background entirely: a document declaring one draws no
/// map, panel, layer or turning background, and sprites still draw over it.
/// </para>
/// <para>
/// Coordinates: <see cref="Width"/> by <see cref="Height"/> pixels with the origin at the top left. A plot outside
/// that is dropped rather than wrapping onto another row.
/// </para>
/// </remarks>
/// <param name="Clear">The palette entry the surface is filled with each frame, or null to leave it as drawn.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeBitmap(CartridgeValue? Clear = null) {
    /// <summary>The surface's height in pixels.</summary>
    public const int Height = 160;
    /// <summary>The surface's width in pixels.</summary>
    public const int Width = 240;
}

/// <summary>
/// A scrolling background beside the document's own, which is what lets a sky drift at a different rate from the
/// ground in front of it.
/// </summary>
/// <remarks>
/// Depth: <paramref name="Priority"/> runs 0 (nearest the viewer) through 3. Surfaces sharing a priority resolve by
/// hardware order, and these layers sit behind the document's own background and its panel, so a layer tying with one
/// of those draws beneath it.
/// </remarks>
/// <param name="Map">Exactly 1024 tile indices over a 32 by 32 grid, as the document's own map is.</param>
/// <param name="MapPalettes">One palette index per cell, or null to put every cell on palette zero.</param>
/// <param name="ScrollX">Pixels the layer is scrolled left.</param>
/// <param name="ScrollY">Pixels the layer is scrolled up.</param>
/// <param name="Priority">Draw order, 0 nearest through 3 furthest.</param>
/// <param name="Visible">Zero hides the layer; any other value draws it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeLayer(
    int[] Map,
    int[]? MapPalettes,
    CartridgeValue ScrollX,
    CartridgeValue ScrollY,
    int Priority,
    CartridgeValue Visible);

/// <summary>
/// A scroll change applied from one scanline down, which is how a picture gets a fixed panel over a scrolling world
/// or layers that drift at different speeds.
/// </summary>
/// <remarks>
/// Rows take effect at their own scanline and hold until the next row's, so the values authored here are the scroll
/// for the band BELOW the line rather than above it. The band above the first row uses the document's own scroll.
/// </remarks>
/// <param name="Line">The scanline the change takes effect on, 1 through 143.</param>
/// <param name="ScrollX">The horizontal scroll from that line down.</param>
/// <param name="ScrollY">The vertical scroll from that line down.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeRasterRow(int Line, CartridgeValue ScrollX, CartridgeValue ScrollY);

/// <summary>
/// A background that rotates and scales about a centre. Only the advanced machine has one; the humble machine's
/// backgrounds only scroll, so a document declaring this is refused for that target.
/// </summary>
/// <remarks>
/// <para>
/// The map is square and holds one byte per cell, so its length fixes its size: 256, 1024, 4096 or 16384 cells for a
/// 16, 32, 64 or 128 cell side. Unlike the scrolling background its cells carry no palette field.
/// </para>
/// <para>
/// Angle is a whole turn in 256 steps and <paramref name="Scale"/> is in sixteenths, so 16 is life size, 8 is double
/// size and 32 is half. Both are authored as ordinary byte values so a rule can drive them.
/// </para>
/// </remarks>
/// <param name="Map">The square map's tile indices, row-major.</param>
/// <param name="Angle">The rotation in 256ths of a turn.</param>
/// <param name="Scale">The zoom in sixteenths; 16 leaves the layer life size.</param>
/// <param name="CentreX">The screen column the layer turns about.</param>
/// <param name="CentreY">The screen row the layer turns about.</param>
/// <param name="Visible">Zero hides the layer; any other value shows it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeAffine(
    int[] Map,
    CartridgeValue Angle,
    CartridgeValue Scale,
    CartridgeValue CentreX,
    CartridgeValue CentreY,
    CartridgeValue Visible);

/// <summary>
/// The state slots a clock step fills from the cartridge's battery-backed real-time clock. Every field is optional;
/// a slot left unnamed is simply not written.
/// </summary>
/// <remarks>
/// The clock keeps counting while the machine is off, which is the point of it. Days wrap at 512 and the hardware
/// latches a carry past that; this surface reports the low byte, so a cartridge tracking longer spans must
/// accumulate its own count.
/// </remarks>
/// <param name="Seconds">The state slot receiving 0..59, or null.</param>
/// <param name="Minutes">The state slot receiving 0..59, or null.</param>
/// <param name="Hours">The state slot receiving 0..23, or null.</param>
/// <param name="Days">The state slot receiving the day counter's low byte, or null. cgb only, the advanced machine's
/// clock keeping a calendar date rather than a count of days.</param>
/// <param name="Day">The state slot receiving the day of the month, 1..31, or null. agb only.</param>
/// <param name="Month">The state slot receiving the month, 1..12, or null. agb only.</param>
/// <param name="Year">The state slot receiving the year within its century, 0..99, or null. agb only.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeClock(
    string? Seconds,
    string? Minutes,
    string? Hours,
    string? Days,
    string? Day = null,
    string? Month = null,
    string? Year = null);

/// <summary>
/// One voice's part of a music track: the patterns it plays and the order it plays them in, on one of the machine's
/// four sound channels.
/// </summary>
/// <remarks>
/// <para>
/// A track is as many parts as it has voices, so a melody over a bass over a counter-line is three parts rather than
/// one document pretending to be three. Every part of a track starts and stops together, and each loops on its own
/// length, which is what lets a four-row bass sit under a thirty-two-row melody without either being padded out.
/// </para>
/// <para>
/// Nothing reserves a voice for music or for effects. What a track's parts occupy, its cartridge's effects may not:
/// validation refuses an effect on a voice any track claims, so an effect can never cut a line of the music off.
/// </para>
/// </remarks>
/// <param name="Voice">The channel this part plays on: pulse1, pulse2, wave or noise.</param>
/// <param name="Part">The part's patterns, order and tempo.</param>
/// <param name="Waveform">
/// For the wave voice: thirty-two four-bit samples describing one cycle of its waveform. Required for that voice and
/// refused for any other.
/// </param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeMusicVoice(
    string Voice,
    Puck.Assets.Documents.AudioDocument Part,
    int[]? Waveform = null);

/// <summary>
/// A named sound a play step starts: exactly one of a looping music track or a one-shot effect. A track occupies as
/// many voices as it has parts; an effect holds one voice no track claims, so an effect never interrupts the music
/// playing under it.
/// </summary>
/// <param name="Name">The case-sensitive sound name.</param>
/// <param name="Music">The track's voice parts, or null for an effect.</param>
/// <param name="Effect">The one-shot's voice and rows, or null for music.</param>
/// <param name="Frames">For an effect: how many frames each row holds, 1 through 255.</param>
/// <param name="Sample">
/// For a recorded one-shot: signed eight-bit samples, played on the advanced machine's digital sound path. The
/// humble machine has no digital sound hardware, so a sample is refused there.
/// </param>
/// <param name="Waveform">
/// For an effect on the wave voice: thirty-two four-bit samples describing one cycle of its waveform. Required for
/// that voice and refused for any other.
/// </param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeSound(
    string Name,
    CartridgeMusicVoice[]? Music = null,
    Puck.Assets.Documents.AudioEffectDocument? Effect = null,
    int? Frames = null,
    int[]? Sample = null,
    int[]? Waveform = null);

/// <summary>
/// The state a save step writes to battery-backed memory and a load step restores. The payload is the named variables
/// followed by the named arrays, in declaration order.
/// </summary>
/// <remarks>
/// A load whose stored block fails its magic, version or checksum check leaves the declared state at its authored
/// initial values, so a fresh cartridge and a corrupted one behave alike. Raising <paramref name="Version"/> abandons
/// an existing block the same way.
/// </remarks>
/// <param name="Version">The payload's layout version, 0 through 255.</param>
/// <param name="Variables">The persisted state slots, in payload order.</param>
/// <param name="Arrays">The persisted arrays, in payload order after the variables.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeSave(int Version, string[] Variables, string[] Arrays);

/// <summary>
/// A named rectangle of background tile indices, painted by a blit step. Rows are stored top to bottom, each of
/// <paramref name="Width"/> indices, so <paramref name="Tiles"/> holds width times height entries.
/// </summary>
/// <param name="Name">The case-sensitive screen name.</param>
/// <param name="Width">The width in tiles, 1 through 32.</param>
/// <param name="Tiles">The tile indices in row-major order.</param>
/// <param name="Palettes">The background palette index of each tile, or null to paint the rectangle on palette zero.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeScreen(string Name, int Width, int[] Tiles, int[]? Palettes = null);

/// <summary>
/// A readable byte: exactly one of a literal, a named state slot, or an element of a named array. An array read also
/// carries the <see cref="Index"/> expression evaluated to select the element.
/// </summary>
/// <param name="Constant">A literal in 0 through 255, or null.</param>
/// <param name="Variable">A declared state name, or null.</param>
/// <param name="Array">A declared array name, or null.</param>
/// <param name="Index">The element selector; required with <paramref name="Array"/> and refused without it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeValue(int? Constant = null, string? Variable = null, string? Array = null, CartridgeValue? Index = null);

/// <summary>A writable byte: exactly one of a named state slot or an element of a named array.</summary>
/// <param name="Variable">A declared state name, or null.</param>
/// <param name="Array">A declared array name, or null.</param>
/// <param name="Index">The element selector; required with <paramref name="Array"/> and refused without it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeTarget(string? Variable = null, string? Array = null, CartridgeValue? Index = null);

/// <summary>A rule's conjunction. A key condition tests held/pressed/released input; a compare condition compares two unsigned bytes.</summary>
/// <param name="Kind">key or compare.</param>
/// <param name="Key">For key: a, b, start, select, up, down, left or right.</param>
/// <param name="Mode">For key: held, pressed or released.</param>
/// <param name="Left">For compare: the left operand.</param>
/// <param name="Comparison">For compare: eq, ne, lt, le, gt or ge.</param>
/// <param name="Right">For compare: the right operand.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeCondition(string Kind, string? Key = null, string? Mode = null, CartridgeValue? Left = null, string? Comparison = null, CartridgeValue? Right = null);

/// <summary>
/// One step of a rule body: a state write, a branch, a counted loop, or a loop exit. <paramref name="Kind"/> selects
/// which of the remaining fields apply; validation refuses any field belonging to another kind.
/// </summary>
/// <param name="Kind">set, if, repeat, break, map, blit, save, load, play, stop, clock or fade.</param>
/// <param name="Target">For set: the destination slot or array element.</param>
/// <param name="Operation">For set: set, add, subtract, and, or, xor, mul, div, mod, shl or shr.</param>
/// <param name="Value">For set: the source operand, evaluated when the step executes.</param>
/// <param name="When">For if: all conditions must hold; empty always holds.</param>
/// <param name="Then">For if: the steps taken when every condition holds.</param>
/// <param name="Else">For if: the steps taken otherwise, or null for none.</param>
/// <param name="Count">For repeat: the literal iteration count, 1 through 255.</param>
/// <param name="Index">For repeat: a declared variable the loop writes with the iteration number.</param>
/// <param name="Body">For repeat: the steps of one iteration.</param>
/// <param name="Row">For map and blit: the destination map row.</param>
/// <param name="Column">For map and blit: the destination map column.</param>
/// <param name="Tile">For map: the tile index to write.</param>
/// <param name="Palette">For map: the background palette the cell is drawn through, or null for palette zero.</param>
/// <param name="Screen">For blit: the declared screen to paint.</param>
/// <param name="Sound">For play: the declared music track to start.</param>
/// <param name="Rate">For play of a recorded sound: the playback rate in sixty-fourths of the recording's own, so 64
/// plays it as recorded and 128 an octave up. Absent plays it as recorded.</param>
/// <param name="Amount">For fade: how far toward the target, 0 through 16.</param>
/// <param name="Toward">For fade: black or white.</param>
/// <param name="Colour">For plot: the palette entry the pixel is set to.</param>
/// <param name="Surface">For blend: the surface made translucent — background, panel, affine, sprites or backdrop.</param>
/// <param name="Weight">For blend: how much of the translucent surface shows, 0 through 16; everything below it supplies the rest.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeStatement(
    string Kind,
    CartridgeTarget? Target = null,
    string? Operation = null,
    CartridgeValue? Value = null,
    CartridgeCondition[]? When = null,
    CartridgeStatement[]? Then = null,
    CartridgeStatement[]? Else = null,
    int? Count = null,
    string? Index = null,
    CartridgeStatement[]? Body = null,
    CartridgeValue? Row = null,
    CartridgeValue? Column = null,
    CartridgeValue? Tile = null,
    CartridgeValue? Palette = null,
    string? Screen = null,
    string? Sound = null,
    CartridgeValue? Rate = null,
    CartridgeValue? Amount = null,
    string? Toward = null,
    string? Surface = null,
    CartridgeValue? Weight = null,
    CartridgeValue? Colour = null);

/// <summary>An ordered, conditional group of state changes.</summary>
/// <param name="Name">The diagnostic name.</param>
/// <param name="When">All conditions must hold; empty means every frame.</param>
/// <param name="Body">Steps executed in declaration order.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeRule(string Name, CartridgeCondition[] When, CartridgeStatement[] Body);

/// <summary>A native 8 by 8 sprite bound to cartridge state. Coordinates are screen pixels; zero visibility hides it.</summary>
/// <param name="Name">The sprite's author-facing name.</param>
/// <param name="Tile">Tile index; values outside the authored tile bank hide the sprite.</param>
/// <param name="X">Screen X coordinate.</param>
/// <param name="Y">Screen Y coordinate.</param>
/// <param name="Visible">Zero hides the sprite; any other value shows it.</param>
/// <param name="Palette">The object palette index, or null for palette zero.</param>
/// <param name="FlipX">Non-zero mirrors the sprite horizontally, or null to leave it unmirrored.</param>
/// <param name="FlipY">Non-zero mirrors the sprite vertically, or null to leave it unmirrored.</param>
/// <param name="BehindBackground">Non-zero draws the sprite behind the background's non-zero pixels.</param>
/// <param name="Turn">
/// The sprite's rotation in 256ths of a turn, or null to leave it upright. Only the advanced machine turns objects;
/// the humble machine can only mirror them, so this is refused for that target.
/// </param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CartridgeSprite(
    string Name,
    CartridgeValue Tile,
    CartridgeValue X,
    CartridgeValue Y,
    CartridgeValue Visible,
    CartridgeValue? Palette = null,
    CartridgeValue? FlipX = null,
    CartridgeValue? FlipY = null,
    CartridgeValue? BehindBackground = null,
    CartridgeValue? Turn = null);
