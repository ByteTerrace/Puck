namespace Puck.Forge.Framework;

/// <summary>A text overlay on a screen: full tile-cell swaps into the framework font at a fixed map cell. Overlay
/// cells always render in background palette 0 — on an art-backed screen the linker zeroes their attributes — so no
/// baked art can ever ship a screen the player cannot read their way off of (the menu-text contract).</summary>
/// <param name="Row">The map row (0..31).</param>
/// <param name="Column">The map column of the first character.</param>
/// <param name="Text">The text (the framework font's character set: space, 0-9, A-Z, '&gt;', '-', '.').</param>
internal sealed record ScreenText(int Row, int Column, string Text);

/// <summary>One step of a scripted input sequence (an attract script): hold <paramref name="Buttons"/> for
/// <paramref name="Frames"/> frames.</summary>
/// <param name="Buttons">The active-high button bits (<see cref="InputModule.ButtonLeft"/> etc.; never 0xFF — the
/// script terminator).</param>
/// <param name="Frames">How many frames the step holds (1..255).</param>
internal readonly record struct InputScriptStep(byte Buttons, byte Frames);

/// <summary>A default score-table entry: three initials and a decimal score (encoded to the framework's high-score
/// payload shape — initials as font tile ids, then the score as 3 packed-BCD bytes, most significant first).</summary>
/// <param name="Initials">Exactly three characters from the framework font.</param>
/// <param name="Score">The score (0..999999).</param>
internal sealed record ScoreTableEntry(string Initials, int Score);

/// <summary>A fixed-stride record table: the linked block plus the stride/count facts SM83 indexing needs.</summary>
/// <param name="Table">The linked block.</param>
/// <param name="Stride">The bytes per record.</param>
/// <param name="Count">The record count.</param>
internal readonly record struct RomRecords(RomTable Table, int Stride, int Count);

/// <summary>A linked screen: the 1024-byte map block (overlays applied) and, when the screen is art-backed on CGB,
/// the matching attribute block (overlay cells zeroed back to palette 0).</summary>
/// <param name="Map">The map block.</param>
/// <param name="Attributes">The attribute block, or <see langword="null"/> (literal screen, or a DMG bake).</param>
internal sealed record LinkedScreen(RomTable Map, RomTable? Attributes);

/// <summary>
/// The declarative game manifest — a game's identity as DATA. A game declares its tile segments, palettes, screens
/// (literal cells or a baked <c>PBAK</c> background, both with text overlays), rule tables, strings, input scripts,
/// and sprite art up front; <see cref="Link"/> then drives <see cref="AssetLinker"/> and the data window once,
/// returning a <see cref="LinkedManifest"/> the game's SM83 emission references by table address. Allocation follows
/// DECLARATION ORDER (tiles into the bank, palettes into the slots), so <see cref="FontTileBase"/> is known the
/// moment the font is declared — before the framework is even constructed. Shared by every framework game: rules,
/// layouts, and decks are manifest tables, never copies of another game's plumbing.
/// </summary>
internal sealed class GameManifest {
    private const int MapByteCount = 0x400;
    private const int MapSide = 32;

    private readonly List<Declaration> m_allocations = [];
    private readonly List<(string Name, byte[] Bytes)> m_tables = [];
    private readonly List<(string Name, int Stride, int Count, byte[] Bytes)> m_records = [];
    private readonly List<(string Name, string Text)> m_texts = [];
    private readonly List<(string Name, byte[] Bytes)> m_scripts = [];
    private readonly List<(string Name, byte[] Cells, IReadOnlyList<ScreenText> Overlays)> m_literalScreens = [];
    private readonly HashSet<string> m_names = [];
    private int m_declaredTileCount;
    private int m_fontTileBase = -1;

    /// <summary>The tile id of the framework font's first glyph — computed from the declarations before it, so a game
    /// constructs its <see cref="GameFramework"/> with this value. Throws until <see cref="DefineFontTiles"/> ran.</summary>
    public byte FontTileBase =>
        ((m_fontTileBase >= 0) ? (byte)m_fontTileBase : throw new InvalidOperationException(message: "DefineFontTiles has not been called."));

