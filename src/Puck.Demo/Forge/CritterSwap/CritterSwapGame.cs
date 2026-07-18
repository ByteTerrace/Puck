using Puck.Demo.Forge.Framework;

namespace Puck.Demo.Forge;

/// <summary>
/// The CRITTER-SWAP cartridge: a whimsical two-cart trading toy on the SM83 game framework. Its SRAM holds ONE critter
/// (a species id + a level); the title paints that critter — its little face, its name, its level, in its own field
/// colour — under PRESS START TO TRADE. START opens the trade, which runs the reusable <see cref="LinkProtocolModule"/>
/// over the link cable: two carts that both offer negotiate roles (DIV-seeded, boot-order-proof), exchange a small
/// checksummed block, and — on a clean handshake — each commits the OTHER cart's critter to its own battery save and
/// shows the new arrival. With no partner the protocol times out and the cart narrates NO LINK instead of freezing. The
/// two cabinets in the overworld seed DIFFERENT starting critters (their save slots), so the swap is visible at a glance.
/// </summary>
internal sealed class CritterSwapGame {
    private const byte GameLcdc = Hw.LcdBackgroundAndObjects;

    // Screen layout (the 20-column visible width; the critter portion is shared by the title and arrival screens).
    private const int FaceRow = 5;
    private const int FaceColumn = 9;
    private const int LevelLabelColumn = 8;
    private const int LevelRow = 10;
    private const int NameRow = 8;

    private readonly GameFramework m_fw;
    private readonly LinkProtocolModule m_link;
    private readonly RomTable m_bgPalettes;
    private readonly RomTable m_objPalettes;
    private readonly RomTable m_tiles;
    private readonly RomTable m_bootMap;
    private readonly RomTable m_paletteTable;
    private readonly RomTable[] m_names;
    private readonly RomTable m_strTitle;
    private readonly RomTable m_strPromptStart;
    private readonly RomTable m_strPromptTrade;
    private readonly RomTable m_strTrading;
    private readonly RomTable m_strLinking;
    private readonly RomTable m_strArrival;
    private readonly RomTable m_strPressA;
    private readonly RomTable m_strNoLink;
    private readonly RomTable m_strNoPartner;
    private readonly RomTable m_strPressB;
    private readonly RomTable m_strLevel;
    private readonly int m_subRenderCore;
    private readonly int m_subRenderName;
    private readonly int m_subApplyPalette;
    private readonly int m_subBuildOutBlock;

    // The manifest: the framework font at bank base 0 (so face tile ids clear the font), the species face tiles right
    // after it, a default palette pair the runtime recolours per species, and one blank boot map (each state paints).
    private static GameManifest BuildManifest() {
        var manifest = new GameManifest();

        manifest.DefineFontTiles();
        manifest.DefineTiles(name: "faces", tiles2bpp: CritterSwapProtocol.BuildFaceTiles());
        manifest.DefineBackgroundPalettes(name: "bg", paletteData: HgbImage.EncodePalette(palette: CritterSwapProtocol.Species[0].Palette));
        manifest.DefineObjectPalettes(name: "obj", paletteData: HgbImage.EncodePalette(palette: CritterSwapProtocol.Species[0].Palette));
        manifest.DefineScreen(name: "boot", cells: new byte[0x400], overlays: []);

        return manifest;
    }

