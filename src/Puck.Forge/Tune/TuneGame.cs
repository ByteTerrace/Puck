using Puck.Authoring;
using Puck.Forge.Framework;

namespace Puck.Forge.Tune;

/// <summary>
/// The minimal framework jukebox cartridge: a single play state that boots straight into the compiled
/// <see cref="AudioDocument"/>'s music loop and shows the song name plus "PUSH START" — pressing START toggles the
/// loop between playing and stopped. Everything about the loop (and the standard effect streams, kept so the driver
/// binds exactly like every other framework game) comes from <see cref="AudioDocumentCompiler"/>, never a hand array;
/// the cartridge's identity beyond the compiled sound tables is otherwise the smallest possible manifest — one tile
/// set, the font, one palette, one screen.
/// </summary>
internal sealed class TuneGame {
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;
    private const int PromptRow = 10;
    private const int TitleRow = 6;

    private readonly GameFramework m_fw;
    private readonly RomTable m_bgPalettes;
    private readonly RomTable m_objPalettes;
    private readonly RomTable m_tiles;
    private readonly RomTable m_playMap;

    // The game's identity as a declarative manifest: one flat tile (the felt-style background fill) + the font +
    // one palette + the single screen (name + prompt as text overlays) + the document's compiled sound tables (the
    // music loop swapped for the document's; the standard effect streams kept so the driver binds like every other
    // framework game).
    private static GameManifest BuildManifest(AudioDocument document, byte[] musicLoop) {
        var manifest = new GameManifest();

        manifest.DefineTiles(name: "game-tiles", tiles2bpp: BuildBlankTile());
        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg-gameplay", paletteData: BuildPalette());
        manifest.DefineObjectPalettes(name: "obj-gameplay", paletteData: BuildPalette());
        manifest.DefineScreen(name: "play", cells: new byte[0x400], overlays: BuildOverlays(document: document));
        SoundTables.DefineIn(manifest: manifest, musicLoop: musicLoop);

        return manifest;
    }

    private TuneGame(AudioDocument document, byte[] musicLoop) {
        var manifest = BuildManifest(document: document, musicLoop: musicLoop);
        var sound = new ApuSoundDriver();

        // The jukebox persists nothing; the framework still needs a non-empty defaults payload for the save mirror.
        m_fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: [0x00], saveVersion: 1, sound: sound);

        var linked = manifest.Link(framework: m_fw);

        sound.Bind(linked: linked);

        m_bgPalettes = linked.BackgroundPalettes;
        m_objPalettes = linked.ObjectPalettes;
        m_tiles = linked.TileBank;
        m_playMap = linked.Screen(name: "play").Map;

        m_fw.States.DefineState(id: TuneProtocol.StatePlay, emitEnter: EmitPlayEnter, emitTick: EmitPlayTick);
    }

    /// <summary>Assembles the jukebox <c>.gbc</c> from a compiled audio document.</summary>
    /// <param name="document">The normalized document (see <see cref="AudioDocumentStore.Load"/>).</param>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(AudioDocument document, string title) {
        ArgumentNullException.ThrowIfNull(document);

        var musicLoop = AudioDocumentCompiler.CompileMusicLoop(document: document);
        var game = new TuneGame(document: document, musicLoop: musicLoop);

        return game.m_fw.BuildRom(
            title: title,
            bootSpec: new FrameworkBootSpec(
                BgPalettes: game.m_bgPalettes,
                InitialMap: game.m_playMap,
                InitialState: TuneProtocol.StatePlay,
                Lcdc: GameLcdc,
                ObjPalettes: game.m_objPalettes,
                Tiles: game.m_tiles,
                TileByteCount: game.m_tiles.Length
            )
        );
    }

    // Entering play: the flag starts set, and the loop starts immediately — no title screen to wait through, the
    // one screen IS the jukebox.
    private void EmitPlayEnter(Sm83Emitter e) {
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: TuneProtocol.PlayingFlag);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicLoop);
    }

    // START (edge) toggles the playing flag and starts/stops the loop to match.
    private void EmitPlayTick(Sm83Emitter e) {
        var toStop = e.NewLabel();
        var done = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.TestBit(bit: 7, register: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: done);

        e.LoadAFromAddress(address: TuneProtocol.PlayingFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.Zero, label: toStop);

        // Was playing: stop it.
        e.XorA();
        e.StoreAToAddress(address: TuneProtocol.PlayingFlag);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicStop);
        e.JumpRelative(label: done);

        // Was stopped: start it (from the top — retriggering restarts the pattern, matching every other framework
        // game's START-to-play convention).
        e.MarkLabel(label: toStop);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: TuneProtocol.PlayingFlag);
        m_fw.Sound.EmitEffect(emitter: e, effectId: SoundTables.MusicLoop);

        e.MarkLabel(label: done);
    }

    // The overlays: the song name (sanitized to the framework font's character set) centred-ish on one row, "PUSH
    // START" on another — the whole title screen this cart needs, since it has no separate title state.
    private static IReadOnlyList<ScreenText> BuildOverlays(AudioDocument document) {
        var name = SanitizeForFont(text: (document.Name ?? "UNTITLED"));
        var nameColumn = Math.Clamp(value: ((32 - name.Length) / 2), max: 31, min: 0);

        return [
            new ScreenText(Row: TitleRow, Column: nameColumn, Text: name),
            new ScreenText(Row: PromptRow, Column: 8, Text: "PUSH START"),
        ];
    }

    // The title sanitizer restricts to space, 0-9, A-Z, '>', '-' for the cart's title art (the framework font also
    // has '.', deliberately excluded here): fold to upper invariant and replace anything else with a space so an
    // arbitrary document name never throws the linker's overlay encoder.
    private static string SanitizeForFont(string text) {
        var upper = text.ToUpperInvariant();
        var builder = new System.Text.StringBuilder(capacity: upper.Length);

        foreach (var character in upper) {
            builder.Append(value: (((character is (>= '0') and (<= '9')) || (character is (>= 'A') and (<= 'Z')) || (character is ' ' or '>' or '-')) ? character : ' '));
        }

        var sanitized = builder.ToString().Trim();

        return ((sanitized.Length > 20) ? sanitized[..20] : sanitized);
    }

    // A single flat 2bpp tile (colour index 1 throughout) — the jukebox's whole "art": a plain fill behind the text.
    private static byte[] BuildBlankTile() {
        var indices = new byte[64];

        Array.Fill(array: indices, value: (byte)1);

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }

    // A calm two-tone palette (both BG/OBJ tables share it — the OBJ table is unused, but the boot spec needs one).
    private static byte[] BuildPalette() =>
        HgbImage.EncodePalette(palette: [
            new HgbImage.Rgb(R: 0x10, G: 0x18, B: 0x30),
            new HgbImage.Rgb(R: 0x30, G: 0x48, B: 0x78),
            new HgbImage.Rgb(R: 0x88, G: 0xA8, B: 0xD8),
            new HgbImage.Rgb(R: 0xF0, G: 0xF4, B: 0xFC),
        ]);
}