    /// <summary>Declares a raw tile segment (game art the manifest does not interpret).</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="tiles2bpp">The segment's 2bpp bytes (16 per tile).</param>
    public void DefineTiles(string name, byte[] tiles2bpp) {
        ArgumentNullException.ThrowIfNull(tiles2bpp);
        ClaimName(name: name);
        m_allocations.Add(item: new TileDeclaration(Name: name, Tiles: tiles2bpp));
        m_declaredTileCount += (tiles2bpp.Length / 16);
    }

    /// <summary>Declares the framework font's tile segment at the current bank position (required before any screen
    /// declares a text overlay).</summary>
    public void DefineFontTiles() {
        if (m_fontTileBase >= 0) {
            throw new InvalidOperationException(message: "The font tiles are already declared.");
        }

        ClaimName(name: "font");
        m_fontTileBase = m_declaredTileCount;
        m_allocations.Add(item: new FontDeclaration());
        m_declaredTileCount += TextModule.GlyphCount;
    }

    /// <summary>Declares background palettes (slot allocation follows declaration order — the first declaration takes
    /// slot 0, the palette non-art screens render with).</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="paletteData">The palettes' bytes (8 per palette, palette-RAM wire form).</param>
    public void DefineBackgroundPalettes(string name, byte[] paletteData) {
        ArgumentNullException.ThrowIfNull(paletteData);
        ClaimName(name: name);
        m_allocations.Add(item: new BackgroundPaletteDeclaration(Name: name, Data: paletteData));
    }

    /// <summary>Declares object palettes (slot allocation follows declaration order).</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="paletteData">The palettes' bytes (8 per palette, palette-RAM wire form).</param>
    public void DefineObjectPalettes(string name, byte[] paletteData) {
        ArgumentNullException.ThrowIfNull(paletteData);
        ClaimName(name: name);
        m_allocations.Add(item: new ObjectPaletteDeclaration(Name: name, Data: paletteData));
    }

    /// <summary>Declares a literal screen: 1024 map cells plus text overlays.</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="cells">The 32×32 map cells (tile ids in the game's own bank layout).</param>
    /// <param name="overlays">The text overlays.</param>
    public void DefineScreen(string name, byte[] cells, IReadOnlyList<ScreenText> overlays) {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(overlays);

        if (cells.Length != MapByteCount) {
            throw new ArgumentException(message: $"A screen is {MapSide}×{MapSide} = {MapByteCount} cells.", paramName: nameof(cells));
        }

        ClaimName(name: name);
        m_literalScreens.Add(item: (name, cells, overlays));
    }

    /// <summary>Declares an art-backed screen: a baked <c>PBAK</c> background section becomes the screen, with the
    /// overlays composed on top (overlay cells swap to font tiles AND return to attribute 0 — the menu-text
    /// contract).</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="art">The parsed background section.</param>
    /// <param name="overlays">The text overlays.</param>
    public void DefineArtScreen(string name, PbakBackground art, IReadOnlyList<ScreenText> overlays) {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(overlays);
        ClaimName(name: name);
        m_allocations.Add(item: new ArtScreenDeclaration(Name: name, Art: art, Overlays: overlays));
        m_declaredTileCount += art.TileCount;
    }

    /// <summary>Declares a plain linked background (baked art no screen composes onto).</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="art">The parsed background section.</param>
    public void DefineBackgroundArt(string name, PbakBackground art) {
        ArgumentNullException.ThrowIfNull(art);
        ClaimName(name: name);
        m_allocations.Add(item: new BackgroundArtDeclaration(Name: name, Art: art));
        m_declaredTileCount += art.TileCount;
    }