    private CritterSwapGame() {
        var manifest = BuildManifest();

        if (manifest.FontTileBase != 0) {
            throw new InvalidOperationException(message: $"The manifest landed the font at tile {manifest.FontTileBase}, not the pinned 0 (the species face tiles assume the font occupies tiles 0..{(TextModule.GlyphCount - 1)}).");
        }

        m_fw = new GameFramework(
            fontTileBase: manifest.FontTileBase,
            saveDefaultPayload: [CritterSwapProtocol.DefaultSpeciesForSlot(slot: 0), CritterSwapProtocol.Species[0].Level],
            saveVersion: CritterSwapProtocol.SaveVersion
        );

        var linked = manifest.Link(framework: m_fw);

        m_bgPalettes = linked.BackgroundPalettes;
        m_objPalettes = linked.ObjectPalettes;
        m_tiles = linked.TileBank;
        m_bootMap = linked.Screen(name: "boot").Map;

        // The per-species field palettes (8 bytes each, palette-RAM wire form) the runtime copies into background slot 0.
        var palettes = new byte[(CritterSwapProtocol.SpeciesCount * 8)];

        for (var index = 0; (index < CritterSwapProtocol.SpeciesCount); index++) {
            HgbImage.EncodePalette(palette: CritterSwapProtocol.Species[index].Palette).CopyTo(array: palettes, index: (index * 8));
        }

        m_paletteTable = m_fw.Data.Add(name: "species-palettes", bytes: palettes);

        m_names = new RomTable[CritterSwapProtocol.SpeciesCount];

        for (var index = 0; (index < CritterSwapProtocol.SpeciesCount); index++) {
            m_names[index] = m_fw.Data.AddText(name: $"name-{index}", text: CritterSwapProtocol.Species[index].Name);
        }

        m_strTitle = m_fw.Data.AddText(name: "str-title", text: "CRITTER SWAP");
        m_strPromptStart = m_fw.Data.AddText(name: "str-prompt-start", text: "PRESS START");
        m_strPromptTrade = m_fw.Data.AddText(name: "str-prompt-trade", text: "TO TRADE");
        m_strTrading = m_fw.Data.AddText(name: "str-trading", text: "TRADING");
        m_strLinking = m_fw.Data.AddText(name: "str-linking", text: "LINK CABLE");
        m_strArrival = m_fw.Data.AddText(name: "str-arrival", text: "NEW FRIEND");
        m_strPressA = m_fw.Data.AddText(name: "str-press-a", text: "PRESS A");
        m_strNoLink = m_fw.Data.AddText(name: "str-no-link", text: "NO LINK");
        m_strNoPartner = m_fw.Data.AddText(name: "str-no-partner", text: "NO PARTNER");
        m_strPressB = m_fw.Data.AddText(name: "str-press-b", text: "PRESS B");
        m_strLevel = m_fw.Data.AddText(name: "str-level", text: "LV");

        m_link = new LinkProtocolModule(emitter: m_fw.Emitter, link: new LinkModule(emitter: m_fw.Emitter), ram: CritterSwapProtocol.LinkRam);

        m_subRenderCore = m_fw.Emitter.NewLabel();
        m_subRenderName = m_fw.Emitter.NewLabel();
        m_subApplyPalette = m_fw.Emitter.NewLabel();
        m_subBuildOutBlock = m_fw.Emitter.NewLabel();

        m_fw.States.DefineState(id: CritterSwapProtocol.StateTitle, emitEnter: EmitTitleEnter, emitTick: EmitTitleTick);
        m_fw.States.DefineState(id: CritterSwapProtocol.StateTrade, emitEnter: EmitTradeEnter, emitTick: EmitTradeTick);
        m_fw.States.DefineState(id: CritterSwapProtocol.StateArrival, emitEnter: EmitArrivalEnter, emitTick: EmitArrivalTick);
        m_fw.States.DefineState(id: CritterSwapProtocol.StateNoLink, emitEnter: EmitNoLinkEnter, emitTick: EmitNoLinkTick);
    }

    /// <summary>Assembles the cartridge.</summary>
    /// <param name="title">The header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title) {
        var game = new CritterSwapGame();
        var spec = new FrameworkBootSpec(
            BgPalettes: game.m_bgPalettes,
            ObjPalettes: game.m_objPalettes,
            Tiles: game.m_tiles,
            TileByteCount: game.m_tiles.Length,
            InitialMap: game.m_bootMap,
            Lcdc: GameLcdc,
            InitialState: CritterSwapProtocol.StateTitle
        );

