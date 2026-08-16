using Puck.Forge.Framework;

namespace Puck.Forge.Games;

/// <summary>
/// A minimal winnable cartridge: one state, one mechanic — walk a counter from 0 to
/// <see cref="ArcadeQuestProtocol.WinPosition"/> by pressing right — kept deliberately small: no sprites, no
/// scrolling, no save data, just the framework's text printers over a flat background. Modeled directly on
/// <c>Puck.Forge.Tune.TuneGame</c>'s shape (the smallest existing framework game): one manifest, one state, a
/// <c>Build</c>/<c>Verify</c> facade pair.
/// </summary>
internal sealed class ArcadeQuestGame {
    private const int CounterColumn = 10;
    private const int CounterLabelColumn = 6;
    private const int CounterRow = 9;
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;
    private const int PromptColumn = 3;
    private const int PromptRow = 6;
    private const int TitleColumn = 5;
    private const int TitleRow = 2;
    private const int WinColumn = 6;
    private const int WinRow = 13;

    private readonly GameFramework m_fw;
    private readonly RomTable m_bgPalettes;
    private readonly RomTable m_objPalettes;
    private readonly RomTable m_tiles;
    private readonly RomTable m_playMap;
    private readonly RomTable m_winText;

    // The game's identity as a declarative manifest: one flat tile (the neon-arcade background fill) + the font +
    // one palette + the single screen (title/prompt/counter-label as baked text overlays) + the runtime "YOU WIN"
    // string the win edge prints live.
    private static GameManifest BuildManifest() {
        var manifest = new GameManifest();

        manifest.DefineTiles(name: "game-tiles", tiles2bpp: MinimalArt.BuildBlankTile());
        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg-arcade", paletteData: BuildPalette());
        manifest.DefineObjectPalettes(name: "obj-arcade", paletteData: BuildPalette());
        manifest.DefineScreen(name: "play", cells: new byte[0x400], overlays: BuildOverlays());
        manifest.DefineText(name: "win-text", text: "YOU WIN");

        return manifest;
    }

    private ArcadeQuestGame() {
        var manifest = BuildManifest();

        // No persistence; the framework still needs a non-empty defaults payload for the save mirror.
        m_fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: [0x00], saveVersion: 1);

        var linked = manifest.Link(framework: m_fw);

        m_bgPalettes = linked.BackgroundPalettes;
        m_objPalettes = linked.ObjectPalettes;
        m_tiles = linked.TileBank;
        m_playMap = linked.Screen(name: "play").Map;
        m_winText = linked.Text(name: "win-text");

        m_fw.States.DefineState(emitEnter: EmitPlayEnter, emitTick: EmitPlayTick, id: ArcadeQuestProtocol.StatePlay);
    }

    /// <summary>Assembles the arcade-quest <c>.gbc</c>.</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title) {
        var game = new ArcadeQuestGame();

        return game.m_fw.BuildRom(
            title: title,
            bootSpec: new FrameworkBootSpec(
                BgPalettes: game.m_bgPalettes,
                InitialMap: game.m_playMap,
                InitialState: ArcadeQuestProtocol.StatePlay,
                Lcdc: GameLcdc,
                ObjPalettes: game.m_objPalettes,
                Tiles: game.m_tiles,
                TileByteCount: game.m_tiles.Length
            )
        );
    }

    // Entering play: defense-in-depth clear of the counter and win flag (see ArcadeQuestProtocol's remarks — the
    // framework's own boot block-fill already zero-fills this span every boot; this restates the guarantee at the
    // game's own boundary rather than relying on it silently, matching the Demo's win-flag correctness discipline).
    private void EmitPlayEnter(Sm83Emitter e) {
        e.XorA();
        e.StoreAToAddress(address: ArcadeQuestProtocol.Position);
        e.StoreAToAddress(address: ArcadeQuestProtocol.WinFlag);
    }
    // Every frame: once won, do nothing further (the early-return guard below). Otherwise, on a RIGHT edge,
    // increment the counter, repaint it, and — on reaching WinPosition — latch the win flag and print "YOU WIN".
    private void EmitPlayTick(Sm83Emitter e) {
        var done = e.NewLabel();
        var skipWin = e.NewLabel();

        e.LoadAFromAddress(address: ArcadeQuestProtocol.WinFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: done);

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.TestBit(bit: 0, register: Reg8.A); // InputModule.ButtonRight
        e.JumpRelative(condition: Condition.Zero, label: done);

        e.LoadAFromAddress(address: ArcadeQuestProtocol.Position);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: ArcadeQuestProtocol.Position);

        m_fw.Text.EmitPrintBcdQueued(bcdAddress: ArcadeQuestProtocol.Position, byteCount: 1, column: CounterColumn, row: CounterRow);

        e.LoadAFromAddress(address: ArcadeQuestProtocol.Position);
        e.ArithmeticImmediate(op: AluOp.Compare, value: ArcadeQuestProtocol.WinPosition);
        e.JumpRelative(condition: Condition.NotZero, label: skipWin);

        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: ArcadeQuestProtocol.WinFlag);
        m_fw.Text.EmitPrintQueued(column: WinColumn, row: WinRow, text: m_winText);

        e.MarkLabel(label: skipWin);
        e.MarkLabel(label: done);
    }
    private static IReadOnlyList<ScreenText> BuildOverlays() => [
        new ScreenText(Column: TitleColumn, Row: TitleRow, Text: "WALK RIGHT"),
        new ScreenText(Column: PromptColumn, Row: PromptRow, Text: "PRESS RIGHT 3X"),
        new ScreenText(Column: CounterLabelColumn, Row: CounterRow, Text: "POS"),
        new ScreenText(Column: CounterColumn, Row: CounterRow, Text: "00"),
    ];
    // A neon-arcade two-tone palette (both BG/OBJ tables share it — the OBJ table is unused, but the boot spec
    // needs one).
    private static byte[] BuildPalette() =>
        HgbImage.EncodePalette(palette: [
            new HgbImage.Rgb(B: 0x18, G: 0x08, R: 0x08),
            new HgbImage.Rgb(B: 0x48, G: 0x10, R: 0x20),
            new HgbImage.Rgb(B: 0xA0, G: 0x30, R: 0x60),
            new HgbImage.Rgb(B: 0xF8, G: 0xC0, R: 0xF0),
        ]);
}