    /// <summary>Declares a bundle's sprite sections for linking (each set's frames land relocated, with a runtime
    /// frame table — see <see cref="LinkedSpriteSet"/>).</summary>
    /// <param name="name">A unique name (sets land as <c>&lt;name&gt;-0</c>, <c>&lt;name&gt;-1</c>, …).</param>
    /// <param name="bundle">The parsed bundle (at least one sprite section).</param>
    public void DefineSpriteArt(string name, PbakBundle bundle) {
        ArgumentNullException.ThrowIfNull(bundle);

        if (bundle.Sprites.Count == 0) {
            throw new ArgumentException(message: $"The '{name}' bundle carries no sprite sections.", paramName: nameof(bundle));
        }

        ClaimName(name: name);
        m_allocations.Add(item: new SpriteArtDeclaration(Name: name, Bundle: bundle));

        foreach (var sprites in bundle.Sprites) {
            m_declaredTileCount += sprites.TileCount;
        }
    }

    /// <summary>Declares a raw data table (a rules blob, a layout, anything the game indexes itself).</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="bytes">The table's bytes.</param>
    public void DefineTable(string name, byte[] bytes) {
        ArgumentNullException.ThrowIfNull(bytes);
        ClaimName(name: name);
        m_tables.Add(item: (name, bytes));
    }

    /// <summary>Declares a fixed-stride record table (a deck, a piece table, per-entry rule rows) — every record's
    /// length is validated against the stride, and the linked result carries the stride/count facts.</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="stride">The bytes per record (≥ 1).</param>
    /// <param name="records">The records, in table order.</param>
    public void DefineRecords(string name, int stride, IReadOnlyList<byte[]> records) {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: stride, other: 1, paramName: nameof(stride));
        ClaimName(name: name);

        var bytes = new byte[(stride * records.Count)];

        for (var index = 0; (index < records.Count); index++) {
            if (records[index].Length != stride) {
                throw new ArgumentException(message: $"Record {index} of '{name}' is {records[index].Length} bytes (stride {stride}).", paramName: nameof(records));
            }

            records[index].CopyTo(array: bytes, index: (index * stride));
        }