        return game.m_fw.BuildRom(title: title, bootSpec: spec, emitGameLibrary: game.EmitGameLibrary);
    }

    // ==== The game library (the link protocol + the shared render/build subroutines). ===================================

    private void EmitGameLibrary(Sm83Emitter e) {
        m_link.EmitLibrary();
        EmitRenderCoreSubroutine(e: e);
        EmitRenderNameSubroutine(e: e);
        EmitApplyPaletteSubroutine(e: e);
        EmitBuildOutBlockSubroutine(e: e);
    }

    // renderCore: paints the held critter's face (a 2×2 metatile), name, and level onto the (LCD-off) screen — the
    // portion the title and arrival screens share. Reads the save mirror; clobbers everything.
    private void EmitRenderCoreSubroutine(Sm83Emitter e) {
        e.MarkLabel(label: m_subRenderCore);

        // Face: tile id = FaceTileBase + species, painted at a 2×2 block of map cells.
        e.LoadAFromAddress(address: CritterSwapProtocol.SaveSpecies);
        e.ArithmeticImmediate(op: AluOp.Add, value: CritterSwapProtocol.FaceTileBase);
        e.StoreAToAddress(address: Hw.MapCell(row: FaceRow, column: FaceColumn));
        e.StoreAToAddress(address: Hw.MapCell(row: FaceRow, column: (FaceColumn + 1)));
        e.StoreAToAddress(address: Hw.MapCell(row: (FaceRow + 1), column: FaceColumn));
        e.StoreAToAddress(address: Hw.MapCell(row: (FaceRow + 1), column: (FaceColumn + 1)));

        // Name (species dispatch) and level ("LV" + the packed-BCD level byte).
        e.Call(label: m_subRenderName);
        m_fw.Text.EmitPrintDirect(text: m_strLevel, row: LevelRow, column: LevelLabelColumn);
        m_fw.Text.EmitPrintBcdDirect(bcdAddress: CritterSwapProtocol.SaveLevel, byteCount: 1, row: LevelRow, column: (LevelLabelColumn + 3));
        e.Return();
    }

    // renderName: prints the held species' centred name (a compare-chain over the species id — no runtime pointer
    // table; every branch is a fixed, centred EmitPrintDirect). The last species is the fall-through default.
    private void EmitRenderNameSubroutine(Sm83Emitter e) {
        e.MarkLabel(label: m_subRenderName);

        for (var index = 0; (index < CritterSwapProtocol.SpeciesCount); index++) {
            var name = CritterSwapProtocol.Species[index].Name;
            var column = ((20 - name.Length) / 2);

            if (index < (CritterSwapProtocol.SpeciesCount - 1)) {
                var next = e.NewLabel();

                e.LoadAFromAddress(address: CritterSwapProtocol.SaveSpecies);
                e.ArithmeticImmediate(op: AluOp.Compare, value: (byte)index);
                e.JumpRelative(condition: Condition.NotZero, label: next);
                m_fw.Text.EmitPrintDirect(text: m_names[index], row: NameRow, column: column);
                e.Return();
                e.MarkLabel(label: next);
            } else {
                m_fw.Text.EmitPrintDirect(text: m_names[index], row: NameRow, column: column);
                e.Return();
            }
        }
    }

    // applyPalette: copies the held species' four-colour field palette into background palette slot 0 (LCD-off / VBlank
    // only — the enters call it with the LCD off). Reads the save mirror; clobbers A, B, D, E, H, L.
    private void EmitApplyPaletteSubroutine(Sm83Emitter e) {
        var loop = e.NewLabel();

        e.MarkLabel(label: m_subApplyPalette);
        e.LoadAFromAddress(address: CritterSwapProtocol.SaveSpecies);
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×2
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×4
        e.Arithmetic(op: AluOp.Add, source: Reg8.A); // ×8 (8 bytes per palette; species ≤ 7 → offset ≤ 56).
        e.Load(destination: Reg8.E, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.D, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: m_paletteTable.Address);
        e.AddToHl(pair: Reg16.De);

        // BGPI = auto-increment from palette 0, colour 0; then stream the 8 palette bytes through BGPD.
        e.LoadAImmediate(value: Hw.PaletteAutoIncrement);
        e.StoreAToHighPage(port: Hw.PortBgPaletteIndex);
        e.LoadImmediate(destination: Reg8.B, value: 8);
        e.MarkLabel(label: loop);
        e.Load(destination: Reg8.A, source: Reg8.Memory);
        e.StoreAToHighPage(port: Hw.PortBgPaletteData);
        e.Increment(pair: Reg16.Hl);
        e.Decrement(register: Reg8.B);
        e.JumpRelative(condition: Condition.NotZero, label: loop);
        e.Return();
    }

    // buildOutBlock: fills the link protocol's outgoing block from the save mirror — [magic, species, level, checksum]
    // (the additive checksum the LinkProtocolModule validates on the far side). Clobbers A, C.
    private void EmitBuildOutBlockSubroutine(Sm83Emitter e) {
        var block = CritterSwapProtocol.LinkRam.OutBlock;

        e.MarkLabel(label: m_subBuildOutBlock);
        e.LoadAImmediate(value: LinkProtocolModule.MagicByte);
        e.StoreAToAddress(address: block);
        e.LoadAFromAddress(address: CritterSwapProtocol.SaveSpecies);
        e.StoreAToAddress(address: (ushort)(block + 1));
        e.LoadAFromAddress(address: CritterSwapProtocol.SaveLevel);
        e.StoreAToAddress(address: (ushort)(block + 2));
        // checksum = (magic + species + level) & 0xFF.
        e.LoadAImmediate(value: LinkProtocolModule.MagicByte);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: CritterSwapProtocol.SaveSpecies);
        e.Arithmetic(op: AluOp.Add, source: Reg8.C);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadAFromAddress(address: CritterSwapProtocol.SaveLevel);
        e.Arithmetic(op: AluOp.Add, source: Reg8.C);
        e.StoreAToAddress(address: (ushort)(block + 3));
        e.Return();
    }

    // ==== The four states. =============================================================================================

    private void EmitTitleEnter(Sm83Emitter e) {
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        m_fw.Text.EmitPrintDirect(text: m_strTitle, row: 1, column: 4);
        e.Call(label: m_subRenderCore);
        m_fw.Text.EmitPrintDirect(text: m_strPromptStart, row: 14, column: 4);
        m_fw.Text.EmitPrintDirect(text: m_strPromptTrade, row: 15, column: 6);
        e.Call(label: m_subApplyPalette);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }
    private void EmitTitleTick(Sm83Emitter e) {
        var noStart = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.ArithmeticImmediate(op: AluOp.And, value: CritterSwapProtocol.ButtonStart);
        e.JumpRelative(condition: Condition.Zero, label: noStart);
        m_fw.States.EmitRequestState(id: CritterSwapProtocol.StateTrade);
        e.MarkLabel(label: noStart);
    }
    private void EmitTradeEnter(Sm83Emitter e) {
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        m_fw.Text.EmitPrintDirect(text: m_strTrading, row: 8, column: 6);
        m_fw.Text.EmitPrintDirect(text: m_strLinking, row: 10, column: 5);
        e.Call(label: m_subApplyPalette);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);

        // Stage this cart's critter for the exchange, then arm the protocol at its first phase.
        e.Call(label: m_subBuildOutBlock);
        m_link.EmitBegin();
    }
    private void EmitTradeTick(Sm83Emitter e) {
        var doCommit = e.NewLabel();
        var doNoLink = e.NewLabel();
        var done = e.NewLabel();

        // Advance the link protocol one step, then act on a terminal phase.
        m_link.EmitTick();

        e.LoadAFromAddress(address: CritterSwapProtocol.LinkRam.Phase);
        e.ArithmeticImmediate(op: AluOp.Compare, value: LinkProtocolModule.PhaseDoneOk);
        e.JumpAbsolute(condition: Condition.Zero, label: doCommit);
        e.ArithmeticImmediate(op: AluOp.Compare, value: LinkProtocolModule.PhaseNoLink);
        e.JumpAbsolute(condition: Condition.Zero, label: doNoLink);
        e.JumpAbsolute(label: done);

        // COMMIT: write the partner's critter (InBlock[1] species, InBlock[2] level) into our save, persist, and show it.
        e.MarkLabel(label: doCommit);
        e.LoadAFromAddress(address: (ushort)(CritterSwapProtocol.LinkRam.InBlock + 1));
        e.StoreAToAddress(address: CritterSwapProtocol.SaveSpecies);
        e.LoadAFromAddress(address: (ushort)(CritterSwapProtocol.LinkRam.InBlock + 2));
        e.StoreAToAddress(address: CritterSwapProtocol.SaveLevel);
        m_fw.Save.EmitStore();
        m_fw.States.EmitRequestState(id: CritterSwapProtocol.StateArrival);
        e.JumpAbsolute(label: done);

        e.MarkLabel(label: doNoLink);
        m_fw.States.EmitRequestState(id: CritterSwapProtocol.StateNoLink);

        e.MarkLabel(label: done);
    }
    private void EmitArrivalEnter(Sm83Emitter e) {
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        m_fw.Text.EmitPrintDirect(text: m_strArrival, row: 1, column: 5);
        e.Call(label: m_subRenderCore);
        m_fw.Text.EmitPrintDirect(text: m_strPressA, row: 15, column: 6);
        e.Call(label: m_subApplyPalette);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }
    private void EmitArrivalTick(Sm83Emitter e) {
        var stay = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.ArithmeticImmediate(op: AluOp.And, value: (byte)(CritterSwapProtocol.ButtonA | CritterSwapProtocol.ButtonB));
        e.JumpRelative(condition: Condition.Zero, label: stay);
        m_fw.States.EmitRequestState(id: CritterSwapProtocol.StateTitle);
        e.MarkLabel(label: stay);
    }
    private void EmitNoLinkEnter(Sm83Emitter e) {
        m_fw.Bg.EmitLcdOff();
        m_fw.Bg.EmitQueueClear();
        m_fw.Bg.EmitMapClear();
        m_fw.Text.EmitPrintDirect(text: m_strNoLink, row: 6, column: 6);
        m_fw.Text.EmitPrintDirect(text: m_strNoPartner, row: 8, column: 5);
        m_fw.Text.EmitPrintDirect(text: m_strPressB, row: 12, column: 6);
        e.Call(label: m_subApplyPalette);
        m_fw.Bg.EmitLcdOn(lcdc: GameLcdc);
    }
    private void EmitNoLinkTick(Sm83Emitter e) {
        var stay = e.NewLabel();

        e.LoadAFromAddress(address: FrameworkMemoryMap.InputPressed);
        e.ArithmeticImmediate(op: AluOp.And, value: (byte)(CritterSwapProtocol.ButtonA | CritterSwapProtocol.ButtonB));
        e.JumpRelative(condition: Condition.Zero, label: stay);
        m_fw.States.EmitRequestState(id: CritterSwapProtocol.StateTitle);
        e.MarkLabel(label: stay);
    }
}