        m_records.Add(item: (name, stride, records.Count, bytes));
    }

    /// <summary>Declares a string (encoded to font tile ids at link time, <c>0xFF</c>-terminated for the framework
    /// printers).</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="text">The text (the framework font's character set).</param>
    public void DefineText(string name, string text) {
        ArgumentNullException.ThrowIfNull(text);
        ClaimName(name: name);
        m_texts.Add(item: (name, text));
    }

    /// <summary>Declares a scripted input sequence (the attract-script shape: (buttons, frames) pairs,
    /// <c>0xFF</c>-terminated, consumable by <see cref="InputModule.EmitScriptStart"/>).</summary>
    /// <param name="name">A unique name.</param>
    /// <param name="steps">The steps, in play order.</param>
    public void DefineInputScript(string name, IReadOnlyList<InputScriptStep> steps) {
        ArgumentNullException.ThrowIfNull(steps);
        ClaimName(name: name);

        var bytes = new byte[((steps.Count * 2) + 1)];

        for (var index = 0; (index < steps.Count); index++) {
            if (steps[index].Buttons == 0xFF) {
                throw new ArgumentException(message: $"Step {index} of '{name}' holds 0xFF — the script terminator.", paramName: nameof(steps));
            }

            if (steps[index].Frames == 0) {
                throw new ArgumentException(message: $"Step {index} of '{name}' holds for 0 frames.", paramName: nameof(steps));
            }

            bytes[(index * 2)] = steps[index].Buttons;
            bytes[((index * 2) + 1)] = steps[index].Frames;
        }

        bytes[^1] = 0xFF;
        m_scripts.Add(item: (name, bytes));
    }

    /// <summary>Builds a default score table in the framework's high-score payload shape: per entry, three initials
    /// as font tile ids then the score as 3 packed-BCD bytes (most significant first) — the battery save's ROM
    /// defaults for any game with a score board.</summary>
    /// <param name="entries">The entries, best first.</param>
    /// <param name="fontTileBase">The game's font tile base.</param>
    /// <returns>The payload bytes (6 per entry).</returns>
    public static byte[] BuildScoreTable(IReadOnlyList<ScoreTableEntry> entries, byte fontTileBase) {
        ArgumentNullException.ThrowIfNull(entries);

        var payload = new byte[(entries.Count * 6)];
        var index = 0;

        foreach (var entry in entries) {
            if (entry.Initials.Length != 3) {
                throw new ArgumentException(message: $"Initials '{entry.Initials}' are not exactly three characters.", paramName: nameof(entries));
            }

            if ((entry.Score < 0) || (entry.Score > 999999)) {
                throw new ArgumentException(message: $"Score {entry.Score} is outside 0..999999.", paramName: nameof(entries));
            }

            foreach (var character in entry.Initials) {
                payload[index++] = TextModule.TileFor(fontTileBase: fontTileBase, character: character);
            }

            payload[index++] = PackBcdPair(value: (entry.Score / 10000));
            payload[index++] = PackBcdPair(value: ((entry.Score / 100) % 100));
            payload[index++] = PackBcdPair(value: (entry.Score % 100));
        }

        return payload;
    }

    /// <summary>Links the whole manifest: allocations in declaration order through the framework's
    /// <see cref="AssetLinker"/>, then the tables/texts/scripts/screens into the data window, then the bank and
    /// palette-table seals. Call once, after the framework is constructed with <see cref="FontTileBase"/>.</summary>
    /// <param name="framework">The game's framework facade.</param>
    /// <returns>The linked lookup the game's emission references.</returns>
    public LinkedManifest Link(GameFramework framework) {
        ArgumentNullException.ThrowIfNull(framework);

        if ((m_fontTileBase < 0) && ((m_literalScreens.Count > 0) || HasOverlayAllocations())) {
            throw new InvalidOperationException(message: "Screens with text overlays need DefineFontTiles.");
        }

        var screens = new Dictionary<string, LinkedScreen>(comparer: StringComparer.Ordinal);
        var backgroundArt = new Dictionary<string, LinkedBackground>(comparer: StringComparer.Ordinal);
        var spriteArt = new Dictionary<string, IReadOnlyList<LinkedSpriteSet>>(comparer: StringComparer.Ordinal);

        LinkAllocations(framework: framework, screens: screens, backgroundArt: backgroundArt, spriteArt: spriteArt);
        LinkLiteralScreens(framework: framework, screens: screens);

        return new LinkedManifest(
            backgroundArt: backgroundArt,
            backgroundPalettes: framework.Assets.SealBackgroundPalettes(),
            fontTileBase: FontTileBase,
            objectPalettes: framework.Assets.SealObjectPalettes(),
            records: LinkRecords(framework: framework),
            screens: screens,
            scripts: LinkNamedBlocks(framework: framework, blocks: m_scripts),
            spriteArt: spriteArt,
            tables: LinkNamedBlocks(framework: framework, blocks: m_tables),
            texts: LinkTexts(framework: framework),
            tileBank: framework.Assets.SealTileBank()
        );
    }

    // The allocation walk: tiles into the bank and palettes into the slots in DECLARATION order, so every base the
    // manifest predicted at declare time (the font's above all) lands where it said it would.
    private void LinkAllocations(GameFramework framework, Dictionary<string, LinkedScreen> screens, Dictionary<string, LinkedBackground> backgroundArt, Dictionary<string, IReadOnlyList<LinkedSpriteSet>> spriteArt) {
        var linker = framework.Assets;

        foreach (var declaration in m_allocations) {
            switch (declaration) {
                case TileDeclaration tiles:
                    _ = linker.AddTiles(name: tiles.Name, tiles2bpp: tiles.Tiles);
                    break;
                case FontDeclaration:
                    var fontBase = linker.AddTiles(name: "font", tiles2bpp: TextModule.BuildFontTiles());

                    if (fontBase != m_fontTileBase) {
                        throw new InvalidOperationException(message: $"The font landed at tile {fontBase}, not the declared {m_fontTileBase} — an allocation drifted between declare and link.");
                    }

                    break;
                case BackgroundPaletteDeclaration palettes:
                    _ = linker.AddBackgroundPalettes(name: palettes.Name, paletteData: palettes.Data);
                    break;
                case ObjectPaletteDeclaration palettes:
                    _ = linker.AddObjectPalettes(name: palettes.Name, paletteData: palettes.Data);
                    break;
                case ArtScreenDeclaration artScreen:
                    screens.Add(key: artScreen.Name, value: LinkArtScreen(framework: framework, declaration: artScreen));
                    break;
                case BackgroundArtDeclaration art:
                    backgroundArt.Add(key: art.Name, value: linker.LinkBackground(name: art.Name, background: art.Art));
                    break;
                case SpriteArtDeclaration sprites:
                    spriteArt.Add(key: sprites.Name, value: LinkSpriteArt(linker: linker, declaration: sprites));
                    break;
                default:
                    throw new InvalidOperationException(message: $"Unhandled declaration '{declaration.Name}'.");
            }
        }
    }

    // An art-backed screen: relocate the baked background, compose the overlays onto the fresh copies (tile swaps
    // into the font, attributes zeroed back to palette 0 under the text), then land the blocks under the screen's name.
    private static LinkedScreen LinkArtScreen(GameFramework framework, ArtScreenDeclaration declaration) {
        var relocated = framework.Assets.Relocate(name: declaration.Name, background: declaration.Art);

        ApplyOverlays(cells: relocated.TileMap, overlays: declaration.Overlays, text: framework.Text);

        if (relocated.AttributeMap is { } attributes) {
            ClearOverlayCells(attributes: attributes, overlays: declaration.Overlays);
        }

        return new LinkedScreen(
            Attributes: ((relocated.AttributeMap is { } cleared) ? framework.Data.Add(name: $"{declaration.Name}-attributes", bytes: cleared) : (RomTable?)null),
            Map: framework.Data.Add(name: $"{declaration.Name}-map", bytes: relocated.TileMap)
        );
    }
    private static IReadOnlyList<LinkedSpriteSet> LinkSpriteArt(AssetLinker linker, SpriteArtDeclaration declaration) {
        var linked = new List<LinkedSpriteSet>(capacity: declaration.Bundle.Sprites.Count);

        for (var index = 0; (index < declaration.Bundle.Sprites.Count); index++) {
            linked.Add(item: linker.LinkSpriteSet(name: $"{declaration.Name}-{index}", sprites: declaration.Bundle.Sprites[index]));
        }

        return linked;
    }
    private void LinkLiteralScreens(GameFramework framework, Dictionary<string, LinkedScreen> screens) {
        foreach (var (name, cells, overlays) in m_literalScreens) {
            var map = new byte[MapByteCount];

            cells.CopyTo(array: map, index: 0);
            ApplyOverlays(cells: map, overlays: overlays, text: framework.Text);
            screens.Add(key: name, value: new LinkedScreen(Attributes: null, Map: framework.Data.Add(name: $"{name}-map", bytes: map)));
        }
    }
    private Dictionary<string, RomRecords> LinkRecords(GameFramework framework) {
        var linked = new Dictionary<string, RomRecords>(comparer: StringComparer.Ordinal);

        foreach (var (name, stride, count, bytes) in m_records) {
            linked.Add(key: name, value: new RomRecords(Count: count, Stride: stride, Table: framework.Data.Add(name: name, bytes: bytes)));
        }

        return linked;
    }
    private Dictionary<string, RomTable> LinkTexts(GameFramework framework) {
        var linked = new Dictionary<string, RomTable>(comparer: StringComparer.Ordinal);

        foreach (var (name, text) in m_texts) {
            linked.Add(key: name, value: framework.Data.AddText(name: name, text: text));
        }

        return linked;
    }
    private static Dictionary<string, RomTable> LinkNamedBlocks(GameFramework framework, List<(string Name, byte[] Bytes)> blocks) {
        var linked = new Dictionary<string, RomTable>(comparer: StringComparer.Ordinal);

        foreach (var (name, bytes) in blocks) {
            linked.Add(key: name, value: framework.Data.Add(name: name, bytes: bytes));
        }

        return linked;
    }

    // The overlay composition: full tile-cell swaps into the framework font (encoded through the framework's own
    // text module, the single source of the font base).
    private static void ApplyOverlays(byte[] cells, IReadOnlyList<ScreenText> overlays, TextModule text) {
        foreach (var overlay in overlays) {
            ValidateOverlay(overlay: overlay);

            for (var index = 0; (index < overlay.Text.Length); index++) {
                cells[(((overlay.Row * MapSide) + overlay.Column) + index)] = text.TileFor(character: overlay.Text[index]);
            }
        }
    }

    // The matching attribute fixup: overlaid cells return to attribute 0 (palette 0, bank 0), so the text always
    // renders in the game's own palette regardless of the art's assignment.
    private static void ClearOverlayCells(byte[] attributes, IReadOnlyList<ScreenText> overlays) {
        foreach (var overlay in overlays) {
            Array.Clear(array: attributes, index: ((overlay.Row * MapSide) + overlay.Column), length: overlay.Text.Length);
        }
    }
    private static void ValidateOverlay(ScreenText overlay) {
        if ((overlay.Row < 0) || (overlay.Row >= MapSide) || (overlay.Column < 0) || ((overlay.Column + overlay.Text.Length) > MapSide)) {
            throw new InvalidOperationException(message: $"The overlay '{overlay.Text}' at ({overlay.Row}, {overlay.Column}) leaves the {MapSide}×{MapSide} map.");
        }
    }
    private bool HasOverlayAllocations() {
        foreach (var declaration in m_allocations) {
            if (declaration is ArtScreenDeclaration { Overlays.Count: > 0 }) {
                return true;
            }
        }

        return false;
    }
    private void ClaimName(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!m_names.Add(item: name)) {
            throw new ArgumentException(message: $"The manifest already declares '{name}'.", paramName: nameof(name));
        }
    }
    private static byte PackBcdPair(int value) => (byte)(((value / 10) << 4) | (value % 10));

    // The allocation-bearing declaration kinds, walked in declaration order at link time.
    private abstract record Declaration(string Name);
    private sealed record TileDeclaration(string Name, byte[] Tiles) : Declaration(Name: Name);
    private sealed record FontDeclaration() : Declaration(Name: "font");
    private sealed record BackgroundPaletteDeclaration(string Name, byte[] Data) : Declaration(Name: Name);
    private sealed record ObjectPaletteDeclaration(string Name, byte[] Data) : Declaration(Name: Name);
    private sealed record ArtScreenDeclaration(string Name, PbakBackground Art, IReadOnlyList<ScreenText> Overlays) : Declaration(Name: Name);
    private sealed record BackgroundArtDeclaration(string Name, PbakBackground Art) : Declaration(Name: Name);
    private sealed record SpriteArtDeclaration(string Name, PbakBundle Bundle) : Declaration(Name: Name);
}

/// <summary>
/// The linked manifest: every declared asset resolved to its table address (plus the sealed tile bank and palette
/// tables a game hands to its <see cref="FrameworkBootSpec"/>). Lookups throw on an unknown name — a typo fails the
/// forge at build time, never on the machine.
/// </summary>
internal sealed class LinkedManifest {
    private readonly Dictionary<string, LinkedBackground> m_backgroundArt;
    private readonly Dictionary<string, RomRecords> m_records;
    private readonly Dictionary<string, LinkedScreen> m_screens;
    private readonly Dictionary<string, RomTable> m_scripts;
    private readonly Dictionary<string, IReadOnlyList<LinkedSpriteSet>> m_spriteArt;
    private readonly Dictionary<string, RomTable> m_tables;
    private readonly Dictionary<string, RomTable> m_texts;

    /// <summary>Creates the manifest over every declared asset, already resolved to its table address.</summary>
    /// <param name="backgroundArt">The linked background art, keyed by name.</param>
    /// <param name="backgroundPalettes">The sealed background palette table.</param>
    /// <param name="fontTileBase">The linked font tile base.</param>
    /// <param name="objectPalettes">The sealed object palette table.</param>
    /// <param name="records">The linked record tables, keyed by name.</param>
    /// <param name="screens">The linked screens, keyed by name.</param>
    /// <param name="scripts">The linked attract scripts, keyed by name.</param>
    /// <param name="spriteArt">The linked sprite art sets, keyed by name.</param>
    /// <param name="tables">The linked raw tables, keyed by name.</param>
    /// <param name="texts">The linked text blocks, keyed by name.</param>
    /// <param name="tileBank">The sealed tile bank.</param>
    internal LinkedManifest(
        Dictionary<string, LinkedBackground> backgroundArt,
        RomTable backgroundPalettes,
        byte fontTileBase,
        RomTable objectPalettes,
        Dictionary<string, RomRecords> records,
        Dictionary<string, LinkedScreen> screens,
        Dictionary<string, RomTable> scripts,
        Dictionary<string, IReadOnlyList<LinkedSpriteSet>> spriteArt,
        Dictionary<string, RomTable> tables,
        Dictionary<string, RomTable> texts,
        RomTable tileBank
    ) {
        m_backgroundArt = backgroundArt;
        BackgroundPalettes = backgroundPalettes;
        FontTileBase = fontTileBase;
        ObjectPalettes = objectPalettes;
        m_records = records;
        m_screens = screens;
        m_scripts = scripts;
        m_spriteArt = spriteArt;
        m_tables = tables;
        m_texts = texts;
        TileBank = tileBank;
    }

    /// <summary>The sealed background palette table (the boot spec's <c>BgPalettes</c>).</summary>
    public RomTable BackgroundPalettes { get; }
    /// <summary>The linked font tile base.</summary>
    public byte FontTileBase { get; }
    /// <summary>The sealed object palette table (the boot spec's <c>ObjPalettes</c>).</summary>
    public RomTable ObjectPalettes { get; }
    /// <summary>The sealed tile bank (the boot spec's <c>Tiles</c>; its length is the tile byte count).</summary>
    public RomTable TileBank { get; }

    /// <summary>Resolves a linked background by name.</summary>
    /// <param name="name">The declared name.</param>
    /// <returns>The linked background.</returns>
    public LinkedBackground BackgroundArt(string name) => Lookup(source: m_backgroundArt, name: name, kind: "background art");

    /// <summary>Resolves a record table by name.</summary>
    /// <param name="name">The declared name.</param>
    /// <returns>The record table.</returns>
    public RomRecords Records(string name) => Lookup(source: m_records, name: name, kind: "record table");

    /// <summary>Resolves a screen by name.</summary>
    /// <param name="name">The declared name.</param>
    /// <returns>The linked screen.</returns>
    public LinkedScreen Screen(string name) => Lookup(source: m_screens, name: name, kind: "screen");

    /// <summary>Resolves an input script by name.</summary>
    /// <param name="name">The declared name.</param>
    /// <returns>The script table.</returns>
    public RomTable InputScript(string name) => Lookup(source: m_scripts, name: name, kind: "input script");

    /// <summary>Resolves a bundle's linked sprite sets by name.</summary>
    /// <param name="name">The declared name.</param>
    /// <returns>The linked sets, in wire order.</returns>
    public IReadOnlyList<LinkedSpriteSet> SpriteArt(string name) => Lookup(source: m_spriteArt, name: name, kind: "sprite art");

    /// <summary>Resolves a raw table by name.</summary>
    /// <param name="name">The declared name.</param>
    /// <returns>The table.</returns>
    public RomTable Table(string name) => Lookup(source: m_tables, name: name, kind: "table");

    /// <summary>Resolves a string by name.</summary>
    /// <param name="name">The declared name.</param>
    /// <returns>The string table (<c>0xFF</c>-terminated tile ids).</returns>
    public RomTable Text(string name) => Lookup(source: m_texts, name: name, kind: "text");

    private static TValue Lookup<TValue>(Dictionary<string, TValue> source, string name, string kind) =>
        (source.TryGetValue(key: name, value: out var value) ? value : throw new KeyNotFoundException(message: $"The manifest declares no {kind} named '{name}'."));
}
